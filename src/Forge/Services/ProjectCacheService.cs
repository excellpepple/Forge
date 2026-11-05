using System;
using System.IO;
using System.Text.Json;
using Forge.Models;

namespace Forge.Services;

// Reads/writes Tools/.workflow-cache.json -- see ProjectCache's own header
// comment for why this exists. Never fatal: a missing or corrupt cache file
// just means no fast-path paint this time, not an error the user sees.
public static class ProjectCacheService
{
    private static string GetPath(string projectRoot) => Path.Combine(projectRoot, "Tools", ".workflow-cache.json");

    public static ProjectCache? Load(string projectRoot)
    {
        try
        {
            var path = GetPath(projectRoot);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<ProjectCache>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string projectRoot, ProjectCache cache)
    {
        try
        {
            var path = GetPath(projectRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch
        {
            // Best-effort -- a failed cache write just means next open is
            // slow again, not a reason to disrupt whatever the user is doing.
        }
    }
}
