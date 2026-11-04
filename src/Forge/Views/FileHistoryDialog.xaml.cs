using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Forge.Services;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace Forge.Views;

// `git log --follow` for one file, with "open", "diff with working copy" and
// "restore" actions per revision. History holds LFS pointers for LFS-tracked
// files, not real content -- GitCliService.ExtractFileVersionAsync resolves
// that via `git lfs smudge` before anything here touches the bytes.
public partial class FileHistoryDialog : FluentWindow
{
    private readonly GitCliService _gitCli;
    private readonly string _repoDir;
    private readonly string _relativePath;
    private readonly string _workingCopyFullPath;
    private readonly string? _uprojectPath;
    private readonly string? _editorExePath;

    // True if Restore actually ran -- the caller (MainWindow) uses this to
    // know whether the Files page needs a refresh after the dialog closes.
    public bool DidRestore { get; private set; }

    public FileHistoryDialog(GitCliService gitCli, string repoDir, string relativePath, string workingCopyFullPath,
        string? uprojectPath, string? editorExePath)
    {
        InitializeComponent();
        _gitCli = gitCli;
        _repoDir = repoDir;
        _relativePath = relativePath;
        _workingCopyFullPath = workingCopyFullPath;
        _uprojectPath = uprojectPath;
        _editorExePath = editorExePath;

        PathText.Text = relativePath;
        Loaded += async (_, _) => await LoadHistoryAsync();
    }

    private async Task LoadHistoryAsync()
    {
        try
        {
            var history = await _gitCli.GetFileHistoryAsync(_repoDir, _relativePath);
            HistoryList.ItemsSource = history;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't load history: {ex.Message}", "Forge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void HistoryList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var hasSelection = HistoryList.SelectedItem is FileHistoryEntry;
        OpenButton.IsEnabled = hasSelection;
        DiffButton.IsEnabled = hasSelection && _uprojectPath != null && _editorExePath != null;
        RestoreButton.IsEnabled = hasSelection;
    }

    private FileHistoryEntry? Selected => HistoryList.SelectedItem as FileHistoryEntry;

    // Named "<basename>_<shortsha><ext>" so opening several old versions
    // side by side doesn't overwrite the same temp file, and the filename
    // alone tells you which revision it is.
    private string TempExtractPath(string shortSha)
    {
        var name = Path.GetFileNameWithoutExtension(_relativePath);
        var ext = Path.GetExtension(_relativePath);
        return Path.Combine(Path.GetTempPath(), "ForgeHistory", $"{name}_{shortSha}{ext}");
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } entry) return;
        try
        {
            var outPath = TempExtractPath(entry.ShortSha);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            await _gitCli.ExtractFileVersionAsync(_repoDir, entry.Sha, _relativePath, outPath);
            Process.Start(new ProcessStartInfo(outPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't open that version: {ex.Message}", "Forge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Diff_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } entry || _uprojectPath == null || _editorExePath == null) return;
        if (!File.Exists(_editorExePath))
        {
            MessageBox.Show(this, $"Editor not found at:\n{_editorExePath}", "Forge", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var oldPath = TempExtractPath(entry.ShortSha);
            Directory.CreateDirectory(Path.GetDirectoryName(oldPath)!);
            await _gitCli.ExtractFileVersionAsync(_repoDir, entry.Sha, _relativePath, oldPath);

            var psi = new ProcessStartInfo(_editorExePath)
            {
                UseShellExecute = false
            };
            psi.ArgumentList.Add(_uprojectPath);
            psi.ArgumentList.Add("-diff");
            psi.ArgumentList.Add(oldPath);
            psi.ArgumentList.Add(_workingCopyFullPath);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't launch the diff: {ex.Message}", "Forge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } entry) return;

        var confirm = MessageBox.Show(this,
            $"This replaces the current working copy of '{_relativePath}' with the version from {entry.ShortSha} " +
            $"({entry.Date:g}, \"{entry.Subject}\"). Continue?",
            "Forge", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            // Best-effort: a lockable file checks out read-only until locked.
            // If someone else already holds the lock, let the real LFS error
            // surface rather than silently restoring over their in-progress work.
            try { await _gitCli.LockAsync(_repoDir, _relativePath); }
            catch (Exception lockEx) when (!lockEx.Message.Contains("already locked by you", StringComparison.OrdinalIgnoreCase))
            {
                var proceed = MessageBox.Show(this,
                    $"Couldn't lock this file first: {lockEx.Message}\n\nRestore anyway?",
                    "Forge", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (proceed != MessageBoxResult.Yes) return;
            }

            await _gitCli.CheckoutRevisionAsync(_repoDir, entry.Sha, _relativePath);
            DidRestore = true;
            MessageBox.Show(this, "Restored.", "Forge", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't restore that version: {ex.Message}", "Forge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
