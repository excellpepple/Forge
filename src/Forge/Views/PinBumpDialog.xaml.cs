using System.Collections.Generic;
using System.Windows;
using Forge.Services;
using Wpf.Ui.Controls;

namespace Forge.Views;

// "Unified superproject history": a pin-bump commit ("Bump Game pin: ...")
// only records that a pointer moved. This shows what actually moved -- the
// real commits `git log oldSha..newSha` pulled in, inside the submodule.
public partial class PinBumpDialog : FluentWindow
{
    public sealed record SubmoduleSection(string Header, List<FileHistoryEntry> Commits);

    public PinBumpDialog(List<SubmoduleSection> sections)
    {
        InitializeComponent();
        SubmodulesList.ItemsSource = sections;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
