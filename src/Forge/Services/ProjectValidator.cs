using System.IO;

namespace Forge.Services;

public static class ProjectValidator
{
    public static bool IsForgeProject(string folder) =>
        File.Exists(Path.Combine(folder, "Tools", "ProjectWorkflow.psm1")) &&
        File.Exists(Path.Combine(folder, "project.config.json"));
}
