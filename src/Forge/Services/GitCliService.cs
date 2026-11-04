using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Forge.Models;

namespace Forge.Services;

// Calls git and git-lfs directly via Process, bypassing PowerShellHost on
// purpose: the file-tree/status/lock queries here can run against thousands
// of files, and PowerShell pipeline overhead adds up at that scale in a way
// it never does for the occasional Sync/Build call. ProjectWorkflow.psm1
// (via PowerShellHost) keeps owning workflow logic -- this is a second,
// narrower layer underneath the Files page, not a replacement.
//
// Deliberately not LibGit2Sharp: it has no concept of LFS locks or pointer
// files, which is most of what this service exists to read.
public sealed record LfsLockEntry(string Path, string OwnerName, DateTimeOffset? LockedAt);

public sealed record FileHistoryEntry(string Sha, string AuthorName, DateTimeOffset Date, string Subject)
{
    public string ShortSha => Sha.Length > 8 ? Sha[..8] : Sha;
}

public sealed class GitCliService
{
    private static readonly string[] AttrsToCheck = { "lockable", "filter" };

    public async Task<List<FileEntry>> GetFileStatesAsync(string repoPath, string repoAbsoluteDir, bool includeLocks)
    {
        var tracked = await GetTrackedFilesAsync(repoAbsoluteDir);
        var statusByPath = await GetStatusAsync(repoAbsoluteDir);
        var allPaths = new HashSet<string>(tracked, StringComparer.Ordinal);
        allPaths.UnionWith(statusByPath.Keys);

        var attrsByPath = await GetAttributesAsync(repoAbsoluteDir, allPaths);

        var entries = new List<FileEntry>(allPaths.Count);
        foreach (var path in allPaths)
        {
            var (lockable, isLfs) = attrsByPath.TryGetValue(path, out var a) ? a : (false, false);
            var status = statusByPath.TryGetValue(path, out var s) ? s : FileStatus.Unmodified;
            var entry = new FileEntry
            {
                RepoPath = repoPath,
                RelativePath = path,
                Status = status,
                IsLockable = lockable,
                IsLfsTracked = isLfs
            };
            if (isLfs)
            {
                var fullPath = Path.Combine(repoAbsoluteDir, path.Replace('/', Path.DirectorySeparatorChar));
                entry.IsDownloaded = !IsLfsPointerFile(fullPath);
            }
            entries.Add(entry);
        }

        if (includeLocks)
        {
            var locks = await GetLocksAsync(repoAbsoluteDir);
            if (locks.Count > 0)
            {
                var myName = await GetUserNameAsync(repoAbsoluteDir);
                var byPath = entries.ToDictionary(e => e.RelativePath, StringComparer.Ordinal);
                foreach (var l in locks)
                {
                    if (!byPath.TryGetValue(l.Path, out var entry))
                    {
                        // Locked but not otherwise seen (e.g. not currently
                        // modified) -- still worth surfacing.
                        entry = new FileEntry { RepoPath = repoPath, RelativePath = l.Path, IsLockable = true, IsLfsTracked = true };
                        entries.Add(entry);
                        byPath[l.Path] = entry;
                    }
                    entry.LockOwner = l.OwnerName;
                    entry.IsLockedByMe = !string.IsNullOrEmpty(myName) && string.Equals(l.OwnerName, myName, StringComparison.OrdinalIgnoreCase);
                    entry.LockedAt = l.LockedAt;
                }
            }
        }

        return entries;
    }

    // ---------------------------------------------------------------------

    // --stage (not bare -z) so gitlink entries (mode 160000 -- nested
    // submodules, e.g. ConquestBattleSystem inside Game) can be filtered
    // out. Without this they show up as pseudo-files in the Files tree;
    // they're not real files, and the Submodules tab already represents them
    // properly.
    public async Task<List<string>> GetTrackedFilesAsync(string repoDir)
    {
        var result = await RunGitAsync(repoDir, new[] { "ls-files", "-z", "--stage" });
        var files = new List<string>();
        foreach (var record in SplitNul(result.StdOut))
        {
            // "<mode> <sha> <stage>\t<path>"
            var tab = record.IndexOf('\t');
            if (tab < 0) continue;
            var meta = record[..tab];
            var path = record[(tab + 1)..];
            if (meta.StartsWith("160000 ")) continue; // gitlink / nested submodule
            files.Add(path);
        }
        return files;
    }

    // Parses `git status --porcelain=v2 -z --ignore-submodules=all`. Record
    // types: '1' ordinary, '2' renamed/copied (consumes an extra
    // NUL-separated field for the original path), 'u' unmerged/conflicted,
    // '?' untracked, '!' ignored (skipped -- Forge has no reason to show
    // ignored files here). --ignore-submodules=all matches
    // ProjectWorkflow.psm1's own Get-LocalChanges: a submodule that moved or
    // has local edits would otherwise show up here too, as a one-line change
    // on its gitlink -- it's reported once, by the submodule itself, in the
    // Submodules tab, where the actual files are.
    public async Task<Dictionary<string, FileStatus>> GetStatusAsync(string repoDir)
    {
        var result = await RunGitAsync(repoDir, new[] { "status", "--porcelain=v2", "-z", "--ignore-submodules=all" });
        var fields = SplitNul(result.StdOut).ToArray();
        var map = new Dictionary<string, FileStatus>(StringComparer.Ordinal);

        for (var i = 0; i < fields.Length; i++)
        {
            var record = fields[i];
            if (record.Length == 0) continue;

            switch (record[0])
            {
                case '1':
                {
                    var parts = record.Split(' ', 9);
                    if (parts.Length < 9) break;
                    map[parts[8]] = ClassifyXY(parts[1]);
                    break;
                }
                case '2':
                {
                    var parts = record.Split(' ', 9);
                    i++; // the next NUL field is the original (pre-rename) path; not shown separately in v1
                    if (parts.Length < 9) break;
                    map[parts[8]] = FileStatus.Renamed;
                    break;
                }
                case 'u':
                {
                    var parts = record.Split(' ', 11);
                    if (parts.Length < 11) break;
                    map[parts[10]] = FileStatus.Conflicted;
                    break;
                }
                case '?':
                {
                    var path = record.Length > 2 ? record[2..] : "";
                    if (path.Length > 0) map[path] = FileStatus.New;
                    break;
                }
            }
        }
        return map;
    }

    private static FileStatus ClassifyXY(string xy)
    {
        if (xy.Contains('A')) return FileStatus.New;
        if (xy.Contains('D')) return FileStatus.Deleted;
        return FileStatus.Modified;
    }

    // Checks the "lockable" and "filter" (looking for "lfs") attributes for
    // every given path in one call via --stdin, instead of one process per
    // file.
    public async Task<Dictionary<string, (bool Lockable, bool IsLfs)>> GetAttributesAsync(string repoDir, IEnumerable<string> paths)
    {
        var pathList = paths.ToList();
        var result = new Dictionary<string, (bool, bool)>(StringComparer.Ordinal);
        if (pathList.Count == 0) return result;

        var stdin = string.Join('\0', pathList) + '\0';
        var args = new List<string> { "check-attr" };
        args.AddRange(AttrsToCheck);
        args.Add("-z");
        args.Add("--stdin");

        var proc = await RunGitAsync(repoDir, args, stdin);
        var fields = SplitNul(proc.StdOut).ToArray();

        // Output is flat NUL-separated triples: path, attribute, value, repeating.
        for (var i = 0; i + 2 < fields.Length; i += 3)
        {
            var path = fields[i];
            var attr = fields[i + 1];
            var value = fields[i + 2];
            var (lockable, isLfs) = result.TryGetValue(path, out var existing) ? existing : (false, false);
            if (attr == "lockable" && value == "set") lockable = true;
            if (attr == "filter" && value == "lfs") isLfs = true;
            result[path] = (lockable, isLfs);
        }
        return result;
    }

    public async Task<List<LfsLockEntry>> GetLocksAsync(string repoDir)
    {
        var result = await RunGitAsync(repoDir, new[] { "lfs", "locks", "--json" });
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut)) return new List<LfsLockEntry>();

        try
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            var list = new List<LfsLockEntry>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var path = el.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
                var ownerName = el.TryGetProperty("owner", out var o) && o.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                DateTimeOffset? lockedAt = el.TryGetProperty("locked_at", out var la) && DateTimeOffset.TryParse(la.GetString(), out var dt) ? dt : null;
                if (path.Length > 0) list.Add(new LfsLockEntry(path, ownerName, lockedAt));
            }
            return list;
        }
        catch (JsonException)
        {
            // Older git-lfs or an unexpected format -- treat as "no locks
            // visible" rather than failing the whole file-state refresh.
            return new List<LfsLockEntry>();
        }
    }

    // Only ever locks/unlocks the caller's own lock -- force-unlocking someone
    // else's lock is a deliberately separate, not-yet-designed permission
    // decision (deferred past this phase), so there is no -f/--force here.
    public async Task LockAsync(string repoDir, string relativePath)
    {
        var result = await RunGitAsync(repoDir, new[] { "lfs", "lock", relativePath });
        if (result.ExitCode != 0) throw new InvalidOperationException(FirstNonEmptyLine(result.StdErr, result.StdOut));
    }

    public async Task UnlockAsync(string repoDir, string relativePath)
    {
        var result = await RunGitAsync(repoDir, new[] { "lfs", "unlock", relativePath });
        if (result.ExitCode != 0) throw new InvalidOperationException(FirstNonEmptyLine(result.StdErr, result.StdOut));
    }

    private static string FirstNonEmptyLine(params string[] texts)
    {
        foreach (var text in texts)
        {
            var trimmed = text.Trim();
            if (trimmed.Length > 0) return trimmed.Split('\n')[0].Trim();
        }
        return "git-lfs command failed.";
    }

    public async Task<string> GetHeadShaAsync(string repoDir)
    {
        var result = await RunGitAsync(repoDir, new[] { "rev-parse", "HEAD" });
        if (result.ExitCode != 0) throw new InvalidOperationException(FirstNonEmptyLine(result.StdErr, result.StdOut));
        return result.StdOut.Trim();
    }

    // \x1f (unit separator) splits fields instead of a printable character --
    // a commit subject can contain almost anything except newline/NUL, but
    // never a control character meant for this.
    private const char FieldSeparator = '\x1f';

    public async Task<List<FileHistoryEntry>> GetFileHistoryAsync(string repoDir, string relativePath)
    {
        var result = await RunGitAsync(repoDir, new[]
        {
            "log", "--follow", $"--format=%H{FieldSeparator}%an{FieldSeparator}%aI{FieldSeparator}%s", "--", relativePath
        });
        if (result.ExitCode != 0) throw new InvalidOperationException(FirstNonEmptyLine(result.StdErr, result.StdOut));

        var entries = new List<FileHistoryEntry>();
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(FieldSeparator);
            if (parts.Length < 4 || !DateTimeOffset.TryParse(parts[2], out var date)) continue;
            entries.Add(new FileHistoryEntry(parts[0], parts[1], date, parts[3]));
        }
        return entries;
    }

    // Feeds the commit graph (History page). allBranches=false ->
    // `--first-parent` (the Simple mode: only the mainline's own commits
    // are rows; a merge commit still records its other parents, which
    // CommitGraph.Generate turns into unresolved side-lane stubs since
    // those parents' own commits never appear as rows in this list --
    // exactly the "unmerged branches still visible as side lanes" the spec
    // asks for). allBranches=true -> `--all --topo-order` (Full mode: every
    // commit and merge gets its own row and proper lane).
    public async Task<List<GraphCommit>> GetCommitLogAsync(string repoDir, bool allBranches, int? maxCount = null)
    {
        var args = new List<string> { "log" };
        args.Add(allBranches ? "--all" : "--first-parent");
        if (allBranches) args.Add("--topo-order");
        if (maxCount.HasValue) { args.Add("-n"); args.Add(maxCount.Value.ToString()); }
        args.Add($"--format=%H{FieldSeparator}%P{FieldSeparator}%an{FieldSeparator}%aI{FieldSeparator}%s");

        var result = await RunGitAsync(repoDir, args);
        if (result.ExitCode != 0) throw new InvalidOperationException(FirstNonEmptyLine(result.StdErr, result.StdOut));

        string headSha;
        try { headSha = await GetHeadShaAsync(repoDir); } catch { headSha = ""; }

        var commits = new List<GraphCommit>();
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(FieldSeparator);
            if (parts.Length < 5 || !DateTimeOffset.TryParse(parts[3], out var date)) continue;
            commits.Add(new GraphCommit
            {
                Sha = parts[0],
                Parents = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(),
                AuthorName = parts[2],
                Date = date,
                Subject = parts[4],
                IsCurrentHead = string.Equals(parts[0], headSha, StringComparison.Ordinal)
            });
        }
        return commits;
    }

    public sealed record SubmodulePinChange(string RelativePath, string OldSha, string NewSha);

    // Unified superproject history: a "Bump Game pin" commit on its own just
    // says a pointer moved. This reads what it actually moved FROM and TO --
    // `git diff --raw` on a gitlink shows ":160000 160000 <old> <new> M\t<path>"
    // -- so the caller can then list the real commits via GetCommitRangeAsync.
    // Returns empty for a root commit (no parent to diff against) rather than
    // throwing, since that's an expected case, not a failure.
    public async Task<List<SubmodulePinChange>> GetSubmodulePinChangesAsync(string repoDir, string commitSha)
    {
        // --no-abbrev: git diff --raw abbreviates SHAs by default (commonly
        // to 7 chars), which is ambiguous to paste back into other git
        // commands and breaks anything assuming a full 40-char SHA.
        var result = await RunGitAsync(repoDir, new[] { "diff", "--raw", "--no-abbrev", $"{commitSha}^", commitSha });
        if (result.ExitCode != 0) return new List<SubmodulePinChange>();

        var changes = new List<SubmodulePinChange>();
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length == 0 || line[0] != ':') continue;
            var tab = line.IndexOf('\t');
            if (tab < 0) continue;
            var meta = line[1..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (meta.Length < 4 || meta[0] != "160000" || meta[1] != "160000") continue; // gitlink -> gitlink only
            changes.Add(new SubmodulePinChange(line[(tab + 1)..], meta[2], meta[3]));
        }
        return changes;
    }

    // The real commits a pin bump pulled in, oldest-looking-first omitted --
    // same newest-first order as GetFileHistoryAsync/GetCommitLogAsync, for
    // the same display convention throughout Forge.
    public async Task<List<FileHistoryEntry>> GetCommitRangeAsync(string submoduleDir, string oldSha, string newSha)
    {
        var result = await RunGitAsync(submoduleDir, new[]
        {
            "log", $"--format=%H{FieldSeparator}%an{FieldSeparator}%aI{FieldSeparator}%s", $"{oldSha}..{newSha}"
        });
        if (result.ExitCode != 0) throw new InvalidOperationException(FirstNonEmptyLine(result.StdErr, result.StdOut));

        var entries = new List<FileHistoryEntry>();
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(FieldSeparator);
            if (parts.Length < 4 || !DateTimeOffset.TryParse(parts[2], out var date)) continue;
            entries.Add(new FileHistoryEntry(parts[0], parts[1], date, parts[3]));
        }
        return entries;
    }

    // History holds LFS pointers, not real content, for LFS-tracked files --
    // `git show` alone would extract the ~130-byte pointer text, not the
    // asset. Smudging through `git lfs smudge` resolves it to the real bytes
    // the same way checkout does. Raw byte copy throughout (not the text-
    // based RunGitAsync) since the content can be arbitrary binary.
    public async Task ExtractFileVersionAsync(string repoDir, string revision, string relativePath, string outputFilePath)
    {
        var tempRaw = outputFilePath + ".raw";
        try
        {
            await RunGitRawToFileAsync(repoDir, new[] { "show", $"{revision}:{relativePath}" }, tempRaw);

            if (IsLfsPointerFile(tempRaw))
            {
                using var pointerStream = File.OpenRead(tempRaw);
                await RunGitRawToFileAsync(repoDir, new[] { "lfs", "smudge" }, outputFilePath, pointerStream);
            }
            else
            {
                File.Copy(tempRaw, outputFilePath, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(tempRaw)) File.Delete(tempRaw);
        }
    }

    // Lockable files are checked out read-only until locked -- take the
    // lock first so the checkout below doesn't leave a read-only file the
    // user then can't save over in Unreal. Mirrors the UEGitPlugin's own
    // "Revert" guard (lock before restoring), kept best-effort: a lock
    // failure (e.g. already locked by someone else) still surfaces, but the
    // caller decides whether to proceed with the checkout anyway.
    public async Task CheckoutRevisionAsync(string repoDir, string revision, string relativePath)
    {
        var result = await RunGitAsync(repoDir, new[] { "checkout", revision, "--", relativePath });
        if (result.ExitCode != 0) throw new InvalidOperationException(FirstNonEmptyLine(result.StdErr, result.StdOut));
    }

    // Matches the project's own "Bump superproject Game pin" GitHub Action,
    // which builds its commit message from the pushed commit's own subject
    // line (`git log -1 --format=%s`) rather than a generic placeholder.
    public async Task<string> GetHeadCommitSubjectAsync(string repoDir)
    {
        var result = await RunGitAsync(repoDir, new[] { "log", "-1", "--format=%s", "HEAD" });
        if (result.ExitCode != 0) throw new InvalidOperationException(FirstNonEmptyLine(result.StdErr, result.StdOut));
        return result.StdOut.Trim();
    }

    // `git add -- <paths>` first, THEN a pathspec-restricted commit. The add
    // step is required for brand-new untracked files -- a pathspec-limited
    // `git commit -- <path>` refuses an untracked path with "pathspec did
    // not match any file(s) known to git" (found live, testing Submit on a
    // new file); it only works for paths git already tracks. Both steps are
    // scoped to exactly `paths`, so this still never touches whatever else
    // happens to be staged in the repo at the time (verified against a real
    // nested-submodule scenario, and separately against a new-file-plus-
    // unrelated-staged-change scenario, before trusting this in Submit).
    public async Task CommitPathsAsync(string repoDir, IEnumerable<string> paths, string message)
    {
        var pathList = paths.ToList();

        var addArgs = new List<string> { "add", "--" };
        addArgs.AddRange(pathList);
        var addResult = await RunGitAsync(repoDir, addArgs);
        if (addResult.ExitCode != 0) throw new InvalidOperationException(FirstNonEmptyLine(addResult.StdErr, addResult.StdOut));

        var commitArgs = new List<string> { "commit", "-m", message, "--" };
        commitArgs.AddRange(pathList);
        var commitResult = await RunGitAsync(repoDir, commitArgs);
        if (commitResult.ExitCode != 0) throw new InvalidOperationException(FirstNonEmptyLine(commitResult.StdErr, commitResult.StdOut));
    }

    // -u sets/refreshes the upstream unconditionally -- harmless when one
    // already exists, and handles the "never pushed this branch before"
    // case without needing to detect it first.
    public async Task PushAsync(string repoDir)
    {
        var result = await RunGitAsync(repoDir, new[] { "push", "-u", "origin", "HEAD" });
        if (result.ExitCode != 0) throw new InvalidOperationException(FirstNonEmptyLine(result.StdErr, result.StdOut));
    }

    // Points a submodule gitlink at an arbitrary commit without needing that
    // commit checked out in the working tree -- the same primitive the
    // project's own "Bump superproject Game pin" GitHub Action uses. Caller
    // commits the result with CommitPathsAsync, pathspec-restricted to
    // relativePath, so this never gets bundled with unrelated staged work.
    public async Task BumpSubmodulePinAsync(string parentRepoDir, string relativePath, string newSha)
    {
        var result = await RunGitAsync(parentRepoDir, new[] { "update-index", "--add", "--cacheinfo", $"160000,{newSha},{relativePath}" });
        if (result.ExitCode != 0) throw new InvalidOperationException(FirstNonEmptyLine(result.StdErr, result.StdOut));
    }

    public async Task<string> GetUserNameAsync(string repoDir)
    {
        var result = await RunGitAsync(repoDir, new[] { "config", "--get", "user.name" });
        return result.StdOut.Trim();
    }

    // Shelve ("save for later" -- a friendlier name than "stash", per the
    // spec's own plain-language principle). -u includes untracked files:
    // without it, a new file you were part-way through wouldn't be shelved
    // at all, silently left behind.
    public async Task StashSaveAsync(string repoDir, string message)
    {
        var result = await RunGitAsync(repoDir, new[] { "stash", "push", "-u", "-m", message });
        if (result.ExitCode != 0) throw new InvalidOperationException(FirstNonEmptyLine(result.StdErr, result.StdOut));
    }

    public sealed record StashEntry(string Ref, string Subject, DateTimeOffset Date);

    public async Task<List<StashEntry>> GetStashListAsync(string repoDir)
    {
        var result = await RunGitAsync(repoDir, new[] { "stash", "list", $"--format=%gd{FieldSeparator}%s{FieldSeparator}%cI" });
        if (result.ExitCode != 0) throw new InvalidOperationException(FirstNonEmptyLine(result.StdErr, result.StdOut));

        var entries = new List<StashEntry>();
        foreach (var line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(FieldSeparator);
            if (parts.Length < 3 || !DateTimeOffset.TryParse(parts[2], out var date)) continue;
            entries.Add(new StashEntry(parts[0], parts[1], date));
        }
        return entries;
    }

    public async Task StashPopAsync(string repoDir, string stashRef)
    {
        var result = await RunGitAsync(repoDir, new[] { "stash", "pop", stashRef });
        if (result.ExitCode != 0) throw new InvalidOperationException(FirstNonEmptyLine(result.StdErr, result.StdOut));
    }

    public async Task StashDropAsync(string repoDir, string stashRef)
    {
        var result = await RunGitAsync(repoDir, new[] { "stash", "drop", stashRef });
        if (result.ExitCode != 0) throw new InvalidOperationException(FirstNonEmptyLine(result.StdErr, result.StdOut));
    }

    // Deletes LFS objects from the local cache that no ref/working-copy
    // state still points to -- frees disk space without touching anything
    // actually reachable. Returns git-lfs's own summary line(s) for display.
    public async Task<string> PruneLfsAsync(string repoDir)
    {
        var result = await RunGitAsync(repoDir, new[] { "lfs", "prune" });
        if (result.ExitCode != 0) throw new InvalidOperationException(FirstNonEmptyLine(result.StdErr, result.StdOut));
        return result.StdOut.Trim();
    }

    // Binary conflict helper: a merge conflict on an asset can't be shown as
    // a text diff, so instead of git's raw error, name who last touched each
    // side and when -- "mine" (HEAD) and "theirs" (MERGE_HEAD), each
    // specifically for this path, not just whichever commit the branch tips
    // happen to be at.
    public sealed record ConflictSide(string AuthorName, DateTimeOffset? Date, string Subject);

    public async Task<ConflictSide?> GetConflictSideAsync(string repoDir, string relativePath, string reference)
    {
        var result = await RunGitAsync(repoDir, new[]
        {
            "log", "-1", $"--format=%an{FieldSeparator}%aI{FieldSeparator}%s", reference, "--", relativePath
        });
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut)) return null;

        var parts = result.StdOut.TrimEnd('\n').Split(FieldSeparator);
        if (parts.Length < 3) return null;
        DateTimeOffset? date = DateTimeOffset.TryParse(parts[1], out var d) ? d : null;
        return new ConflictSide(parts[0], date, parts[2]);
    }

    // Resolves a conflicted file by taking one side wholesale -- the only
    // sane option for a binary asset (no line-level merge is possible).
    public async Task ResolveConflictAsync(string repoDir, string relativePath, bool keepOurs)
    {
        var checkoutResult = await RunGitAsync(repoDir, new[] { "checkout", keepOurs ? "--ours" : "--theirs", "--", relativePath });
        if (checkoutResult.ExitCode != 0) throw new InvalidOperationException(FirstNonEmptyLine(checkoutResult.StdErr, checkoutResult.StdOut));

        var addResult = await RunGitAsync(repoDir, new[] { "add", "--", relativePath });
        if (addResult.ExitCode != 0) throw new InvalidOperationException(FirstNonEmptyLine(addResult.StdErr, addResult.StdOut));
    }

    // Reads the "# branch.ab +N -M" header line from `git status --branch`
    // (N = commits ahead of the upstream, M = behind) instead of a separate
    // rev-list call. Returns 0 if the branch has no upstream configured --
    // e.g. a detached HEAD right after Sync, which is normal, not an error.
    public async Task<int> GetUnpushedCountAsync(string repoDir)
    {
        var result = await RunGitAsync(repoDir, new[] { "status", "--porcelain=v2", "--branch", "-z" });
        foreach (var record in SplitNul(result.StdOut))
        {
            if (!record.StartsWith("# branch.ab ")) continue;
            var parts = record.Split(' ');
            if (parts.Length >= 3 && parts[2].StartsWith('+') && int.TryParse(parts[2][1..], out var ahead))
                return ahead;
        }
        return 0;
    }

    // LFS pointer files always start with this exact text (same signature
    // ProjectWorkflow.psm1's build-time check already uses) -- reading a few
    // bytes is far cheaper and more certain than relying on `git lfs
    // ls-files` output formatting.
    private static bool IsLfsPointerFile(string fullPath)
    {
        try
        {
            if (!File.Exists(fullPath)) return false;
            using var fs = File.OpenRead(fullPath);
            var head = new byte[24];
            var read = fs.Read(head, 0, head.Length);
            if (read <= 0) return false;
            var text = Encoding.ASCII.GetString(head, 0, read);
            return text.StartsWith("version https://git-lfs");
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------------

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

    private static async Task<ProcessResult> RunGitAsync(string workingDir, IEnumerable<string> args, string? stdin = null)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        if (stdin != null)
        {
            await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
        }

        await Task.WhenAll(stdOutTask, stdErrTask, process.WaitForExitAsync());
        return new ProcessResult(process.ExitCode, await stdOutTask, await stdErrTask);
    }

    private static IEnumerable<string> SplitNul(string s) =>
        s.Split('\0', StringSplitOptions.RemoveEmptyEntries);

    // Writes the process's raw stdout bytes straight to a file, bypassing
    // RunGitAsync's text decoding entirely -- that path is fine for status/
    // log output, but would corrupt arbitrary binary asset content (what
    // `git show <rev>:<path>` and `git lfs smudge` produce).
    private static async Task RunGitRawToFileAsync(string workingDir, IEnumerable<string> args, string outputFilePath, Stream? stdin = null)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        process.Start();

        Task copyTask;
        await using (var outFile = File.Create(outputFilePath))
        {
            copyTask = process.StandardOutput.BaseStream.CopyToAsync(outFile);
            var stdErrTask = process.StandardError.ReadToEndAsync();

            if (stdin != null)
            {
                await stdin.CopyToAsync(process.StandardInput.BaseStream);
                process.StandardInput.Close();
            }

            await Task.WhenAll(copyTask, stdErrTask, process.WaitForExitAsync());
            if (process.ExitCode != 0)
                throw new InvalidOperationException(FirstNonEmptyLine(await stdErrTask, ""));
        }
    }
}
