using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;

namespace Forge.Services;

// Snapshot/kill helper for actually stopping a build -- PowerShell's own
// pipeline Stop() cannot do it. Invoke-ProjectBuild compiles via a blocking
// `& cmd.exe /c "Build.bat ..."` call (see ProjectWorkflow.psm1), and
// PowerShell can only interrupt a pipeline at a script checkpoint, never a
// thread that's blocked waiting on a native child process. cmd.exe (and
// everything it spawns -- Build.bat, UnrealBuildTool, cl.exe/link.exe) is a
// real OS child of Forge.exe, so killing that subtree is the only way to
// actually stop it.
public static class ProcessTreeKiller
{
    // Call right before starting the operation you might need to cancel.
    public static HashSet<int> SnapshotChildPids() => new(FindDirectChildPids(Environment.ProcessId));

    // Kills every CURRENT direct child of Forge.exe that was not in the
    // snapshot -- i.e. spawned by this operation, not something that was
    // already running (an editor launched by an earlier build, say). Each
    // is killed with taskkill's /T so its own descendants (Build.bat ->
    // UnrealBuildTool -> cl.exe/link.exe) die with it. Never touches an
    // UnrealEditor* process, even a brand new one, as a second safety net
    // in case the build had already reached the launch-the-editor step.
    public static void KillNewChildren(HashSet<int> before)
    {
        foreach (var pid in FindDirectChildPids(Environment.ProcessId))
        {
            if (before.Contains(pid)) continue;
            TryKillTree(pid);
        }
    }

    private static void TryKillTree(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            if (proc.ProcessName.StartsWith("UnrealEditor", StringComparison.OrdinalIgnoreCase)) return;
        }
        catch (ArgumentException) { return; } // already exited

        try
        {
            using var killer = Process.Start(new ProcessStartInfo("taskkill.exe", $"/PID {pid} /T /F")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            killer?.WaitForExit(3000);
        }
        catch { /* best-effort -- nothing more we can do if taskkill itself fails to launch */ }
    }

    private static List<int> FindDirectChildPids(int parentPid)
    {
        var result = new List<int>();
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT ProcessId FROM Win32_Process WHERE ParentProcessId = {parentPid}");
            foreach (ManagementObject mo in searcher.Get())
                result.Add(Convert.ToInt32((uint)mo["ProcessId"]));
        }
        catch { /* WMI unavailable -- nothing more we can do here */ }
        return result;
    }
}
