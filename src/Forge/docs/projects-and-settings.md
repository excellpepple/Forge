# Projects & Settings

How to get a project open in Forge for the first time, and what's under
Settings once one is.

## The Welcome screen

The first thing you see when nothing's open yet (and any time afterward via
**Register a Project...** in the sidebar, which stays available even with a
project open). Two ways in:

- **Open Project...** -- pick a folder that's already set up for Forge (it
  contains `Tools\ProjectWorkflow.psm1` and `project.config.json`). If a
  folder is missing either, Forge says so rather than guessing.
- **Register a Project...** -- point Forge at an Unreal project folder that
  *isn't* set up for Forge yet. See below.

Projects you've opened before show up underneath, and in the recent-projects
dropdown next to **Open Project...** elsewhere in the app. Forge also
reopens whatever project was open last time, automatically, on launch.

## Registering a project that isn't Forge-compatible yet

Click **Register a Project...** and choose the project's folder. Forge looks
for a `.uproject` file directly inside it, or one level down (the common
case: picking a repo root whose `.uproject` lives in a `Game\` subfolder).

- If the folder is **already** Forge-compatible, Forge just opens it --
  nothing changes.
- If it finds a `.uproject` but the folder isn't set up yet, it shows you
  the project's name and lets you confirm a title and editor build target
  (both prefilled from the `.uproject`'s filename), then adds the two files
  Forge needs: `Tools\ProjectWorkflow.psm1` and `project.config.json`.
  Nothing else about the project changes.
- If no `.uproject` is found at all, it says so -- pick the project's own
  folder, not a parent folder several levels up.

This covers the common case of a project with no separate Game/Engine
submodules. Right after registering, if the project doesn't bundle its own
engine, you'll want to point Forge at one -- see **Engine location** below.

Cloning a brand-new project from scratch (i.e. `git clone` itself) isn't
done through Forge yet -- the folder needs to already exist on disk first.

## Settings

### Theme

**Mode**: Light, Dark, or System (follows your Windows theme and switches
live if it changes). Independently for Light and Dark mode, you can pick:

- **Accent** -- buttons, selected tabs, active filters, links
- **Background** -- the main window background
- **Icon color** -- sidebar and toolbar icon tint

Pick from the preset swatches or enter any hex color. Changes apply
immediately.

### General

**Enable Git LFS lock management** -- lets Forge show and manage LFS file
locks (who's editing what -- the same locks the Unreal Editor itself sees).
Only matters for projects that actually use LFS locking; leave it off
otherwise.

### Project

Read-only summary of the open project (title, editor target, folder), plus:

**Engine location** -- by default Forge uses the project's bundled engine
submodule. If this project doesn't distribute its own engine, use
**Browse...** to point Forge at one installed elsewhere (an Epic Games
Launcher install, a custom build) -- specifically, the folder containing
`Engine\Binaries\Win64\UnrealEditor.exe`. **Clear** goes back to the
bundled submodule. This choice is personal to your machine: it's never
shared with or visible to teammates, and never committed to the repo, so
each person on a team can point at their own engine location independently.

## Adding Forge support to another project by hand

Registering a project (above) does this for you automatically for the
common case. To do it by hand, or for a project with separate Game/Engine
submodules that registering doesn't cover, add two files at the project's
root:

- `Tools\ProjectWorkflow.psm1` -- copy this from an existing
  Forge-compatible project; it's the same file every project shares, and
  carries no project-specific values itself.
- `project.config.json` -- the values that *are* project-specific: project
  title, the editor build target, where the `.uproject` lives, which
  submodules are required, and so on.

The submodule list and their branches are **not** repeated in that config
file -- Forge (and the module) read those straight from `.gitmodules`.

### The `Docs` list

`project.config.json` can include a `Docs` array to control what shows up
under "Project Docs" in the Help tab:

```json
"Docs": [
  { "Title": "README", "Path": "README.md", "Type": "markdown" },
  { "Title": "Get Started Guide (PDF)", "Path": "docs/get-started.pdf", "Type": "pdf" }
]
```

`Path` is relative to the project's root. `Type` is `"markdown"` (rendered
right there in the Help tab) or `"pdf"` (opened in your system's PDF viewer
via an "Open" button -- Forge never launches an external program on its own
just because you changed a dropdown selection). This field is entirely
optional; a project with no `Docs` list just shows "Forge Docs" with
nothing above it.
