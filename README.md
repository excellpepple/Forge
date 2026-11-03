# Forge

A standalone desktop app for the non-programmer-friendly workflow around
multi-repo, git-submodule-based Unreal Engine projects (the "superproject"
pattern used by `CORE-Super`). Forge wraps the project's own
`Tools\ProjectWorkflow.psm1` PowerShell module instead of reimplementing its
logic, so the safety behavior that module has (unsaved-work detection, pin
vs. upstream state, LFS placeholder detection, engine registration, etc.)
stays in exactly one place, shared with that project's `Setup.bat` /
`Sync.bat` / `Launch.bat` CLI entry points.

Forge itself carries no project-specific knowledge. It can open any folder
that ships:

- `Tools\ProjectWorkflow.psm1` -- the shared engine (git/build/safety logic)
- `project.config.json` -- the project-specific values the module needs
  (title, editor target, the `.uproject`'s relative path, etc.)

The submodule list and branches are **not** duplicated in that config file;
they come straight from `.gitmodules`.

## What it does

- **Overview** -- project summary, quick Sync / Build & Launch actions.
- **Submodules** -- a tree view of every submodule (nested included) with
  its state (Match / Ahead / Behind / Diverged / Missing), its branch (and
  `[pinned: X]` when the checked-out branch differs from what's pinned),
  branch switching (lists what's on `origin` to pick from; cascades to
  nested submodules), and a per-submodule "watch branch" that flags new
  commits upstream without changing anything.
- **Build** -- selective sync (just the engine, just a plugin, ...) and the
  same build options `Launch.ps1` exposes on the command line.
- **Log** -- live output from the underlying PowerShell calls, with a banner
  for an expected, named failure (as opposed to a crash).
- **Help** -- renders the opened project's configurable `Docs` list (markdown
  rendered inline via WebView2; PDFs open externally) under "Project Docs",
  plus Forge's own user guide under "Forge Docs". Falls back to plain text if
  the WebView2 Runtime isn't installed.
- Hover any control for a tooltip explaining what it does.

## Building

Requires the .NET 7 SDK.

```bash
dotnet build
```

## Running

```bash
dotnet run --project src/Forge
```

## Publishing a single-file build

```bash
dotnet publish src/Forge -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

This produces `publish\Forge.exe` plus a handful of native DLLs that
`PublishSingleFile` cannot embed (WPF's and PowerShell's own native
dependencies) -- copy the whole `publish\` folder, not just the `.exe`.

## Adding a new project

1. Copy `Tools\ProjectWorkflow.psm1` from an existing Forge-compatible
   project (e.g. `CORE-Super`) into the new project's `Tools\` folder.
2. Add a `project.config.json` at the new project's root. See
   `CORE-Super\project.config.json` for the shape: `ProjectTitle`,
   `SuperRepoUrl`, `SuperRepoWeb`, `SuperBranch`, `RequiredSubmodules`,
   `EngineSubmodulePath`, `GameSubmodulePath`, `UProjectRelativePath`,
   `EditorTarget`, `LfsRepos`, `EditorProcessNames`, `Upstreams`, and
   optionally `Docs` -- a list of `{ "Title", "Path", "Type" }` entries
   (`Type` is `"markdown"` or `"pdf"`, `Path` relative to the project root)
   that controls what shows up in the Help tab.
3. Open the project folder in Forge.

## Known limitations (v1)

- Cloning a brand-new project from scratch is not supported yet -- the
  project folder has to already exist and have `Tools\ProjectWorkflow.psm1`
  importable (i.e. `git clone` already done).
- Forge does not check whether a project's `ProjectWorkflow.psm1` is behind
  the canonical version of that file; keeping it in sync across projects is
  manual for now.
- Windows only (same as the PowerShell module it wraps).
