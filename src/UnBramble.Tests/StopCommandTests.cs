using System.Diagnostics;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// Real-process integration tests for `unbramble stop`: it kills any other live `unbramble.exe`
/// process so a stray watcher never has to be killed by hand in Task Manager. These spawn genuine
/// OS processes rather than driving Program.Main in-process via <see cref="CliRunner"/> (unlike
/// most of this suite) -- `stop`'s entire job is finding and killing a SEPARATE process by image
/// name, which only a real subprocess can exercise honestly.
///
/// Both tests run against a private, uniquely-renamed copy of the built exe (<see
/// cref="IsolatedCliCopy"/>) rather than the shared "unbramble.exe" name -- see that class's own
/// doc comment for why: `stop` matches by image name system-wide with no project scoping, by
/// design, so running it against the real name here could kill an unrelated, genuinely running
/// unbramble.exe process elsewhere on the machine.
/// </summary>
public class StopCommandTests
{
    [Fact]
    public void Stop_NothingRunning_PrintsNoneFoundAndExitsZero()
    {
        using var cli = IsolatedCliCopy.Create();

        var (exitCode, stdOut, stdErr) = RunExe(cli.ExePath, "stop");

        Assert.True(exitCode == 0, $"expected exit 0, got {exitCode}\n-- stdout --\n{stdOut}\n-- stderr --\n{stdErr}");
        Assert.Contains("No unbramble processes running.", stdOut);
    }

    [Fact]
    public void Stop_RealWatcherRunning_KillsItAndReportsWhichProjectItWasWatching()
    {
        using var fixture = FixtureCopy.Create();
        using var cli = IsolatedCliCopy.Create();

        var watchStart = new ProcessStartInfo(cli.ExePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        watchStart.ArgumentList.Add("watch-worker");
        watchStart.ArgumentList.Add(fixture.Root);

        using var watchProcess = Process.Start(watchStart) ?? throw new InvalidOperationException("failed to start watcher worker");

        // The watcher runs indefinitely and keeps writing cs-analysis diagnostics to stderr --
        // drain both redirected streams asynchronously so a full OS pipe buffer never blocks it.
        watchProcess.OutputDataReceived += (_, _) => { };
        watchProcess.ErrorDataReceived += (_, _) => { };
        watchProcess.BeginOutputReadLine();
        watchProcess.BeginErrorReadLine();

        try
        {
            var heartbeatPath = Path.Combine(fixture.Root, ".unbramble", "watcher.heartbeat");
            Poll.Until(() => File.Exists(heartbeatPath), message: "watcher never wrote a heartbeat file");
            Assert.False(watchProcess.HasExited, "watcher process exited unexpectedly before 'stop' ran");

            var (exitCode, stdOut, stdErr) = RunExe(cli.ExePath, "stop", "-p", fixture.Root);

            Assert.True(exitCode == 0, $"expected exit 0, got {exitCode}\n-- stdout --\n{stdOut}\n-- stderr --\n{stdErr}");
            Assert.Contains($"PID {watchProcess.Id}", stdOut);
            Assert.Contains($"was watching {fixture.Root}", stdOut);

            Poll.Until(() => watchProcess.HasExited, message: "watcher process did not actually exit after 'stop'");
        }
        finally
        {
            if (!watchProcess.HasExited)
            {
                watchProcess.Kill();
            }
        }
    }

    private static (int ExitCode, string StdOut, string StdErr) RunExe(string exePath, params string[] args)
    {
        var startInfo = new ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"failed to start {exePath}");
        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, stdOut, stdErr);
    }
}
