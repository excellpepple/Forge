namespace Forge.Models;

// One entry from a project's configurable "Docs" list (project.config.json),
// or one of Forge's own bundled docs. Type is "markdown" (rendered inline in
// the Help tab) or "pdf" (opened in the system's default viewer).
public sealed class DocEntry
{
    public string Title { get; set; } = "";
    public string Path { get; set; } = "";
    public string Type { get; set; } = "markdown";

    // True for a doc Forge ships with itself (explains Forge), false for one
    // the opened project declares (explains the project). Drives which
    // section of the Help tab's picker it shows under, and which base folder
    // Path resolves against.
    public bool IsBuiltIn { get; set; }
}
