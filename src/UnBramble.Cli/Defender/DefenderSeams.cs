using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace UnBramble.Cli.Defender;

public readonly record struct PowerShellResult(int ExitCode, string StdOut, string StdErr);

/// <summary>
/// Shells to `powershell.exe -NoProfile -NonInteractive -EncodedCommand &lt;base64&gt;` -- the
/// AOT-safe way to touch Defender (plan: `UnBramble.Cli` publishes `PublishAot=true`, so
/// `System.Management`/WMI client libraries are a trim/AOT hazard; shelling out matches what
/// Rider's own bundled script does). The one seam every Defender-facing check/apply call goes
/// through, so tests can fake it instead of touching the real machine's Defender config.
/// </summary>
public interface IPowerShellRunner
{
    PowerShellResult Run(string script);
}

/// <summary>Reads `HKLM\SOFTWARE\Microsoft\Windows Defender\Features\TPExclusions` -- a plain,
/// non-elevated registry read. 1 means exclusions are tamper-protected on this machine; never
/// written by this tool.</summary>
public interface IDefenderRegistryReader
{
    bool ExclusionsAreTamperProtected();
}

/// <summary>Shells to `fsutil devdrv query &lt;drive&gt;` -- Dev Drive volumes default to Defender
/// "performance mode," so exclusions are largely unnecessary there.</summary>
public interface IDevDriveChecker
{
    bool IsDevDrive(string path);
}

public enum ElevatedLaunchOutcome
{
    Completed,
    UserCancelled,
    Failed,
}

public readonly record struct ElevatedLaunchResult(ElevatedLaunchOutcome Outcome, int ExitCode, string? ErrorMessage);

/// <summary>
/// The one UAC-prompting launch this feature ever performs: it elevates <c>powershell.exe</c>
/// DIRECTLY (a single elevation hop) via `-File &lt;temp .ps1 path&gt;`, and that one elevated
/// PowerShell process reads the plan file and writes the result file itself (plan-in / result-out,
/// exactly the contract the non-elevated parent already reads back). One prompt per invocation, no
/// persisted elevation.
///
/// <para><b>History -- three real bugs found in this feature in a row, each confirmed on a live
/// elevated run on the author's own machine:</b></para>
/// <para>1) The ORIGINAL design left `ProcessStartInfo.WorkingDirectory` unset on both the elevated
/// self-relaunch and the inner `powershell.exe` call, so both silently inherited the calling
/// process's own ambient current directory (fixed by pinning `WorkingDirectory` -- see
/// <see cref="DefenderProcessLaunchBuilder"/>'s doc comments).</para>
/// <para>2) After that fix, the design was still TWO elevation hops -- elevate <c>unbramble.exe</c>
/// itself (unsigned) into a hidden `defender-apply` verb, which then spawned
/// `powershell.exe -EncodedCommand ...` as its own direct child. This was suspected (with real
/// citations for Defender ASR/behavior-monitoring rules that target exactly this "unsigned process
/// spawns encoded PowerShell" shape) to be the cause of a live "Access is denied" failure and was
/// collapsed to a single hop (elevate `powershell.exe` directly, so its actual OS-level parent is
/// the trusted Windows AppInfo service, not <c>unbramble.exe</c>).</para>
/// <para>3) That single-hop fix STILL failed identically on a live retest -- with a real UAC prompt
/// shown and approved, and even from a caller that was already running elevated, and regardless of
/// which shell (`pwsh`/`powershell`) was used to invoke it -- which disproved the ASR/parent-trust
/// theory entirely (confirmed further by `Get-MpPreference`'s `AttackSurfaceReductionRules_Ids`
/// being completely empty on the test machine: no ASR rules were even active). The actual cause:
/// the script was being passed as a base64 `-EncodedCommand`, and for a real multi-entry plan that
/// argument string is several thousand characters (measured ~8,200 for a typical plan) --
/// `ShellExecuteEx`'s `lpParameters` has a real, documented practical limit around ~2,000
/// characters, past which it can fail with exactly this "Access is denied"-shaped error, especially
/// combined with the `runas` verb. This explains why every environmental variable tried (working
/// directory, parent process trust, calling shell, already-elevated-or-not) changed nothing -- the
/// one constant across every failed attempt was the long encoded command line. Fixed by writing the
/// script to a short-lived temp `.ps1` file and invoking `-File &lt;path&gt;` instead, collapsing
/// the argument string from ~8,200 characters to under 150.</para>
/// </summary>
public interface IElevatedPowerShellRunner
{
    /// <summary>Elevate `powershell.exe` and run <paramref name="script"/> under it (via a short-lived
    /// temp `.ps1` file -- see this interface's own doc comment for why a file rather than an inline
    /// encoded command). The script is responsible for writing the result file it was built with;
    /// this seam only reports whether the elevated process launched, was UAC-cancelled, or failed to
    /// start.</summary>
    ElevatedLaunchResult RunElevated(string script);
}

/// <summary>
/// Builds the <see cref="ProcessStartInfo"/> objects for the two real (non-faked) process launches
/// in this feature, split out into a public static type -- rather than left as private methods on
/// the `internal` <see cref="RealPowerShellRunner"/>/<see cref="RealElevatedPowerShellRunner"/>
/// classes -- specifically so <c>UnBramble.Tests</c> can assert on the constructed
/// <see cref="ProcessStartInfo"/> without starting a real process. This project deliberately grants
/// no `InternalsVisibleTo` to the test assembly (see e.g. `HomeCommandTests`' own doc comment), so
/// anything the test suite needs to reach has to be public on its own merits.
/// </summary>
public static class DefenderProcessLaunchBuilder
{
    /// <summary>Builds the NON-elevated `powershell.exe -EncodedCommand ...` launch used only for
    /// the eligibility read-outs (<see cref="DefenderStatusReader"/>'s `Get-MpComputerStatus`).
    /// `WorkingDirectory` is pinned to <see cref="Environment.SystemDirectory"/> deliberately: these
    /// cmdlets have no functional dependency on any particular current directory, and leaving
    /// `WorkingDirectory` unset makes the process silently inherit whatever ambient current
    /// directory happens to be on the caller's call stack. System32 is always present and accessible
    /// regardless of elevation, so it's a safe, neutral choice.</summary>
    public static ProcessStartInfo BuildPowerShellStartInfo(string script)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.SystemDirectory,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encoded);
        return startInfo;
    }

    /// <summary>Builds the single `runas`-elevated `powershell.exe` launch that does the actual
    /// exclusion work -- the one elevation hop this feature performs. <paramref name="scriptPath"/>
    /// is the path to a temp `.ps1` file already containing the full check-then-add-then-verify
    /// script (written by <see cref="RealElevatedPowerShellRunner"/> before calling this), run via
    /// `-File` rather than an inline `-EncodedCommand`.
    ///
    /// <para>Why `-File` and not `-EncodedCommand` (the third real bug found in this feature, see
    /// <see cref="IElevatedPowerShellRunner"/>'s doc comment for the full history): a real
    /// multi-entry plan's script, base64-encoded, produces an `-EncodedCommand` argument several
    /// thousand characters long -- well past `ShellExecuteEx`'s documented practical `lpParameters`
    /// limit of roughly 2,000 characters, especially combined with the `runas` verb, where exceeding
    /// it can fail with an "Access is denied"-shaped error. A `-File &lt;path&gt;` argument is under
    /// 150 characters regardless of plan size, so this can never recur no matter how large a plan
    /// grows. `-ExecutionPolicy Bypass` is required for a `-File` launch specifically (unlike
    /// `-EncodedCommand`, which always ignores the execution policy) -- this bypasses only THIS one
    /// process's policy check, it changes nothing machine-wide.</para>
    ///
    /// <para>`UseShellExecute = true` + `Verb = "runas"` is mandatory here (redirected streams are
    /// impossible across a `runas` boundary, which is exactly why the script writes its results to a
    /// file rather than stdout). `WorkingDirectory` is pinned to
    /// <see cref="Environment.SystemDirectory"/> because `ShellExecuteEx` otherwise treats an unset
    /// `lpDirectory` as "use the calling process's current directory," and there's no reason for the
    /// elevated PowerShell to sit in the user's project root. The window is hidden -- the UAC prompt
    /// is the only UI this should show.</para>
    ///
    /// <para>Elevating `powershell.exe` DIRECTLY (rather than elevating the unsigned
    /// <c>unbramble.exe</c> and having it spawn PowerShell) is still preserved from the prior fix:
    /// a `runas` launch is created by the Windows AppInfo service, so the elevated PowerShell's
    /// parent is that trusted system service, not an unsigned custom binary.</para></summary>
    public static ProcessStartInfo BuildElevatedPowerShellStartInfo(string scriptPath) => new()
    {
        FileName = "powershell.exe",
        Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
        UseShellExecute = true,
        Verb = "runas",
        WindowStyle = ProcessWindowStyle.Hidden,
        WorkingDirectory = Environment.SystemDirectory,
    };
}

internal sealed class RealPowerShellRunner : IPowerShellRunner
{
    public PowerShellResult Run(string script)
    {
        var startInfo = DefenderProcessLaunchBuilder.BuildPowerShellStartInfo(script);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return new PowerShellResult(-1, string.Empty, "failed to start powershell.exe");
        }

        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new PowerShellResult(process.ExitCode, stdOut, stdErr);
    }
}

internal sealed class RealDefenderRegistryReader : IDefenderRegistryReader
{
    public bool ExclusionsAreTamperProtected()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows Defender\Features");
            var value = key?.GetValue("TPExclusions");
            return value is int i && i == 1;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }
}

internal sealed class RealDevDriveChecker : IDevDriveChecker
{
    public bool IsDevDrive(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
            {
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = "fsutil.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("devdrv");
            startInfo.ArgumentList.Add("query");
            startInfo.ArgumentList.Add(root);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            var stdOut = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            // fsutil's own wording for a Dev Drive volume is "is a Dev Drive" vs "is not a Dev
            // Drive" -- matching on the shorter positive phrase would also match the negative
            // sentence (it's a substring of it), so the negative form is checked first.
            if (stdOut.Contains("is not a Dev Drive", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return process.ExitCode == 0 && stdOut.Contains("is a Dev Drive", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }
}

internal sealed class RealElevatedPowerShellRunner : IElevatedPowerShellRunner
{
    public ElevatedLaunchResult RunElevated(string script)
    {
        // Written to the OS temp directory (not the project root) with a random name so concurrent
        // runs across different projects never collide. UTF-8 WITH a BOM -- Windows PowerShell 5.1's
        // `-File` script parsing relies on the BOM to detect the file isn't ANSI, which matters here
        // since a plan entry's path can contain non-ASCII characters. Deleted in `finally` -- by the
        // time `WaitForExit` returns the elevated process (which already read the file) is done, so
        // there's no race with the delete.
        var scriptPath = Path.Combine(Path.GetTempPath(), $"unbramble-defender-{Guid.NewGuid():N}.ps1");
        try
        {
            File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            var startInfo = DefenderProcessLaunchBuilder.BuildElevatedPowerShellStartInfo(scriptPath);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new ElevatedLaunchResult(ElevatedLaunchOutcome.Failed, -1, "failed to start elevated powershell.exe");
            }

            process.WaitForExit();
            return new ElevatedLaunchResult(ElevatedLaunchOutcome.Completed, process.ExitCode, null);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED -- user dismissed the UAC prompt.
        {
            return new ElevatedLaunchResult(ElevatedLaunchOutcome.UserCancelled, -1, null);
        }
        catch (Exception ex)
        {
            return new ElevatedLaunchResult(ElevatedLaunchOutcome.Failed, -1, ex.Message);
        }
        finally
        {
            try
            {
                File.Delete(scriptPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort only -- a leftover temp .ps1 is harmless.
            }
        }
    }
}
