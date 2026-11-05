using System;
using System.Windows.Media;
using Forge.Services;

namespace Forge.Models;

public enum LogLevel { Step, Success, Warning, Error, Debug, Info }

// One line in the Log tab. Level is classified from the PowerShell stream
// (Error/Warning/Verbose/Debug) plus, for plain Write-Host output, whatever
// ConsoleColor the module's own Write-Step/Write-Ok/Write-Warn/Write-Dim
// helpers chose -- so the Log tab's colors follow the module's intent
// instead of guessing from the text.
public sealed class LogEntry
{
    public DateTimeOffset Timestamp { get; } = DateTimeOffset.Now;
    public string Text { get; }
    public LogLevel Level { get; }

    public LogEntry(string text, LogLevel level)
    {
        Text = text;
        Level = level;
    }

    public string TimeText => Timestamp.ToString("HH:mm:ss");

    public bool IsHeading => Level == LogLevel.Step;

    public Brush Brush => Level switch
    {
        LogLevel.Step => (Brush)System.Windows.Application.Current.Resources["ForgeAccentBrush"],
        LogLevel.Success => (Brush)System.Windows.Application.Current.Resources["ForgeMatchBrush"],
        LogLevel.Warning => (Brush)System.Windows.Application.Current.Resources["ForgeWarnBrush"],
        LogLevel.Error => (Brush)System.Windows.Application.Current.Resources["ForgeDangerTextBrush"],
        _ => (Brush)System.Windows.Application.Current.Resources["TextFillColorPrimaryBrush"]
    };

    public double TextOpacity => Level == LogLevel.Debug ? 0.55 : 1.0;

    public static LogLevel Classify(HostOutputStream stream, ConsoleColor? color)
    {
        if (stream == HostOutputStream.Error) return LogLevel.Error;
        if (stream == HostOutputStream.Warning) return LogLevel.Warning;
        if (stream is HostOutputStream.Verbose or HostOutputStream.Debug) return LogLevel.Debug;

        return color switch
        {
            ConsoleColor.Green or ConsoleColor.DarkGreen => LogLevel.Success,
            ConsoleColor.Yellow or ConsoleColor.DarkYellow => LogLevel.Warning,
            ConsoleColor.Red or ConsoleColor.DarkRed => LogLevel.Error,
            ConsoleColor.Cyan or ConsoleColor.DarkCyan => LogLevel.Step,
            ConsoleColor.DarkGray or ConsoleColor.Gray => LogLevel.Debug,
            _ => LogLevel.Info
        };
    }
}
