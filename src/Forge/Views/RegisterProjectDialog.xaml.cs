using System;
using System.IO;
using System.Windows;
using Forge.Services;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace Forge.Views;

// "Register a Project": point Forge at an existing Unreal project folder
// and either open it directly (already Forge-compatible) or scaffold the
// two files it needs (see ProjectScaffoldService).
public partial class RegisterProjectDialog : FluentWindow
{
    private string? _folder;
    private string? _uprojectPath;
    private bool _alreadyCompatible;

    public string? ResultProjectPath { get; private set; }

    public RegisterProjectDialog()
    {
        InitializeComponent();
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select the Unreal project's folder",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        _folder = dialog.SelectedPath;
        FolderBox.Text = _folder;
        Evaluate();
    }

    private void Evaluate()
    {
        if (_folder == null) return;

        if (ProjectValidator.IsForgeProject(_folder))
        {
            _alreadyCompatible = true;
            _uprojectPath = null;
            StatusText.Text = "This folder is already set up for Forge -- no changes needed.";
            FormPanel.Visibility = Visibility.Collapsed;
            ConfirmButton.Content = "Open";
            ConfirmButton.IsEnabled = true;
            return;
        }

        _alreadyCompatible = false;
        _uprojectPath = ProjectScaffoldService.FindUProject(_folder);
        if (_uprojectPath == null)
        {
            StatusText.Text = "No .uproject file found directly in this folder or one level inside it. Pick the project's own folder.";
            FormPanel.Visibility = Visibility.Collapsed;
            ConfirmButton.IsEnabled = false;
            return;
        }

        var name = Path.GetFileNameWithoutExtension(_uprojectPath);
        StatusText.Text = $"Found {name}.uproject. Forge will add Tools\\ProjectWorkflow.psm1 and project.config.json here.";
        TitleBox.Text = name;
        EditorTargetBox.Text = $"{name}Editor";
        FormPanel.Visibility = Visibility.Visible;
        ConfirmButton.Content = "Register";
        ConfirmButton.IsEnabled = true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (_folder == null) return;

        if (_alreadyCompatible)
        {
            ResultProjectPath = _folder;
            DialogResult = true;
            return;
        }

        if (_uprojectPath == null) return;
        var title = TitleBox.Text.Trim();
        var editorTarget = EditorTargetBox.Text.Trim();
        if (title.Length == 0 || editorTarget.Length == 0)
        {
            MessageBox.Show(this, "Fill in both fields first.", "Forge", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            ProjectScaffoldService.Scaffold(_folder, _uprojectPath, title, editorTarget);
            ResultProjectPath = _folder;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't set up this project: {ex.Message}", "Forge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
