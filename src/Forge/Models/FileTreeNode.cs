using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Media;

namespace Forge.Models;

// A folder or file row in the Files page tree. Unlike SubmoduleNode (which
// reconstructs nesting from a handful of items via longest-prefix matching,
// fine for a dozen submodules), this builds a real folder trie from
// potentially thousands of file paths -- prefix matching across every node
// would be O(n^2) and wouldn't synthesize folder nodes for directories that
// aren't themselves tracked.
public sealed class FileTreeNode : INotifyPropertyChanged
{
    public string Name { get; private set; }
    public bool IsFile { get; }
    public FileEntry? File { get; }
    public ObservableCollection<FileTreeNode> Children { get; } = new();

    private readonly Dictionary<string, FileTreeNode> _childIndex = new();

    // Bound TwoWay to TreeViewItem.IsExpanded (FileTreeItemStyle). BuildTree
    // decides the initial value -- forcing every node open regardless of
    // size froze the whole window the moment a few thousand files got
    // realized and laid out in one pass, which is exactly the "the tree
    // doesn't respond to clicks" symptom this was built to avoid.
    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    // Whether this file is checked for the next Submit. Source of truth
    // lives in MainWindow (_checkedFiles, keyed by FileEntry.FullPath) since
    // BuildTree creates fresh node objects on every refresh/filter change --
    // this is just that state reflected into the current tree, and pushed
    // back out via the checkbox's Click handler, not a TwoWay binding (no
    // need for the extra indirection when the handler already has the
    // FileEntry).
    public bool IsChecked { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public FileTreeNode(string name, FileEntry? file = null)
    {
        Name = name;
        IsFile = file != null;
        File = file;
    }

    public string StatusText => IsFile ? File!.Status.ToString() : "";

    public Brush StatusBrush => (IsFile ? File!.Status : FileStatus.Unmodified) switch
    {
        FileStatus.Modified => (Brush)System.Windows.Application.Current.Resources["ForgeWarnBrush"],
        FileStatus.New => (Brush)System.Windows.Application.Current.Resources["ForgeMatchBrush"],
        FileStatus.Deleted => (Brush)System.Windows.Application.Current.Resources["ForgeDangerTextBrush"],
        FileStatus.Conflicted => (Brush)System.Windows.Application.Current.Resources["ForgeDangerTextBrush"],
        FileStatus.Renamed => (Brush)System.Windows.Application.Current.Resources["ForgeWarnBrush"],
        _ => Brushes.Transparent
    };

    public string LockText => File?.LockOwner is { } owner
        ? (File.IsLockedByMe ? "Locked by you" : $"Locked by {owner}") + (File.IsStale ? $" (stale, {(int)(DateTimeOffset.UtcNow - File.LockedAt!.Value).TotalDays}d)" : "")
        : "";

    public string DownloadText => IsFile && File!.IsLfsTracked && !File.IsDownloaded ? "Not downloaded" : "";

    // Groups a flat file list into repo-root nodes, each containing a real
    // folder tree built from '/'-separated RelativePath segments.
    // Above this many files, auto-expanding every folder means WPF has to
    // realize and lay out thousands of TreeViewItems in one go -- that's
    // what froze the window. Below it (the normal case once you've filtered
    // to Changed/Locked/Stale/a search term), opening everything is cheap
    // and genuinely more convenient.
    private const int AutoExpandFileLimit = 300;

    public static List<FileTreeNode> BuildTree(IEnumerable<FileEntry> files, IEnumerable<string> repoDisplayOrder, ISet<string>? checkedPaths = null)
    {
        var fileList = files.ToList();
        var byRepo = fileList.GroupBy(f => f.RepoPath).ToDictionary(g => g.Key, g => g.ToList());
        var roots = new List<FileTreeNode>();
        var autoExpandAll = fileList.Count <= AutoExpandFileLimit;

        foreach (var repoPath in repoDisplayOrder)
        {
            if (!byRepo.TryGetValue(repoPath, out var repoFiles) || repoFiles.Count == 0) continue;

            var repoLabel = string.IsNullOrEmpty(repoPath) ? "(superproject)" : repoPath;
            var repoNode = new FileTreeNode(repoLabel) { IsExpanded = true }; // repo level alone is always cheap
            foreach (var file in repoFiles.OrderBy(f => f.RelativePath, System.StringComparer.OrdinalIgnoreCase))
                InsertFile(repoNode, file, checkedPaths);
            CompactChains(repoNode);
            if (autoExpandAll) ExpandAll(repoNode);
            roots.Add(repoNode);
        }
        return roots;
    }

    private static void ExpandAll(FileTreeNode node)
    {
        node.IsExpanded = true;
        foreach (var child in node.Children) ExpandAll(child);
    }

    // Collapses a run of folders that each hold nothing but a single
    // subfolder into one breadcrumb-style node ("Plugins/ConquestBattleSystem/
    // Content" instead of three separate tree levels to click through) --
    // without this, a typical Unreal content path turns the tree into a deep,
    // mostly-empty staircase before you reach anything worth looking at.
    private static void CompactChains(FileTreeNode node)
    {
        while (node.Children.Count == 1 && !node.Children[0].IsFile)
        {
            var onlyChild = node.Children[0];
            node.Name = $"{node.Name}/{onlyChild.Name}";
            node.Children.Clear();
            foreach (var grandchild in onlyChild.Children)
                node.Children.Add(grandchild);
        }
        foreach (var child in node.Children)
            CompactChains(child);
    }

    private static void InsertFile(FileTreeNode repoNode, FileEntry file, ISet<string>? checkedPaths)
    {
        var segments = file.RelativePath.Split('/', System.StringSplitOptions.RemoveEmptyEntries);
        var current = repoNode;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var folderName = segments[i];
            if (!current._childIndex.TryGetValue(folderName, out var folder))
            {
                folder = new FileTreeNode(folderName);
                current._childIndex[folderName] = folder;
                current.Children.Add(folder);
            }
            current = folder;
        }
        var leaf = new FileTreeNode(segments.Length > 0 ? segments[^1] : file.RelativePath, file)
        {
            IsChecked = checkedPaths != null && checkedPaths.Contains(file.FullPath)
        };
        current.Children.Add(leaf);
    }
}
