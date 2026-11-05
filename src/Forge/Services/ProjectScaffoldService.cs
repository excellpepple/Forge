using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Forge.Services;

// "Register a Project": bootstraps the two files a folder needs to become
// Forge-compatible (Tools/ProjectWorkflow.psm1 + project.config.json) into
// a folder that already has a .uproject but wasn't built with Forge's
// superproject+submodule pattern. Deliberately scoped to the simple case --
// no separate Game/Engine submodules (GameSubmodulePath/EngineSubmodulePath
// are left empty, which Join-Path treats as "the project root itself" --
// verified against the real module). The engine is expected to be supplied
// afterward via Settings > Project > Engine location (LocalSettings.EnginePath),
// not through this scaffold.
public static class ProjectScaffoldService
{
    // Looks for a .uproject directly in the folder, or one level down (the
    // common case: picking the repo root of a project whose .uproject lives
    // in a subfolder, e.g. CORE-Super's own Game/IMGCORE/IMGCORE.uproject).
    public static string? FindUProject(string folder)
    {
        var direct = SafeGetFiles(folder);
        if (direct.Count > 0) return direct[0];

        foreach (var sub in SafeGetDirectories(folder))
        {
            var found = SafeGetFiles(sub);
            if (found.Count > 0) return found[0];
        }
        return null;
    }

    private static System.Collections.Generic.List<string> SafeGetFiles(string dir)
    {
        try { return Directory.GetFiles(dir, "*.uproject", SearchOption.TopDirectoryOnly).ToList(); }
        catch { return new(); }
    }

    private static System.Collections.Generic.List<string> SafeGetDirectories(string dir)
    {
        try { return Directory.GetDirectories(dir).ToList(); }
        catch { return new(); }
    }

    public static void Scaffold(string projectRoot, string uprojectAbsolutePath, string title, string editorTarget)
    {
        var toolsDir = Path.Combine(projectRoot, "Tools");
        Directory.CreateDirectory(toolsDir);

        var templateSource = Path.Combine(AppContext.BaseDirectory, "templates", "ProjectWorkflow.psm1");
        if (!File.Exists(templateSource))
            throw new InvalidOperationException($"Forge's bundled module template is missing at {templateSource}.");
        File.Copy(templateSource, Path.Combine(toolsDir, "ProjectWorkflow.psm1"), overwrite: true);

        var uprojectRelative = Path.GetRelativePath(projectRoot, uprojectAbsolutePath).Replace('\\', '/');

        var config = new
        {
            ProjectTitle = title,
            SuperRepoUrl = "",
            SuperRepoWeb = "",
            SuperBranch = "",
            RequiredSubmodules = Array.Empty<string>(),
            EngineSubmodulePath = "",
            GameSubmodulePath = "",
            UProjectRelativePath = uprojectRelative,
            EditorTarget = editorTarget,
            LfsRepos = Array.Empty<string>(),
            EditorProcessNames = new[] { "UnrealEditor", "UnrealEditor-Cmd" },
            Upstreams = Array.Empty<object>()
        };
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(projectRoot, "project.config.json"), json);
    }
}
