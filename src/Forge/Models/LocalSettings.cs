using System.Collections.Generic;

namespace Forge.Models;

// Mirrors Tools\.workflow-local.json (per-checkout, gitignored). Keyed by
// submodule path, e.g. { "UnrealEngine": "5.8", "Game": "development" }.
public sealed class LocalSettings
{
    public Dictionary<string, string> WatchBranches { get; set; } = new();
}
