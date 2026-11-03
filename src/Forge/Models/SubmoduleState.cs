using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Media;

namespace Forge.Models;

// Mirrors one entry from Get-SubmoduleStates: Path, Pin (commit the parent
// pins), Head (commit actually checked out), State (Match/Ahead/Behind/
// Diverged/Missing).
public sealed class SubmoduleState
{
    public string Path { get; set; } = "";
    public string? Pin { get; set; }
    public string? Head { get; set; }
    public string State { get; set; } = "";

    // The branch actually checked out right now ($null when detached -- the
    // normal state right after a sync, before Sync-SubmoduleBranches
    // re-attaches it) and the branch this path's parent declares for it in
    // its .gitmodules. The two often match but are not the same thing: a
    // developer can be on a feature branch while the pin still names
    // 'development'.
    public string? CurrentBranch { get; set; }
    public string? DeclaredBranch { get; set; }

    public string ShortPin => Short(Pin);
    public string ShortHead => Short(Head);
    private static string Short(string? sha) => string.IsNullOrEmpty(sha) ? "-" : sha[..Math.Min(8, sha.Length)];

    // Split into two lines rather than one long string -- a combined
    // "branch [pinned: X]" line overflows its column for anything but short
    // branch names. The UI renders BranchName and PinnedAnnotation as two
    // separate, differently-styled lines instead.
    public string BranchName => CurrentBranch ?? "(detached)";

    // Empty string rather than null so XAML can bind it directly without a
    // null-to-Visibility converter -- an empty second line just reads blank.
    public string PinnedAnnotation =>
        !string.IsNullOrEmpty(DeclaredBranch) && DeclaredBranch != CurrentBranch
            ? $"pinned: {DeclaredBranch}"
            : "";
}

// A node in the submodule tree the Submodules tab binds to. Get-SubmoduleStates
// returns a flat list where nesting is implied by '/' in Path (e.g.
// "Game/IMGCORE/Plugins/ConquestBattleSystem" is nested under "Game") --
// BuildTree reconstructs the hierarchy from that instead of relying on
// output order.
public sealed class SubmoduleNode : INotifyPropertyChanged
{
    public SubmoduleState State { get; }
    public ObservableCollection<SubmoduleNode> Children { get; } = new();

    public string Name => State.Path.Contains('/') ? State.Path[(State.Path.LastIndexOf('/') + 1)..] : State.Path;

    public string? WatchBranch { get; private set; }
    private int? _watchAhead;
    private string? _watchError;

    public void SetWatchBranch(string? branch)
    {
        WatchBranch = branch;
        _watchAhead = null;
        _watchError = null;
        OnChanged(nameof(WatchBranch));
        OnChanged(nameof(WatchInfo));
    }

    public void SetWatchStatus(BranchWatchStatus status)
    {
        _watchAhead = status.Error == null ? status.Ahead : null;
        _watchError = status.Error;
        OnChanged(nameof(WatchInfo));
    }

    public string WatchInfo
    {
        get
        {
            if (string.IsNullOrEmpty(WatchBranch)) return "";
            if (_watchError != null) return $"watching '{WatchBranch}': {_watchError}";
            if (_watchAhead is > 0) return $"watching '{WatchBranch}': {_watchAhead} new commit(s)";
            if (_watchAhead == 0) return $"watching '{WatchBranch}': up to date";
            return $"watching '{WatchBranch}'";
        }
    }

    public Brush StateBrush => State.State switch
    {
        "Match" => Brushes.SeaGreen,
        "Ahead" => Brushes.DarkOrange,
        "Behind" => Brushes.Firebrick,
        "Diverged" => Brushes.Firebrick,
        "Missing" => Brushes.Gray,
        _ => Brushes.Black
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public SubmoduleNode(SubmoduleState state) => State = state;

    public static List<SubmoduleNode> BuildTree(IEnumerable<SubmoduleState> flat)
    {
        var nodes = flat.Select(s => new SubmoduleNode(s)).ToList();
        var roots = new List<SubmoduleNode>();

        foreach (var node in nodes)
        {
            var parent = nodes
                .Where(n => n != node && node.State.Path.StartsWith(n.State.Path + "/", StringComparison.Ordinal))
                .OrderByDescending(n => n.State.Path.Length)
                .FirstOrDefault();

            if (parent != null) parent.Children.Add(node);
            else roots.Add(node);
        }
        return roots;
    }
}
