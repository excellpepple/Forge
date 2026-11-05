# UI patterns

Forge is one `MainWindow` with every "page" as a `Grid` toggled by
visibility, not a multi-window or `Frame`/`NavigationService` app, and not
MVVM -- it's direct code-behind throughout. This is deliberate for an app
this size: there's one window, state lives in `MainWindow`'s own fields, and
the indirection a full MVVM stack buys isn't worth it here. Follow the
existing code-behind style for new work rather than introducing a different
pattern partway through the app.

## Pages and navigation

Every page is a `Grid` in `MainWindow.xaml`, registered in two dictionaries
built in the constructor:

```csharp
_pages = new Dictionary<string, Grid> { ["Overview"] = OverviewPage, ["Files"] = FilesPage, /* ... */ };
_navButtons = new Dictionary<string, Button> { ["Overview"] = OverviewNavButton, /* ... */ };
```

`ShowPage(string key)` is the only thing that switches pages: it walks
`_pages` setting `Visibility`, and walks `_navButtons` repainting the
active/inactive look (background, icon/label `Foreground`, label
`FontWeight`) via `SetResourceReference` against the `Forge*Brush`
resources -- see [Theming](#theming) below. `WelcomePage` is in `_pages`
but **not** in `_navButtons` -- it has no sidebar entry by design (it's
only reachable before a project is open, or via the explicit "Register a
Project..." sidebar button, which exists specifically because Welcome
itself has no nav button once a project auto-reopens on launch).

`SetProjectNavEnabled(bool)` enables/disables the project-scoped nav
buttons (Overview/Submodules/Files/History/Build/Log) as a block -- Settings
and Help stay enabled even with no project open.

**Adding a new page:** add the `Grid` in XAML (`Visibility="Collapsed"`,
matching the existing pages' `Margin`/structure), add it to `_pages` (and
`_navButtons` if it needs a sidebar entry), and call `ShowPage("YourKey")`
from wherever should navigate to it.

## Busy state

Buttons **stay clickable** during a long operation -- there is no
`IsEnabled = false` sweep. `PowerShellHost` already serializes every call
through its own gate (see
[POWERSHELL_HOSTING.md](POWERSHELL_HOSTING.md#why-one-gate-for-everything)),
so a second click can't corrupt anything; worst case it queues. What
changes during a busy operation is only the *feedback*:

- `SetStatus(message, busy)` -- sets the status-bar text (bottom of the
  window) and shows/hides its indeterminate `ProgressBar`.
- `SetBusy(bool)` -- sets the `_isBusy` flag and resets status to "Ready"
  when turning off.
- `DeclineIfBusy()` -- for the *handful* of operations where letting a
  second call silently queue behind the first would be actively confusing
  (Sync, Build, Submit, opening a different project mid-operation): checks
  `_isBusy`, and if true, shows a transient notify-banner ("Still working:
  ...") and returns `true` so the caller can bail out instead of queuing.
  **Most handlers do not call this** -- only add it to a new handler if
  queuing behind it would genuinely confuse the user (e.g. two Submits
  racing), not by default.

```csharp
private async Task RunSomething()
{
    if (DeclineIfBusy()) return;      // only for Sync/Build/Submit-shaped ops
    SetBusy(true);
    SetStatus("Doing the thing...", busy: true);
    try { await _host!.InvokeSomethingAsync(); }
    catch (ForgeWorkflowException ex) { ShowBanner(ex.Message); }
    catch (ForgeUnexpectedException ex) { ShowBanner($"Unexpected error: {ex.Message}"); }
    finally { SetBusy(false); }
}
```

A subtlety: `RefreshFilesAsync` only claims the status bar when nothing else
already has it (`var standalone = !_isBusy;`), so it doesn't stomp a
caller's more specific message (e.g. during Submit, which calls it as part
of a larger flow).

## Theming

Never hardcode a `Color`/`Brush` in new XAML or code-behind for anything
that should respect Light/Dark/custom-accent. Use the `Forge*Brush`
`DynamicResource`s that `ThemeService` maintains (`ForgeAccentBrush`,
`ForgeAccentSoftBrush`, `ForgeOnAccentBrush`, `ForgeBackgroundBrush`,
`ForgeSidebarBackgroundBrush`, `ForgeIconBrush`, and the semantic
`ForgeMatchBrush`/`ForgeWarnBrush`/`ForgeDangerTextBrush`/
`ForgeDangerFillBrush` pairs each with a `*BgBrush` soft-tint variant) --
in XAML via `{DynamicResource ForgeAccentBrush}`, in code-behind via
`control.SetResourceReference(Control.ForegroundProperty, "ForgeAccentBrush")`
(not a direct assignment, so it keeps tracking theme changes).

The semantic colors (`Match`/`Warn`/`Danger`) are **not** user-customizable
on purpose -- see `ThemeService.ApplyForgeBrushes`'s own comment. Don't add
a settings-page picker for those; only accent/background/icon are meant to
be customizable.

`App.xaml` defines static fallback values for every one of these keys, used
only before `ThemeService.ApplyAndWatch()` runs on startup. Its own comment
says to keep those in sync with `ThemeSettings`' actual defaults -- update
both if you change a default color.

## Filterable list toolbars (Files / Log)

Files and Log both follow the same toolbar shape, and a new filterable list
should too:

1. A `Border` styled `{StaticResource ToolbarCardStyle}` holding a
   horizontal `StackPanel` of `Button`s styled `{StaticResource
   SegButtonStyle}`, each with `Tag="SomeFilterValue"` and a shared
   `Click` handler.
2. A master in-memory list (`_allFiles`) or an `ObservableCollection` +
   `ICollectionView` (Log's `_logEntries`/`_logView`) -- see below for which
   to pick.
3. A click handler that records the clicked `Tag` into a field (`_filesFilter`/
   `_logFilter`), repaints which button looks "active" (toggle
   `Background`/`Foreground` via `SetResourceReference`, same
   active/inactive pattern as `ShowPage`'s nav buttons), and re-applies the
   filter.
4. A search `TextBox` whose `TextChanged` re-applies the same filter.

**Rebuild-on-every-change vs. `ICollectionView`:** Files rebuilds its whole
`FilesTree.ItemsSource` from `_allFiles` on every filter/search change
(cheap -- it only happens on an explicit user action or an explicit
refresh). Log instead binds through `CollectionViewSource.GetDefaultView(_logEntries)`
with a `Filter` predicate, and calls `.Refresh()` **only when the
filter/search criteria themselves change** -- new log lines are just
`_logEntries.Add(...)`, which the view picks up without re-evaluating the
whole list. Use the `ICollectionView` approach for anything that grows
continuously in the background (streaming output, live data); use the
simple rebuild approach for anything that only changes on an explicit user
action.

## Large/virtualized lists

Any list that can realistically hold hundreds+ rows (Files tree, Log list)
must virtualize:

```xml
<TreeView VirtualizingPanel.IsVirtualizing="True" VirtualizingPanel.VirtualizationMode="Recycling" ...>
<ListBox VirtualizingPanel.IsVirtualizing="True" VirtualizingPanel.VirtualizationMode="Recycling" VirtualizingPanel.ScrollUnit="Pixel" ...>
```

`FileTreeNode.BuildTree` additionally caps **auto-expanding** folders at
`AutoExpandFileLimit` (300 files) -- above that, WPF has to realize and lay
out thousands of `TreeViewItem`s in one pass just from the initial
auto-expand, which is what actually froze the window before this limit
existed (virtualization alone doesn't help if everything is forced open at
once). If you add another tree-shaped view over a potentially-large
collection, keep this same cap rather than defaulting to "expand
everything."

## Row templates: dock right, don't use fixed-width columns

The Files tree's row template used to be a `StackPanel` of fixed-`Width`
`TextBlock`s -- which misaligned across tree depths, because each row's
indentation ate into its own fixed widths differently. The current pattern
(Files rows, Log rows) is a `DockPanel`: primary content (name/icon on the
left) fills the remaining space via the last, un-docked child; secondary
details dock `Right` in order, so they stay aligned to the row's right
edge regardless of nesting or content length. Follow this shape for any
new row template with "a name plus some right-aligned metadata."

For a status "pill" (Files' colored status badge), pair a `*Brush` (text)
with the matching `*BgBrush` (soft background) from the same semantic
color -- see `Models/FileTreeNode.StatusBrush`/`StatusBgBrush` -- rather
than inventing new colors.

## A real WPF gotcha hit this way: local values beat style triggers

When building the Files row's folder/document icon, a `Symbol="Folder24"`
attribute set directly on the `SymbolIcon`, **plus** a `Style` with a
`DataTrigger` meant to override it to `DocumentText24` for files, silently
did nothing -- every row showed the folder icon. **A local value always
wins over a style trigger's setter in WPF**, regardless of trigger order or
specificity. The fix: move the default into a plain `Setter` *inside the
same `Style`* instead of a local attribute:

```xml
<!-- Wrong: the DataTrigger below can never win against this local value -->
<ui:SymbolIcon Symbol="Folder24">
    <ui:SymbolIcon.Style>
        <Style TargetType="ui:SymbolIcon">
            <Style.Triggers>
                <DataTrigger Binding="{Binding IsFile}" Value="True">
                    <Setter Property="Symbol" Value="DocumentText24"/>
                </DataTrigger>
            </Style.Triggers>
        </Style>
    </ui:SymbolIcon.Style>
</ui:SymbolIcon>

<!-- Right: both the default and the override live in the same Style -->
<ui:SymbolIcon>
    <ui:SymbolIcon.Style>
        <Style TargetType="ui:SymbolIcon">
            <Setter Property="Symbol" Value="Folder24"/>
            <Style.Triggers>
                <DataTrigger Binding="{Binding IsFile}" Value="True">
                    <Setter Property="Symbol" Value="DocumentText24"/>
                </DataTrigger>
            </Style.Triggers>
        </Style>
    </ui:SymbolIcon.Style>
</ui:SymbolIcon>
```

If a `DataTrigger`/`Trigger` setter on a property doesn't seem to be firing,
check for a local attribute on that same property first.

## Dialogs

Every dialog (`Views/*.xaml` + `.xaml.cs`) is a `ui:FluentWindow` opened
modally (`new SomeDialog { Owner = this }; if (dialog.ShowDialog() == true)
{ ... }`) with a public result property the caller reads after it closes
(e.g. `RegisterProjectDialog.ResultProjectPath`). Keep new dialogs
resizable (`ResizeMode="CanResize"` with sensible `MinWidth`/`MinHeight`)
unless there's a specific reason not to -- a fixed-size `NoResize` dialog
with a `TextBox` holding a long path or message is the kind of thing that
looks fine in testing and then clips content for someone with a longer
real path (this was a real bug: `RegisterProjectDialog` used to be
`NoResize` at a cramped 480x380).

## Icons

Icons come from `ui:SymbolIcon`'s `Symbol` property (the
`Wpf.Ui.Controls.SymbolRegular` enum from the WPF-UI package). **Only use a
symbol name you've confirmed exists** -- an invalid enum value fails at
XAML parse/runtime, not compile time. The safest way to confirm one exists
is to reuse a name already used elsewhere in `MainWindow.xaml` (`grep
'Symbol="' MainWindow.xaml`), or check the installed package's enum
directly rather than guessing a plausible-sounding name.
