using System;

namespace Forge.Models;

public enum FileStatus { Unmodified, Modified, New, Deleted, Renamed, Conflicted }

// One tracked (or untracked-but-present) file, aggregated across every git
// command in the "File tree with states" data model: ls-files, status,
// check-attr lockable, lfs locks, lfs ls-files. RepoPath is the submodule's
// path ("" for the superproject itself, matching how SubmoduleState and the
// rest of Forge already identify repos), RelativePath is the file's path
// within that repo.
public sealed class FileEntry
{
    public string RepoPath { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public FileStatus Status { get; set; } = FileStatus.Unmodified;

    public bool IsLockable { get; set; }
    public string? LockOwner { get; set; } // null = not locked
    public bool IsLockedByMe { get; set; }
    public DateTimeOffset? LockedAt { get; set; }

    public bool IsLfsTracked { get; set; }
    public bool IsDownloaded { get; set; } = true; // only meaningful when IsLfsTracked

    public string FullPath => string.IsNullOrEmpty(RepoPath) ? RelativePath : $"{RepoPath}/{RelativePath}";

    // A lock held this long without being released is probably forgotten
    // rather than active work -- flagged so someone can follow up, never
    // auto-released (force-unlock is a deliberately separate, not-yet-built
    // permission decision).
    public static readonly TimeSpan StaleLockThreshold = TimeSpan.FromDays(7);

    public bool IsStale => LockedAt.HasValue && DateTimeOffset.UtcNow - LockedAt.Value > StaleLockThreshold;
}
