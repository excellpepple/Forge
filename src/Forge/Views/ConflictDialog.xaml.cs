using System;
using System.Windows;
using Forge.Services;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace Forge.Views;

// Binary conflict helper: "keep mine" / "keep theirs" with who-and-when
// instead of git's raw conflict error, since a binary asset can't be
// line-merged anyway -- the spec's own framing.
public partial class ConflictDialog : FluentWindow
{
    private readonly GitCliService _gitCli;
    private readonly string _repoDir;
    private readonly string _relativePath;

    // True once a side was actually kept -- the caller (MainWindow) uses
    // this to know whether the Files page needs a refresh.
    public bool Resolved { get; private set; }

    public ConflictDialog(GitCliService gitCli, string repoDir, string relativePath)
    {
        InitializeComponent();
        _gitCli = gitCli;
        _repoDir = repoDir;
        _relativePath = relativePath;
        PathText.Text = relativePath;

        Loaded += async (_, _) => await LoadSidesAsync();
    }

    private async System.Threading.Tasks.Task LoadSidesAsync()
    {
        OursText.Text = await DescribeSideAsync("HEAD");
        TheirsText.Text = await DescribeSideAsync("MERGE_HEAD");
    }

    private async System.Threading.Tasks.Task<string> DescribeSideAsync(string reference)
    {
        try
        {
            var side = await _gitCli.GetConflictSideAsync(_repoDir, _relativePath, reference);
            return side == null
                ? "No history for this side."
                : $"{side.AuthorName}, {(side.Date.HasValue ? side.Date.Value.ToString("g") : "unknown time")} -- \"{side.Subject}\"";
        }
        catch (Exception ex)
        {
            return $"Couldn't read: {ex.Message}";
        }
    }

    private async void KeepMine_Click(object sender, RoutedEventArgs e) => await Resolve(keepOurs: true);
    private async void KeepTheirs_Click(object sender, RoutedEventArgs e) => await Resolve(keepOurs: false);

    private async System.Threading.Tasks.Task Resolve(bool keepOurs)
    {
        try
        {
            await _gitCli.ResolveConflictAsync(_repoDir, _relativePath, keepOurs);
            Resolved = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't resolve: {ex.Message}", "Forge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
