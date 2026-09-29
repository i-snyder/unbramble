using System.ComponentModel;
using UnBramble.Cli;

namespace UnBramble.Tests;

public class PackageUpdaterTests
{
    [Fact]
    public void Run_WinGetInstallation_StopsProcessesAndSchedulesUpgrade()
    {
        var calls = new List<string>();
        var statuses = new List<string>();
        var errors = new List<string>();
        using var stdOut = new StringWriter();
        using var stdErr = new StringWriter();

        var result = PackageUpdater.Run(
            new PackageUpdater.Dependencies(
                arguments =>
                {
                    calls.Add(string.Join(' ', arguments));
                    return new PackageUpdater.CommandResult(0, "registered", "");
                },
                () => { calls.Add("stop"); return 0; },
                pid => calls.Add($"schedule {pid}"),
                CurrentProcessId: 42,
                InstallationDirectory: @"C:\Apps\UnBramble"),
            statuses.Add,
            errors.Add,
            stdOut,
            stdErr);

        Assert.Equal(0, result);
        Assert.Equal(
        [
            "list --id i-snyder.unbramble --exact --accept-source-agreements --disable-interactivity",
            "stop",
            "schedule 42",
        ], calls);
        Assert.Contains("Package: i-snyder.unbramble (WinGet)", statuses);
        Assert.Contains(statuses, line => line.StartsWith("Update: scheduled", StringComparison.Ordinal));
        Assert.Empty(errors);
        Assert.Equal(string.Empty, stdOut.ToString());
        Assert.Equal(string.Empty, stdErr.ToString());
    }

    [Fact]
    public void Run_ManualInstallation_PrintsGuidanceWithoutStoppingProcesses()
    {
        var stopped = false;
        var scheduled = false;
        var statuses = new List<string>();
        var errors = new List<string>();
        using var stdOut = new StringWriter();
        using var stdErr = new StringWriter();

        var result = PackageUpdater.Run(
            new PackageUpdater.Dependencies(
                _ => new PackageUpdater.CommandResult(unchecked((int)0x8A150014), "No installed package found matching input criteria.\n", ""),
                () => { stopped = true; return 0; },
                _ => scheduled = true,
                CurrentProcessId: 42,
                InstallationDirectory: @"C:\Apps\UnBramble\"),
            statuses.Add,
            errors.Add,
            stdOut,
            stdErr);

        Assert.Equal(1, result);
        Assert.False(stopped);
        Assert.False(scheduled);
        Assert.Contains("Installation: not managed by WinGet", statuses);
        Assert.Contains(@"Location: C:\Apps\UnBramble", statuses);
        Assert.Contains(statuses, line => line.Contains("unbramble stop", StringComparison.Ordinal));
        Assert.Contains(statuses, line => line.Contains("releases/latest", StringComparison.Ordinal));
        Assert.Empty(errors);
        Assert.Equal(string.Empty, stdOut.ToString());
        Assert.Equal(string.Empty, stdErr.ToString());
    }

    [Fact]
    public void Run_WinGetProbeFailure_PreservesDiagnostic()
    {
        var errors = new List<string>();
        using var stdOut = new StringWriter();
        using var stdErr = new StringWriter();

        var result = PackageUpdater.Run(
            new PackageUpdater.Dependencies(
                _ => new PackageUpdater.CommandResult(23, "source unavailable\n", "network error\n"),
                () => 0,
                _ => { },
                CurrentProcessId: 42,
                InstallationDirectory: @"C:\Apps\UnBramble"),
            _ => { },
            errors.Add,
            stdOut,
            stdErr);

        Assert.Equal(1, result);
        Assert.Contains("source unavailable", stdOut.ToString());
        Assert.Contains("network error", stdErr.ToString());
        Assert.Contains(errors, line => line.Contains("exit code 23", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_MissingWinGet_PrintsFocusedError()
    {
        var errors = new List<string>();

        var result = PackageUpdater.Run(
            new PackageUpdater.Dependencies(
                _ => throw new Win32Exception("not found"),
                () => 0,
                _ => { },
                CurrentProcessId: 42,
                InstallationDirectory: @"C:\Apps\UnBramble"),
            _ => { },
            errors.Add,
            TextWriter.Null,
            TextWriter.Null);

        Assert.Equal(1, result);
        Assert.Contains(errors, line => line.Contains("winget", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, line => line.Contains("not found", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_StopFailure_DoesNotScheduleUpgrade()
    {
        var scheduled = false;

        var result = PackageUpdater.Run(
            new PackageUpdater.Dependencies(
                _ => new PackageUpdater.CommandResult(0, "", ""),
                () => 7,
                _ => scheduled = true,
                CurrentProcessId: 42,
                InstallationDirectory: @"C:\Apps\UnBramble"),
            _ => { },
            _ => { },
            TextWriter.Null,
            TextWriter.Null);

        Assert.Equal(7, result);
        Assert.False(scheduled);
    }

    [Fact]
    public void UpdateScript_WaitsForCallerAndRunsExactWinGetUpgrade()
    {
        var script = PackageUpdater.BuildUpdateScript();

        Assert.Contains("Wait-Process -Id $ParentProcessId", script, StringComparison.Ordinal);
        Assert.Contains(
            "winget.exe upgrade --id i-snyder.unbramble --exact --source winget --accept-source-agreements",
            script,
            StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $ScriptPath", script, StringComparison.Ordinal);
        Assert.Contains("Press Enter to close", script, StringComparison.Ordinal);
    }
}
