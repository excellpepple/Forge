using System;
using System.ComponentModel;
using System.Collections.Generic;
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

// Housekeeping: disk usage for Unreal's own build caches (safe to delete --
// the editor regenerates them, just slowly, on next open) plus a `git lfs
// prune` for the local LFS object cache.
public partial class HousekeepingDialog : FluentWindow
{
    public sealed class FolderEntry : INotifyPropertyChanged
    {
        public string Name { get; }
        public string Path { get; }

        private string _sizeText = "Calculating...";
        public string SizeText
        {
            get => _sizeText;
            set { _sizeText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeText))); }
        }

        private bool _canClean;
        public bool CanClean
        {
            get => _canClean;
            set { _canClean = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanClean))); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public FolderEntry(string name, string path)
        {
            Name = name;
            Path = path;
        }
    }

    private readonly GitCliService _gitCli;
    private readonly List<string> _lfsRepoDirs;
    private readonly List<FolderEntry> _folders;

    public HousekeepingDialog(GitCliService gitCli, string projectDir, IEnumerable<string> lfsRepoDirs)
    {
        InitializeComponent();
        _gitCli = gitCli;
        _lfsRepoDirs = lfsRepoDirs.ToList();

        _folders = new List<FolderEntry>
        {
            new("DerivedDataCache", System.IO.Path.Combine(projectDir, "DerivedDataCache")),
            new("Intermediate", System.IO.Path.Combine(projectDir, "Intermediate"))
        };
        FoldersList.ItemsSource = _folders;

        Loaded += async (_, _) => await CalculateSizesAsync();
    }

    private async Task CalculateSizesAsync()
    {
        foreach (var folder in _folders)
        {
            if (!Directory.Exists(folder.Path))
            {
                folder.SizeText = "Not present";
                folder.CanClean = false;
                continue;
            }

            try
            {
                var (bytes, fileCount) = await Task.Run(() => MeasureFolder(folder.Path));
                folder.SizeText = $"{FormatSize(bytes)} ({fileCount:N0} file(s))";
                folder.CanClean = true;
            }
            catch (Exception ex)
            {
                folder.SizeText = $"Couldn't measure: {ex.Message}";
                folder.CanClean = false;
            }
        }
    }

    private static (long Bytes, int FileCount) MeasureFolder(string path)
    {
        long bytes = 0;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                bytes += new FileInfo(file).Length;
                count++;
            }
            catch
            {
                // A file that vanished mid-scan (e.g. the editor is running
                // and touching its own cache) shouldn't abort the whole count.
            }
        }
        return (bytes, count);
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.#} {units[unit]}";
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: FolderEntry folder }) return;

        var confirm = MessageBox.Show(this,
            $"Permanently delete '{folder.Name}' ({folder.SizeText})? Unreal rebuilds it automatically, just slowly, next time it's needed.",
            "Forge", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        folder.CanClean = false;
        folder.SizeText = "Deleting...";
        try
        {
            await Task.Run(() => Directory.Delete(folder.Path, recursive: true));
            folder.SizeText = "Not present";
        }
        catch (Exception ex)
        {
            folder.SizeText = $"Couldn't delete: {ex.Message}";
            folder.CanClean = true;
        }
    }

    private async void Prune_Click(object sender, RoutedEventArgs e)
    {
        PruneResultText.Text = "Pruning...";
        try
        {
            var results = new List<string>();
            foreach (var repoDir in _lfsRepoDirs)
            {
                try { results.Add(await _gitCli.PruneLfsAsync(repoDir)); }
                catch (Exception ex) { results.Add($"{System.IO.Path.GetFileName(repoDir)}: {ex.Message}"); }
            }
            PruneResultText.Text = results.Count == 0 ? "Nothing to prune." : string.Join("\n", results.Where(r => r.Length > 0));
            if (PruneResultText.Text.Length == 0) PruneResultText.Text = "Done -- nothing needed pruning.";
        }
        catch (Exception ex)
        {
            PruneResultText.Text = $"Couldn't prune: {ex.Message}";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
