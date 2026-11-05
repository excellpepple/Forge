<#
.SYNOPSIS
    Shared engine behind Setup.ps1, Sync.ps1, Launch.ps1 and the Forge desktop
    app. Everything that decides "is this checkout safe to update" and "does
    this checkout match what the team pinned" lives here, so no caller can
    give two different answers.

.DESCRIPTION
    Import this with:

        Import-Module (Join-Path $Root 'Tools\ProjectWorkflow.psm1') -Force

    It reads project.config.json next to this module's parent folder (i.e.
    <repo root>\project.config.json) on import, so the SAME module file can be
    copied into any project that follows this superproject + submodule
    pattern -- nothing in this file names "CORE" or "IMGCORE" directly.

    A failure that should stop the current operation and be shown to the user
    verbatim -- not a bug, just "this cannot proceed" -- is raised by calling
    Fail, which throws a [ProjectWorkflow.WorkflowFailException]. A CLI
    wrapper catches that type and exits 1; Forge catches it and shows the
    message as a banner instead of crashing.

    Keep this file ASCII. Windows PowerShell 5.1 reads a BOM-less file as the
    system ANSI codepage, so a stray em-dash turns into mojibake at best and a
    parse error at worst.
#>

# ---------------------------------------------------------------------------
# A real, catchable failure type. A plain PowerShell class defined in a .psm1
# is only resolvable to a caller that used `using module` at parse time,
# which a hosted C# app cannot do -- so this is a genuine .NET type instead,
# created once per process and reused on every re-import.
if (-not ([System.Management.Automation.PSTypeName]'ProjectWorkflow.WorkflowFailException').Type) {
    Add-Type -WarningAction SilentlyContinue -TypeDefinition @"
    using System;
    namespace ProjectWorkflow {
        public class WorkflowFailException : Exception {
            public WorkflowFailException(string message) : base(message) { }
        }
    }
"@
}

# ---------------------------------------------------------------------------
# Project definition -- loaded from project.config.json, not hardcoded. This
# is the one thing that changes between projects that otherwise share this
# whole file. The submodule list and branches are NOT duplicated here: they
# already live in .gitmodules and are read from there (Get-DeclaredSubmodules
# below), so there is exactly one place that can go stale.

# $PSScriptRoot is this file's own folder (Tools\); the project root is one
# level up, regardless of who imports this module or from where.
$Root = Split-Path $PSScriptRoot -Parent

$ConfigPath = Join-Path $Root 'project.config.json'
if (-not (Test-Path $ConfigPath)) {
    throw [ProjectWorkflow.WorkflowFailException]::new(
        "project.config.json is missing at $ConfigPath. The project folder is incomplete, or this module was copied without it.")
}
$Config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json

$ProjectTitle       = $Config.ProjectTitle
$SuperRepoUrl       = $Config.SuperRepoUrl
$SuperRepoWeb       = $Config.SuperRepoWeb
$SuperBranch        = $Config.SuperBranch
$RequiredSubmodules = @($Config.RequiredSubmodules)
$LfsRepos           = @($Config.LfsRepos)
$EditorProcessNames = @($Config.EditorProcessNames)
$Upstreams          = @($Config.Upstreams)

# Each entry: { Title, Path (relative to $Root), Type ("markdown" or "pdf") }.
# Lets a project list however many docs it wants in Forge's Help tab without
# any hardcoded file names here. Optional -- an older config without it just
# means no project docs show up.
$Docs = @($Config.Docs | Where-Object { $_ })

$WorkflowLocalSettingsPath = Join-Path $Root 'Tools\.workflow-local.json'

# A local EnginePath override (machine-specific, gitignored, never shared or
# committed) lets a project that doesn't bundle its own engine submodule
# point at one installed elsewhere instead (Epic Games Launcher, a custom
# build). Read before computing $EngineDir below so the override actually
# takes effect; a missing or corrupt local settings file just means no
# override, not a failure -- Get-WorkflowLocalSettings below already has its
# own, more visible warning for a genuinely corrupt file.
$LocalEnginePathOverride = $null
if (Test-Path $WorkflowLocalSettingsPath) {
    try {
        $localSettingsForEngine = Get-Content -LiteralPath $WorkflowLocalSettingsPath -Raw | ConvertFrom-Json
        if ($localSettingsForEngine.EnginePath) { $LocalEnginePathOverride = $localSettingsForEngine.EnginePath }
    } catch { }
}

$EngineDir    = if ($LocalEnginePathOverride) { $LocalEnginePathOverride } else { Join-Path $Root $Config.EngineSubmodulePath }
$GameDir      = Join-Path $Root $Config.GameSubmodulePath
$UProject     = Join-Path $GameDir $Config.UProjectRelativePath
$ProjectDir   = Split-Path $UProject -Parent
$EditorTarget = $Config.EditorTarget

$BuildBat  = Join-Path $EngineDir 'Engine\Build\BatchFiles\Build.bat'
$EditorExe = Join-Path $EngineDir 'Engine\Binaries\Win64\UnrealEditor.exe'
$SCWExe    = Join-Path $EngineDir 'Engine\Binaries\Win64\ShaderCompileWorker.exe'

# Every bare `git ...` call below (no -C) relies on the process's current
# directory being $Root -- true by default for the CLI scripts (launched
# from inside the project folder), but not guaranteed for a host that embeds
# PowerShell, like Forge. Fixing it once here, scoped to this session's
# PowerShell path (SessionState), is cheaper and safer than adding -C
# everywhere, and does not touch the hosting process's own working directory.
Set-Location $Root

# ---------------------------------------------------------------------------
# Output

$script:Step = 0
function Write-Step($msg) {
    $script:Step++
    Write-Host ""
    Write-Host ("[{0}] {1}" -f $script:Step, $msg) -ForegroundColor Cyan
}
function Write-Ok($msg)   { Write-Host "    OK   $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "    WARN $msg" -ForegroundColor Yellow }
function Write-Dim($msg)  { Write-Host "         $msg" -ForegroundColor DarkGray }

function Stop-Log { try { Stop-Transcript | Out-Null } catch { } }

# A CLI wrapper calls Set-WorkflowLogPath right after Import-Module so Fail
# can name the log file. Optional: a caller that never sets it (an ad hoc
# PowerShell prompt, or Forge, which shows its own log pane) just gets the
# message without that line.
$script:LogPath = $null
function Set-WorkflowLogPath([string]$Path) { $script:LogPath = $Path }

# Prints the error exactly as every caller has always seen it, then raises it
# as a catchable, typed failure instead of exiting the process -- exiting
# here would take down whatever hosts this module, not just the operation
# that failed.
function Fail($msg) {
    Write-Host ""
    Write-Host "ERROR: $msg" -ForegroundColor Red
    Write-Host ""
    if ($script:LogPath) { Write-Host "Full log: $script:LogPath" -ForegroundColor DarkGray }
    Stop-Log
    throw [ProjectWorkflow.WorkflowFailException]::new($msg)
}

# ---------------------------------------------------------------------------
# Submodule inventory

# Reads one directory's own .gitmodules (not necessarily $Root's -- a nested
# submodule declares its own children in its own .gitmodules). Returns an
# empty list for a directory with none, rather than failing.
function Get-DeclaredSubmodulesAt([string]$dir) {
    $gm = Join-Path $dir '.gitmodules'
    $result = @()
    if (-not (Test-Path $gm)) { return $result }
    $lines = git config -f $gm --get-regexp '^submodule\..*\.path$' 2>$null
    foreach ($line in $lines) {
        if ($line -match '^submodule\.(.+)\.path\s+(.+)$') {
            $name = $Matches[1]
            $result += [pscustomobject]@{
                Name   = $name
                Path   = $Matches[2]
                Branch = (git config -f $gm --get "submodule.$name.branch" 2>$null)
            }
        }
    }
    return $result
}

function Get-DeclaredSubmodules {
    return Get-DeclaredSubmodulesAt $Root
}

function Test-SubmoduleCheckedOut($relPath) {
    $dir = Join-Path $Root $relPath
    if (-not (Test-Path (Join-Path $dir '.git'))) { return $false }
    $null = git -C $dir rev-parse --resolve-git-dir .git 2>$null
    return ($LASTEXITCODE -eq 0)
}

# Every checked-out submodule at any depth, with the commit its parent pins
# and the commit actually on disk.
#
# `git submodule status --recursive` is not enough on its own: for a
# submodule that has moved it prints the commit on DISK, not the pin, and it
# cannot say whether the move was forwards or backwards. That distinction is
# the whole point -- "ahead of the pin" is a developer mid-work, "behind the
# pin" is a stale copy that will fail to build.
#
# State is one of:
#   Match     on the pinned commit
#   Ahead     pinned commit is an ancestor of what is on disk
#   Behind    what is on disk is an ancestor of the pin, or the pin has not
#             even been downloaded yet
#   Diverged  neither contains the other
#   Missing   declared, but never checked out
function Get-SubmoduleStates([string]$repoRel = '') {
    $repoDir = if ($repoRel) { Join-Path $Root $repoRel } else { $Root }
    $states = @()
    $declared = Get-DeclaredSubmodulesAt $repoDir

    foreach ($line in @(git -C $repoDir submodule status 2>$null)) {
        if ($line -notmatch '^\s*([-+U ]?)([0-9a-f]{40})\s+(\S+)') { continue }
        $flag = $Matches[1]; $rel = $Matches[3]
        $path = if ($repoRel) { "$repoRel/$rel" } else { $rel }

        # The branch this path's PARENT declares for it (e.g. 'development'),
        # distinct from CurrentBranch below (what is actually checked out
        # right now -- the two can differ, or CurrentBranch can be $null when
        # detached).
        $declaredBranch = ($declared | Where-Object { $_.Path -eq $rel } | Select-Object -First 1).Branch

        # The pin as git itself sees it: the gitlink in the parent's index.
        $pin = $null
        $entry = git -C $repoDir ls-files -s -- $rel 2>$null
        if ($entry -match '^160000\s+([0-9a-f]{40})\s') { $pin = $Matches[1] }

        if ($flag -eq '-' -or -not (Test-SubmoduleCheckedOut $path)) {
            $states += [pscustomobject]@{
                Path = $path; Pin = $pin; Head = $null; State = 'Missing'
                CurrentBranch = $null; DeclaredBranch = $declaredBranch
            }
            continue
        }

        $dir  = Join-Path $Root $path
        $head = git -C $dir rev-parse HEAD 2>$null
        $currentBranch = git -C $dir symbolic-ref --quiet --short HEAD 2>$null
        if ($LASTEXITCODE -ne 0 -or -not $currentBranch) { $currentBranch = $null }

        if ($head -eq $pin) {
            $state = 'Match'
        } else {
            $null = git -C $dir cat-file -e "$pin^{commit}" 2>$null
            if ($LASTEXITCODE -ne 0) {
                $state = 'Behind'
            } else {
                $null = git -C $dir merge-base --is-ancestor $pin $head 2>$null
                if ($LASTEXITCODE -eq 0) { $state = 'Ahead' }
                else {
                    $null = git -C $dir merge-base --is-ancestor $head $pin 2>$null
                    $state = if ($LASTEXITCODE -eq 0) { 'Behind' } else { 'Diverged' }
                }
            }
        }
        $states += [pscustomobject]@{
            Path = $path; Pin = $pin; Head = $head; State = $state
            CurrentBranch = $currentBranch; DeclaredBranch = $declaredBranch
        }
        $states += Get-SubmoduleStates $path
    }
    return $states
}

# Remote branch names available for a submodule to switch to (no local fetch
# of objects -- just the ref names), for Forge's branch picker. Returns an
# empty list rather than failing when the submodule is not checked out yet
# or origin cannot be reached, since the caller is always a UI popup rather
# than an operation that should abort.
function Get-SubmoduleBranches {
    param([Parameter(Mandatory)] [string]$Path)
    if (-not (Test-SubmoduleCheckedOut $Path)) { return @() }
    $dir = Join-Path $Root $Path
    $branches = @()
    foreach ($line in @(git -C $dir ls-remote --heads origin 2>$null)) {
        if ($line -match 'refs/heads/(.+)$') { $branches += $Matches[1] }
    }
    return @($branches | Sort-Object -Unique)
}

# The project's configurable doc list for Forge's Help tab (see $Docs above).
function Get-ProjectDocs {
    return @($Docs)
}

# ---------------------------------------------------------------------------
# Unsaved work

# One entry per repository (the superproject plus every checked-out
# submodule) describing what an update could destroy or trip over:
#
#   Changed    modified, added or deleted tracked files. These BLOCK an
#              update -- git aborts the checkout halfway and leaves the
#              submodule half-updated.
#   Unpushed   commits that exist only on this machine. An update moves the
#              submodule to the pinned commit and those commits fall off the
#              branch. Also blocks.
#   Untracked  new files git has never been told about. Harmless unless a
#              pulled commit adds a file at the same path, so they are
#              reported but do not block.
#
# The engine is skipped for untracked files: scanning its ~200k-file tree for
# them takes long enough to look like a hang, and nobody creates engine files
# by hand.
function Get-LocalChanges {
    $repos = @([pscustomobject]@{ Path = ''; Pin = $null })
    foreach ($s in Get-SubmoduleStates) {
        if ($s.State -ne 'Missing') { $repos += $s }
    }

    $report = @()
    foreach ($repo in $repos) {
        $rel = $repo.Path
        $dir = if ($rel) { Join-Path $Root $rel } else { $Root }
        $untrackedMode = if ($rel -eq 'UnrealEngine') { '-uno' } else { '-unormal' }

        # --ignore-submodules=all: a submodule that moved or has edits shows
        # up in its PARENT as a one-line change too. It is reported once, by
        # the submodule itself, where the actual files are.
        $lines = @(git -C $dir status --porcelain=v1 --ignore-submodules=all $untrackedMode 2>$null)
        $changed   = @($lines | Where-Object { $_ -and $_ -notlike '`?`? *' } | ForEach-Object { $_.Substring(3) })
        $untracked = @($lines | Where-Object { $_ -like '`?`? *' }                | ForEach-Object { $_.Substring(3) })

        # Commits reachable from HEAD that no remote branch has. Covers every
        # case in one question: on a branch with or without an upstream, or
        # detached. Pushing updates the local origin/* refs, so this is
        # accurate without a network round trip.
        #
        # The pinned commit is excluded too. A pin that no longer sits on any
        # branch is the superproject's problem (Setup's fetchability check
        # reports it), not work this person could lose.
        $unpushed = 0
        $exclude = @('--remotes')
        if ($repo.Pin) { $exclude += $repo.Pin }
        $count = git -C $dir rev-list --count HEAD --not @exclude 2>$null
        if ($LASTEXITCODE -eq 0 -and $count -match '^\d+$') { $unpushed = [int]$count }

        # The superproject cannot "lose" commits the same way, and a developer
        # committing script changes before pushing is normal; Update-Superproject
        # deals with a diverged superproject on its own terms.
        if (-not $rel) { $unpushed = 0 }

        if ($changed.Count -or $untracked.Count -or $unpushed) {
            $report += [pscustomobject]@{
                Path      = $(if ($rel) { $rel } else { '(outer project folder)' })
                Changed   = $changed
                Untracked = $untracked
                Unpushed  = $unpushed
            }
        }
    }
    return $report
}

function Get-ShelveMessage($report, [string]$action) {
    $blocking = @($report | Where-Object { $_.Changed.Count -or $_.Unpushed })
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("You have work in progress that $action would overwrite or orphan.")
    [void]$sb.AppendLine("Nothing has been changed yet.")
    [void]$sb.AppendLine("")

    foreach ($r in $blocking) {
        if ($r.Changed.Count) {
            [void]$sb.AppendLine("  $($r.Path): $($r.Changed.Count) changed file(s)")
            foreach ($f in ($r.Changed | Select-Object -First 10)) { [void]$sb.AppendLine("      $f") }
            if ($r.Changed.Count -gt 10) { [void]$sb.AppendLine("      ...and $($r.Changed.Count - 10) more") }
        }
        if ($r.Unpushed) {
            [void]$sb.AppendLine("  $($r.Path): $($r.Unpushed) commit(s) that are not on GitHub yet")
        }
    }

    [void]$sb.AppendLine("")
    if (@($blocking | Where-Object { $_.Changed.Count }).Count) {
        [void]$sb.AppendLine("Changed files -- SHELVE them in Rider:")
        [void]$sb.AppendLine("  1. Close the Unreal editor.")
        [void]$sb.AppendLine("  2. In Rider: Git > Uncommitted Changes > Shelve Changes...")
        [void]$sb.AppendLine("     Tick every changelist listed, give the shelf a name, click Shelve.")
        [void]$sb.AppendLine("  3. Run Sync.bat again.")
        [void]$sb.AppendLine("  4. Back in Rider: open the Commit window (Alt+0), Shelf tab,")
        [void]$sb.AppendLine("     right-click your shelf > Unshelve.")
        [void]$sb.AppendLine("")
        [void]$sb.AppendLine("If you do NOT want those changes, Rollback them in Rider instead.")
    }
    if (@($blocking | Where-Object { $_.Unpushed }).Count) {
        if (@($blocking | Where-Object { $_.Changed.Count }).Count) { [void]$sb.AppendLine("") }
        [void]$sb.AppendLine("Commits not on GitHub -- PUSH them in Rider first:")
        [void]$sb.AppendLine("  Git > Push... (Ctrl+Shift+K), then run Sync.bat again.")
    }
    return $sb.ToString().TrimEnd()
}

# Fails with Rider instructions if anything would block an update. Untracked
# files are listed but let through.
function Assert-NoBlockingChanges([string]$action) {
    $report = @(Get-LocalChanges)
    $blocking = @($report | Where-Object { $_.Changed.Count -or $_.Unpushed })
    if ($blocking.Count) { Fail (Get-ShelveMessage $report $action) }

    $withUntracked = @($report | Where-Object { $_.Untracked.Count })
    if ($withUntracked.Count) {
        foreach ($r in $withUntracked) {
            Write-Warn "$($r.Path): $($r.Untracked.Count) new file(s) not added to version control yet"
        }
        Write-Dim "These are left alone. If you meant to keep them, add them to VCS in"
        Write-Dim "Rider and commit them -- until then nobody else can see them."
    } else {
        Write-Ok "no unsaved work in the way"
    }
}

function Assert-EditorClosed {
    $running = @(Get-Process -Name $EditorProcessNames -ErrorAction SilentlyContinue)
    if ($running.Count) {
        Fail @"
The Unreal editor is running ($($running[0].ProcessName).exe).

It holds project files open, and updating underneath it either fails partway
or leaves the editor showing assets that no longer match what is on disk.
Save your work, close the editor, and run this again.
"@
    }
    Write-Ok "Unreal editor is not running"
}

# ---------------------------------------------------------------------------
# Smart App Control

# Windows 11 Smart App Control refuses to load unsigned code it has no cloud
# reputation for -- which describes every DLL a local Unreal build produces.
# The editor compiles fine and then dies at startup with "Bad Image ... Error
# status 0xc0e90002" naming one of its own DLLs. It cannot be given
# exceptions; the only fix is turning it off. It is on by default on clean
# Windows 11 installs, so this bites a new contributor's first launch.
#
# Returns 'On', 'Evaluation', 'Off' or $null (older Windows / not readable).
# Evaluation mode only observes and does not block, but Windows may switch it
# to On by itself later.
function Get-SmartAppControlState {
    try {
        $key = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Control\CI\Policy')
        if (-not $key) { return $null }
        try { $v = $key.GetValue('VerifiedAndReputablePolicyState') } finally { $key.Close() }
        switch ($v) { 0 { 'Off' } 1 { 'On' } 2 { 'Evaluation' } default { $null } }
    } catch {
        return $null
    }
}

$SmartAppControlFix = @"
Turn it off (this has to be done by you, on this machine):

  Windows Security > App & browser control > Smart App Control settings > Off

Smart App Control cannot make exceptions for individual files, and Microsoft
does not intend it for machines that compile code. Microsoft Defender
antivirus keeps protecting the machine as normal. Note that on some Windows
versions it cannot be switched back on without resetting Windows.
"@

# ---------------------------------------------------------------------------
# Engine registration

# The .uproject names its engine in EngineAssociation. A source-built engine
# is identified by a GUID, resolved through this per-user registry key.
# Unreal ITSELF edits the key every time the editor or the version selector
# enumerates engines (DesktopPlatformWindows.cpp, EnumerateEngineInstallations):
# it deletes any entry whose folder is not a valid engine, and any entry
# pointing at a folder an EARLIER entry already claims. Separately, when it
# finds an engine folder with no entry at all, it registers it under a
# brand-new random GUID.
#
# Together those produce a nasty failure: at some point the editor runs
# unregistered and mints its own GUID for this engine folder. Every later
# registration of the project's GUID adds a second entry for the same folder,
# which Unreal deletes as a duplicate the next time the editor starts -- so
# "run Setup, then Launch" works exactly once.
#
# The only stable state is ONE entry for this folder, and it has to be the
# project's. So any other GUID pointing here is removed; Unreal would delete
# one of the two anyway, and this picks the right one.
#
# The GUID itself is not defined here: it is read from the .uproject, so the
# game repo stays the single source of truth for which engine it wants.
$EngineBuildsSubKey = 'Software\Epic Games\Unreal Engine\Builds'

function ConvertTo-EnginePath([string]$path) {
    return ($path -replace '\\', '/').TrimEnd('/')
}

function Test-EngineRoot([string]$path) {
    # Unreal's own validity test (IsValidRootDirectory): an entry failing it is
    # deleted on sight.
    #
    # .NET rather than Join-Path/Test-Path: registry entries can name a drive
    # that no longer exists (an unplugged disk, an old machine's layout), and
    # Join-Path throws on an unknown drive instead of returning false.
    try {
        return [System.IO.Directory]::Exists("$path/Engine/Binaries") -and
               [System.IO.Directory]::Exists("$path/Engine/Build")
    } catch {
        return $false
    }
}

function Get-EngineAssociation {
    try { return (Get-Content $UProject -Raw | ConvertFrom-Json).EngineAssociation } catch { return $null }
}

function Test-EngineGuid([string]$assoc) {
    return ($assoc -match '^\{[0-9A-Fa-f-]{36}\}$')
}

# Returns Status = Ok | Registered | Conflict, plus the GUIDs removed and, for
# a Conflict, the other engine the GUID currently points at. A Conflict means
# the GUID is mapped to a DIFFERENT, still-valid engine folder -- another
# checkout on this machine -- and is left alone unless -Force is given.
function Register-Engine([string]$assoc, [string]$engineDir, [switch]$Force,
                         [string]$subKey = $EngineBuildsSubKey) {
    $target = ConvertTo-EnginePath (Resolve-Path -LiteralPath $engineDir).Path
    $result = [pscustomobject]@{ Status = 'Ok'; Removed = @(); Existing = $null; Target = $target }

    $reg = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($subKey)
    try {
        $current = $reg.GetValue($assoc)
        if ($current) {
            $result.Existing = ConvertTo-EnginePath ([string]$current)
            if ($result.Existing -ne $target -and -not $Force -and (Test-EngineRoot $result.Existing)) {
                $result.Status = 'Conflict'
                return $result
            }
        }

        foreach ($name in $reg.GetValueNames()) {
            if ($name -eq $assoc) { continue }
            if ((ConvertTo-EnginePath ([string]$reg.GetValue($name))) -eq $target) {
                $reg.DeleteValue($name, $false)
                $result.Removed += $name
            }
        }

        if ($result.Existing -ne $target) {
            $reg.SetValue($assoc, $target, [Microsoft.Win32.RegistryValueKind]::String)
            $result.Status = 'Registered'
        }
    } finally {
        $reg.Close()
    }
    return $result
}

# ---------------------------------------------------------------------------
# Updating

# Puts the superproject on $SuperBranch at the latest commit from GitHub.
#
# Submodule recursion is switched off for these commands on purpose: the
# submodules are updated afterwards in one explicit, diagnosable step, rather
# than as a side effect buried inside a checkout or merge.
function Update-Superproject {
    git fetch --quiet --no-recurse-submodules origin $SuperBranch
    if ($LASTEXITCODE -ne 0) {
        Fail @"
Could not download updates from GitHub (git fetch failed).

Check your internet connection, and that you can open this page while signed
in to GitHub:

  $SuperRepoWeb

If that page opens but this still fails, the '$SuperBranch' branch may not
exist on GitHub yet; tell whoever maintains the project setup.
"@
    }

    $current = git symbolic-ref --quiet --short HEAD 2>$null
    if ($current -ne $SuperBranch) {
        $was = if ($current) { "'$current'" } else { 'a detached commit' }
        Write-Warn "outer project is on $was, not '$SuperBranch' -- switching"
        $null = git show-ref --verify --quiet "refs/heads/$SuperBranch"
        if ($LASTEXITCODE -eq 0) {
            git -c submodule.recurse=false checkout --quiet $SuperBranch
        } else {
            git -c submodule.recurse=false checkout --quiet -b $SuperBranch --track "origin/$SuperBranch"
        }
        if ($LASTEXITCODE -ne 0) { Fail "Could not switch the outer project to '$SuperBranch'. The git error is above." }
    }

    # A branch created by hand, or before the default changed, can lack an
    # upstream; `git pull` then refuses with "no tracking information".
    git branch --quiet --set-upstream-to="origin/$SuperBranch" $SuperBranch 2>$null | Out-Null

    $counts = git rev-list --left-right --count "HEAD...origin/$SuperBranch" 2>$null
    $ahead = 0; $behind = 0
    if ($counts -match '^(\d+)\s+(\d+)$') { $ahead = [int]$Matches[1]; $behind = [int]$Matches[2] }

    if ($ahead -and $behind) {
        Fail @"
Your copy of the outer project has $ahead commit(s) that are not on GitHub, and
GitHub has $behind that you do not. Sync will not guess how to combine them.

If you did not mean to commit anything in the outer project folder, send this
log to whoever maintains the project setup.
"@
    }
    if ($behind) {
        git -c submodule.recurse=false merge --quiet --ff-only "origin/$SuperBranch"
        if ($LASTEXITCODE -ne 0) { Fail "Could not fast-forward the outer project. The git error is above." }
        Write-Ok "outer project: pulled $behind new commit(s) on '$SuperBranch'"
    } else {
        Write-Ok "outer project: already up to date on '$SuperBranch'"
    }
    if ($ahead) { Write-Warn "outer project: $ahead local commit(s) not pushed yet" }
}

# `git submodule update --init --recursive` for the given top-level paths.
# Returns $true on success. On failure the caller should Fail with
# Get-UpdateFailureMessage, which works out WHY rather than guessing.
function Invoke-SubmoduleUpdate([string[]]$paths, [string[]]$extraArgs = @()) {
    git submodule sync --recursive --quiet -- @paths 2>$null | Out-Null
    git submodule update --init --recursive @extraArgs -- @paths
    return ($LASTEXITCODE -eq 0)
}

function Get-SubmoduleUrls([string[]]$paths) {
    $urls = @()
    foreach ($p in $paths) {
        $u = git config -f (Join-Path $Root '.gitmodules') --get "submodule.$p.url" 2>$null
        if ($u) { $urls += [pscustomobject]@{ Path = $p; Url = $u } }
        $nested = Join-Path (Join-Path $Root $p) '.gitmodules'
        if (Test-Path $nested) {
            foreach ($line in @(git config -f $nested --get-regexp '^submodule\..*\.url$' 2>$null)) {
                if ($line -match '^submodule\.(.+)\.url\s+(.+)$') {
                    $urls += [pscustomobject]@{ Path = "$p/$($Matches[1])"; Url = $Matches[2] }
                }
            }
        }
    }
    return $urls
}

# Explains a failed `git submodule update`. Checked in order of likelihood,
# and each check is a fact rather than a guess -- blaming GitHub permissions
# for every failure sends people hunting for an access problem that does not
# exist.
function Get-UpdateFailureMessage([string[]]$paths) {
    $report = @(Get-LocalChanges)

    if (@($report | Where-Object { $_.Changed.Count -or $_.Unpushed }).Count) {
        return (Get-ShelveMessage $report 'updating')
    }

    $withUntracked = @($report | Where-Object { $_.Untracked.Count })
    if ($withUntracked.Count) {
        $list = ($withUntracked | ForEach-Object { "  $($_.Path): $($_.Untracked.Count) file(s)" }) -join "`n"
        return @"
The update stopped because new files on your machine are in the way: files
git is not tracking sit at the same paths as files the update brings in.

$list

The exact file names are listed above, just before "Aborting". In Rider, add
the ones you want to keep to version control and shelve them, or move them out
of the project folder. Then run this again.
"@
    }

    $denied = @()
    $env:GIT_TERMINAL_PROMPT = '0'
    foreach ($u in Get-SubmoduleUrls $paths) {
        $null = git ls-remote --quiet $u.Url HEAD 2>$null
        if ($LASTEXITCODE -ne 0) { $denied += $u }
    }
    Remove-Item Env:\GIT_TERMINAL_PROMPT -ErrorAction SilentlyContinue

    if ($denied.Count) {
        $list = ($denied | ForEach-Object { "  $($_.Path)`n      $($_.Url)" }) -join "`n"
        return @"
Your GitHub account cannot read these repositories:

$list

Ask the project owner to give your GitHub account read access to each one in
the InMidnightGames organization.
"@
    }

    return @"
The submodule update failed, and it was not unsaved work or GitHub access.
The git error is printed above -- the last few lines are usually the cause.

If it mentions a missing commit, whoever last updated the project may not have
pushed everything; tell them. Otherwise send this log to whoever maintains the
project setup.
"@
}

# `git submodule update` leaves every submodule on a bare commit (detached
# HEAD), and commits made there belong to no branch. Where the pin IS the tip
# of the branch declared in .gitmodules, re-attach that branch so the checkout
# is immediately workable in Rider.
#
# Where the pin is NOT the branch tip, stay at the pin. The superproject is the
# authority on which commit belongs to this build; silently fast-forwarding to
# a branch tip would defeat the point of pinning.
#
# Only top-level submodules are handled. Nested ones (the Game's plugins) are
# pinned by Game itself and are left exactly where Game puts them.
function Sync-SubmoduleBranches {
    foreach ($s in Get-DeclaredSubmodules) {
        $dir = Join-Path $Root $s.Path
        if (-not (Test-SubmoduleCheckedOut $s.Path)) { continue }
        if (-not $s.Branch) {
            Write-Warn "$($s.Path): no branch declared in .gitmodules -- staying at the pinned commit."
            Write-Dim  "Add one with: git config -f .gitmodules submodule.$($s.Name).branch <branch>"
            continue
        }

        git -C $dir fetch --quiet origin $s.Branch 2>$null

        $entry = git ls-tree HEAD $s.Path 2>$null
        if (-not $entry) { Write-Warn "$($s.Path): no pin recorded in HEAD; skipping"; continue }
        $pinned = ($entry -split '\s+')[2]

        $tip = git -C $dir rev-parse --verify --quiet "origin/$($s.Branch)" 2>$null
        if (-not $tip) {
            Write-Warn "$($s.Path): origin has no branch '$($s.Branch)'. Check the branch name in .gitmodules."
            continue
        }

        if ($pinned -eq $tip) {
            git -C $dir checkout -B $s.Branch --track "origin/$($s.Branch)" --quiet 2>$null
            Write-Ok "$($s.Path): on '$($s.Branch)' @ $($pinned.Substring(0,8))"
        } else {
            git -C $dir checkout --quiet --detach $pinned 2>$null
            Write-Warn "$($s.Path): pin $($pinned.Substring(0,8)) is behind origin/$($s.Branch) ($($tip.Substring(0,8)))"
            Write-Dim  "Staying at the pinned commit. To adopt the branch tip:"
            Write-Dim  "  git -C $($s.Path) checkout $($s.Branch) && git add $($s.Path)"
        }
    }
}

# ---------------------------------------------------------------------------
# Branch switching (Forge) -- not used by Setup/Sync/Launch

# Switches one submodule to a branch and cascades: whatever that branch's
# tree pins its own nested submodules at is what they are updated to. Mirrors
# how Sync-SubmoduleBranches treats top-level pins -- the parent is always
# the authority on which commit its children should be at.
function Set-SubmoduleBranch {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Branch
    )
    $dir = Join-Path $Root $Path
    if (-not (Test-SubmoduleCheckedOut $Path)) {
        Fail "$Path is not checked out. Run Setup or Sync first."
    }

    # Guard the submodule itself and everything nested inside it -- a branch
    # switch is a checkout, and a checkout aborts halfway if anything
    # underneath is dirty or unpushed.
    $affected = @($Path) + @(Get-SubmoduleStates $Path | ForEach-Object { $_.Path })
    $report = @(Get-LocalChanges | Where-Object { $affected -contains $_.Path })
    $blocking = @($report | Where-Object { $_.Changed.Count -or $_.Unpushed })
    if ($blocking.Count) { Fail (Get-ShelveMessage $report "switching $Path to '$Branch'") }

    $withUntracked = @($report | Where-Object { $_.Untracked.Count })
    foreach ($r in $withUntracked) {
        Write-Warn "$($r.Path): $($r.Untracked.Count) new file(s) not added to version control yet"
    }

    git -C $dir fetch --quiet origin $Branch
    if ($LASTEXITCODE -ne 0) {
        Fail "Could not fetch '$Branch' for $Path from origin. Check the branch name and your connection."
    }
    $null = git -C $dir show-ref --verify --quiet "refs/remotes/origin/$Branch"
    if ($LASTEXITCODE -ne 0) { Fail "origin has no branch '$Branch' for $Path." }

    git -C $dir checkout -B $Branch --track "origin/$Branch" --quiet
    if ($LASTEXITCODE -ne 0) { Fail "Could not switch $Path to '$Branch'. The git error is above." }
    Write-Ok "$Path -> '$Branch'"

    if (-not (Invoke-SubmoduleUpdate @($Path))) {
        Fail (Get-UpdateFailureMessage @($Path))
    }
    Write-Ok "${Path}: nested submodules synced to match '$Branch'"
}

# Per-submodule "is there anything new on the branch I'm watching" check,
# independent of the branch .gitmodules pins. Informational only -- never
# changes anything on disk. Returns an object with an Error field instead of
# calling Fail, since this is meant to be polled repeatedly (including while
# offline) without interrupting whatever else the caller is doing.
function Get-BranchWatchStatus {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$WatchBranch
    )
    if (-not (Test-SubmoduleCheckedOut $Path)) {
        return [pscustomobject]@{ Path = $Path; WatchBranch = $WatchBranch; Ahead = 0; Error = 'not checked out' }
    }
    $dir = Join-Path $Root $Path

    git -C $dir fetch --quiet origin $WatchBranch 2>$null
    if ($LASTEXITCODE -ne 0) {
        return [pscustomobject]@{ Path = $Path; WatchBranch = $WatchBranch; Ahead = 0; Error = "could not reach origin" }
    }

    $null = git -C $dir show-ref --verify --quiet "refs/remotes/origin/$WatchBranch"
    if ($LASTEXITCODE -ne 0) {
        return [pscustomobject]@{ Path = $Path; WatchBranch = $WatchBranch; Ahead = 0; Error = "origin has no branch '$WatchBranch'" }
    }

    $count = git -C $dir rev-list --count "HEAD..origin/$WatchBranch" 2>$null
    if ($LASTEXITCODE -ne 0 -or $count -notmatch '^\d+$') {
        return [pscustomobject]@{ Path = $Path; WatchBranch = $WatchBranch; Ahead = 0; Error = 'could not compare commits' }
    }

    return [pscustomobject]@{ Path = $Path; WatchBranch = $WatchBranch; Ahead = [int]$count; Error = $null }
}

# ---------------------------------------------------------------------------
# Per-checkout local settings (Forge) -- gitignored, never shared

# Schema: { "WatchBranches": { "<submodule path>": "<branch>" } }
function Get-WorkflowLocalSettings {
    if (-not (Test-Path $WorkflowLocalSettingsPath)) {
        return [pscustomobject]@{ WatchBranches = [pscustomobject]@{} }
    }
    try {
        return Get-Content -LiteralPath $WorkflowLocalSettingsPath -Raw | ConvertFrom-Json
    } catch {
        Write-Warn "Tools\.workflow-local.json could not be read; ignoring it. ($($_.Exception.Message))"
        return [pscustomobject]@{ WatchBranches = [pscustomobject]@{} }
    }
}

function Set-WorkflowLocalSettings {
    param([Parameter(Mandatory)] $Settings)
    $Settings | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $WorkflowLocalSettingsPath -Encoding utf8
}

# ---------------------------------------------------------------------------
# Project info (Forge) -- a stable, read-only summary instead of exporting
# every internal variable

function Get-ProjectInfo {
    [pscustomobject]@{
        Title              = $ProjectTitle
        Root               = $Root
        SuperBranch        = $SuperBranch
        SuperRepoUrl       = $SuperRepoUrl
        SuperRepoWeb       = $SuperRepoWeb
        RequiredSubmodules = $RequiredSubmodules
        EngineDir          = $EngineDir
        GameDir            = $GameDir
        ProjectDir         = $ProjectDir
        UProject           = $UProject
        EditorTarget       = $EditorTarget
        EditorExe          = $EditorExe
        LfsRepos           = $LfsRepos
        IsEngineExternal   = [bool]$LocalEnginePathOverride
    }
}

# ---------------------------------------------------------------------------
# Setup -- the body of Setup.ps1

function Invoke-ProjectSetup {
    param(
        [switch]$SkipEngineDeps,
        [switch]$SkipRegister,
        [switch]$SkipGenerate,
        [switch]$Force,
        [switch]$PreflightOnly
    )

    function Invoke-Native($exe, $argList, $workDir) {
        Push-Location $workDir
        try {
            & $exe @argList
            if ($LASTEXITCODE -ne 0) { Fail "$exe exited with code $LASTEXITCODE" }
        } finally {
            Pop-Location
        }
    }

    Write-Host ""
    Write-Host "=== $ProjectTitle :: superproject setup ===" -ForegroundColor White
    Write-Host "Root: $Root"

    # -----------------------------------------------------------------------
    Write-Step "Checking prerequisites"

    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        Fail @"
git is not installed, or is not on your PATH.

  Install Git for Windows: https://git-scm.com/download/win
  Tick "Git LFS" during installation.

After installing, close this window, open a NEW one, and run Setup.bat again.
A newly installed program is not visible to windows that were already open.
"@
    }

    $gitOut     = & git --version 2>$null
    $rawVersion = @($gitOut)[0]
    if ($rawVersion -match '(\d+)\.(\d+)\.(\d+)') {
        $gitMajorMinor = [version]("{0}.{1}" -f $Matches[1], $Matches[2])
        Write-Ok "git $($Matches[0])"
    } else {
        $gitMajorMinor = [version]'0.0'
        Write-Warn "Could not parse a version from 'git --version' (got: '$rawVersion')"
    }

    $CanPartialFetch = $gitMajorMinor -ge [version]'2.36'
    if (-not $CanPartialFetch) {
        Write-Warn "git $gitMajorMinor predates 2.36; engine will be fetched as a full clone (much larger)."
    }

    $lfsOut     = & git lfs version 2>$null
    $lfsExit    = $LASTEXITCODE
    $lfsVersion = @($lfsOut)[0]
    if ($lfsExit -ne 0 -or -not $lfsVersion) {
        Fail @"
Git LFS is not installed.

Almost every art asset in this project is stored in LFS. Without it you will
check out small text placeholder files instead of real assets, and the editor
will fail to load content.

  Install: https://git-lfs.com
  Then run: git lfs install

After installing, close this window, open a NEW one, and run Setup.bat again.
"@
    }
    Write-Ok (($lfsVersion -split ' ')[0])

    if (-not (git config --global --get filter.lfs.smudge)) {
        Write-Warn "Git LFS filters not registered; running 'git lfs install'"
        & git lfs install | Out-Null
        if (-not (git config --global --get filter.lfs.smudge)) {
            Fail @"
'git lfs install' ran but the LFS filters are still not configured.

Without them, every art asset checks out as a small text placeholder and the
editor will fail to load content -- with no error from git along the way.

Run this yourself and note any error it prints:

  git lfs install
"@
        }
        Write-Ok "Ran 'git lfs install'"
    }
    Write-Ok "Git LFS filters registered"

    $null = git rev-parse --is-inside-work-tree 2>$null
    if ($LASTEXITCODE -ne 0) {
        Fail @"
This folder is not a git repository.

That usually means the project was downloaded as a ZIP from GitHub. ZIP
downloads do not include submodules, so the engine, game and art folders would
all be empty. Clone it instead:

  git clone $SuperRepoUrl

Then run Setup.bat from inside the folder that creates.
"@
    }
    Write-Ok "git repository detected"

    $pf86 = ${env:ProgramFiles(x86)}
    if ($pf86) {
        $vswhere = Join-Path $pf86 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path $vswhere) {
            $vs = & $vswhere -latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property displayName 2>$null
            if ($vs) { Write-Ok "Visual Studio: $vs" }
            else     { Write-Warn "No VS install with the C++ desktop workload found. The engine will not compile without it." }
        } else {
            Write-Warn "Could not detect Visual Studio. The engine needs VS 2022 with 'Desktop development with C++'."
        }
    } else {
        Write-Warn "ProgramFiles(x86) is not set; skipping the Visual Studio check."
    }

    switch (Get-SmartAppControlState) {
        'On' {
            Write-Warn "Windows Smart App Control is ON. The editor will not start until it is off."
            foreach ($line in ($SmartAppControlFix -split "`r?`n")) { Write-Dim $line }
        }
        'Evaluation' { Write-Warn "Smart App Control is in evaluation mode; if Windows turns it On, the editor will be blocked. Turning it Off now is safest." }
        default      { Write-Ok "Smart App Control is off or not present" }
    }

    if ($PreflightOnly) {
        Write-Host ""
        Write-Host "Preflight checks passed. Re-run without -PreflightOnly to do the real setup." -ForegroundColor Green
        return
    }

    # -----------------------------------------------------------------------
    Write-Step "Configuring git for this clone"

    git config --local submodule.recurse true
    git config --local fetch.recurseSubmodules on-demand
    git config --local push.recurseSubmodules check
    git config --local diff.submodule log
    git config --local status.submodulesummary 1
    git remote set-head origin $SuperBranch 2>$null | Out-Null
    Write-Ok "submodule.recurse, push.recurseSubmodules=check, diff.submodule=log"

    # -----------------------------------------------------------------------
    Write-Step "Checking for interrupted submodule checkouts"

    $repaired = 0
    foreach ($s in Get-DeclaredSubmodules) {
        $dir       = Join-Path $Root $s.Path
        $dotGit    = Join-Path $dir '.git'
        $moduleDir = Join-Path $Root (Join-Path '.git\modules' $s.Name)
        $broken    = $false

        if (Test-Path $dotGit) {
            $null = git -C $dir rev-parse --resolve-git-dir .git 2>$null
            if ($LASTEXITCODE -ne 0) { $broken = $true }
        } elseif (Test-Path $moduleDir) {
            $null = git rev-parse --resolve-git-dir $moduleDir 2>$null
            if ($LASTEXITCODE -ne 0) { $broken = $true }
        }

        if (-not $broken) { continue }

        if ((Test-Path $dotGit) -and -not (Test-Path $dotGit -PathType Container)) {
            $gitfile = Get-Content -LiteralPath $dotGit -TotalCount 1 -ErrorAction SilentlyContinue
            if ($gitfile -match '^gitdir:\s*(.+)$') {
                $target = $Matches[1].Trim()
                if (-not [System.IO.Path]::IsPathRooted($target)) { $target = Join-Path $dir $target }
                if (Test-Path (Join-Path $target 'HEAD')) {
                    Fail @"
$($s.Path)\.git points at a repository git cannot open:

  $target

That path exists, so this is a deliberate setup rather than wreckage from an
interrupted clone, and Setup will not delete it. Fix or remove it by hand,
then run Setup again.
"@
                }
            }
        }

        Write-Warn "$($s.Path): leftover from an interrupted clone -- removing so it can be re-fetched"

        Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path $dir) { & cmd.exe /c rd /s /q "$dir" 2>$null }
        if (Test-Path $dir) {
            Fail @"
Could not delete $dir.

Something is holding a file open in it -- the Unreal Editor, Visual Studio, an
open File Explorer window, or an antivirus scan. Close them and run Setup
again. If it still fails, delete that folder by hand first.
"@
        }

        if (Test-Path $moduleDir) {
            Remove-Item -LiteralPath $moduleDir -Recurse -Force -ErrorAction SilentlyContinue
            if (Test-Path $moduleDir) { & cmd.exe /c rd /s /q "$moduleDir" 2>$null }
        }

        git submodule deinit -f -- $s.Path 2>$null | Out-Null
        $repaired++
    }

    if ($repaired -eq 0) { Write-Ok "no interrupted checkouts to clean up" }
    else { Write-Ok "$repaired cleaned up; fetched fresh below" }

    # -----------------------------------------------------------------------
    Write-Step "Checking for unsaved work"

    $alreadySetUp = -not @($RequiredSubmodules | Where-Object { -not (Test-SubmoduleCheckedOut $_) }).Count
    if ($alreadySetUp) {
        Write-Warn "This copy is already set up. For day-to-day updates use Sync.bat -- it is"
        Write-Dim  "much faster and skips the engine download and registration steps."
        Write-Dim  "Continuing with a full setup."
        Assert-EditorClosed
        Assert-NoBlockingChanges 're-running Setup'
    } else {
        Write-Ok "fresh copy; nothing to check"
    }

    # -----------------------------------------------------------------------
    Write-Step "Switching to the '$SuperBranch' branch"

    Update-Superproject

    # -----------------------------------------------------------------------
    Write-Step "Fetching submodules (this is the long part)"

    if ($CanPartialFetch) {
        Write-Host "    UnrealEngine (blobless partial clone)..."
        $ok = Invoke-SubmoduleUpdate @('UnrealEngine') @('--filter=blob:none')
    } else {
        Write-Host "    UnrealEngine (full clone)..."
        $ok = Invoke-SubmoduleUpdate @('UnrealEngine')
    }
    if (-not $ok) { Fail (Get-UpdateFailureMessage @('UnrealEngine')) }
    Write-Ok "UnrealEngine"

    Write-Host "    Game + SourceAssets (recursive, includes plugins)..."
    if (-not (Invoke-SubmoduleUpdate @('Game', 'SourceAssets'))) {
        Fail (Get-UpdateFailureMessage @('Game', 'SourceAssets'))
    }
    Write-Ok "Game + SourceAssets + nested plugin submodules"

    if (Test-Path (Join-Path $ProjectDir 'Content')) {
        Push-Location $GameDir
        $lfsCount = (git lfs ls-files 2>$null | Measure-Object -Line).Lines
        Pop-Location
        if ($lfsCount -gt 0) { Write-Ok "$lfsCount LFS files resolved in Game/" }
        else { Write-Warn "No LFS files reported in Game/. Content may not have downloaded." }
    }

    # -----------------------------------------------------------------------
    Write-Step "Aligning submodules to their declared branches"

    Sync-SubmoduleBranches

    # -----------------------------------------------------------------------
    Write-Step "Verifying every pinned commit is fetchable by a fresh clone"

    $unfetchable = 0
    foreach ($line in (git submodule status --recursive 2>$null)) {
        if ($line -notmatch '^\s*([-+U]?)([0-9a-f]{40})\s+(\S+)') { continue }
        $flag = $Matches[1]; $sha = $Matches[2]; $path = $Matches[3]
        $short = $sha.Substring(0, 8)

        if ($flag -eq '-') { Write-Warn "$path : not initialized; cannot verify"; continue }

        $dir = Join-Path $Root $path
        $containing = git -C $dir branch -r --contains $sha 2>$null
        $onOrigin = $containing | Where-Object { $_ -match '^\s*origin/' }

        if ($onOrigin) {
            Write-Ok "$path @ $short"
        } elseif ($containing) {
            $unfetchable++
            Write-Warn "$path @ $short is NOT on any origin/ branch (only:$(($containing -join ',').Trim()))"
            Write-Host  "         A fresh clone pulls from origin, so this pin will fail for others." -ForegroundColor DarkGray
            Write-Host  "         Push the commit to origin, or repoint the pin." -ForegroundColor DarkGray
        } else {
            $unfetchable++
            Write-Warn "$path @ $short is on NO remote branch at all."
            Write-Host  "         This commit exists only locally. Push it before sharing the superproject." -ForegroundColor DarkGray
        }
    }
    if ($unfetchable -gt 0) {
        Write-Warn "$unfetchable pin(s) are not fetchable from origin. Other contributors' setup WILL fail until fixed."
    }

    # -----------------------------------------------------------------------
    Write-Step "Registering upstream remotes for forked repos"

    foreach ($u in $Upstreams) {
        $dir = Join-Path $Root $u.Path
        if (-not (Test-Path (Join-Path $dir '.git'))) {
            Write-Warn "$($u.Path) not checked out; skipping its upstream."
            continue
        }

        $remotes = @(git -C $dir remote)
        $current = $null
        if ($remotes -contains 'upstream') {
            $current = git -C $dir remote get-url upstream
        }

        if ($current) {
            if ($current -ne $u.Url) {
                git -C $dir remote set-url upstream $u.Url
                Write-Ok "$($u.Path): upstream repointed to $($u.Url)"
            } else {
                Write-Ok "$($u.Path): upstream already set"
            }
        } else {
            git -C $dir remote add upstream $u.Url
            Write-Ok "$($u.Path): upstream -> $($u.Url)"
        }

        git -C $dir remote set-url --push upstream 'DISABLED-do-not-push-to-upstream'

        if ($u.Note) { Write-Host "         note: $($u.Note)" -ForegroundColor DarkGray }
    }

    Write-Host ""
    Write-Host "    Sync a fork from its upstream with:" -ForegroundColor DarkGray
    Write-Host "      git -C <path> fetch upstream" -ForegroundColor DarkGray
    Write-Host "      git -C <path> merge upstream/<branch>   # then push to origin" -ForegroundColor DarkGray

    # -----------------------------------------------------------------------
    Write-Step "Downloading engine binary dependencies"

    if ($SkipEngineDeps) {
        Write-Warn "Skipped (-SkipEngineDeps)"
    } else {
        $engineSetup = Join-Path $EngineDir 'Setup.bat'
        if (-not (Test-Path $engineSetup)) { Fail "Missing $engineSetup - the UnrealEngine submodule did not check out." }
        Write-Host "    Running UnrealEngine\Setup.bat -- expect ~15-20 GB and a long wait."
        Invoke-Native 'cmd.exe' @('/c', $engineSetup) $EngineDir
        Write-Ok "Engine dependencies installed"
    }

    # -----------------------------------------------------------------------
    Write-Step "Registering the engine for $(Split-Path $UProject -Leaf)"

    if ($SkipRegister) {
        Write-Warn "Skipped (-SkipRegister)"
    } elseif (-not (Test-Path $UProject)) {
        Write-Warn "$UProject not found; skipping registration."
    } else {
        $assoc = Get-EngineAssociation

        if ([string]::IsNullOrWhiteSpace($assoc)) {
            Write-Warn "EngineAssociation is empty; Unreal will prompt for an engine on first open."
        } elseif (-not (Test-EngineGuid $assoc)) {
            Write-Warn "EngineAssociation '$assoc' is a launcher version, not a source-build GUID. Nothing to register."
            Write-Dim  "Use Launch.bat to open the editor. Double-clicking the .uproject would look"
            Write-Dim  "for an Epic Games Launcher install of UE $assoc instead of this engine."
        } else {
            $reg = Register-Engine $assoc $EngineDir -Force:$Force

            if ($reg.Status -eq 'Conflict') {
                Write-Warn "GUID $assoc is already mapped on this machine:"
                Write-Host "         current: $($reg.Existing)"
                Write-Host "         new    : $($reg.Target)"
                Write-Warn "Repointing it will change which engine that OTHER checkout builds against."
                $reply = Read-Host "         Repoint it? [y/N]"
                if ($reply -match '^[Yy]') {
                    $reg = Register-Engine $assoc $EngineDir -Force
                } else {
                    Write-Warn "Left unchanged. Pass -Force to overwrite, or open the .uproject and pick an engine manually."
                }
            }

            foreach ($g in $reg.Removed) {
                Write-Warn "removed $g, a second registration of this engine folder"
            }
            if ($reg.Status -ne 'Conflict') { Write-Ok "$assoc -> $($reg.Target)" }
        }
    }

    # -----------------------------------------------------------------------
    Write-Step "Generating project files"

    if ($SkipGenerate) {
        Write-Warn "Skipped (-SkipGenerate)"
    } else {
        $gen = Join-Path $EngineDir 'GenerateProjectFiles.bat'
        if (-not (Test-Path $gen)) { Fail "Missing $gen" }
        if (-not (Test-Path $UProject)) { Fail "Missing $UProject - the Game submodule did not check out." }

        Push-Location $EngineDir
        try {
            & cmd.exe /c "`"$gen`" -project=`"$UProject`" -game -engine"
            if ($LASTEXITCODE -ne 0) { Fail "GenerateProjectFiles.bat exited with code $LASTEXITCODE" }
        } finally {
            Pop-Location
        }
        Write-Ok "Solution generated: $([System.IO.Path]::ChangeExtension($UProject, '.sln'))"
    }

    # -----------------------------------------------------------------------
    $slnPath = [System.IO.Path]::ChangeExtension($UProject, '.sln')
    try { $slnDisplay = Resolve-Path -LiteralPath $slnPath -Relative -ErrorAction Stop } catch { $slnDisplay = $slnPath }

    Write-Host ""
    Write-Host "=== Setup complete ===" -ForegroundColor Green
    Write-Host @"

Next step: run Launch.bat. It builds the engine and the $EditorTarget
target, then opens the editor. Expect 1-3 hours on the first build; later
builds are incremental and take a minute or two.

For C++ work, open $slnDisplay in Rider or Visual Studio.

Day-to-day, get everyone else's latest work with Sync.bat, then Launch.bat.
Sync updates the engine, game, art assets and plugins together; pulling Game
on its own from Rider leaves its plugins behind and the build fails.

"@
}

# ---------------------------------------------------------------------------
# Sync -- the body of Sync.ps1 (steps 3-7; the launch-after-sync prompt is
# CLI-specific UX and stays in Sync.ps1 itself)

function Invoke-ProjectSync {
    param(
        [string[]]$Only,
        [switch]$SkipLfs
    )

    $scope = if ($Only -and $Only.Count) { @($Only) } else { @($RequiredSubmodules) }

    Write-Host ""
    Write-Host "=== $ProjectTitle :: sync with the team ===" -ForegroundColor White
    Write-Host "Root: $Root"

    # -----------------------------------------------------------------------
    Write-Step "Checking this copy is set up"

    $null = git -C $Root rev-parse --is-inside-work-tree 2>$null
    if ($LASTEXITCODE -ne 0) {
        Fail @"
This folder is not a git repository. Clone the project instead of downloading
it as a ZIP, then run Setup.bat:

  git clone $SuperRepoUrl
"@
    }

    $missing = @($RequiredSubmodules | Where-Object { -not (Test-SubmoduleCheckedOut $_) })
    if ($missing.Count) {
        Fail @"
These folders have not been downloaded yet: $($missing -join ', ')

Run Setup.bat first. Sync only updates a copy that Setup has already set up.
"@
    }
    Write-Ok "Setup has been run"

    Assert-EditorClosed

    # -----------------------------------------------------------------------
    Write-Step "Checking for unsaved work"

    Assert-NoBlockingChanges 'syncing'

    # -----------------------------------------------------------------------
    Write-Step "Updating the outer project"

    Update-Superproject

    # -----------------------------------------------------------------------
    if ($Only -and $Only.Count) {
        Write-Step "Updating selected submodule(s): $($scope -join ', ')"
    } else {
        Write-Step "Updating engine, game, art assets and plugins (can take a while)"
    }

    $before = @{}
    foreach ($s in Get-SubmoduleStates) { $before[$s.Path] = $s }

    foreach ($s in $before.Values) {
        if (($scope -contains $s.Path) -and ($s.State -eq 'Ahead' -or $s.State -eq 'Diverged')) {
            Write-Warn "$($s.Path) is newer than the version the team pinned; moving it to the pinned version."
            Write-Dim  "Anything already pushed is safe on GitHub. Folders added since the pin will"
            Write-Dim  "disappear until the pin is updated -- that is expected, not data loss."
        }
    }

    if (-not (Invoke-SubmoduleUpdate $scope)) {
        Fail (Get-UpdateFailureMessage $scope)
    }

    if (-not ($Only -and $Only.Count)) {
        # Optional submodules (anything not in $RequiredSubmodules) are updated
        # only if this person already has them. A plain update, without --init,
        # does exactly that and never clones a repository someone has no access
        # to. Selective sync (-Only) skips this sweep on purpose: it is meant to
        # touch only what was asked for.
        $optional = @(Get-DeclaredSubmodules | Where-Object {
            $RequiredSubmodules -notcontains $_.Path -and (Test-SubmoduleCheckedOut $_.Path)
        } | ForEach-Object { $_.Path })
        if ($optional.Count) {
            git submodule update --recursive -- @optional
            if ($LASTEXITCODE -ne 0) { Fail (Get-UpdateFailureMessage $optional) }
        }
    }

    $moved = 0
    foreach ($s in Get-SubmoduleStates) {
        $was = $before[$s.Path]
        if ($s.Head -and (-not $was -or $was.Head -ne $s.Head)) {
            $moved++
            $subject = git -C (Join-Path $Root $s.Path) log -1 --format=%s 2>$null
            Write-Ok "$($s.Path) -> $($s.Head.Substring(0,8))  $subject"
        }
    }
    if (-not $moved) { Write-Ok "everything was already at the pinned versions" }

    # -----------------------------------------------------------------------
    if ($SkipLfs) {
        Write-Step "Downloading art assets (Git LFS)"
        Write-Warn "Skipped (-SkipLfs)"
    } else {
        Write-Step "Downloading art assets (Git LFS)"
        foreach ($p in $LfsRepos) {
            if ($Only -and $Only.Count -and -not ($scope | Where-Object { $p -eq $_ -or $p.StartsWith("$_/") })) { continue }
            if (-not (Test-SubmoduleCheckedOut $p)) { continue }
            git -C (Join-Path $Root $p) lfs pull
            if ($LASTEXITCODE -ne 0) {
                Fail @"
Downloading art assets for $p failed. The error is above.

If it mentions a network problem, run Sync.bat again -- it picks up where it
left off. If it mentions storage quota or permissions, tell the project owner.
"@
            }
            Write-Ok "$p assets downloaded"
        }
    }

    # -----------------------------------------------------------------------
    Write-Step "Re-attaching submodules to their branches"

    Sync-SubmoduleBranches

    # -----------------------------------------------------------------------
    Write-Step "Verifying"

    $bad = @(Get-SubmoduleStates | Where-Object {
        ($scope -contains $_.Path) -and
        $_.State -ne 'Match' -and
        -not ($_.State -eq 'Missing' -and $RequiredSubmodules -notcontains $_.Path.Split('/')[0])
    })
    if ($bad.Count) {
        $list = ($bad | ForEach-Object { "  $($_.Path): $($_.State)" }) -join "`n"
        Fail @"
Sync finished, but these folders still do not match the pinned versions:

$list

Run Sync.bat once more. If the same list comes back, send this log to whoever
maintains the project setup.
"@
    }
    Write-Ok "every folder matches the version the team pinned"

    $superHead = git log -1 --format='%h %s' 2>$null
    Write-Host ""
    Write-Host "=== Up to date ===" -ForegroundColor Green
    Write-Host "  $SuperBranch @ $superHead"
}

# ---------------------------------------------------------------------------
# Build + launch -- the body of Launch.ps1

function Invoke-ProjectBuild {
    param(
        [switch]$NoBuild,
        [switch]$BuildOnly,
        [switch]$Rebuild,
        [ValidateSet('Development', 'DebugGame', 'Debug')]
        [string]$Configuration = 'Development',
        [switch]$PreflightOnly,
        [switch]$SkipSyncCheck
    )

    $Target = $EditorTarget
    $ProjectDirRelative = $ProjectDir.Substring($Root.Length).TrimStart('\', '/')

    $RunSetupFirst = @"

Run Setup.bat first. It only needs to be done once, but it has to finish
successfully before the project can be built.
"@

    Write-Host ""
    Write-Host "=== $ProjectTitle :: build and launch editor ===" -ForegroundColor White
    Write-Host "Root: $Root"

    # -----------------------------------------------------------------------
    Write-Step "Checking the project is set up"

    if (-not (Test-Path $UProject)) {
        Fail @"
Could not find the game project at:
  $UProject

Either this script is not in the project root, or the Game submodule was never
checked out.
$RunSetupFirst
"@
    }
    Write-Ok "$(Split-Path $UProject -Leaf) found"

    if (-not (Test-Path (Join-Path $EngineDir 'Engine'))) {
        Fail @"
The engine folder is missing or empty:
  $EngineDir

This is what an uninitialized submodule looks like -- the folder exists but
nothing is in it.
$RunSetupFirst
"@
    }
    Write-Ok "Engine source present"

    if (-not (Test-Path (Join-Path $EngineDir 'Engine\Binaries\ThirdParty'))) {
        Fail @"
The engine's binary dependencies are missing.

These are roughly 15-20 GB of files that are NOT stored in git, downloaded by
Setup.bat. Their absence usually means Setup.bat was interrupted or closed
early.
$RunSetupFirst
"@
    }
    Write-Ok "Engine binary dependencies present"

    if (-not (Test-Path $BuildBat)) {
        Fail @"
The engine build script is missing:
  $BuildBat
$RunSetupFirst
"@
    }

    # -----------------------------------------------------------------------
    Write-Step "Checking this copy matches the team's pinned versions"

    if ($SkipSyncCheck) {
        Write-Warn "Skipped (-SkipSyncCheck)"
    } else {
        $current = git -C $Root symbolic-ref --quiet --short HEAD 2>$null
        if ($current -ne $SuperBranch) {
            $was = if ($current) { "'$current'" } else { 'a detached commit' }
            Fail @"
The outer project is on $was, not '$SuperBranch', so you would be building
a different version of the game from everyone else.

Run Sync.bat. It switches branches for you.
"@
        }

        git -C $Root fetch --quiet --no-recurse-submodules origin $SuperBranch 2>$null
        if ($LASTEXITCODE -eq 0) {
            $behind = git -C $Root rev-list --count "HEAD..origin/$SuperBranch" 2>$null
            if ($behind -match '^\d+$' -and [int]$behind -gt 0) {
                Write-Warn "The team has pushed $behind update(s) you do not have yet. Run Sync.bat to get them."
            }
        } else {
            Write-Dim "Could not reach GitHub to check for updates; carrying on offline."
        }

        $states = @(Get-SubmoduleStates | Where-Object {
            -not ($_.State -eq 'Missing' -and $RequiredSubmodules -notcontains $_.Path.Split('/')[0])
        })
        $stale = @($states | Where-Object { $_.State -eq 'Behind' -or $_.State -eq 'Missing' })
        if ($stale.Count) {
            $list = ($stale | ForEach-Object { "  $($_.Path): $(if ($_.State -eq 'Missing') { 'not downloaded' } else { 'older than the pinned version' })" }) -join "`n"
            Fail @"
Parts of this copy are out of step with each other:

$list

Building like this usually fails with compiler errors that have nothing to do
with anything you changed. Run Sync.bat, then this again.
"@
        }
        foreach ($s in @($states | Where-Object { $_.State -eq 'Ahead' -or $_.State -eq 'Diverged' })) {
            Write-Warn "$($s.Path) is not at the pinned version ($($s.State.ToLower())); building it as-is."
        }
        Write-Ok "submodules are at (or ahead of) their pinned versions"
    }

    # -----------------------------------------------------------------------
    Write-Step "Checking art assets downloaded properly"

    $contentDir = Join-Path $ProjectDir 'Content'
    if (Test-Path $contentDir) {
        $sample = Get-ChildItem -Path $contentDir -Filter *.uasset -Recurse -File -ErrorAction SilentlyContinue |
                  Select-Object -First 25
        $pointers = 0
        foreach ($f in $sample) {
            try {
                $fs = [System.IO.File]::OpenRead($f.FullName)
                try {
                    $head = New-Object byte[] 24
                    $read = $fs.Read($head, 0, 24)
                } finally { $fs.Dispose() }
                if ($read -gt 0) {
                    $text = [System.Text.Encoding]::ASCII.GetString($head, 0, $read)
                    if ($text.StartsWith('version https://git-lfs')) { $pointers++ }
                }
            } catch { }
        }
        if ($sample.Count -eq 0) {
            Write-Warn "No .uasset files found under $ProjectDirRelative\Content -- content may not have been fetched."
        } elseif ($pointers -gt 0) {
            Fail @"
Art assets have not downloaded -- $pointers of the $($sample.Count) files checked are
Git LFS placeholders rather than real assets.

Opening the editor now would show a project with missing or broken content.

Fix it with:

  git lfs install
  git -C Game lfs pull
  git -C SourceAssets lfs pull

'git lfs install' only needs running once per machine. If it was not run before
you first cloned, this is the expected result.
"@
        } else {
            Write-Ok "$($sample.Count) sampled assets are real files, not LFS placeholders"
        }
    } else {
        Write-Warn "$ProjectDirRelative\Content not found; skipping the asset check."
    }

    # -----------------------------------------------------------------------
    Write-Step "Checking the engine is registered to this checkout"

    $assoc = Get-EngineAssociation

    if ($assoc -and (Test-EngineGuid $assoc)) {
        $reg = Register-Engine $assoc $EngineDir
        if ($reg.Status -eq 'Conflict') {
            Fail @"
This project is registered to a DIFFERENT engine than the one in this folder.

  registered: $($reg.Existing)
  expected:   $($reg.Target)

Building now would use the wrong engine. Re-run Setup.bat and accept the prompt
to repoint it, or run:

  .\Setup.ps1 -SkipEngineDeps -SkipGenerate -Force
"@
        }
        foreach ($g in $reg.Removed) {
            Write-Warn "removed $g, a second registration of this engine folder that Unreal"
            Write-Dim  "would otherwise keep in place of the project's own ID"
        }
        if ($reg.Status -eq 'Registered') {
            Write-Warn "engine registration was missing (Unreal removes it in some cases); restored"
        }
        Write-Ok "engine registered -> $($reg.Target)"
    } else {
        Write-Ok "project names engine '$assoc' (not a source-build GUID); nothing to register"
        Write-Dim "Always open the project with Launch.bat, not by double-clicking the .uproject."
    }

    # -----------------------------------------------------------------------
    Write-Step "Checking Windows will let the editor run"

    switch (Get-SmartAppControlState) {
        'On' {
            Fail @"
Windows Smart App Control is ON, and it will block the editor from starting.

It refuses to load unsigned files it has never seen before, and every editor
DLL built on this machine is exactly that. The build would succeed and the
editor would then fail with "Bad Image ... Error status 0xc0e90002".

$SmartAppControlFix

Then run this again.
"@
        }
        'Evaluation' {
            Write-Warn "Smart App Control is in evaluation mode. It is not blocking anything now,"
            Write-Dim  "but if Windows switches it On, the editor will fail with 'Bad Image ...'"
            Write-Dim  "0xc0e90002. Turning it Off now avoids that surprise:"
            Write-Dim  "Windows Security > App & browser control > Smart App Control settings > Off"
        }
        default { Write-Ok "Smart App Control is off or not present" }
    }

    # -----------------------------------------------------------------------
    Write-Step "Checking the C++ compiler"

    $hasCompiler = $false
    $pf86 = ${env:ProgramFiles(x86)}
    if ($pf86) {
        $vswhere = Join-Path $pf86 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path $vswhere) {
            $vs = & $vswhere -latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property displayName 2>&1 |
                  Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }
            if ($vs) { $hasCompiler = $true; Write-Ok "Visual Studio: $vs" }
        }
    }

    if (-not $hasCompiler) {
        if ($NoBuild) {
            Write-Warn "No C++ compiler found, but -NoBuild was given so nothing will be compiled."
        } else {
            Fail @"
No C++ compiler found. The engine cannot be built without one.

Install Visual Studio 2022 (the free Community edition is fine):

  https://visualstudio.microsoft.com/downloads/

In the installer, tick the workload named:

  Desktop development with C++

That is roughly 10 GB. Nothing else in the installer is required. Once it has
finished, run this script again.

If someone has already built the editor for you and you only need to open it,
you can skip compiling entirely:

  .\Launch.ps1 -NoBuild
"@
        }
    }

    if ($PreflightOnly) {
        Write-Host ""
        Write-Host "All checks passed. Re-run without -PreflightOnly to build and launch." -ForegroundColor Green
        return
    }

    # -----------------------------------------------------------------------
    Write-Step "Checking the shader compiler helper"

    if (Test-Path $SCWExe) {
        Write-Ok "ShaderCompileWorker present"
    } elseif ($NoBuild) {
        Write-Warn "ShaderCompileWorker is missing and -NoBuild was given."
        Write-Dim "The editor will fail as soon as it tries to compile a shader."
    } else {
        Write-Warn "ShaderCompileWorker has not been built on this machine yet."
        Write-Dim "Building it now -- this takes a few minutes, not hours."

        $scwArgs = @('ShaderCompileWorker', 'Win64', 'Development', '-WaitMutex')
        Write-Dim "$BuildBat $($scwArgs -join ' ')"

        & cmd.exe /c "`"$BuildBat`" $($scwArgs -join ' ')"
        $scwExit = $LASTEXITCODE

        if ($scwExit -ne 0) {
            Fail @"
ShaderCompileWorker failed to build (exit code $scwExit).

The editor cannot compile shaders without it, so there is no point launching.
The compiler errors are above.
"@
        }
        if (-not (Test-Path $SCWExe)) {
            Fail @"
The ShaderCompileWorker build reported success but the executable is missing:
  $SCWExe

Please send the log to whoever maintains the setup.
"@
        }
        Write-Ok "ShaderCompileWorker built"
    }

    # -----------------------------------------------------------------------
    Write-Step "Building $Target ($Configuration, Win64)"

    if ($NoBuild) {
        Write-Warn "Skipped (-NoBuild)"
        if (-not (Test-Path $EditorExe)) {
            Fail @"
-NoBuild was given, but the editor has never been built on this machine:
  $EditorExe

There is nothing to launch. Run this script without -NoBuild.
"@
        }
    } else {
        $firstBuild = -not (Test-Path $EditorExe)
        if ($firstBuild) {
            Write-Warn "The engine has not been built on this machine yet."
            Write-Dim "The first build takes 1-3 hours and will use every CPU core."
            Write-Dim "Later builds are incremental and usually take a minute or two."
            Write-Dim "Leave this window open -- closing it cancels the build."
        }

        $buildArgs = @($Target, 'Win64', $Configuration, "-Project=`"$UProject`"", '-WaitMutex')
        if ($Rebuild) {
            $buildArgs += '-Clean'
            Write-Warn "Full rebuild requested; this discards incremental progress."
        }

        Write-Dim "$BuildBat $($buildArgs -join ' ')"
        $sw = [System.Diagnostics.Stopwatch]::StartNew()

        & cmd.exe /c "`"$BuildBat`" $($buildArgs -join ' ')"
        $buildExit = $LASTEXITCODE

        $sw.Stop()
        $elapsed = '{0:hh\:mm\:ss}' -f $sw.Elapsed

        if ($buildExit -ne 0) {
            Fail @"
The build failed after $elapsed (exit code $buildExit).

The compiler errors are above. The last few lines are usually the real cause;
everything after the first error tends to be noise.

If the errors mention missing files or headers, the most likely cause is that
someone else changed C++ code and you need their latest work. Run Sync.bat,
then this script again.
"@
        }
        Write-Ok "Build succeeded in $elapsed"
    }

    # -----------------------------------------------------------------------
    Write-Step "Launching the editor"

    if ($BuildOnly) {
        Write-Warn "Skipped (-BuildOnly)"
        Write-Host ""
        Write-Host "=== Build complete ===" -ForegroundColor Green
        return
    }

    if (-not (Test-Path $EditorExe)) {
        Fail @"
The build reported success but the editor executable is missing:
  $EditorExe

This should not happen. Please send the log to whoever maintains the setup.
"@
    }

    Write-Dim "$EditorExe `"$UProject`""
    Start-Process -FilePath $EditorExe -ArgumentList "`"$UProject`""
    Write-Ok "Editor starting"

    Write-Host ""
    Write-Host "=== Done ===" -ForegroundColor Green
    Write-Host @"

The editor is opening in a separate window. It can take a few minutes the first
time while it compiles shaders -- that is normal, and much faster afterwards.

Next time, if you have not changed any C++ code, you can skip the build:

  Launch.bat -NoBuild

"@
}

# ---------------------------------------------------------------------------
$FunctionsToExport = @(
    'Write-Step', 'Write-Ok', 'Write-Warn', 'Write-Dim', 'Stop-Log', 'Set-WorkflowLogPath', 'Fail',
    'Get-DeclaredSubmodules', 'Test-SubmoduleCheckedOut', 'Get-SubmoduleStates',
    'Get-LocalChanges', 'Get-ShelveMessage', 'Assert-NoBlockingChanges', 'Assert-EditorClosed',
    'Get-SmartAppControlState', 'ConvertTo-EnginePath', 'Test-EngineRoot', 'Get-EngineAssociation',
    'Test-EngineGuid', 'Register-Engine',
    'Update-Superproject', 'Invoke-SubmoduleUpdate', 'Get-SubmoduleUrls', 'Get-UpdateFailureMessage',
    'Sync-SubmoduleBranches',
    'Set-SubmoduleBranch', 'Get-BranchWatchStatus', 'Get-SubmoduleBranches',
    'Get-WorkflowLocalSettings', 'Set-WorkflowLocalSettings',
    'Get-ProjectInfo', 'Get-ProjectDocs',
    'Invoke-ProjectSetup', 'Invoke-ProjectSync', 'Invoke-ProjectBuild'
)
$VariablesToExport = @(
    'ProjectTitle', 'SuperRepoUrl', 'SuperRepoWeb', 'SuperBranch',
    'RequiredSubmodules', 'EngineDir', 'GameDir', 'ProjectDir', 'UProject', 'EditorTarget',
    'LfsRepos', 'EditorProcessNames', 'Root', 'SmartAppControlFix'
)
Export-ModuleMember -Function $FunctionsToExport -Variable $VariablesToExport
