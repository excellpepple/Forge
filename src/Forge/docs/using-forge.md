# Using Forge

Forge is a desktop front end for a project's own `Tools\ProjectWorkflow.psm1`
script -- the same engine/git logic behind `Setup.bat`, `Sync.bat` and
`Launch.bat`. Nothing Forge does is different from what those scripts do;
it just gives it a UI, a submodule tree, and a couple of extras those
scripts don't offer on the command line.

## Opening a project

Click **Open Project...** and pick a folder that contains both:

- `Tools\ProjectWorkflow.psm1`
- `project.config.json`

If a folder is missing either, Forge will say so rather than guessing.
Projects you've opened before show up in the dropdown next to the button.

## The tabs

**Overview** -- the project's name, pinned branch, and the two buttons
you'll use most: **Sync** (get everyone's latest work) and **Build &
Launch** (compile and open the editor with default options).

**Submodules** -- every submodule this project is built from, nested where
one lives inside another (a plugin inside the game, for example). Each row
shows:

- its state: **Match** (on the pinned commit), **Ahead** (you're mid-work,
  normal), **Behind**/**Diverged**/**Missing** (needs a Sync)
- the branch actually checked out, and `[pinned: X]` if that's different
  from what the team pinned
- a short commit hash

**Switch Branch...** changes the selected submodule to a different branch,
listing what's available on `origin` so you can pick one. If anything
nested inside it is also a submodule, that gets updated too, to whatever
the new branch pins it at. This refuses safely if there's unsaved work in
the way -- nothing is ever discarded.

**Set Watch Branch...** is purely informational: pick a branch to keep an
eye on (it doesn't have to be the pinned branch), and **Check for
Updates** reports whether it has new commits you don't have yet. Nothing
on disk changes from this -- it's a heads-up, not a sync.

**Build** -- lets you sync just a subset of submodules instead of
everything (useful when you know only the engine changed, say), and
exposes the same build options as `Launch.bat` on the command line: no
build, build-only, full rebuild, configuration, and skipping the
pinned-version check.

**Log** -- live output from whatever Forge is currently running. If an
operation refuses to proceed (unsaved work in the way, a missing
compiler, and so on), the exact reason also shows as a red banner at the
top of the window so you don't have to go hunting for it in the log.

**Help** -- this page, plus whatever docs the open project lists in its
own `project.config.json`.

## Adding Forge support to another project

A project becomes Forge-compatible by adding two files at its root:

- `Tools\ProjectWorkflow.psm1` -- copy this from an existing
  Forge-compatible project; it's the same file every project shares, and
  carries no project-specific values itself
- `project.config.json` -- the values that *are* project-specific:
  project title, the editor build target, where the `.uproject` lives,
  which submodules are required, and so on

The submodule list and their branches are **not** repeated in that config
file -- Forge (and the module) read those straight from `.gitmodules`.

### The `Docs` list

`project.config.json` can include a `Docs` array to control what shows up
under "Project Docs" in this Help tab:

```json
"Docs": [
  { "Title": "README", "Path": "README.md", "Type": "markdown" },
  { "Title": "Get Started Guide (PDF)", "Path": "docs/get-started.pdf", "Type": "pdf" }
]
```

`Path` is relative to the project's root. `Type` is `"markdown"` (rendered
right here) or `"pdf"` (opened in your system's PDF viewer via an "Open"
button -- Forge never launches an external program on its own just because
you changed a dropdown selection).
