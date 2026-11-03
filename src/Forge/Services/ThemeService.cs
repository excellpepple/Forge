using System;
using System.Windows;
using System.Windows.Media;
using Forge.Models;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Forge.Services;

// Owns Forge's theme: persists ThemeSettings (inside AppSettings.json),
// drives WPF-UI's own theme/backdrop so its stock controls follow along, and
// maintains a set of "Forge*Brush" DynamicResource entries that the app's
// own custom-drawn parts (sidebar, submodule badges, the fail banner) bind
// to. WPF-UI handles Light/Dark switching and system-theme watching; this
// class layers the user's chosen accent/background/icon colors on top of
// whichever mode is active.
public sealed class ThemeService
{
    private readonly FluentWindow _window;

    public ThemeSettings Settings { get; }

    public ThemeService(FluentWindow window)
    {
        _window = window;
        Settings = AppSettingsService.Load().Theme;
        ApplicationThemeManager.Changed += OnWpfUiThemeChanged;
    }

    // Call once the window is constructed. Starts (or stops) following the
    // OS theme depending on Settings.Mode, then paints Forge's own brushes
    // for whichever mode that resolves to.
    public void ApplyAndWatch()
    {
        if (Settings.Mode == ThemeMode.System)
        {
            SystemThemeWatcher.Watch(_window, WindowBackdropType.Mica, updateAccents: false);
        }
        else
        {
            // UnWatch throws if the window has never been loaded yet (true
            // the first time ApplyAndWatch runs, from the constructor, for
            // anyone whose saved settings already say Light/Dark) -- nothing
            // to unwatch in that case anyway.
            if (_window.IsLoaded) SystemThemeWatcher.UnWatch(_window);
            var appTheme = Settings.Mode == ThemeMode.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
            ApplicationThemeManager.Apply(appTheme, WindowBackdropType.Mica, updateAccent: false);
        }
        ApplyForgeBrushes();
    }

    public void SetMode(ThemeMode mode)
    {
        Settings.Mode = mode;
        Save();
        ApplyAndWatch();
    }

    public void SetLightAccent(string hex) { Settings.LightAccent = hex; Save(); ApplyForgeBrushes(); }
    public void SetLightBackground(string hex) { Settings.LightBackground = hex; Save(); ApplyForgeBrushes(); }
    public void SetLightIcon(string hex) { Settings.LightIcon = hex; Save(); ApplyForgeBrushes(); }
    public void SetDarkAccent(string hex) { Settings.DarkAccent = hex; Save(); ApplyForgeBrushes(); }
    public void SetDarkBackground(string hex) { Settings.DarkBackground = hex; Save(); ApplyForgeBrushes(); }
    public void SetDarkIcon(string hex) { Settings.DarkIcon = hex; Save(); ApplyForgeBrushes(); }

    private void Save()
    {
        var all = AppSettingsService.Load();
        all.Theme = Settings;
        AppSettingsService.Save(all);
    }

    // Fires when WPF-UI's own theme flips (including via SystemThemeWatcher
    // reacting to an OS theme change in System mode) -- re-paint Forge's
    // brushes for whichever mode it just switched to.
    private void OnWpfUiThemeChanged(ApplicationTheme currentApplicationTheme, Color systemAccent) => ApplyForgeBrushes();

    private static bool IsEffectivelyDark() => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

    private void ApplyForgeBrushes()
    {
        var dark = IsEffectivelyDark();
        var accent = ParseColor(dark ? Settings.DarkAccent : Settings.LightAccent);
        var bg = ParseColor(dark ? Settings.DarkBackground : Settings.LightBackground);
        var icon = ParseColor(dark ? Settings.DarkIcon : Settings.LightIcon);

        SetBrush("ForgeAccentBrush", accent);
        SetBrush("ForgeAccentSoftBrush", Mix(accent, dark ? Colors.Black : Colors.White, dark ? 0.78 : 0.85));
        SetBrush("ForgeOnAccentBrush", ReadableOn(accent));
        SetBrush("ForgeBackgroundBrush", bg);
        SetBrush("ForgeSidebarBackgroundBrush", Mix(bg, Colors.Black, dark ? 0.3 : 0.06));
        SetBrush("ForgeIconBrush", icon);

        // Semantic colors are not user-customizable -- see Section 4.2 of the
        // design discussion: status meaning stays separate from the accent.
        SetBrush("ForgeMatchBrush", ParseColor(dark ? "#5DCAA5" : "#0F6E56"));
        SetBrush("ForgeMatchBgBrush", ParseColor(dark ? "#0C2420" : "#E1F5EE"));
        SetBrush("ForgeWarnBrush", ParseColor(dark ? "#EFB56A" : "#8A5A0B"));
        SetBrush("ForgeWarnBgBrush", ParseColor(dark ? "#2E2410" : "#FAEEDA"));
        SetBrush("ForgeDangerTextBrush", ParseColor(dark ? "#F0997B" : "#993C1D"));
        SetBrush("ForgeDangerBgBrush", ParseColor(dark ? "#2E140C" : "#FAECE7"));
        SetBrush("ForgeDangerFillBrush", ParseColor(dark ? "#7A2D24" : "#C0392B"));
        SetBrush("ForgeOnDangerBrush", ParseColor(dark ? "#FFECE8" : "#FFFFFF"));
    }

    private static void SetBrush(string key, Color color) =>
        Application.Current.Resources[key] = new SolidColorBrush(color);

    private static Color ParseColor(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static Color Mix(Color c, Color target, double amount)
    {
        byte Blend(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
        return Color.FromRgb(Blend(c.R, target.R), Blend(c.G, target.G), Blend(c.B, target.B));
    }

    // White or near-black text, whichever reads better on the given fill.
    private static Color ReadableOn(Color fill)
    {
        var luminance = 0.299 * fill.R + 0.587 * fill.G + 0.114 * fill.B;
        return luminance > 150 ? Color.FromRgb(0x0A, 0x14, 0x20) : Colors.White;
    }
}
