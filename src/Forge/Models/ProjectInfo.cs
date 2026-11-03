using System;

namespace Forge.Models;

// Mirrors the PSCustomObject returned by Get-ProjectInfo in
// Tools\ProjectWorkflow.psm1. Keep property names identical to the
// PowerShell property names -- PowerShellHost.ConvertFrom maps by name.
public sealed class ProjectInfo
{
    public string Title { get; set; } = "";
    public string Root { get; set; } = "";
    public string SuperBranch { get; set; } = "";
    public string SuperRepoUrl { get; set; } = "";
    public string SuperRepoWeb { get; set; } = "";
    public string[] RequiredSubmodules { get; set; } = Array.Empty<string>();
    public string EngineDir { get; set; } = "";
    public string GameDir { get; set; } = "";
    public string ProjectDir { get; set; } = "";
    public string UProject { get; set; } = "";
    public string EditorTarget { get; set; } = "";
    public string EditorExe { get; set; } = "";
    public string[] LfsRepos { get; set; } = Array.Empty<string>();
}
