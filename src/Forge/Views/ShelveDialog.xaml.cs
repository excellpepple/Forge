using System;
using System.Linq;
using System.Windows;
using Forge.Services;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace Forge.Views;

// Shelve = a friendlier name for `git stash`, per the spec's "plain
// language, not Git terms" principle. Offered standalone here rather than
// only inline during Sync -- same reasoning as Submit's own dialog: show
// what's there, let the user act, don't guess for them.
public partial class ShelveDialog : FluentWindow
{
    private readonly GitCliService _gitCli;
    private readonly string _repoDir;
    private readonly string _repoLabel;

    // True if anything was popped -- the caller (MainWindow) uses this to
    // know whether the Files/Submodules pages need a refresh.
    public bool DidRestore { get; private set; }

    public ShelveDialog(GitCliService gitCli, string repoDir, string repoLabel)
    {
        InitializeComponent();
        _gitCli = gitCli;
        _repoDir = repoDir;
        _repoLabel = repoLabel;
        RepoText.Text = repoLabel;

        Loaded += async (_, _) => await LoadAsync();
    }

    private async System.Threading.Tasks.Task LoadAsync()
    {
        try { StashList.ItemsSource = await _gitCli.GetStashListAsync(_repoDir); }
        catch (Exception ex) { MessageBox.Show(this, $"Couldn't load shelved changes: {ex.Message}", "Forge", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var message = ShelveMessageBox.Text.Trim();
        if (message.Length == 0)
        {
            MessageBox.Show(this, "Enter a short note first.", "Forge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            await _gitCli.StashSaveAsync(_repoDir, message);
            ShelveMessageBox.Clear();
            await LoadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't shelve: {ex.Message}", "Forge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: GitCliService.StashEntry entry }) return;
        var confirm = MessageBox.Show(this,
            $"Restore \"{entry.Subject}\" over the current working copy of {_repoLabel}?",
            "Forge", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await _gitCli.StashPopAsync(_repoDir, entry.Ref);
            DidRestore = true;
            await LoadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't restore: {ex.Message}", "Forge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Drop_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: GitCliService.StashEntry entry }) return;
        var confirm = MessageBox.Show(this,
            $"Permanently discard \"{entry.Subject}\"? This can't be undone.",
            "Forge", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await _gitCli.StashDropAsync(_repoDir, entry.Ref);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't drop: {ex.Message}", "Forge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
