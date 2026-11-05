using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Security;

namespace Forge.Services;

public enum HostOutputStream { Host, Error, Warning, Verbose, Debug }

public sealed class HostOutputEventArgs : EventArgs
{
    public string Text { get; }
    public HostOutputStream Stream { get; }

    // The module's own Write-Step/Write-Ok/Write-Warn/Write-Dim helpers
    // already pick a ConsoleColor per call (see ProjectWorkflow.psm1) --
    // carrying it through lets the Log tab color/classify each line the
    // same way the module intended, instead of guessing from its text.
    public ConsoleColor? Foreground { get; }

    public HostOutputEventArgs(string text, HostOutputStream stream, ConsoleColor? foreground = null)
    {
        Text = text;
        Stream = stream;
        Foreground = foreground;
    }
}

// Captures every Write-Host/Write-Warning/Write-Error/etc. call made by the
// imported module and raises it as an event instead of writing to a real
// console -- that is how the Log tab gets live output, with no changes
// needed to ProjectWorkflow.psm1's own Write-Step/Write-Ok/Write-Warn calls.
public sealed class ForgeHostUserInterface : PSHostUserInterface
{
    public event EventHandler<HostOutputEventArgs>? OutputReceived;

    public override PSHostRawUserInterface RawUI { get; } = new ForgeHostRawUserInterface();

    private void Raise(string text, HostOutputStream stream, ConsoleColor? foreground = null) => OutputReceived?.Invoke(this, new HostOutputEventArgs(text, stream, foreground));

    public override void Write(string value) => Raise(value, HostOutputStream.Host);
    public override void Write(ConsoleColor foregroundColor, ConsoleColor backgroundColor, string value) => Raise(value, HostOutputStream.Host, foregroundColor);
    public override void WriteLine(string value) => Raise(value + Environment.NewLine, HostOutputStream.Host);
    public override void WriteLine() => Raise(Environment.NewLine, HostOutputStream.Host);
    public override void WriteLine(ConsoleColor foregroundColor, ConsoleColor backgroundColor, string value) => Raise(value + Environment.NewLine, HostOutputStream.Host, foregroundColor);
    public override void WriteErrorLine(string value) => Raise(value + Environment.NewLine, HostOutputStream.Error);
    public override void WriteWarningLine(string value) => Raise(value + Environment.NewLine, HostOutputStream.Warning);
    public override void WriteVerboseLine(string value) => Raise(value + Environment.NewLine, HostOutputStream.Verbose);
    public override void WriteDebugLine(string value) => Raise(value + Environment.NewLine, HostOutputStream.Debug);

    // Build.bat's output reaches here via the console, not Write-Progress, so
    // there is nothing useful to render; a no-op avoids an exception from
    // the default base implementation.
    public override void WriteProgress(long sourceId, ProgressRecord record) { }

    public override Dictionary<string, PSObject> Prompt(string caption, string message, Collection<FieldDescription> descriptions) => new();
    public override int PromptForChoice(string caption, string message, Collection<ChoiceDescription> choices, int defaultChoice) => defaultChoice;
    public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName) => new(userName ?? "", new SecureString());
    public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName, PSCredentialTypes allowedCredentialTypes, PSCredentialUIOptions options) => new(userName ?? "", new SecureString());

    // The one Read-Host prompt in the shared module (Setup's engine-GUID
    // conflict check) offers a y/N choice; "N" (decline repointing an engine
    // another checkout on this machine uses) is the safe default when nobody
    // is sitting at a console to answer it.
    public override string ReadLine() => "N";
    public override SecureString ReadLineAsSecureString() => new();
}

// A minimal but non-throwing PSHostRawUserInterface. Some PowerShell
// internals (formatting, progress) probe this even when a script never
// calls $Host.UI.RawUI directly, so plausible fixed values are safer here
// than NotImplementedException.
public sealed class ForgeHostRawUserInterface : PSHostRawUserInterface
{
    public override ConsoleColor BackgroundColor { get; set; } = ConsoleColor.Black;
    public override ConsoleColor ForegroundColor { get; set; } = ConsoleColor.White;
    public override Size BufferSize { get; set; } = new(160, 9999);
    public override Coordinates CursorPosition { get; set; } = new(0, 0);
    public override int CursorSize { get; set; } = 25;
    public override bool KeyAvailable => false;
    public override Size MaxPhysicalWindowSize => new(160, 80);
    public override Size MaxWindowSize => new(160, 80);
    public override Size WindowSize { get; set; } = new(160, 80);
    public override Coordinates WindowPosition { get; set; } = new(0, 0);
    public override string WindowTitle { get; set; } = "Forge";

    public override void FlushInputBuffer() { }
    public override BufferCell[,] GetBufferContents(Rectangle rectangle) => new BufferCell[0, 0];
    public override KeyInfo ReadKey(ReadKeyOptions options) => new();
    public override void ScrollBufferContents(Rectangle source, Coordinates destination, Rectangle clip, BufferCell fill) { }
    public override void SetBufferContents(Rectangle rectangle, BufferCell fill) { }
    public override void SetBufferContents(Coordinates origin, BufferCell[,] contents) { }
}

public sealed class ForgeHost : PSHost
{
    private readonly Guid _instanceId = Guid.NewGuid();

    public ForgeHostUserInterface ForgeUI { get; } = new();

    // A module calling `exit` would otherwise tear down the whole app, not
    // just the operation that failed. ProjectWorkflow.psm1's Fail() no longer
    // does this (it throws instead), but overriding this is defense in depth
    // for anything else that might.
    public int? LastShouldExitCode { get; private set; }

    public override Guid InstanceId => _instanceId;
    public override string Name => "ForgeHost";
    public override Version Version { get; } = new(1, 0, 0);
    public override PSHostUserInterface UI => ForgeUI;
    public override CultureInfo CurrentCulture => CultureInfo.CurrentCulture;
    public override CultureInfo CurrentUICulture => CultureInfo.CurrentUICulture;
    public override PSObject? PrivateData => null;

    public override void EnterNestedPrompt() { }
    public override void ExitNestedPrompt() { }
    public override void NotifyBeginApplication() { }
    public override void NotifyEndApplication() { }
    public override void SetShouldExit(int exitCode) => LastShouldExitCode = exitCode;
}
