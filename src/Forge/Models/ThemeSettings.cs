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

    // Ember-orange is Forge's own identity (the Fire24 icon, the name
    // itself) and happens to land close to Unreal Editor's own selection-
    // highlight orange -- a real point of kinship with UE5's UI rather than
    // a copy of it. Keeping the SAME accent family in both modes (instead
    // of switching to an unrelated blue in Dark, as this used to) is what
    // makes it read as one deliberate identity rather than a default-blue
    // dark mode bolted onto an orange light mode.
    public string LightAccent { get; set; } = "#E8621A";
    public string LightBackground { get; set; } = "#F7F5F2";
    public string LightIcon { get; set; } = "#E8621A";

    // A touch warmer than flat neutral charcoal (soot/forge, not generic
    // app-dark-mode gray), with a brighter, more saturated ember accent --
    // dark UI needs more pop from an accent than light UI does to read as
    // vibrant rather than muddy.
    public string DarkAccent { get; set; } = "#FF8A3D";
    public string DarkBackground { get; set; } = "#161412";
    public string DarkIcon { get; set; } = "#FF8A3D";
}
