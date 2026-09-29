using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace UnBramble.Cli;

/// <summary>Updates a WinGet-managed installation after the running portable executable exits.</summary>
public static class PackageUpdater
{
    public const string PackageIdentifier = "i-snyder.unbramble";
    private const int PackageNotInstalledExitCode = unchecked((int)0x8A150014);

    public readonly record struct CommandResult(int ExitCode, string StdOut, string StdErr);

    public readonly record struct Dependencies(
        Func<IReadOnlyList<string>, CommandResult> RunWinget,
        Func<int> StopOtherProcesses,
        Action<int> ScheduleUpgrade,
        int CurrentProcessId,
        string InstallationDirectory)
    {
        public static Dependencies CreateReal(Func<int> stopOtherProcesses) => new(
            RunWingetCommand,
            stopOtherProcesses,
            ScheduleUpgradeAfterExit,
            Environment.ProcessId,
            Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory);
    }

    public static int Run(
        Dependencies dependencies,
        Action<string> writeStatus,
        Action<string> writeError,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        CommandResult registration;
        try
        {
            registration = dependencies.RunWinget(
            [
                "list",
                "--id", PackageIdentifier,
                "--exact",
                "--accept-source-agreements",
                "--disable-interactivity",
            ]);
        }
        catch (Win32Exception ex)
        {
            writeError($"Windows Package Manager ('winget') isn't available: {ex.Message}");
            return 1;
        }

        if (registration.ExitCode == PackageNotInstalledExitCode)
        {
            writeStatus("Installation: not managed by WinGet");
            writeStatus($"Location: {Path.TrimEndingDirectorySeparator(dependencies.InstallationDirectory)}");
            writeStatus(
                "Next: run 'unbramble stop', download the latest release from " +
                "https://github.com/i-snyder/unbramble/releases/latest, then replace the files in this folder.");
            return 1;
        }

        if (registration.ExitCode != 0)
        {
            Forward(registration.StdOut, standardOutput);
            Forward(registration.StdErr, standardError);
            writeError($"WinGet couldn't check for an installed '{PackageIdentifier}' package (exit code {registration.ExitCode}).");
            return 1;
        }

        writeStatus($"Package: {PackageIdentifier} (WinGet)");
        var stopResult = dependencies.StopOtherProcesses();
        if (stopResult != 0)
        {
            return stopResult;
        }

        try
        {
            dependencies.ScheduleUpgrade(dependencies.CurrentProcessId);
        }
        catch (Exception ex)
        {
            writeError($"could not open the WinGet updater: {ex.Message}");
            return 1;
        }

        writeStatus("Update: scheduled in a separate window after this process exits.");
        return 0;
    }

    public static string BuildUpdateScript() => $$"""
        param(
          [Parameter(Mandatory=$true)][int]$ParentProcessId,
          [Parameter(Mandatory=$true)][string]$ScriptPath
        )

        $ErrorActionPreference = 'Stop'
        $exitCode = 1

        try {
          $host.UI.RawUI.WindowTitle = 'UnBramble Update'
          Write-Host ''
          Write-Host 'UnBramble update'
          Write-Host 'Waiting for the running CLI to close...'
          Wait-Process -Id $ParentProcessId -ErrorAction SilentlyContinue

          Write-Host ''
          & winget.exe upgrade --id {{PackageIdentifier}} --exact --source winget --accept-source-agreements
          $exitCode = $LASTEXITCODE

          Write-Host ''
          if ($exitCode -eq 0) {
            Write-Host 'Update complete. Open a new terminal before running UnBramble again.' -ForegroundColor Green
          } else {
            Write-Host "WinGet could not update UnBramble (exit code $exitCode)." -ForegroundColor Red
          }
        } catch {
          Write-Host ''
          Write-Host "Update failed: $($_.Exception.Message)" -ForegroundColor Red
          $exitCode = 1
        } finally {
          Remove-Item -LiteralPath $ScriptPath -Force -ErrorAction SilentlyContinue
        }

        Write-Host ''
        [void](Read-Host 'Press Enter to close')
        exit $exitCode
        """;

    private static CommandResult RunWingetCommand(IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "winget.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.SystemDirectory,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("could not start Windows Package Manager ('winget').");
        var stdOut = process.StandardOutput.ReadToEndAsync();
        var stdErr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return new CommandResult(
            process.ExitCode,
            stdOut.GetAwaiter().GetResult(),
            stdErr.GetAwaiter().GetResult());
    }

    private static void ScheduleUpgradeAfterExit(int processId)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"unbramble-update-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(scriptPath, BuildUpdateScript(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        try
        {
            var powershellPath = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var cmdPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var startInfo = new ProcessStartInfo
            {
                FileName = cmdPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Environment.SystemDirectory,
            };
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/s");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("start");
            startInfo.ArgumentList.Add("UnBramble Update");
            startInfo.ArgumentList.Add(powershellPath);
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-ParentProcessId");
            startInfo.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-ScriptPath");
            startInfo.ArgumentList.Add(scriptPath);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("could not start the update helper.");
        }
        catch
        {
            File.Delete(scriptPath);
            throw;
        }
    }

    private static void Forward(string text, TextWriter writer)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        writer.Write(text);
        if (!text.EndsWith('\n'))
        {
            writer.WriteLine();
        }
    }
}
