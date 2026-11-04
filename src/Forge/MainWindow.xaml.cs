using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
using ShapePath = System.Windows.Shapes.Path;
using Polyline = System.Windows.Shapes.Polyline;
using Ellipse = System.Windows.Shapes.Ellipse;

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
    private readonly GitCliService _gitCli = new();
    private List<FileEntry> _allFiles = new();
    private string _filesFilter = "All";
    private FileTreeNode? _selectedFileNode;
    private readonly HashSet<string> _checkedFiles = new();

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
            ["Files"] = FilesPage,
            ["History"] = HistoryPage,
            ["Build"] = BuildPage,
            ["Log"] = LogPage,
            ["Help"] = HelpPage,
            ["Settings"] = SettingsPage
        };
        _navButtons = new Dictionary<string, Button>
        {
            ["Overview"] = OverviewNavButton,
            ["Submodules"] = SubmodulesNavButton,
            ["Files"] = FilesNavButton,
            ["History"] = HistoryNavButton,
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
        _notifyBannerTimer.Tick += (_, _) =>
        {
            _notifyBannerTimer.Stop();
            NotifyBanner.Visibility = Visibility.Collapsed;
        };
        _branchRefreshTimer.Tick += async (_, _) =>
        {
            await PrefetchAllBranchesAsync();
            // Piggybacks on the same 15-minute timer so lock/stale state (and
            // the unlock-notification watch list) stays current passively,
            // not just when the user happens to hit Refresh on the Files page.
            try { await RefreshFilesAsync(); } catch { /* best-effort background refresh */ }
        };
        PopulateRecentProjects(null);

        InitializeThemePickers();
        UpdateAdvancedViewLabel();

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
        OverviewNavButton.IsEnabled = SubmodulesNavButton.IsEnabled = FilesNavButton.IsEnabled =
            HistoryNavButton.IsEnabled = BuildNavButton.IsEnabled = LogNavButton.IsEnabled = enabled;

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
            _checkedFiles.Clear();

            await _host.ImportProjectAsync(path);
            _projectInfo = await _host.GetProjectInfoAsync();
            _localSettings = await _host.GetLocalSettingsAsync();
            _projectDocs = await _host.GetProjectDocsAsync();
            _projectRoot = path;

            UpdateOverviewUi();
            await RefreshSubmodulesAsync();
            PopulateHistoryRepos();
            PopulateHelpDocs();
            UpdateLfsSettingsUi();
            UpdateFilesFilterButtons();
            await RefreshFilesAsync();

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

    private void SettingsSubTab_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBlock { Tag: string tag }) return;
        var showTheme = tag == "Theme";
        ThemeTabContent.Visibility = showTheme ? Visibility.Visible : Visibility.Collapsed;
        GeneralTabContent.Visibility = showTheme ? Visibility.Collapsed : Visibility.Visible;

        ThemeSubTab.FontWeight = showTheme ? FontWeights.Medium : FontWeights.Normal;
        GeneralSubTab.FontWeight = showTheme ? FontWeights.Normal : FontWeights.Medium;
        if (showTheme) ThemeSubTab.SetResourceReference(ForegroundProperty, "ForgeAccentBrush");
        else ThemeSubTab.ClearValue(ForegroundProperty);
        if (!showTheme) GeneralSubTab.SetResourceReference(ForegroundProperty, "ForgeAccentBrush");
        else GeneralSubTab.ClearValue(ForegroundProperty);
    }

    // -----------------------------------------------------------------------
    // General settings (Settings page): the LFS locking feature flag.
    // Tri-state in storage (LocalSettings.EnableLfsLocking: null = not yet
    // decided), resolved to a concrete on/off here so the rest of the app
    // only ever asks IsLfsLockingEnabled() for a plain bool.

    private bool IsLfsLockingEnabled() =>
        _localSettings.EnableLfsLocking ?? (_projectInfo?.LfsRepos.Length > 0);

    private void UpdateLfsSettingsUi()
    {
        EnableLfsLockingCheck.IsChecked = IsLfsLockingEnabled();
        LfsAutoDetectNote.Text = _localSettings.EnableLfsLocking.HasValue
            ? "Set manually for this checkout."
            : $"Automatic: {(IsLfsLockingEnabled() ? "on" : "off")}, based on whether this project declares any LFS repos.";
    }

    private async void EnableLfsLockingCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        _localSettings.EnableLfsLocking = EnableLfsLockingCheck.IsChecked;
        UpdateLfsSettingsUi();
        try { await _host.SetLocalSettingsAsync(_localSettings); }
        catch (ForgeWorkflowException ex) { ShowBanner(ex.Message); }
        catch (ForgeUnexpectedException ex) { ShowBanner($"Unexpected error: {ex.Message}"); }
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

    // Shown instead of a Windows toast (see ToastService's header comment --
    // CommunityToolkit.WinUI.Notifications never actually displayed anything
    // on this machine, unpackaged-app registration or not, and isn't worth
    // chasing further for one feature). Auto-dismisses so it behaves like a
    // transient notification rather than a banner the user has to clear.
    private readonly DispatcherTimer _notifyBannerTimer = new() { Interval = TimeSpan.FromSeconds(6) };

    private void ShowNotifyBanner(string message)
    {
        NotifyBannerText.Text = message;
        NotifyBanner.Visibility = Visibility.Visible;
        _notifyBannerTimer.Stop();
        _notifyBannerTimer.Start();
    }

    private void DismissNotifyBanner_Click(object sender, RoutedEventArgs e) => NotifyBanner.Visibility = Visibility.Collapsed;

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

    // Escape hatch (spec's own principle: a genuinely useful slice, not a
    // full Git client) -- hands off to Fork or SourceGit for anything Forge
    // doesn't cover, rather than growing every advanced feature itself.
    // No selection -> opens the superproject root.
    private static readonly string[] AdvancedViewCandidates =
    {
        @"%LOCALAPPDATA%\Fork\Fork.exe",
        @"%LOCALAPPDATA%\Programs\SourceGit\SourceGit.exe",
        @"%LOCALAPPDATA%\SourceGit\SourceGit.exe"
    };

    private static string? FindAdvancedViewTool() =>
        AdvancedViewCandidates
            .Select(Environment.ExpandEnvironmentVariables)
            .FirstOrDefault(File.Exists);

    // Names the button/menu item after whichever tool Forge actually found,
    // rather than a vague "Advanced View" -- the user should never be
    // surprised by which external app this launches.
    private void UpdateAdvancedViewLabel()
    {
        var tool = FindAdvancedViewTool();
        var label = tool == null ? "Open in External Git Tool..." : $"Open in {Path.GetFileNameWithoutExtension(tool)}...";
        OpenAdvancedViewButtonLabel.Text = label;

        // The context-menu MenuItem lives in a standalone ContextMenu
        // resource (SubmoduleContextMenu), not the window's own visual tree,
        // so it never got an x:Name-generated field -- find it by content instead.
        if (Resources["SubmoduleContextMenu"] is ContextMenu menu)
        {
            var item = menu.Items.OfType<System.Windows.Controls.MenuItem>()
                .FirstOrDefault(m => (m.Header as string)?.StartsWith("Open in") == true);
            if (item != null) item.Header = label;
        }
    }

    private void OpenAdvancedViewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_projectInfo == null) return;
        var repoDir = _selectedNode == null ? _projectInfo.Root : GetRepoAbsoluteDir(_selectedNode.State.Path);
        if (repoDir == null) return;

        var tool = FindAdvancedViewTool();
        if (tool == null)
        {
            MessageBox.Show(this,
                "Couldn't find Fork or SourceGit installed on this machine. Install one of them to use this.",
                "Forge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try { Process.Start(new ProcessStartInfo(tool, $"\"{repoDir}\"") { UseShellExecute = true }); }
        catch (Exception ex) { ShowBanner($"Couldn't launch {Path.GetFileNameWithoutExtension(tool)}: {ex.Message}"); }
    }

    private async void ShelveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_projectInfo == null) return;
        var repoPath = _selectedNode?.State.Path ?? "";
        var repoDir = _selectedNode == null ? _projectInfo.Root : GetRepoAbsoluteDir(repoPath);
        if (repoDir == null) return;
        var repoLabel = string.IsNullOrEmpty(repoPath) ? "(superproject)" : repoPath;

        var dialog = new ShelveDialog(_gitCli, repoDir, repoLabel) { Owner = this };
        dialog.ShowDialog();
        if (dialog.DidRestore)
        {
            await RefreshSubmodulesAsync();
            await RefreshFilesAsync();
        }
    }

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
    private void TreeItem_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
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
        if (!await CheckStaleLocksBeforeSyncAsync()) return;

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

    // Pre-flight warning (spec: "syncing while holding stale locks"). The
    // other two risks the spec names are already covered structurally
    // rather than needing a separate check here: UEGitPlugin's own
    // read-only-until-locked enforcement already stops editing a file
    // someone else has locked (background section of the spec itself), and
    // Submit already unlocks automatically right after a successful push --
    // there's no path to "pushed without unlocking" through Forge.
    // Returns false to abort the sync (never happens today -- stale locks
    // don't actually block a sync technically, so this is advisory only,
    // same as the spec's own framing).
    private async Task<bool> CheckStaleLocksBeforeSyncAsync()
    {
        if (!IsLfsLockingEnabled()) return true;

        try { await RefreshFilesAsync(); } catch { return true; } // best-effort; don't block Sync over a failed precheck

        var stale = _allFiles.Where(f => f.IsLockedByMe && f.IsStale).ToList();
        if (stale.Count == 0) return true;

        var list = string.Join("\n", stale.Select(f => $"  {f.RelativePath}"));
        var release = MessageBox.Show(this,
            $"You're holding {stale.Count} stale lock(s) that don't cover any pending work:\n{list}\n\nRelease them before syncing?",
            "Forge", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (release == MessageBoxResult.Yes)
        {
            foreach (var file in stale)
            {
                var repoDir = GetRepoAbsoluteDir(file.RepoPath);
                if (repoDir == null) continue;
                try { await _gitCli.UnlockAsync(repoDir, file.RelativePath); }
                catch { /* best-effort -- a lock that fails to release here isn't worth blocking Sync over */ }
            }
        }

        return true;
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

    private void HousekeepingButton_Click(object sender, RoutedEventArgs e)
    {
        if (_projectInfo == null) return;
        var lfsRepoDirs = _projectInfo.LfsRepos
            .Select(GetRepoAbsoluteDir)
            .Where(d => d != null)
            .Cast<string>()
            .ToList();

        new HousekeepingDialog(_gitCli, _projectInfo.ProjectDir, lfsRepoDirs) { Owner = this }.ShowDialog();
    }

    // -----------------------------------------------------------------------
    // Files tab: aggregated file state (status/lockable/LFS/locks) across
    // every repo except the engine submodule. Queried directly via git/
    // git-lfs (GitCliService), not through PowerShellHost -- see that
    // class's own header comment for why.

    private IEnumerable<(string RepoPath, string AbsoluteDir)> GetFileScanRepos()
    {
        if (_projectInfo == null) yield break;

        yield return ("", _projectInfo.Root);

        if (SubmodulesTree.ItemsSource is not IEnumerable<SubmoduleNode> roots) yield break;

        var engineRelative = NormalizeRelative(_projectInfo.Root, _projectInfo.EngineDir);
        foreach (var node in Flatten(roots))
        {
            var path = node.State.Path;
            if (path == engineRelative || path.StartsWith(engineRelative + "/", StringComparison.Ordinal))
                continue; // the engine's ~200k-file tree is excluded from scans everywhere else too
            yield return (path, Path.Combine(_projectInfo.Root, path.Replace('/', Path.DirectorySeparatorChar)));
        }
    }

    private static string NormalizeRelative(string root, string absolute) =>
        Path.GetRelativePath(root, absolute).Replace('\\', '/');

    private async Task RefreshFilesAsync()
    {
        if (_projectInfo == null) return;

        var repos = GetFileScanRepos().ToList();
        var includeLocks = IsLfsLockingEnabled();
        var results = await Task.WhenAll(repos.Select(r => _gitCli.GetFileStatesAsync(r.RepoPath, r.AbsoluteDir, includeLocks)));

        // `git lfs locks` run from a repo also reports locks that physically
        // live inside one of ITS nested submodules (e.g. Game reports locks
        // under ConquestBattleSystem too), which is separately scanned as
        // its own repo -- the same physical file then shows up under two
        // FullPath-colliding FileEntry objects. Merge them: keep the one
        // from the more specific (longer/nested) RepoPath for status/
        // lockable/LFS accuracy, but carry over lock info from whichever
        // entry actually has it -- a nested repo with no .gitattributes
        // lockable pattern of its own (e.g. ConquestBattleSystem) can see
        // none of its own files as locked even though the shallower repo's
        // lock listing correctly reports them as locked.
        _allFiles = results.SelectMany(r => r)
            .GroupBy(f => f.FullPath)
            .Select(MergeDuplicateFileEntries)
            .ToList();
        ApplyFilesFilter();
        await UpdateYourStuffSummaryAsync();
        await CheckNotifyOnUnlockAsync();
    }

    // Fires a toast for every watched path (see NotifyOnUnlockButton_Click)
    // that's no longer locked by someone else as of this refresh, then stops
    // watching it -- one-shot, not a standing alert. A path is only ever
    // added to the watch list while it IS locked by someone else, so seeing
    // it unlocked (or gone) on any later refresh is exactly the transition
    // to report; there's no need to track what the previous refresh saw.
    private async Task CheckNotifyOnUnlockAsync()
    {
        if (_host == null || _localSettings.NotifyOnUnlock.Count == 0) return;

        var byPath = _allFiles.ToDictionary(f => f.FullPath, f => f);
        var freed = _localSettings.NotifyOnUnlock
            .Where(path => !byPath.TryGetValue(path, out var f) || f.LockOwner == null)
            .ToList();
        if (freed.Count == 0) return;

        foreach (var path in freed)
        {
            _localSettings.NotifyOnUnlock.Remove(path);
            var name = Path.GetFileName(path.Replace('/', Path.DirectorySeparatorChar));
            ShowNotifyBanner($"'{(string.IsNullOrEmpty(name) ? path : name)}' is free to lock now.");
        }
        try { await _host.SetLocalSettingsAsync(_localSettings); }
        catch { /* best-effort -- worst case this fires again next refresh */ }
    }

    private static FileEntry MergeDuplicateFileEntries(IEnumerable<FileEntry> group)
    {
        var list = group.ToList();
        if (list.Count == 1) return list[0];

        var primary = list.OrderByDescending(f => f.RepoPath.Length).First();
        var lockSource = list.FirstOrDefault(f => f.LockOwner != null) ?? primary;
        if (!ReferenceEquals(primary, lockSource))
        {
            primary.LockOwner = lockSource.LockOwner;
            primary.IsLockedByMe = lockSource.IsLockedByMe;
            primary.LockedAt = lockSource.LockedAt;
        }
        return primary;
    }

    // "Your stuff" card on the Overview page: reuses _allFiles (just
    // refreshed above) for locks/outdated, and asks every repo directly for
    // its ahead-of-upstream count -- RefreshSubmodulesAsync's SubmoduleState
    // doesn't carry that.
    private async Task UpdateYourStuffSummaryAsync()
    {
        if (_projectInfo == null) return;

        var repos = GetFileScanRepos().ToList();
        var aheadCounts = await Task.WhenAll(repos.Select(r => _gitCli.GetUnpushedCountAsync(r.AbsoluteDir)));
        var unpushed = aheadCounts.Sum();

        var myLocks = _allFiles.Count(f => f.IsLockedByMe);
        var outdated = _allFiles.Count(f => f.IsLfsTracked && !f.IsDownloaded);

        YourStuffUnpushedText.Text = unpushed == 0 ? "Nothing unpushed" : $"{unpushed} unpushed commit(s)";
        YourStuffLocksText.Text = myLocks == 0 ? "No locks held" : $"{myLocks} file(s) locked by you";
        YourStuffOutdatedText.Text = outdated == 0 ? "Everything downloaded" : $"{outdated} file(s) not downloaded";
    }

    private async void RefreshFilesButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try { await RefreshFilesAsync(); }
        catch (Exception ex) { ShowBanner($"Unexpected error: {ex.Message}"); }
        finally { SetBusy(false); }
    }

    private void FilesFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        _filesFilter = tag;
        UpdateFilesFilterButtons();
        ApplyFilesFilter();
    }

    private void UpdateFilesFilterButtons()
    {
        var buttons = new[]
        {
            (FilesFilterAllButton, "All"), (FilesFilterChangedButton, "Changed"),
            (FilesFilterLockedButton, "Locked"), (FilesFilterOutdatedButton, "NotDownloaded"),
            (FilesFilterStaleButton, "Stale")
        };
        foreach (var (button, tag) in buttons)
        {
            var isActive = _filesFilter == tag;
            if (isActive) button.SetResourceReference(BackgroundProperty, "ForgeAccentSoftBrush");
            else button.ClearValue(BackgroundProperty);
            button.SetResourceReference(ForegroundProperty, isActive ? "ForgeAccentBrush" : "TextFillColorPrimaryBrush");
        }
    }

    private void FilesSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilesFilter();

    private void ApplyFilesFilter()
    {
        IEnumerable<FileEntry> filtered = _filesFilter switch
        {
            "Changed" => _allFiles.Where(f => f.Status != FileStatus.Unmodified),
            "Locked" => _allFiles.Where(f => f.LockOwner != null),
            "NotDownloaded" => _allFiles.Where(f => f.IsLfsTracked && !f.IsDownloaded),
            "Stale" => _allFiles.Where(f => f.IsStale),
            _ => _allFiles
        };

        var search = FilesSearchBox.Text.Trim();
        if (search.Length > 0)
            filtered = filtered.Where(f => f.FullPath.Contains(search, StringComparison.OrdinalIgnoreCase));

        var filteredList = filtered.ToList();
        var repoOrder = GetFileScanRepos().Select(r => r.RepoPath).ToList();
        FilesTree.ItemsSource = FileTreeNode.BuildTree(filteredList, repoOrder, _checkedFiles);
        FilesStatusText.Text = $"{filteredList.Count} of {_allFiles.Count} file(s)";
        UpdateCheckedFilesStatus();
    }

    private void FilesTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        => _selectedFileNode = e.NewValue as FileTreeNode;

    // _checkedFiles (by FullPath) is the persistent source of truth across
    // tree rebuilds -- see FileTreeNode.IsChecked's own comment for why.
    private void FileCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: FileTreeNode { IsFile: true, File: { } file } } checkBox) return;
        if (checkBox.IsChecked == true) _checkedFiles.Add(file.FullPath);
        else _checkedFiles.Remove(file.FullPath);
        UpdateCheckedFilesStatus();
    }

    private void UpdateCheckedFilesStatus()
    {
        if (_checkedFiles.Count == 0)
        {
            CheckedFilesStatusText.Text = "";
            SubmitButton.IsEnabled = false;
            return;
        }

        var repos = _checkedFiles.Select(GetRepoPathFromFullPath).Distinct().ToList();
        if (repos.Count > 1)
        {
            CheckedFilesStatusText.Text = $"{_checkedFiles.Count} file(s) checked across {repos.Count} repos -- Submit one repo at a time.";
            SubmitButton.IsEnabled = false;
        }
        else
        {
            var repoLabel = string.IsNullOrEmpty(repos[0]) ? "(superproject)" : repos[0];
            CheckedFilesStatusText.Text = $"{_checkedFiles.Count} file(s) checked in {repoLabel}";
            SubmitButton.IsEnabled = true;
        }
    }

    // FileEntry.FullPath is "{RepoPath}/{RelativePath}" (RepoPath "" for the
    // superproject) -- recovering RepoPath means matching against the known
    // repo paths rather than guessing from slash position, since RepoPath
    // itself can contain slashes for nested submodules.
    private string GetRepoPathFromFullPath(string fullPath)
    {
        var match = _allFiles.FirstOrDefault(f => f.FullPath == fullPath);
        return match?.RepoPath ?? "";
    }

    private string? GetRepoAbsoluteDir(string repoPath) => _projectInfo == null ? null
        : string.IsNullOrEmpty(repoPath) ? _projectInfo.Root
        : Path.Combine(_projectInfo.Root, repoPath.Replace('/', Path.DirectorySeparatorChar));

    // Lock/unlock only ever act on the caller's own lock -- see GitCliService's
    // LockAsync/UnlockAsync header comment for why there's no force option.

    private async void LockSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFileNode is not { IsFile: true, File: { } file })
        {
            MessageBox.Show(this, "Select a file in the tree first.", "Forge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!file.IsLockable)
        {
            MessageBox.Show(this, "This file isn't set up as lockable (no matching 'lockable' .gitattributes pattern).",
                "Forge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (file.LockOwner != null)
        {
            MessageBox.Show(this, file.IsLockedByMe ? "You already have this file locked." : $"Already locked by {file.LockOwner}.",
                "Forge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var repoDir = GetRepoAbsoluteDir(file.RepoPath);
        if (repoDir == null) return;

        SetBusy(true);
        try
        {
            await _gitCli.LockAsync(repoDir, file.RelativePath);
            await RefreshFilesAsync();
        }
        catch (Exception ex) { ShowBanner($"Couldn't lock '{file.RelativePath}': {ex.Message}"); }
        finally { SetBusy(false); }
    }

    private async void UnlockSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFileNode is not { IsFile: true, File: { } file })
        {
            MessageBox.Show(this, "Select a file in the tree first.", "Forge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (file.LockOwner == null)
        {
            MessageBox.Show(this, "This file isn't locked.", "Forge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!file.IsLockedByMe)
        {
            MessageBox.Show(this, $"This is locked by {file.LockOwner}, not you -- Forge can't release someone else's lock.",
                "Forge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var repoDir = GetRepoAbsoluteDir(file.RepoPath);
        if (repoDir == null) return;

        SetBusy(true);
        try
        {
            await _gitCli.UnlockAsync(repoDir, file.RelativePath);
            await RefreshFilesAsync();
        }
        catch (Exception ex) { ShowBanner($"Couldn't unlock '{file.RelativePath}': {ex.Message}"); }
        finally { SetBusy(false); }
    }

    private async void NotifyOnUnlockButton_Click(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;
        if (_selectedFileNode is not { IsFile: true, File: { } file })
        {
            MessageBox.Show(this, "Select a file in the tree first.", "Forge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var key = file.FullPath;
        if (_localSettings.NotifyOnUnlock.Remove(key))
        {
            MessageBox.Show(this, $"No longer watching '{file.RelativePath}'.", "Forge", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            if (file.LockOwner == null)
            {
                MessageBox.Show(this, "This file isn't locked by anyone right now.", "Forge", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (file.IsLockedByMe)
            {
                MessageBox.Show(this, "You're the one holding this lock.", "Forge", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _localSettings.NotifyOnUnlock.Add(key);
            MessageBox.Show(this, $"You'll get a notification when '{file.RelativePath}' is unlocked.", "Forge", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        SetBusy(true);
        try { await _host.SetLocalSettingsAsync(_localSettings); }
        catch (ForgeWorkflowException ex) { ShowBanner(ex.Message); }
        catch (ForgeUnexpectedException ex) { ShowBanner($"Unexpected error: {ex.Message}"); }
        finally { SetBusy(false); }
    }

    private async void ViewHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_projectInfo == null) return;
        if (_selectedFileNode is not { IsFile: true, File: { } file })
        {
            MessageBox.Show(this, "Select a file in the tree first.", "Forge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var repoDir = GetRepoAbsoluteDir(file.RepoPath);
        if (repoDir == null) return;
        var workingCopyPath = Path.Combine(repoDir, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));

        var uproject = File.Exists(_projectInfo.UProject) ? _projectInfo.UProject : null;
        var editorExe = File.Exists(_projectInfo.EditorExe) ? _projectInfo.EditorExe : null;

        var dialog = new FileHistoryDialog(_gitCli, repoDir, file.RelativePath, workingCopyPath, uproject, editorExe) { Owner = this };
        dialog.ShowDialog();
        if (dialog.DidRestore) await RefreshFilesAsync();
    }

    private async void ResolveConflictButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedFileNode is not { IsFile: true, File: { } file })
        {
            MessageBox.Show(this, "Select a file in the tree first.", "Forge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (file.Status != FileStatus.Conflicted)
        {
            MessageBox.Show(this, "This file isn't conflicted.", "Forge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var repoDir = GetRepoAbsoluteDir(file.RepoPath);
        if (repoDir == null) return;

        var dialog = new ConflictDialog(_gitCli, repoDir, file.RelativePath) { Owner = this };
        dialog.ShowDialog();
        if (dialog.Resolved) await RefreshFilesAsync();
    }

    // -----------------------------------------------------------------------
    // Submit: commit + push the checked files in one repo, release their
    // locks, then cascade a pin-bump commit+push up through every ancestor
    // submodule to the superproject. The only Forge action that pushes to a
    // remote, so SubmitDialog always shows the full plan (every file, every
    // pin-bump commit) before anything runs.

    // One level of the cascade: ParentRepoPath/ParentAbsoluteDir is the repo
    // whose pin needs bumping; RelativePathInParent is the child's gitlink
    // path as seen from that parent (e.g. "IMGCORE/Plugins/ConquestBattleSystem"
    // within Game, or "Game" within the superproject).
    private List<(string ParentRepoPath, string ParentAbsoluteDir, string RelativePathInParent)> GetPinBumpChain(string leafRepoPath)
    {
        var chain = new List<(string, string, string)>();
        if (_projectInfo == null || SubmodulesTree.ItemsSource is not IEnumerable<SubmoduleNode> roots) return chain;

        var allPaths = Flatten(roots).Select(n => n.State.Path).ToList();
        var current = leafRepoPath;

        while (true)
        {
            var parent = allPaths
                .Where(p => p != current && current.StartsWith(p + "/", StringComparison.Ordinal))
                .OrderByDescending(p => p.Length)
                .FirstOrDefault();
            var parentPath = parent ?? ""; // "" = superproject root
            var relative = parent == null ? current : current[(parent.Length + 1)..];
            var parentDir = string.IsNullOrEmpty(parentPath)
                ? _projectInfo.Root
                : Path.Combine(_projectInfo.Root, parentPath.Replace('/', Path.DirectorySeparatorChar));

            chain.Add((parentPath, parentDir, relative));
            if (parent == null) break; // just added the superproject level
            current = parent;
        }
        return chain;
    }

    // Used to flag a cascade step that would pin an ancestor to a commit
    // from a submodule that's NOT currently on its own declared branch --
    // e.g. a feature branch's commit about to get pinned onto the
    // superproject's shared development branch. Deliberately not "does this
    // child's branch name match its parent's": different repos name their
    // stable branch differently by design (Game declares 'development',
    // SourceAssets declares 'main'), so that comparison flags normal,
    // correct submits as mismatches. Surfaced as a warning in SubmitDialog
    // rather than blocked outright: there are legitimate reasons to do this,
    // but never silently.
    private SubmoduleState? GetSubmoduleState(string repoPath)
    {
        if (string.IsNullOrEmpty(repoPath) || SubmodulesTree.ItemsSource is not IEnumerable<SubmoduleNode> roots) return null;
        return Flatten(roots).FirstOrDefault(n => n.State.Path == repoPath)?.State;
    }

    private async void SubmitButton_Click(object sender, RoutedEventArgs e)
    {
        if (_host == null || _projectInfo == null || _checkedFiles.Count == 0) return;

        var checkedEntries = _allFiles.Where(f => _checkedFiles.Contains(f.FullPath)).ToList();
        var repoPaths = checkedEntries.Select(f => f.RepoPath).Distinct().ToList();
        if (repoPaths.Count != 1)
        {
            MessageBox.Show(this, "Checked files span more than one repo -- submit one repo at a time.",
                "Forge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var repoPath = repoPaths[0];
        var repoDir = GetRepoAbsoluteDir(repoPath);
        if (repoDir == null) return;
        var repoLabel = string.IsNullOrEmpty(repoPath) ? "(superproject)" : repoPath;
        var relativePaths = checkedEntries.Select(f => f.RelativePath).ToList();
        var cascade = GetPinBumpChain(repoPath);

        var cascadeLines = new List<string>();
        var branchMismatch = false;
        var childPathForBranchCheck = repoPath;
        foreach (var step in cascade)
        {
            var childName = step.RelativePathInParent.Split('/')[^1];
            var parentLabel = string.IsNullOrEmpty(step.ParentRepoPath) ? "(superproject)" : step.ParentRepoPath;
            var line = $"{parentLabel}: bump pin for '{childName}'";

            var childState = GetSubmoduleState(childPathForBranchCheck);
            if (childState != null && !string.IsNullOrEmpty(childState.DeclaredBranch) && childState.CurrentBranch != childState.DeclaredBranch)
            {
                line += $"  [!] '{childName}' is on '{childState.CurrentBranch ?? "(detached)"}', not its declared branch '{childState.DeclaredBranch}'";
                branchMismatch = true;
            }

            cascadeLines.Add(line);
            childPathForBranchCheck = step.ParentRepoPath;
        }

        string? warning = null;
        var notLockedByMe = checkedEntries.Where(f => f.IsLockable && !f.IsLockedByMe).Select(f => f.RelativePath).ToList();
        if (notLockedByMe.Count > 0)
            warning = $"Not locked by you: {string.Join(", ", notLockedByMe)}. Submitting anyway won't release a lock you don't hold.";
        if (branchMismatch)
        {
            var mismatchNote = "A pin bump above would come from a submodule that's not on its own declared branch right now -- make sure that's really what you want before submitting.";
            warning = warning == null ? mismatchNote : $"{warning}\n{mismatchNote}";
        }

        var dialog = new SubmitDialog(repoLabel, relativePaths, cascadeLines, warning) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var message = dialog.Message;

        ShowPage("Log");
        SetBusy(true);
        try
        {
            LogText.AppendText($"Submitting {relativePaths.Count} file(s) in {repoLabel}...\n");
            await _gitCli.CommitPathsAsync(repoDir, relativePaths, message);
            await _gitCli.PushAsync(repoDir);
            LogText.AppendText($"Committed and pushed {repoLabel}.\n");

            foreach (var file in checkedEntries.Where(f => f.LockOwner != null && f.IsLockedByMe))
            {
                try { await _gitCli.UnlockAsync(repoDir, file.RelativePath); }
                catch (Exception ex) { LogText.AppendText($"Could not unlock '{file.RelativePath}': {ex.Message}\n"); }
            }

            var childSha = await _gitCli.GetHeadShaAsync(repoDir);
            foreach (var step in cascade)
            {
                var childName = step.RelativePathInParent.Split('/')[^1];
                await _gitCli.BumpSubmodulePinAsync(step.ParentAbsoluteDir, step.RelativePathInParent, childSha);
                var subject = await _gitCli.GetHeadCommitSubjectAsync(repoDir);
                var pinMessage = $"Bump {childName} pin: {subject}";
                await _gitCli.CommitPathsAsync(step.ParentAbsoluteDir, new[] { step.RelativePathInParent }, pinMessage);
                await _gitCli.PushAsync(step.ParentAbsoluteDir);
                var parentLabel = string.IsNullOrEmpty(step.ParentRepoPath) ? "(superproject)" : step.ParentRepoPath;
                LogText.AppendText($"Bumped {childName} pin in {parentLabel} and pushed.\n");

                repoDir = step.ParentAbsoluteDir; // GetHeadCommitSubjectAsync above read the PREVIOUS level's HEAD
                childSha = await _gitCli.GetHeadShaAsync(step.ParentAbsoluteDir);
            }

            foreach (var path in checkedEntries.Select(f => f.FullPath)) _checkedFiles.Remove(path);
            LogText.AppendText("Submit complete.\n");

            await RefreshSubmodulesAsync();
            await RefreshFilesAsync();
        }
        catch (Exception ex)
        {
            ShowBanner($"Submit failed partway through: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    // -----------------------------------------------------------------------
    // History: commit graph for one repo at a time. Simple mode feeds
    // `git log --first-parent`, Full mode `git log --all --topo-order` --
    // see GitCliService.GetCommitLogAsync's own comment for why that single
    // difference is enough to produce both of the spec's two modes from the
    // same lane-layout algorithm (CommitGraph, ported from SourceGit).

    private string _historyMode = "Simple";
    private string? _historyRepoDir;

    private sealed record HistoryRepoOption(string Label, string RepoPath);

    private void PopulateHistoryRepos()
    {
        if (_projectInfo == null) return;
        var options = new List<HistoryRepoOption> { new("(superproject)", "") };
        if (SubmodulesTree.ItemsSource is IEnumerable<SubmoduleNode> roots)
            options.AddRange(Flatten(roots).Select(n => new HistoryRepoOption(n.State.Path, n.State.Path)));

        HistoryRepoCombo.ItemsSource = options;
        HistoryRepoCombo.DisplayMemberPath = nameof(HistoryRepoOption.Label);
        HistoryRepoCombo.SelectedIndex = options.FindIndex(o => o.RepoPath == "Game") is var i && i >= 0 ? i : 0;
    }

    private async void HistoryRepoCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => await RefreshHistoryGraphAsync();

    private async void RefreshHistoryButton_Click(object sender, RoutedEventArgs e) => await RefreshHistoryGraphAsync();

    private async void HistoryMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        _historyMode = tag;
        UpdateHistoryModeButtons();
        await RefreshHistoryGraphAsync();
    }

    private void UpdateHistoryModeButtons()
    {
        foreach (var (button, tag) in new[] { (HistorySimpleButton, "Simple"), (HistoryFullButton, "Full") })
        {
            var isActive = _historyMode == tag;
            if (isActive) button.SetResourceReference(BackgroundProperty, "ForgeAccentSoftBrush");
            else button.ClearValue(BackgroundProperty);
            button.SetResourceReference(ForegroundProperty, isActive ? "ForgeAccentBrush" : "TextFillColorPrimaryBrush");
        }
    }

    private async Task RefreshHistoryGraphAsync()
    {
        if (_projectInfo == null || HistoryRepoCombo.SelectedItem is not HistoryRepoOption repo) return;
        var repoDir = GetRepoAbsoluteDir(repo.RepoPath);
        if (repoDir == null) return;
        _historyRepoDir = repoDir;

        UpdateHistoryModeButtons();
        SetBusy(true);
        HistoryStatusText.Text = "Loading...";
        try
        {
            var commits = await _gitCli.GetCommitLogAsync(repoDir, allBranches: _historyMode == "Full", maxCount: 300);
            var graph = CommitGraph.Generate(commits, firstParentOnly: false);
            DrawHistoryGraph(graph, commits.Count);
            HistoryCommitList.ItemsSource = commits;
            HistoryStatusText.Text = $"{commits.Count} commit(s)";
        }
        catch (Exception ex)
        {
            ShowBanner($"Couldn't load history: {ex.Message}");
            HistoryStatusText.Text = "";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private const double HistoryRowHeightPx = 24;
    private const double HistoryLaneWidthPx = 16;

    private void DrawHistoryGraph(CommitGraph graph, int commitCount)
    {
        HistoryGraphCanvas.Children.Clear();

        double ScaleX(double x) => x / 12.0 * HistoryLaneWidthPx;
        double ScaleY(double y) => y * HistoryRowHeightPx;

        foreach (var path in graph.Paths)
        {
            if (path.Points.Count < 2) continue;
            var poly = new Polyline
            {
                Stroke = new SolidColorBrush(CommitGraph.ColorForLane(path.Color)),
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round
            };
            foreach (var p in path.Points) poly.Points.Add(new System.Windows.Point(ScaleX(p.X), ScaleY(p.Y)));
            HistoryGraphCanvas.Children.Add(poly);
        }

        foreach (var link in graph.Links)
        {
            var figure = new PathFigure { StartPoint = new System.Windows.Point(ScaleX(link.Start.X), ScaleY(link.Start.Y)) };
            figure.Segments.Add(new QuadraticBezierSegment(
                new System.Windows.Point(ScaleX(link.Control.X), ScaleY(link.Control.Y)),
                new System.Windows.Point(ScaleX(link.End.X), ScaleY(link.End.Y)), true));
            var geo = new PathGeometry();
            geo.Figures.Add(figure);
            HistoryGraphCanvas.Children.Add(new ShapePath
            {
                Data = geo,
                Stroke = new SolidColorBrush(CommitGraph.ColorForLane(link.Color)),
                StrokeThickness = 2
            });
        }

        const double dotRadius = 4;
        foreach (var dot in graph.Dots)
        {
            var brush = new SolidColorBrush(CommitGraph.ColorForLane(dot.Color));
            var isHead = dot.Type == CommitGraph.DotType.Head;
            var ellipse = new Ellipse
            {
                Width = dotRadius * 2,
                Height = dotRadius * 2,
                Fill = isHead ? Brushes.White : brush,
                Stroke = brush,
                StrokeThickness = isHead ? 2 : 0
            };
            Canvas.SetLeft(ellipse, ScaleX(dot.Center.X) - dotRadius);
            Canvas.SetTop(ellipse, ScaleY(dot.Center.Y) - dotRadius);
            HistoryGraphCanvas.Children.Add(ellipse);
        }

        var maxX = graph.Dots.Count > 0 ? graph.Dots.Max(d => d.Center.X) : 0;
        foreach (var p in graph.Paths)
            foreach (var pt in p.Points)
                maxX = Math.Max(maxX, pt.X);

        HistoryGraphCanvas.Width = ScaleX(maxX) + HistoryLaneWidthPx * 2;
        HistoryGraphCanvas.Height = Math.Max(commitCount * HistoryRowHeightPx, HistoryCommitList.ActualHeight);
    }

    // Unified superproject history: a "Bump Game pin" commit only records
    // that a pointer moved. On double-click, show what actually moved --
    // works for any repo with its own nested submodules, not just the
    // superproject (e.g. a "Bump ConquestBattleSystem pin" commit inside
    // Game's own history expands the same way).
    private async void HistoryCommitRow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is not FrameworkElement { DataContext: GraphCommit commit } || _historyRepoDir == null) return;

        SetBusy(true);
        try
        {
            var pinChanges = await _gitCli.GetSubmodulePinChangesAsync(_historyRepoDir, commit.Sha);
            if (pinChanges.Count == 0)
            {
                MessageBox.Show(this, "This commit doesn't change any submodule pins.", "Forge", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var sections = new List<PinBumpDialog.SubmoduleSection>();
            foreach (var change in pinChanges)
            {
                var submoduleDir = Path.Combine(_historyRepoDir, change.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(submoduleDir)) continue;
                try
                {
                    var commits = await _gitCli.GetCommitRangeAsync(submoduleDir, change.OldSha, change.NewSha);
                    var header = $"{change.RelativePath}  ({change.OldSha[..8]} -> {change.NewSha[..8]}, {commits.Count} commit(s))";
                    sections.Add(new PinBumpDialog.SubmoduleSection(header, commits));
                }
                catch (Exception ex)
                {
                    sections.Add(new PinBumpDialog.SubmoduleSection($"{change.RelativePath}: {ex.Message}", new List<FileHistoryEntry>()));
                }
            }

            new PinBumpDialog(sections) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            ShowBanner($"Couldn't read pin changes: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
        }
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
