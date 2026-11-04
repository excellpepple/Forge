using System;
using System.Collections.Generic;

namespace Forge.Models;

// One row of the commit graph (History page). Separate from FileHistoryEntry
// (that's `git log --follow` for a single file; this is `git log` for the
// whole repo, feeding CommitGraph's lane layout).
public sealed class GraphCommit
{
    public string Sha { get; set; } = "";
    public List<string> Parents { get; set; } = new();
    public string Subject { get; set; } = "";
    public string AuthorName { get; set; } = "";
    public DateTimeOffset Date { get; set; }
    public bool IsCurrentHead { get; set; }

    public string ShortSha => Sha.Length > 8 ? Sha[..8] : Sha;

    // Filled in by CommitGraph.Generate.
    public int Color { get; set; }
    public double LeftMargin { get; set; }
}
