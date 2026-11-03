namespace Forge.Models;

// Mirrors the PSCustomObject returned by Get-BranchWatchStatus. Informational
// only: Ahead counts commits on origin/WatchBranch not yet in this checkout.
// Error is set instead of throwing when the check itself could not run
// (offline, branch renamed, etc.) so polling never interrupts the UI.
public sealed class BranchWatchStatus
{
    public string Path { get; set; } = "";
    public string WatchBranch { get; set; } = "";
    public int Ahead { get; set; }
    public string? Error { get; set; }
}
