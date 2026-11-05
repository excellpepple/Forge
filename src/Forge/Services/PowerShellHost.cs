using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Threading;
using System.Threading.Tasks;
using Forge.Models;

namespace Forge.Services;

// A deliberate, expected failure -- the message is exactly what
// ProjectWorkflow.psm1's Fail() printed, meant to be shown to the user
// verbatim (as the Log tab's banner), not logged as a bug.
public sealed class ForgeWorkflowException : Exception
{
    public ForgeWorkflowException(string message) : base(message) { }
}

// Anything else: a real bug, a missing file, a .NET exception that escaped
// the PowerShell pipeline. Shown differently from ForgeWorkflowException so
// the user can tell "this operation refused" from "something broke".
public sealed class ForgeUnexpectedException : Exception
{
    public ForgeUnexpectedException(string message, Exception? inner) : base(message, inner) { }
}

// Hosts one PowerShell runspace per opened project, imports that project's
// Tools\ProjectWorkflow.psm1, and exposes its functions as async C# calls.
// Pipeline invocation is synchronous, so every call runs on a background
// thread via Task.Run; callers are expected to await from the UI thread.
public sealed class PowerShellHost : IDisposable
{
    private readonly ForgeHost _host;
    private readonly Runspace _runspace;

    // A Runspace only runs one pipeline at a time. Background work (branch
    // prefetch) and foreground work (the user clicking Sync) now both go
    // through this host, so calls are queued here instead of leaving two
    // threads race to use the same runspace, which throws.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public event EventHandler<HostOutputEventArgs>? OutputReceived
    {
        add => _host.ForgeUI.OutputReceived += value;
        remove => _host.ForgeUI.OutputReceived -= value;
    }

    public PowerShellHost()
    {
        _host = new ForgeHost();
        var iss = InitialSessionState.CreateDefault();

        // Matches what Setup.bat/Sync.bat/Launch.bat already do on the CLI
        // side (-ExecutionPolicy Bypass) -- without it, Windows' default
        // Restricted policy refuses to import a local .psm1 at all. This is
        // scoped to Forge's own in-process runspace only; it does not touch
        // the system-wide execution policy.
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;

        _runspace = RunspaceFactory.CreateRunspace(_host, iss);
        _runspace.Open();
    }

    public async Task ImportProjectAsync(string projectRoot)
    {
        var moduleFile = Path.Combine(projectRoot, "Tools", "ProjectWorkflow.psm1");
        if (!File.Exists(moduleFile))
            throw new ForgeUnexpectedException($"{moduleFile} was not found. This folder is not a Forge-compatible project.", null);

        await InvokeAsync(ps => ps.AddCommand("Import-Module")
            .AddParameter("Name", moduleFile)
            .AddParameter("Force", true)
            .AddParameter("WarningAction", "SilentlyContinue"));
    }

    public async Task<ProjectInfo> GetProjectInfoAsync()
    {
        var results = await InvokeAsync(ps => ps.AddCommand("Get-ProjectInfo"));
        return ConvertFrom<ProjectInfo>(results.Single());
    }

    public async Task<List<SubmoduleState>> GetSubmoduleStatesAsync()
    {
        var results = await InvokeAsync(ps => ps.AddCommand("Get-SubmoduleStates"));
        return results.Select(ConvertFrom<SubmoduleState>).ToList();
    }

    // only == null -> full sync, matching Sync.bat exactly. A non-null,
    // non-empty list restricts the update to those top-level submodules
    // (the Build tab's selective-sync checklist).
    public Task InvokeSyncAsync(IEnumerable<string>? only = null) => InvokeAsync(ps =>
    {
        ps.AddCommand("Invoke-ProjectSync");
        var onlyArray = only?.ToArray();
        if (onlyArray is { Length: > 0 }) ps.AddParameter("Only", onlyArray);
    });

    public Task InvokeBuildAsync(bool noBuild, bool buildOnly, bool rebuild, string configuration, bool skipSyncCheck) => InvokeAsync(ps =>
        ps.AddCommand("Invoke-ProjectBuild")
            .AddParameter("NoBuild", noBuild)
            .AddParameter("BuildOnly", buildOnly)
            .AddParameter("Rebuild", rebuild)
            .AddParameter("Configuration", configuration)
            .AddParameter("SkipSyncCheck", skipSyncCheck));

    public Task InvokeSetupAsync(bool skipEngineDeps, bool skipRegister, bool skipGenerate, bool force) => InvokeAsync(ps =>
        ps.AddCommand("Invoke-ProjectSetup")
            .AddParameter("SkipEngineDeps", skipEngineDeps)
            .AddParameter("SkipRegister", skipRegister)
            .AddParameter("SkipGenerate", skipGenerate)
            .AddParameter("Force", force));

    public Task SetSubmoduleBranchAsync(string path, string branch) => InvokeAsync(ps =>
        ps.AddCommand("Set-SubmoduleBranch").AddParameter("Path", path).AddParameter("Branch", branch));

    public async Task<List<string>> GetSubmoduleBranchesAsync(string path)
    {
        var results = await InvokeAsync(ps => ps.AddCommand("Get-SubmoduleBranches").AddParameter("Path", path));
        return results.Select(r => r.BaseObject.ToString() ?? "").Where(s => s.Length > 0).ToList();
    }

    public async Task<List<DocEntry>> GetProjectDocsAsync()
    {
        var results = await InvokeAsync(ps => ps.AddCommand("Get-ProjectDocs"));
        return results.Where(r => r != null).Select(r => ConvertFrom<DocEntry>(r)).ToList();
    }

    public async Task<BranchWatchStatus> GetBranchWatchStatusAsync(string path, string watchBranch)
    {
        var results = await InvokeAsync(ps => ps.AddCommand("Get-BranchWatchStatus")
            .AddParameter("Path", path).AddParameter("WatchBranch", watchBranch));
        return ConvertFrom<BranchWatchStatus>(results.Single());
    }

    public async Task<LocalSettings> GetLocalSettingsAsync()
    {
        var results = await InvokeAsync(ps => ps.AddCommand("Get-WorkflowLocalSettings"));
        return ConvertLocalSettings(results.SingleOrDefault());
    }

    public Task SetLocalSettingsAsync(LocalSettings settings)
    {
        var watchBranches = new PSObject();
        foreach (var kv in settings.WatchBranches)
            watchBranches.Properties.Add(new PSNoteProperty(kv.Key, kv.Value));
        var payload = new PSObject();
        payload.Properties.Add(new PSNoteProperty("WatchBranches", watchBranches));
        payload.Properties.Add(new PSNoteProperty("EnableLfsLocking", (object?)settings.EnableLfsLocking));
        payload.Properties.Add(new PSNoteProperty("NotifyOnUnlock", settings.NotifyOnUnlock.ToArray()));
        payload.Properties.Add(new PSNoteProperty("EnginePath", (object?)settings.EnginePath));
        return InvokeAsync(ps => ps.AddCommand("Set-WorkflowLocalSettings").AddParameter("Settings", payload));
    }

    private async Task<Collection<PSObject>> InvokeAsync(Action<PowerShell> configure)
    {
        await _gate.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                using var ps = PowerShell.Create();
                ps.Runspace = _runspace;
                configure(ps);

                Collection<PSObject> results;
                try
                {
                    results = ps.Invoke();
                }
                catch (Exception ex)
                {
                    throw Translate(ex);
                }

                // A terminating error raised inside the pipeline (Fail()'s throw)
                // does not always escape Invoke() as a .NET exception -- it can
                // just stop the pipeline and land here instead. Check both.
                if (ps.Streams.Error.Count > 0)
                {
                    var last = ps.Streams.Error[^1];
                    throw Translate(last.Exception ?? new InvalidOperationException(last.ToString()));
                }
                return results;
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    // Walks the exception chain looking for ProjectWorkflow.WorkflowFailException
    // by full type name rather than by reference: that type is compiled at
    // module-import time via Add-Type, in an assembly this project never
    // references directly, so it cannot be caught with a C# `catch` clause.
    private static Exception Translate(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current.GetType().FullName == "ProjectWorkflow.WorkflowFailException")
                return new ForgeWorkflowException(current.Message);
        }
        return new ForgeUnexpectedException(ex.Message, ex);
    }

    private static T ConvertFrom<T>(PSObject? obj) where T : new()
    {
        var result = new T();
        if (obj == null) return result;
        foreach (var prop in typeof(T).GetProperties())
        {
            var psProp = obj.Properties[prop.Name];
            if (psProp?.Value == null) continue;

            if (prop.PropertyType == typeof(string[]))
            {
                var items = psProp.Value is IEnumerable enumerable and not string
                    ? enumerable.Cast<object>().Select(o => o?.ToString() ?? "").ToArray()
                    : new[] { psProp.Value.ToString() ?? "" };
                prop.SetValue(result, items);
            }
            else if (prop.PropertyType == typeof(int))
            {
                prop.SetValue(result, Convert.ToInt32(psProp.Value));
            }
            else if (prop.PropertyType == typeof(bool))
            {
                prop.SetValue(result, Convert.ToBoolean(psProp.Value));
            }
            else
            {
                prop.SetValue(result, psProp.Value.ToString());
            }
        }
        return result;
    }

    private static LocalSettings ConvertLocalSettings(PSObject? obj)
    {
        var result = new LocalSettings();
        if (obj == null) return result;

        if (obj.Properties["WatchBranches"]?.Value is PSObject watchBranches)
        {
            foreach (var p in watchBranches.Properties)
                result.WatchBranches[p.Name] = p.Value?.ToString() ?? "";
        }

        var lfsValue = obj.Properties["EnableLfsLocking"]?.Value;
        if (lfsValue != null) result.EnableLfsLocking = Convert.ToBoolean(lfsValue);

        if (obj.Properties["NotifyOnUnlock"]?.Value is IEnumerable notifyList and not string)
            result.NotifyOnUnlock = notifyList.Cast<object>().Select(o => o?.ToString() ?? "").Where(s => s.Length > 0).ToList();

        var enginePathValue = obj.Properties["EnginePath"]?.Value?.ToString();
        if (!string.IsNullOrEmpty(enginePathValue)) result.EnginePath = enginePathValue;

        return result;
    }

    public void Dispose()
    {
        _runspace.Close();
        _runspace.Dispose();
    }
}
