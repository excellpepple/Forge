using System;
using System.Collections.Generic;

namespace Forge.Models;

// A snapshot of the last successful project load, written to
// Tools/.workflow-cache.json (gitignored, sibling to .workflow-local.json)
// so the next open can paint the UI immediately from this instead of
// sitting blank/disabled while PowerShell imports the module and git scans
// every repo. OpenProjectAsync always kicks off the real refresh right
// after painting this, and overwrites the file with current data when that
// finishes -- this is a fast first paint, never the source of truth.
public sealed class ProjectCache
{
    public ProjectInfo? ProjectInfo { get; set; }
    public List<SubmoduleState> SubmoduleStates { get; set; } = new();
    public List<FileEntry> Files { get; set; } = new();
    public DateTimeOffset CachedAt { get; set; }
}
