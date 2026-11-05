# Using Forge

Forge is a desktop front end for a project's own `Tools\ProjectWorkflow.psm1`
script -- the same engine/git logic behind `Setup.bat`, `Sync.bat` and
`Launch.bat`. Nothing Forge does is different from what those scripts do; it
just gives it a UI, a submodule tree, a file browser with locking, and a
few extras those scripts don't offer on the command line.

For how to open or register a project, and how to customize Settings, see
**Projects & Settings** (next to this doc in the list on the left).

## The sidebar

**Overview**, **Submodules**, **Files**, **History**, **Build** and **Log**
are only enabled once a project is open. **Help** and **Settings** are
always available. A status bar along the bottom of the window shows what
Forge is currently doing (if anything) and a progress indicator -- buttons
stay clickable while something is running; you don't have to wait for one
operation to finish before starting to look around or queue up the next.

## Overview

The project's title, pinned branch, and the two buttons you'll use most:
**Sync** (get everyone's latest work) and **Build & Launch** (compile and
open the editor with default options).

## Submodules

Every submodule this project is built from, nested where one lives inside
another (a plugin inside the game, for example). Each row shows:

- its state: **Match** (on the pinned commit), **Ahead** (you're mid-work,
  normal), **Behind**/**Diverged**/**Missing** (needs a Sync)
- the branch actually checked out, and a second line reading
  `pinned: X` when that differs from what the team pinned
- a short commit hash

Right-click a row for:

- **Switch Branch...** -- change this submodule to a different branch,
  listing what's available on `origin`. Anything nested inside it that's
  also a submodule gets updated too, to whatever the new branch pins it at.
  Refuses safely if there's unsaved work in the way -- nothing is ever
  discarded.
- **Set Watch Branch...** -- purely informational: pick a branch to keep an
  eye on (it doesn't have to be the pinned branch). **Check for Updates**
  then reports whether it has new commits you don't have yet. Nothing on
  disk changes -- it's a heads-up, not a sync.
- **Shelve...** -- stash this submodule's uncommitted changes out of the
  way (e.g. to switch branches cleanly, or hand the machine to someone
  else temporarily) and restore them later from the same menu.
- **Open in External Git Tool...** -- hands this repo off to whatever git
  GUI you have installed, for anything Forge doesn't cover itself.

## Files

Every tracked file across every repo (except the engine, which is too large
to scan this way), as one tree. Each row shows a folder/file icon, the
file's status as a small colored pill when it has one (Modified/New/
Deleted/Conflicted/Renamed), who holds a lock on it if anyone, and whether
it's an LFS file that hasn't downloaded yet.

Toolbar filters: **All** / **Changed** / **Locked** / **Not downloaded** /
**Stale locks**, plus a search box that filters by file or folder name.

Check the box next to a file to include it in your next **Submit**. Select
a file (or right-click it) for:

- **Lock** / **Unlock** -- claim or release an LFS lock so only you can
  check in changes to it. Requires the LFS locking feature flag (Settings →
  General).
- **Notify Me When Unlocked** -- get an in-app notification the next time
  someone else's lock on this file is released.
- **History...** -- every commit that touched this file: browse past
  versions, see a visual diff between any two, and restore an old version
  into your working copy.
- **Resolve Conflict...** -- when the same asset changed on both sides, pick
  which version to keep (yours or theirs).
- **Submit...** -- commit every checked file, push, release their locks,
  and bump every ancestor submodule's pin up through the superproject in
  one step. This is the one operation that replaces a manual
  commit/push/pin-bump chain across multiple repos.

## History

The commit graph for one repo at a time (pick which from the dropdown),
**Simple** or **Full** detail. Double-click a commit that bumped a
submodule's pin to see the *real* commits that pin bump pulled in, instead
of just "bumped pin to abc1234" -- this is what makes the superproject's own
history actually readable across repo boundaries.

## Build

**Sync** -- sync everything (same as the Overview tab's Sync button), or
just a chosen subset of top-level submodules (useful when you know only one
piece changed, like the engine).

**Build** -- the same options `Launch.bat` exposes on the command line: skip
building and just launch the editor, build without launching afterward, a
full rebuild (slow -- discards incremental progress, normally unnecessary),
which configuration (Development/DebugGame/Debug), and skipping the
pinned-version check (for deliberately building an unpinned combination).
Starting a build switches you to the **Log** tab to watch it; a **Cancel
Build** button appears there while one is running -- it force-stops the
compiler. A canceled build is safe to leave half-finished: running Build
again just resumes the incremental build from where it stopped.

**Housekeeping...** -- see how much space `DerivedDataCache`/`Intermediate`
are using, clean them, and prune the local LFS cache.

## Log

Live output from whatever Forge is currently running, color-coded by what
kind of line it is (a step header, a success, a warning, an error, or
just informational detail) and filterable by **All** / **Warnings** /
**Errors**, plus a search box. If an operation refuses to proceed (unsaved
work in the way, a missing compiler, and so on), the exact reason also
shows as a red banner at the top of the window so you don't have to go
hunting for it in the log.

## Help

This page, plus whatever docs the open project lists in its own
`project.config.json` (shown under "Project Docs" above "Forge Docs" in
the dropdown, when there are any).
