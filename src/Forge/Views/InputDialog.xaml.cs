using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;

namespace Forge.Views;

// A single-field prompt. Doubles as a branch picker: pass `suggestions` and
// the field becomes an editable combo box (pick one, or type a branch that
// isn't in the list yet); omit it and it behaves like a plain text prompt.
// Pass `onRefresh` to show a Refresh button that re-runs it and repopulates
// the list in place (used to re-fetch just this one submodule's branches).
public partial class InputDialog : Window
{
    private readonly Func<Task<List<string>>>? _onRefresh;

    public string Value => (InputCombo.Text ?? "").Trim();

    public InputDialog(string title, string prompt, string defaultValue = "",
        IEnumerable<string>? suggestions = null, Func<Task<List<string>>>? onRefresh = null)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        if (suggestions != null) InputCombo.ItemsSource = suggestions;
        InputCombo.Text = defaultValue;
        _onRefresh = onRefresh;
        RefreshButton.Visibility = onRefresh != null ? Visibility.Visible : Visibility.Collapsed;

        Loaded += (_, _) =>
        {
            InputCombo.Focus();
            if (InputCombo.Template.FindName("PART_EditableTextBox", InputCombo) is System.Windows.Controls.TextBox editBox)
                editBox.SelectAll();
        };
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_onRefresh == null) return;
        RefreshButton.IsEnabled = false;
        var previousText = InputCombo.Text;
        try
        {
            InputCombo.ItemsSource = await _onRefresh();
            InputCombo.Text = previousText;
        }
        catch
        {
            // Leave the existing list in place; the caller's own log/banner
            // already surfaces the real error if this was a genuine failure.
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
