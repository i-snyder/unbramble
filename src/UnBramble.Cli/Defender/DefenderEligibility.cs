using System.Text.Json;

namespace UnBramble.Cli.Defender;

/// <summary>Parsed <c>Get-MpComputerStatus</c> fields this feature actually needs.</summary>
public readonly record struct DefenderRuntimeStatus(string AmRunningMode, bool RealTimeProtectionEnabled);

public enum DefenderEligibility
{
    Proceed,
    SkippedNotWindows,
    SkippedDevDrive,
    SkippedNoDefenderModule,
    SkippedThirdPartyAv,
    SkippedManaged,
}

public readonly record struct DefenderEligibilityResult(DefenderEligibility Status, string? Note)
{
    public bool ShouldProceed => Status == DefenderEligibility.Proceed;
}

/// <summary>
/// The eligibility pre-check, as a pure function over already-read facts -- every fact-gathering
/// step (Dev Drive query, `Get-MpComputerStatus`, tamper-protection registry read) is a separate
/// seam (<see cref="IDevDriveChecker"/>, <see cref="IPowerShellRunner"/> via
/// <see cref="DefenderStatusReader"/>, <see cref="IDefenderRegistryReader"/>) so the DECISION
/// here is testable without shelling out or touching the registry at all. Checks in order: Dev
/// Drive first (skip entirely, no prompt), then "is Defender the active real-time AV," then
/// tamper-protected exclusions.
/// </summary>
public static class DefenderEligibilityCheck
{
    public static DefenderEligibilityResult Evaluate(
        bool isWindows, bool isDevDrive, DefenderRuntimeStatus? status, bool exclusionsTamperProtected)
    {
        if (!isWindows)
        {
            return new DefenderEligibilityResult(DefenderEligibility.SkippedNotWindows, null);
        }

        if (isDevDrive)
        {
            return new DefenderEligibilityResult(
                DefenderEligibility.SkippedDevDrive,
                "Windows Defender setup: skipped -- this project's volume is a Dev Drive, which already runs " +
                "Defender in performance mode, so exclusions are largely unnecessary.");
        }

        if (status is null)
        {
            // Defender module entirely missing (removed, LTSC oddities, etc.) -- skip silently:
            // nothing useful to say, and nothing to offer.
            return new DefenderEligibilityResult(DefenderEligibility.SkippedNoDefenderModule, null);
        }

        var isActiveRealTimeAv =
            string.Equals(status.Value.AmRunningMode, "Normal", StringComparison.OrdinalIgnoreCase)
            && status.Value.RealTimeProtectionEnabled;
        if (!isActiveRealTimeAv)
        {
            return new DefenderEligibilityResult(
                DefenderEligibility.SkippedThirdPartyAv,
                "Windows Defender setup: skipped -- a third-party antivirus appears to be active; Defender " +
                "exclusions wouldn't help.");
        }

        if (exclusionsTamperProtected)
        {
            return new DefenderEligibilityResult(
                DefenderEligibility.SkippedManaged,
                "Windows Defender setup: skipped -- exclusions on this machine are managed by your organization; " +
                "ask IT.");
        }

        return new DefenderEligibilityResult(DefenderEligibility.Proceed, null);
    }
}

/// <summary>Runs `Get-MpComputerStatus | ConvertTo-Json` through the shared
/// <see cref="IPowerShellRunner"/> seam and parses out the two fields
/// <see cref="DefenderEligibilityCheck"/> needs. Returns null on ANY failure (non-zero exit,
/// empty output, unparseable JSON, missing field) -- indistinguishable, by design, from "the
/// Defender module isn't there," since every one of those cases gets the same fail-soft "skip
/// silently" treatment.</summary>
public static class DefenderStatusReader
{
    public static DefenderRuntimeStatus? TryGetStatus(IPowerShellRunner runner)
    {
        PowerShellResult result;
        try
        {
            result = runner.Run("Get-MpComputerStatus | ConvertTo-Json -Compress");
        }
        catch (Exception)
        {
            return null;
        }

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(result.StdOut);
            var root = doc.RootElement;
            if (!root.TryGetProperty("AMRunningMode", out var modeElement) || modeElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var mode = modeElement.GetString();
            if (mode is null)
            {
                return null;
            }

            var rtpEnabled = root.TryGetProperty("RealTimeProtectionEnabled", out var rtpElement)
                && rtpElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                && rtpElement.GetBoolean();

            return new DefenderRuntimeStatus(mode, rtpEnabled);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
