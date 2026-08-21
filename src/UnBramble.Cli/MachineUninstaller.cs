using System.Diagnostics;
using System.Text;

namespace UnBramble.Cli;

/// <summary>Removes this manual ZIP installation from the current user's PATH and schedules its
/// tightly validated installation directory for deletion after the running executable exits.</summary>
public static class MachineUninstaller
{
    private static readonly HashSet<string> AllowedInstallEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        "unbramble.exe",
        "e_sqlite3.dll",
        "LICENSES.md",
    };

    public sealed record Plan(
        string ExecutablePath,
        string InstallDirectory,
        string? OriginalUserPath,
        string? UpdatedUserPath,
        int RemovedPathEntries);

    public readonly record struct Dependencies(
        Action<string?> SetUserPath,
        Action<string, int> ScheduleDirectoryDeletion,
        int CurrentProcessId)
    {
        public static Dependencies CreateReal() => new(
            value => Environment.SetEnvironmentVariable("Path", value, EnvironmentVariableTarget.User),
            ScheduleDeletionAfterExit,
            Environment.ProcessId);
    }

    public static Plan Inspect(string executablePath, string? userPath)
    {
        var fullExecutablePath = Path.GetFullPath(executablePath);
        if (!string.Equals(Path.GetFileName(fullExecutablePath), "unbramble.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"machine uninstall requires an executable named 'unbramble.exe', not '{fullExecutablePath}'.");
        }

        if (!File.Exists(fullExecutablePath))
        {
            throw new FileNotFoundException("could not find the running UnBramble executable.", fullExecutablePath);
        }

        var installDirectory = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(fullExecutablePath)!);
        ValidateInstallDirectory(installDirectory);

        var segments = (userPath ?? string.Empty).Split(';').ToList();
        var removed = segments.RemoveAll(segment => PathEntryMatches(segment, installDirectory));
        var updated = removed == 0 ? userPath : string.Join(';', segments);
        return new Plan(fullExecutablePath, installDirectory, userPath, updated, removed);
    }

    public static void Execute(Plan plan, Dependencies dependencies)
    {
        var pathChanged = plan.RemovedPathEntries > 0;
        if (pathChanged)
        {
            dependencies.SetUserPath(plan.UpdatedUserPath);
        }

        try
        {
            dependencies.ScheduleDirectoryDeletion(plan.InstallDirectory, dependencies.CurrentProcessId);
        }
        catch
        {
            if (pathChanged)
            {
                dependencies.SetUserPath(plan.OriginalUserPath);
            }

            throw;
        }
    }

    public static string BuildCleanupScript() => """
        param(
          [Parameter(Mandatory=$true)][int]$ParentProcessId,
          [Parameter(Mandatory=$true)][string]$InstallDirectory,
          [Parameter(Mandatory=$true)][string]$ScriptPath
        )

        $ErrorActionPreference = 'SilentlyContinue'
        Wait-Process -Id $ParentProcessId -ErrorAction SilentlyContinue

        for ($attempt = 0; $attempt -lt 80; $attempt++) {
          Remove-Item -LiteralPath $InstallDirectory -Recurse -Force -ErrorAction SilentlyContinue
          if (-not (Test-Path -LiteralPath $InstallDirectory)) { break }
          Start-Sleep -Milliseconds 250
        }

        Remove-Item -LiteralPath $ScriptPath -Force -ErrorAction SilentlyContinue
        """;

    private static void ValidateInstallDirectory(string installDirectory)
    {
        if (!Directory.Exists(installDirectory))
        {
            throw new DirectoryNotFoundException($"installation directory not found: {installDirectory}");
        }

        var directoryInfo = new DirectoryInfo(installDirectory);
        if ((directoryInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException($"refusing to recursively remove reparse-point installation directory '{installDirectory}'.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetPathRoot(installDirectory)!);
        var protectedDirectories = new[]
        {
            root,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.SystemDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        if (protectedDirectories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.TrimEndingDirectorySeparator)
            .Any(path => string.Equals(path, installDirectory, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"refusing to remove protected directory '{installDirectory}'.");
        }

        var unexpected = Directory.EnumerateFileSystemEntries(installDirectory)
            .Where(path => !File.Exists(path) || !AllowedInstallEntries.Contains(Path.GetFileName(path)))
            .Select(Path.GetFileName)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unexpected.Count > 0)
        {
            throw new InvalidOperationException(
                $"refusing to remove '{installDirectory}' because it contains files not owned by the release: {string.Join(", ", unexpected)}. Remove this installation manually after checking the directory.");
        }
    }

    private static bool PathEntryMatches(string entry, string installDirectory)
    {
        var candidate = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
        if (candidate.Length == 0)
        {
            return false;
        }

        try
        {
            candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
            return string.Equals(candidate, installDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static void ScheduleDeletionAfterExit(string installDirectory, int processId)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"unbramble-uninstall-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(scriptPath, BuildCleanupScript(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        try
        {
            var powershellPath = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var startInfo = new ProcessStartInfo
            {
                FileName = powershellPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Environment.SystemDirectory,
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-WindowStyle");
            startInfo.ArgumentList.Add("Hidden");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-ParentProcessId");
            startInfo.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("-InstallDirectory");
            startInfo.ArgumentList.Add(installDirectory);
            startInfo.ArgumentList.Add("-ScriptPath");
            startInfo.ArgumentList.Add(scriptPath);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("could not start the uninstall cleanup helper.");
        }
        catch
        {
            File.Delete(scriptPath);
            throw;
        }
    }
}
