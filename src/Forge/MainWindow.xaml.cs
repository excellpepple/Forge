using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Markdig;
using Forge.Models;
using Forge.Services;
using Forge.Views;
using Wpf.Ui.Controls;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using TreeViewItem = System.Windows.Controls.TreeViewItem;

namespace Forge;

public partial class MainWindow : FluentWindow
{
    private PowerShellHost? _host;
    private string? _projectRoot;
    private ProjectInfo? _projectInfo;
    private LocalSettings _localSettings = new();
    private List<DocEntry> _projectDocs = new();
    private SubmoduleNode? _selectedNode;
    private bool _suppressRecentSelection;

    private readonly ThemeService _theme;

    // Available branches per submodule path, fetched in the background
    // (project open + periodically) so the branch picker opens instantly
    // instead of making the user wait on a git call every time.
    private readonly Dictionary<string, List<string>> _branchCache = new();
    private readonly DispatcherTimer _branchRefreshTimer = new() { Interval = TimeSpan.FromMinutes(15) };
    private bool _branchPrefetchInFlight;

    private readonly Control[] _busyGatedControls;
    private readonly Dictionary<string, Grid> _pages;
    private readonly Dictionary<string, Button> _navButtons;
    private string? _currentPage;

    public MainWindow()
    {
        InitializeComponent();

        _theme = new ThemeService(this);
        _theme.ApplyAndWatch();

        _pages = new Dictionary<string, Grid>
        {
            ["Overview"] = OverviewPage,
            ["Submodules"] = SubmodulesPage,
            ["Build"] = BuildPage,
            ["Log"] = LogPage,
            ["Help"] = HelpPage,
            ["Settings"] = SettingsPage
        };
        _navButtons = new Dictionary<string, Button>
        {
            ["Overview"] = OverviewNavButton,
            ["Submodules"] = SubmodulesNavButton,
            ["Build"] = BuildNavButton,
            ["Log"] = LogNavButton,
            ["Help"] = HelpNavButton,
            ["Settings"] = SettingsNavButton
        };

        _busyGatedControls = new Control[]
        {
            OpenProjectButton, RecentProjectsCombo,
            QuickSyncButton, QuickBuildButton,
            RefreshSubmodulesButton, SwitchBranchButton, SetWatchBranchButton, CheckUpdatesButton,
            RunSyncButton, RunBuildButton
        };
        _branchRefreshTimer.Tick += async (_, _) => await PrefetchAllBranchesAsync();
        PopulateRecentProjects(null);

        InitializeThemePickers();

        // Forge's own docs should be readable before any project is open --
        // only the Overview/Submodules/Build/Log tabs need a loaded project.
        PopulateHelpDocs();
        ShowPage("Help");

        _ = AutoReopenLastProjectAsync();
    }

    // -----------------------------------------------------------------------
    // Navigation (replaces a TabControl: plain buttons + Grid visibility, so
    // the sidebar can hold a project switcher above the nav list without
    // fighting TabControl's own header/content layout model)

    private void ShowPage(string key)
    {
        foreach (var (name, grid) in _pages)
            grid.Visibility = name == key ? Visibility.Visible : Visibility.Collapsed;

        foreach (var (name, button) in _navButtons)
        {
            var isActive = name == key;
            var icon = (button.Content as StackPanel)?.Children.OfType<SymbolIcon>().FirstOrDefault();
            var label = (button.Content as StackPanel)?.Children.OfType<TextBlock>().FirstOrDefault();
            if (isActive)
            {
                button.SetResourceReference(BackgroundProperty, "ForgeAccentSoftBrush");
                icon?.SetResourceReference(ForegroundProperty, "ForgeAccentBrush");
                label?.SetResourceReference(ForegroundProperty, "ForgeAccentBrush");
                if (label != null) label.FontWeight = FontWeights.Medium;
            }
            else
            {
                button.ClearValue(BackgroundProperty); // back to the style's Transparent default
                icon?.SetResourceReference(ForegroundProperty, "ForgeIconBrush");
                label?.SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
                if (label != null) label.FontWeight = FontWeights.Normal;
            }
        }

        _currentPage = key;
    }

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string key }) ShowPage(key);
    }

    private void SetProjectNavEnabled(bool enabled) =>
        OverviewNavButton.IsEnabled = SubmodulesNavButton.IsEnabled = BuildNavButton.IsEnabled = LogNavButton.IsEnabled = enabled;

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

    // Reopens whatever project was open last time Forge ran, so the user
    // doesn't have to re-pick it every launch. Silent on failure (missing
    // folder, moved repo, etc.) -- the user can still open one manually.
    private async Task AutoReopenLastProjectAsync()
    {
        var last = AppSettingsService.Load().RecentProjects.FirstOrDefault();
        if (last != null && ProjectValidator.IsForgeProject(last))
            await OpenProjectAsync(last);
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

            SetProjectNavEnabled(true);
            ShowPage("Overview");
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
    // Theme (Settings page)

    private static readonly string[] ColorPresets = { "#D9681F", "#B8860B", "#A3333D", "#3B6EA5", "#1F8A70", "#B5562E" };
    private static readonly string[] LightBgPresets = { "#F7F5F2", "#F2F3F5", "#FFFFFF" };
    private static readonly string[] DarkBgPresets = { "#121212", "#1C1A17", "#15181C" };

    private void InitializeThemePickers()
    {
        var s = _theme.Settings;

        LightAccentPicker.Initialize(ColorPresets, s.LightAccent);
        LightAccentPicker.ColorPicked += hex => _theme.SetLightAccent(hex);
        LightBackgroundPicker.Initialize(LightBgPresets, s.LightBackground);
        LightBackgroundPicker.ColorPicked += hex => _theme.SetLightBackground(hex);
        LightIconPicker.Initialize(ColorPresets, s.LightIcon);
        LightIconPicker.ColorPicked += hex => _theme.SetLightIcon(hex);

        DarkAccentPicker.Initialize(ColorPresets, s.DarkAccent);
        DarkAccentPicker.ColorPicked += hex => _theme.SetDarkAccent(hex);
        DarkBackgroundPicker.Initialize(DarkBgPresets, s.DarkBackground);
        DarkBackgroundPicker.ColorPicked += hex => _theme.SetDarkBackground(hex);
        DarkIconPicker.Initialize(ColorPresets, s.DarkIcon);
        DarkIconPicker.ColorPicked += hex => _theme.SetDarkIcon(hex);

        UpdateModeButtons();
    }

    private void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        var mode = tag switch { "Light" => ThemeMode.Light, "Dark" => ThemeMode.Dark, _ => ThemeMode.System };
        _theme.SetMode(mode);
        UpdateModeButtons();
    }

    private void UpdateModeButtons()
    {
        var buttons = new (Button Button, ThemeMode Mode)[]
        {
            (ModeLightButton, ThemeMode.Light), (ModeDarkButton, ThemeMode.Dark), (ModeSystemButton, ThemeMode.System)
        };
        foreach (var (button, mode) in buttons)
        {
            var isActive = _theme.Settings.Mode == mode;
            if (isActive) button.SetResourceReference(BackgroundProperty, "ForgeAccentSoftBrush");
            else button.ClearValue(BackgroundProperty);
            button.SetResourceReference(ForegroundProperty, isActive ? "ForgeAccentBrush" : "TextFillColorPrimaryBrush");
            button.SetResourceReference(Control.BorderBrushProperty, isActive ? "ForgeAccentBrush" : "ControlElevationBorderBrush");
        }
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

    // TreeViewItem doesn't select on right-click the way it does on left-click,
    // so without this, right-clicking a node the user hadn't already
    // left-clicked would pop the context menu for whatever was selected
    // before -- the wrong submodule. This is the bubbling MouseRightButtonDown,
    // not the tunneling PreviewMouseRightButtonDown: bubbling starts at the
    // actual clicked (innermost) item and works outward, so that item's
    // handler runs -- and marks Handled -- before the event ever reaches a
    // parent's handler. The tunneling Preview version runs parent-first,
    // which is why a nested submodule's right-click was selecting its parent.
    private void SubmoduleTreeItem_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TreeViewItem item) return;
        item.Focus();
        item.IsSelected = true;
        e.Handled = true;
    }

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

        ShowPage("Log");
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
        ShowPage("Log");
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
        ShowPage("Log");
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
            ShowHelpHtml(BuildHelpHtml(body));
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

    // Matches whichever mode Forge itself is currently showing (including an
    // explicit Light/Dark override, not just the OS preference) instead of
    // guessing via prefers-color-scheme -- that guess is what produced the
    // original dark-on-dark bug.
    private string BuildHelpHtml(string body)
    {
        var dark = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme() == Wpf.Ui.Appearance.ApplicationTheme.Dark;
        var (bg, fg, border, codeBg, preBg, link, quote) = dark
            ? ("#1e1e1e", "#dcdcdc", "#444", "#333333", "#2a2a2a", "#6ab0ff", "#aaa")
            : ("#ffffff", "#1a1a1a", "#ddd", "#f0f0f0", "#f5f5f5", "#2564cf", "#666");

        return $@"<!DOCTYPE html><html><head><meta charset='utf-8'><style>
            html, body {{ background: {bg}; color: {fg}; }}
            body {{ font-family: Segoe UI, sans-serif; font-size: 14px; line-height: 1.6; max-width: 900px; margin: 16px auto; padding: 0 16px; }}
            h1, h2, h3 {{ border-bottom: 1px solid {border}; padding-bottom: 4px; }}
            code {{ background: {codeBg}; padding: 1px 4px; border-radius: 3px; font-family: Consolas, monospace; }}
            pre {{ background: {preBg}; padding: 10px; border-radius: 4px; overflow-x: auto; }}
            pre code {{ background: none; padding: 0; }}
            table {{ border-collapse: collapse; }}
            th, td {{ border: 1px solid {border}; padding: 4px 8px; }}
            a {{ color: {link}; }}
            blockquote {{ border-left: 3px solid {border}; margin-left: 0; padding-left: 12px; color: {quote}; }}
            </style></head><body>{body}</body></html>";
    }

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
