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

- **Welcome** -- shown before any project is open (and reachable afterward
  via "Register a Project..." in the sidebar): open a project already set
  up for Forge, register one that isn't yet, or pick a recent project.
- **Overview** -- project summary, quick Sync / Build & Launch actions.
- **Submodules** -- a tree view of every submodule (nested included) with
  its state (Match / Ahead / Behind / Diverged / Missing), its branch (and
  `pinned: X` when the checked-out branch differs from what's pinned),
  branch switching (lists what's on `origin` to pick from; cascades to
  nested submodules), a per-submodule "watch branch" that flags new
  commits upstream without changing anything, shelving uncommitted work,
  and handing a repo off to an external git GUI.
- **Files** -- every tracked file across every repo as one filterable,
  searchable tree, with LFS lock/unlock, an unlock-notification watch,
  per-file history (browse/diff/restore an old version), conflict
  resolution, and **Submit** (commit the checked files, push, release
  locks, and cascade every ancestor submodule's pin bump up through the
  superproject in one step).
- **History** -- the commit graph for one repo at a time, Simple or Full
  detail, with pin-bump commits expandable into the real commits they
  pulled in.
- **Build** -- selective sync (just the engine, just a plugin, ...), the
  same build options `Launch.ps1` exposes on the command line, a **Cancel
  Build** button that force-stops a running compile, and
  DerivedDataCache/Intermediate/LFS-cache housekeeping.
- **Log** -- live, color-coded, filterable (All/Warnings/Errors) output
  from whatever Forge is currently running, with a banner for an expected,
  named failure (as opposed to a crash).
- **Settings** -- theme (mode plus customizable accent/background/icon
  colors), the LFS-lock-management feature flag, and per-project settings
  including an engine-location override for a project that doesn't bundle
  its own engine.
- **Help** -- renders the opened project's configurable `Docs` list (markdown
  rendered inline via WebView2; PDFs open externally) under "Project Docs",
  plus Forge's own user guide under "Forge Docs". Falls back to plain text if
  the WebView2 Runtime isn't installed.
- A background cache paints a reopened project instantly, then always
  refreshes for real in the background; buttons stay clickable throughout
  (a status bar shows what's currently running). Hover any control for a
  tooltip explaining what it does.

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

The easiest way: in Forge, click **Register a Project...** and point it at
a folder containing a `.uproject` -- it scaffolds the two files below for
you automatically (see the in-app "Projects & Settings" doc, Help tab).

To do it by hand instead (or for a project shape registering doesn't cover
-- separate Game/Engine submodules):

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

A project that doesn't bundle its own engine submodule can point Forge at
one installed elsewhere via Settings → Project → Engine location, once
open.

## Developer documentation

For anyone making changes to Forge itself -- architecture, the PowerShell
hosting model, UI conventions, and how project data is persisted (with
diagrams) -- see [`docs-dev/`](docs-dev/ARCHITECTURE.md). Start with
[`docs-dev/ARCHITECTURE.md`](docs-dev/ARCHITECTURE.md).

## Known limitations (v1)

- Cloning a brand-new project from scratch (i.e. `git clone` itself) is not
  done through Forge yet -- the project folder has to already exist on
  disk. "Register a Project" covers everything after that.
- "Register a Project" only covers the simple case: no separate Game/Engine
  submodules. A project shaped like that still needs the by-hand steps
  above.
- Forge does not check whether a project's `ProjectWorkflow.psm1` is behind
  the canonical version of that file (or Forge's own bundled copy used for
  scaffolding); keeping it in sync across projects is manual for now.
- Windows only (same as the PowerShell module it wraps) -- see
  [`docs-dev/ARCHITECTURE.md`](docs-dev/ARCHITECTURE.md#known-limitations)
  for why that's a framework-level constraint, not just a configuration
  choice.
