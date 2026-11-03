using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Markdig;
using Forge.Models;
using Forge.Services;
using Forge.Views;

namespace Forge;

public partial class MainWindow : Window
{
    private PowerShellHost? _host;
    private string? _projectRoot;
    private ProjectInfo? _projectInfo;
    private LocalSettings _localSettings = new();
    private List<DocEntry> _projectDocs = new();
    private SubmoduleNode? _selectedNode;
    private bool _suppressRecentSelection;

    // Available branches per submodule path, fetched in the background
    // (project open + periodically) so the branch picker opens instantly
    // instead of making the user wait on a git call every time.
    private readonly Dictionary<string, List<string>> _branchCache = new();
    private readonly DispatcherTimer _branchRefreshTimer = new() { Interval = TimeSpan.FromMinutes(15) };
    private bool _branchPrefetchInFlight;

    private readonly Control[] _busyGatedControls;

    public MainWindow()
    {
        InitializeComponent();
        _busyGatedControls = new Control[]
        {
            OpenProjectButton, RecentProjectsCombo,
            QuickSyncButton, QuickBuildButton,
            RefreshSubmodulesButton, SwitchBranchButton, SetWatchBranchButton, CheckUpdatesButton,
            RunSyncButton, RunBuildButton
        };
        _branchRefreshTimer.Tick += async (_, _) => await PrefetchAllBranchesAsync();
        PopulateRecentProjects(null);
    }

    // -----------------------------------------------------------------------
    // Opening a project

    private void OpenProjectButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select a Forge-compatible project folder (containing Tools\\ProjectWorkflow.psm1 and project.config.json)."
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            _ = OpenProjectAsync(dialog.SelectedPath);
    }

    private void RecentProjectsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressRecentSelection) return;
        if (RecentProjectsCombo.SelectedItem is string path && path != _projectRoot)
            _ = OpenProjectAsync(path);
    }

    private async Task OpenProjectAsync(string path)
    {
        if (!ProjectValidator.IsForgeProject(path))
        {
            MessageBox.Show(this,
                $"{path}\n\nThis folder does not contain Tools\\ProjectWorkflow.psm1 and project.config.json, so it is not a Forge-compatible project.",
                "Forge", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetBusy(true);
        HideBanner();
        _branchRefreshTimer.Stop();
        try
        {
            _host?.Dispose();
            _host = new PowerShellHost();
            _host.OutputReceived += Host_OutputReceived;
            _branchCache.Clear();

            await _host.ImportProjectAsync(path);
            _projectInfo = await _host.GetProjectInfoAsync();
            _localSettings = await _host.GetLocalSettingsAsync();
            _projectDocs = await _host.GetProjectDocsAsync();
            _projectRoot = path;

            UpdateOverviewUi();
            await RefreshSubmodulesAsync();
            PopulateHelpDocs();

            AppSettingsService.AddRecent(path);
            PopulateRecentProjects(path);

            MainTabs.IsEnabled = true;
            ProjectTitleText.Text = _projectInfo.Title;

            _ = PrefetchAllBranchesAsync(); // background; doesn't block opening the project
            _branchRefreshTimer.Start();
        }
        catch (ForgeWorkflowException ex)
        {
            ShowBanner(ex.Message);
        }
        catch (ForgeUnexpectedException ex)
        {
            ShowBanner($"Unexpected error: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void PopulateRecentProjects(string? select)
    {
        _suppressRecentSelection = true;
        RecentProjectsCombo.Items.Clear();
        foreach (var p in AppSettingsService.Load().RecentProjects)
            RecentProjectsCombo.Items.Add(p);
        if (select != null) RecentProjectsCombo.SelectedItem = select;
        _suppressRecentSelection = false;
    }

    private void UpdateOverviewUi()
    {
        if (_projectInfo == null) return;
        OverviewTitle.Text = _projectInfo.Title;
        OverviewRoot.Text = _projectInfo.Root;
        OverviewBranch.Text = $"Branch: {_projectInfo.SuperBranch}";
        OverviewEditorTarget.Text = $"Editor target: {_projectInfo.EditorTarget}";
        OverviewUProject.Text = $"Project file: {_projectInfo.UProject}";
        SyncSubmodulesList.ItemsSource = _projectInfo.RequiredSubmodules;
    }

    // -----------------------------------------------------------------------
    // Output / log / failure banner

    private void Host_OutputReceived(object? sender, HostOutputEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            LogText.AppendText(e.Text);
            LogText.ScrollToEnd();
        });
    }

    private void ShowBanner(string message)
    {
        FailBannerText.Text = message;
        FailBanner.Visibility = Visibility.Visible;
    }

    private void HideBanner() => FailBanner.Visibility = Visibility.Collapsed;

    private void DismissBanner_Click(object sender, RoutedEventArgs e) => HideBanner();

    private void ClearLogButton_Click(object sender, RoutedEventArgs e) => LogText.Clear();

    // -----------------------------------------------------------------------
    // Submodules tab

    private async Task RefreshSubmodulesAsync()
    {
        if (_host == null) return;
        var flat = await _host.GetSubmoduleStatesAsync();
        var tree = SubmoduleNode.BuildTree(flat);

        foreach (var node in Flatten(tree))
        {
            if (_localSettings.WatchBranches.TryGetValue(node.State.Path, out var branch))
                node.SetWatchBranch(branch);
        }

        SubmodulesTree.ItemsSource = tree;
    }

    private static IEnumerable<SubmoduleNode> Flatten(IEnumerable<SubmoduleNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children))
                yield return child;
        }
    }

    private async void RefreshSubmodulesButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try { await RefreshSubmodulesAsync(); }
        catch (ForgeWorkflowException ex) { ShowBanner(ex.Message); }
        catch (ForgeUnexpectedException ex) { ShowBanner($"Unexpected error: {ex.Message}"); }
        finally { SetBusy(false); }
    }

    // Fetches the available-branches list for every submodule and caches it,
    // so the branch picker dialog opens instantly instead of making the user
    // wait on a git call. Runs on project open, every 15 minutes, and on
    // demand via "Refresh Branch Lists". Doesn't use SetBusy -- it's meant to
    // run alongside whatever else the user is doing; PowerShellHost queues
    // calls internally so this never races a foreground operation.
    private async Task PrefetchAllBranchesAsync()
    {
        if (_host == null || _branchPrefetchInFlight) return;
        if (SubmodulesTree.ItemsSource is not IEnumerable<SubmoduleNode> roots) return;

        _branchPrefetchInFlight = true;
        BranchRefreshStatusText.Text = "Refreshing branch lists...";
        try
        {
            var host = _host;
            foreach (var node in Flatten(roots).ToList())
            {
                try
                {
                    var branches = await host.GetSubmoduleBranchesAsync(node.State.Path);
                    if (_host != host) return; // project was switched mid-refresh
                    _branchCache[node.State.Path] = branches;
                }
                catch
                {
                    // Best-effort: leave whatever was cached before (or
                    // nothing), the picker falls back to a live fetch.
                }
            }
            BranchRefreshStatusText.Text = $"Branch lists refreshed at {DateTime.Now:t}";
        }
        finally
        {
            _branchPrefetchInFlight = false;
        }
    }

    private async void RefreshBranchListsButton_Click(object sender, RoutedEventArgs e) => await PrefetchAllBranchesAsync();

    // Used by the branch picker dialog's own Refresh button for the one
    // submodule being picked for, rather than the whole project.
    private async Task<List<string>> RefreshBranchesAsync(string path)
    {
        var branches = await _host!.GetSubmoduleBranchesAsync(path);
        _branchCache[path] = branches;
        return branches;
    }

    private async Task<List<string>> GetBranchesForPickerAsync(string path)
    {
        if (_branchCache.TryGetValue(path, out var cached)) return cached;

        // Not warmed yet (first use right after opening a project, before
        // the background prefetch finishes) -- fetch live just this once.
        SetBusy(true);
        try
        {
            var branches = await _host!.GetSubmoduleBranchesAsync(path);
            _branchCache[path] = branches;
            return branches;
        }
        finally { SetBusy(false); }
    }

    private void SubmodulesTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        => _selectedNode = e.NewValue as SubmoduleNode;

    private async void SwitchBranchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode == null)
        {
            MessageBox.Show(this, "Select a submodule in the tree first.", "Forge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var node = _selectedNode;
        List<string> branches;
        try { branches = await GetBranchesForPickerAsync(node.State.Path); }
        catch (ForgeWorkflowException ex) { ShowBanner(ex.Message); return; }
        catch (ForgeUnexpectedException ex) { ShowBanner($"Unexpected error: {ex.Message}"); return; }

        var dialog = new InputDialog("Switch Branch", $"Switch '{node.State.Path}' to which branch?",
            node.State.CurrentBranch ?? "", branches, () => RefreshBranchesAsync(node.State.Path)) { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value)) return;
        var branch = dialog.Value;

        var confirm = MessageBox.Show(this,
            $"This switches '{node.State.Path}' to '{branch}', and updates whatever submodules are nested " +
            "inside it to match what that branch pins them at. This fails safely if there is unsaved work " +
            "in the way. Continue?",
            "Forge", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        MainTabs.SelectedIndex = 3; // Log tab
        SetBusy(true);
        try
        {
            await _host!.SetSubmoduleBranchAsync(node.State.Path, branch);
            await RefreshSubmodulesAsync();
        }
        catch (ForgeWorkflowException ex) { ShowBanner(ex.Message); }
        catch (ForgeUnexpectedException ex) { ShowBanner($"Unexpected error: {ex.Message}"); }
        finally { SetBusy(false); }
    }

    private async void SetWatchBranchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode == null)
        {
            MessageBox.Show(this, "Select a submodule in the tree first.", "Forge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var node = _selectedNode;
        List<string> branches;
        try { branches = await GetBranchesForPickerAsync(node.State.Path); }
        catch (ForgeWorkflowException ex) { ShowBanner(ex.Message); return; }
        catch (ForgeUnexpectedException ex) { ShowBanner($"Unexpected error: {ex.Message}"); return; }

        var dialog = new InputDialog("Set Watch Branch",
            $"Watch which branch of '{node.State.Path}' for new commits? Leave blank to stop watching.",
            node.WatchBranch ?? "", branches, () => RefreshBranchesAsync(node.State.Path)) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        if (string.IsNullOrWhiteSpace(dialog.Value))
            _localSettings.WatchBranches.Remove(node.State.Path);
        else
            _localSettings.WatchBranches[node.State.Path] = dialog.Value;
        node.SetWatchBranch(string.IsNullOrWhiteSpace(dialog.Value) ? null : dialog.Value);

        SetBusy(true);
        try { await _host!.SetLocalSettingsAsync(_localSettings); }
        catch (ForgeWorkflowException ex) { ShowBanner(ex.Message); }
        catch (ForgeUnexpectedException ex) { ShowBanner($"Unexpected error: {ex.Message}"); }
        finally { SetBusy(false); }
    }

    private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_host == null || SubmodulesTree.ItemsSource is not IEnumerable<SubmoduleNode> roots) return;
        var watched = Flatten(roots).Where(n => !string.IsNullOrEmpty(n.WatchBranch)).ToList();
        if (watched.Count == 0)
        {
            MessageBox.Show(this, "No submodule has a watch branch set. Use 'Set Watch Branch...' first.", "Forge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetBusy(true);
        try
        {
            foreach (var node in watched)
            {
                try
                {
                    var status = await _host.GetBranchWatchStatusAsync(node.State.Path, node.WatchBranch!);
                    node.SetWatchStatus(status);
                }
                catch (ForgeWorkflowException ex)
                {
                    node.SetWatchStatus(new BranchWatchStatus { Path = node.State.Path, WatchBranch = node.WatchBranch!, Error = ex.Message });
                }
            }
        }
        finally { SetBusy(false); }
    }

    // -----------------------------------------------------------------------
    // Build tab + Overview quick actions

    private async void QuickSyncButton_Click(object sender, RoutedEventArgs e) => await RunSync(null);

    private async void RunSyncButton_Click(object sender, RoutedEventArgs e)
    {
        IEnumerable<string>? only = null;
        if (SyncSelectedRadio.IsChecked == true)
            only = SyncSubmodulesList.SelectedItems.Cast<string>().ToList();
        await RunSync(only);
    }

    private async Task RunSync(IEnumerable<string>? only)
    {
        MainTabs.SelectedIndex = 3; // Log tab
        SetBusy(true);
        try
        {
            await _host!.InvokeSyncAsync(only);
            await RefreshSubmodulesAsync();
        }
        catch (ForgeWorkflowException ex) { ShowBanner(ex.Message); }
        catch (ForgeUnexpectedException ex) { ShowBanner($"Unexpected error: {ex.Message}"); }
        finally { SetBusy(false); }
    }

    private async void QuickBuildButton_Click(object sender, RoutedEventArgs e) =>
        await RunBuild(noBuild: false, buildOnly: false, rebuild: false, configuration: "Development", skipSyncCheck: false);

    private async void RunBuildButton_Click(object sender, RoutedEventArgs e)
    {
        var configuration = (ConfigurationCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "Development";
        await RunBuild(
            noBuild: NoBuildCheck.IsChecked == true,
            buildOnly: BuildOnlyCheck.IsChecked == true,
            rebuild: RebuildCheck.IsChecked == true,
            configuration: configuration,
            skipSyncCheck: SkipSyncCheckCheck.IsChecked == true);
    }

    private async Task RunBuild(bool noBuild, bool buildOnly, bool rebuild, string configuration, bool skipSyncCheck)
    {
        MainTabs.SelectedIndex = 3; // Log tab
        SetBusy(true);
        try
        {
            await _host!.InvokeBuildAsync(noBuild, buildOnly, rebuild, configuration, skipSyncCheck);
        }
        catch (ForgeWorkflowException ex) { ShowBanner(ex.Message); }
        catch (ForgeUnexpectedException ex) { ShowBanner($"Unexpected error: {ex.Message}"); }
        finally { SetBusy(false); }
    }

    // -----------------------------------------------------------------------
    // Help tab

    // Forge's own docs (docs\*.md next to the exe), shown under "Forge Docs"
    // regardless of which project is open -- distinct from the project's own
    // configurable Docs list, which comes from project.config.json.
    private static readonly DocEntry[] ForgeDocs =
    {
        new() { Title = "Using Forge", Path = "docs/using-forge.md", Type = "markdown", IsBuiltIn = true }
    };

    private sealed class HelpListEntry
    {
        public string DisplayText { get; init; } = "";
        public DocEntry? Doc { get; init; }
        public bool IsHeader { get; init; }
        public FontWeight Weight => IsHeader ? FontWeights.Bold : FontWeights.Normal;
    }

    private void PopulateHelpDocs()
    {
        var items = new List<HelpListEntry>();

        if (_projectDocs.Count > 0)
        {
            items.Add(new HelpListEntry { DisplayText = "— Project Docs —", IsHeader = true });
            items.AddRange(_projectDocs.Select(d => new HelpListEntry { DisplayText = d.Title, Doc = d }));
        }

        items.Add(new HelpListEntry { DisplayText = "— Forge Docs —", IsHeader = true });
        items.AddRange(ForgeDocs.Select(d => new HelpListEntry { DisplayText = d.Title, Doc = d }));

        HelpDocCombo.ItemsSource = items;
        HelpDocCombo.SelectedItem = items.FirstOrDefault(i => !i.IsHeader);
    }

    private async void HelpDocCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HelpDocCombo.SelectedItem is HelpListEntry { IsHeader: true } header)
        {
            var idx = HelpDocCombo.Items.IndexOf(header);
            if (idx >= 0 && idx + 1 < HelpDocCombo.Items.Count) HelpDocCombo.SelectedIndex = idx + 1;
            return; // fires again for the new selection
        }
        await ShowSelectedDocAsync();
    }

    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private bool? _webViewAvailable; // null = not tried yet

    private async Task<bool> EnsureWebViewAsync()
    {
        if (_webViewAvailable.HasValue) return _webViewAvailable.Value;
        try
        {
            // The default user-data folder is next to the exe, which fails
            // once Forge is installed somewhere a normal user can't write to
            // (Program Files). %LocalAppData% is always writable per-user.
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Forge", "WebView2");
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            await HelpWebView.EnsureCoreWebView2Async(env);
            _webViewAvailable = true;
        }
        catch
        {
            // Most likely the WebView2 Runtime isn't installed on this
            // machine. Fall back to plain text rather than leaving the Help
            // tab blank.
            _webViewAvailable = false;
        }
        return _webViewAvailable.Value;
    }

    private async Task ShowSelectedDocAsync()
    {
        var entry = (HelpDocCombo.SelectedItem as HelpListEntry)?.Doc;
        if (entry == null)
        {
            ShowHelpHtml("");
            OpenExternalDocButton.Visibility = Visibility.Collapsed;
            return;
        }

        var baseDir = entry.IsBuiltIn ? AppContext.BaseDirectory : _projectRoot;
        var fullPath = baseDir != null ? Path.Combine(baseDir, entry.Path) : null;

        if (entry.Type == "pdf")
        {
            OpenExternalDocButton.Visibility = Visibility.Visible;
            OpenExternalDocButton.Tag = fullPath;
            ShowHelpText(File.Exists(fullPath)
                ? "This is a PDF. Click \"Open\" above to view it in your system's PDF viewer."
                : $"{fullPath} was not found.");
            return;
        }

        OpenExternalDocButton.Visibility = Visibility.Collapsed;
        if (fullPath == null || !File.Exists(fullPath))
        {
            ShowHelpText($"{entry.Path} was not found.");
            return;
        }

        var markdown = File.ReadAllText(fullPath);
        if (await EnsureWebViewAsync())
        {
            var body = Markdig.Markdown.ToHtml(markdown, MarkdownPipeline);
            ShowHelpHtml(HelpHtmlTemplate.Replace("{{BODY}}", body));
        }
        else
        {
            ShowHelpText(markdown);
        }
    }

    private void ShowHelpHtml(string html)
    {
        HelpWebView.Visibility = Visibility.Visible;
        HelpFallbackText.Visibility = Visibility.Collapsed;
        HelpWebView.NavigateToString(html);
    }

    private void ShowHelpText(string text)
    {
        HelpWebView.Visibility = Visibility.Collapsed;
        HelpFallbackText.Visibility = Visibility.Visible;
        HelpFallbackText.Text = text;
    }

    // Declares color-scheme explicitly with real colors for both modes --
    // without this, Chromium's own "auto dark theme for web content" guesses
    // at inverting an unstyled page and can land on unreadable combinations
    // (dark-on-dark). This way it adapts to Windows' light/dark setting
    // correctly instead of guessing.
    private const string HelpHtmlTemplate = @"<!DOCTYPE html><html><head><meta charset='utf-8'>
        <meta name='color-scheme' content='light dark'>
        <style>
        :root {
            color-scheme: light dark;
            --bg: #ffffff; --fg: #1a1a1a; --border: #ddd; --code-bg: #f0f0f0; --pre-bg: #f5f5f5; --link: #2564cf; --quote: #666;
        }
        @media (prefers-color-scheme: dark) {
            :root { --bg: #1e1e1e; --fg: #dcdcdc; --border: #444; --code-bg: #333333; --pre-bg: #2a2a2a; --link: #6ab0ff; --quote: #aaa; }
        }
        html, body { background: var(--bg); color: var(--fg); }
        body { font-family: Segoe UI, sans-serif; font-size: 14px; line-height: 1.6; max-width: 900px; margin: 16px auto; padding: 0 16px; }
        h1, h2, h3 { border-bottom: 1px solid var(--border); padding-bottom: 4px; }
        code { background: var(--code-bg); padding: 1px 4px; border-radius: 3px; font-family: Consolas, monospace; }
        pre { background: var(--pre-bg); padding: 10px; border-radius: 4px; overflow-x: auto; }
        pre code { background: none; padding: 0; }
        table { border-collapse: collapse; }
        th, td { border: 1px solid var(--border); padding: 4px 8px; }
        a { color: var(--link); }
        blockquote { border-left: 3px solid var(--border); margin-left: 0; padding-left: 12px; color: var(--quote); }
        </style></head><body>{{BODY}}</body></html>";

    private void OpenExternalDocButton_Click(object sender, RoutedEventArgs e)
    {
        if (OpenExternalDocButton.Tag is not string path || !File.Exists(path)) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    // -----------------------------------------------------------------------

    private void SetBusy(bool busy)
    {
        foreach (var control in _busyGatedControls)
            control.IsEnabled = !busy;
    }
}
