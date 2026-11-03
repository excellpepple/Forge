namespace Forge.Models;

public enum ThemeMode { Light, Dark, System }

// Persisted under AppSettings.Theme (%AppData%\Forge\settings.json). Mirrors
// the Settings > Theme page: a mode plus independently customizable accent,
// background and icon colors per light/dark mode. Colors are stored as
// "#RRGGBB" strings so they round-trip through JSON and WPF's ColorConverter
// without a custom converter.
public sealed class ThemeSettings
{
    public ThemeMode Mode { get; set; } = ThemeMode.System;

    public string LightAccent { get; set; } = "#D9681F";
    public string LightBackground { get; set; } = "#F7F5F2";
    public string LightIcon { get; set; } = "#D9681F";

    public string DarkAccent { get; set; } = "#7FACDA";
    public string DarkBackground { get; set; } = "#121212";
    public string DarkIcon { get; set; } = "#7FACDA";
}
