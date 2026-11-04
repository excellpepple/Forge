using System.Collections.Generic;
using System.Windows;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace Forge.Views;

// Submit's confirmation step: shows exactly what's about to happen (every
// file being committed, every pin-bump commit the cascade will make) before
// anything touches the repo, since this is the first Forge action that
// pushes to a remote. FluentWindow, not Window -- see InputDialog's own
// comment for why (white-on-white otherwise).
public partial class SubmitDialog : FluentWindow
{
    public string Message => CommitMessageBox.Text.Trim();

    public SubmitDialog(string repoLabel, IEnumerable<string> files, IEnumerable<string> cascadeSteps, string? warning = null)
    {
        InitializeComponent();
        RepoText.Text = $"Submitting from: {repoLabel}";
        FilesList.ItemsSource = files;

        var cascade = new List<string>(cascadeSteps);
        if (cascade.Count == 0)
        {
            CascadeHeader.Visibility = Visibility.Collapsed;
            CascadeBorder.Visibility = Visibility.Collapsed;
        }
        else
        {
            CascadeList.ItemsSource = cascade;
        }

        if (!string.IsNullOrEmpty(warning))
        {
            WarningText.Text = warning;
            WarningText.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) => CommitMessageBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Message))
        {
            MessageBox.Show(this, "Enter a commit message first.", "Forge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
