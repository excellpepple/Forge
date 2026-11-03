using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Forge.Services;

// Global, machine-level app state: which projects have been opened here.
// Distinct from the per-project Tools\.workflow-local.json (watch-branch
// choices etc.), which travels with the repo checkout, not the app install.
public sealed class AppSettings
{
    public List<string> RecentProjects { get; set; } = new();
}

public static class AppSettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Forge", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch
        {
            // A corrupt settings file should not stop the app from starting.
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static AppSettings AddRecent(string projectRoot)
    {
        var settings = Load();
        settings.RecentProjects.RemoveAll(p => string.Equals(p, projectRoot, StringComparison.OrdinalIgnoreCase));
        settings.RecentProjects.Insert(0, projectRoot);
        if (settings.RecentProjects.Count > 10)
            settings.RecentProjects.RemoveRange(10, settings.RecentProjects.Count - 10);
        Save(settings);
        return settings;
    }
}
