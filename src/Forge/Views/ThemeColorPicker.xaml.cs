using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wpf.Ui.Controls;
using TextBox = System.Windows.Controls.TextBox;

namespace Forge.Views;

// A row of preset color swatches plus a custom picker (native Windows color
// dialog) and a hex text box -- used six times on the Settings > Theme page
// (accent/background/icon x light/dark). Deliberately not a DependencyProperty-
// based control: this is only ever built and wired from code-behind, so a
// plain Initialize/event pair is simpler than full XAML-binding plumbing.
public partial class ThemeColorPicker : UserControl
{
    public event Action<string>? ColorPicked;

    private string[] _presets = Array.Empty<string>();
    private string _selected = "#000000";

    public ThemeColorPicker()
    {
        InitializeComponent();
    }

    public void Initialize(IEnumerable<string> presets, string selectedHex)
    {
        _presets = presets.ToArray();
        _selected = selectedHex;
        Render();
    }

    public void SetSelected(string hex)
    {
        _selected = hex;
        Render();
    }

    private void Render()
    {
        SwatchPanel.Children.Clear();
        foreach (var hex in _presets)
            SwatchPanel.Children.Add(MakeSwatch(hex));
        SwatchPanel.Children.Add(MakeCustomSwatch());

        var hexBox = new TextBox
        {
            Width = 66,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            Text = _selected
        };
        hexBox.LostFocus += (_, _) => TryApplyHex(hexBox.Text, hexBox);
        hexBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) TryApplyHex(hexBox.Text, hexBox); };
        SwatchPanel.Children.Add(hexBox);
    }

    private void TryApplyHex(string text, TextBox box)
    {
        text = text.Trim();
        if (!text.StartsWith("#")) text = "#" + text;
        try
        {
            _ = (Color)ColorConverter.ConvertFromString(text);
            Pick(text);
        }
        catch
        {
            box.Text = _selected;
        }
    }

    private Border MakeSwatch(string hex)
    {
        var isSelected = string.Equals(hex, _selected, StringComparison.OrdinalIgnoreCase);
        var border = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
            BorderThickness = new Thickness(isSelected ? 2 : 0),
            BorderBrush = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
        };
        border.MouseLeftButtonUp += (_, _) => Pick(hex);
        return border;
    }

    private Border MakeCustomSwatch()
    {
        var isCustom = _presets.All(p => !string.Equals(p, _selected, StringComparison.OrdinalIgnoreCase));
        var border = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            Background = isCustom
                ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(_selected))
                : (Brush)Application.Current.Resources["ControlFillColorDefaultBrush"],
            BorderThickness = new Thickness(isCustom ? 2 : 1),
            BorderBrush = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
            ToolTip = "Custom color..."
        };
        if (!isCustom)
        {
            var icon = new SymbolIcon { Symbol = SymbolRegular.Eyedropper24, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            border.Child = icon;
        }
        border.MouseLeftButtonUp += (_, _) => OpenColorDialog();
        return border;
    }

    private void OpenColorDialog()
    {
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.ColorTranslator.FromHtml(_selected),
            FullOpen = true
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            var c = dialog.Color;
            Pick($"#{c.R:X2}{c.G:X2}{c.B:X2}");
        }
    }

    private void Pick(string hex)
    {
        _selected = hex;
        Render();
        ColorPicked?.Invoke(hex);
    }
}
