using System.Collections.Generic;

namespace Forge.Models;

// Mirrors Tools\.workflow-local.json (per-checkout, gitignored). Keyed by
// submodule path, e.g. { "UnrealEngine": "5.8", "Game": "development" }.
public sealed class LocalSettings
{
    public Dictionary<string, string> WatchBranches { get; set; } = new();

    // Tri-state: null = not yet decided, auto-detect from the project's
    // LfsRepos (on if non-empty); true/false = the user explicitly overrode
    // the auto-detected value in Settings > General. Gates everything
    // LFS-lock-specific (lock columns, lock panel, lock-aware submit, LFS
    // housekeeping) without touching file-tree/status features that don't
    // need LFS at all.
    public bool? EnableLfsLocking { get; set; }

    // Files (by FileEntry.FullPath) the user asked to be notified about the
    // next time their lock frees up. One-shot: removed once the notification
    // fires (see MainWindow.CheckNotifyOnUnlockAsync).
    public List<string> NotifyOnUnlock { get; set; } = new();
}
