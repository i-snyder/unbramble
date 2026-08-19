using UnBramble.Core.Config;
using UnBramble.Core.Scanning;

namespace UnBramble.Cli.Defender;

/// <summary>
/// `init`-only (plus the explicit `defender` verb family), announced side effect -- same "loud,
/// not silent" philosophy as <c>AgentInstructionsSetup</c>/<c>Program.SetUpIgnoreFiles</c>. Offers
/// to add Windows Defender exclusions (a process exclusion for `unbramble.exe` itself, plus path
/// exclusions for the project root and every root-level junction/symlink target resolved outside
/// it) so real-time scanning doesn't dominate the cost of a cold index (measured ~7-8x on a real
/// project).
///
/// Entry points:
/// <list type="bullet">
/// <item><see cref="MaybeOfferSetup"/> -- the `init`/`HomeCommand` path: eligibility check, then
/// (if eligible and there's something new to do) the consent prompt, elevate, apply, record
/// cycle. Called BEFORE the first sweep, interactive-only for the actual prompt --
/// a non-interactive caller gets a one-line hint instead of hanging on stdin.</item>
/// <item><see cref="RunStatus"/> -- `unbramble defender status`: eligibility + recorded state,
/// no elevation, no prompting, ever.</item>
/// <item><see cref="RunSetup"/> -- `unbramble defender setup`: the same flow as
/// <see cref="MaybeOfferSetup"/> but always forces a fresh prompt (bypassing "already declined"
/// suppression) and always evaluates eligibility fresh.</item>
/// <item><see cref="RunRemove"/> -- `unbramble defender remove`: elevated removal of exactly the
/// entries this tool itself recorded as applied.</item>
/// </list>
/// </summary>
public static class DefenderExclusionSetup
{
    public readonly record struct Dependencies(
        IPowerShellRunner PowerShell,
        IDefenderRegistryReader Registry,
        IDevDriveChecker DevDrive,
        IElevatedPowerShellRunner Elevator,
        string ExePath)
    {
        public static Dependencies CreateReal() => new(
            new RealPowerShellRunner(),
            new RealDefenderRegistryReader(),
            new RealDevDriveChecker(),
            new RealElevatedPowerShellRunner(),
            Environment.ProcessPath ?? "unbramble.exe");
    }

    /// <summary>The `init`/`HomeCommand` path -- see this type's own doc comment.</summary>
    public static void MaybeOfferSetup(
        string projectRoot,
        UnBrambleConfig config,
        Dependencies deps,
        bool interactive,
        bool forcePrompt,
        bool skip,
        Action<string> announce,
        Func<string?> readLine)
    {
        if (skip)
        {
            return;
        }

        Run(projectRoot, config, deps, interactive, forcePrompt, announce, readLine);
    }

    /// <summary>`unbramble defender status`: read-only, never elevates, never prompts.</summary>
    public static void RunStatus(string projectRoot, UnBrambleConfig config, Dependencies deps, Action<string> announce)
    {
        var eligibility = EvaluateEligibility(projectRoot, deps);
        if (!eligibility.ShouldProceed)
        {
            announce(eligibility.Note ?? $"Windows Defender setup: not applicable ({eligibility.Status}).");
        }

        var state = DefenderStateFile.TryRead(projectRoot);
        if (state is null)
        {
            announce("Defender exclusions: not set up yet. Run 'unbramble defender setup' to configure.");
            return;
        }

        announce($"Defender exclusions: {state.Decision}" + (state.AppliedAtUtc is not null ? $" (as of {state.AppliedAtUtc})" : "") + ".");
        foreach (var entry in state.Entries)
        {
            announce($"  {entry.Type,-7} {entry.Value}  [{entry.Outcome}]");
        }

        if (!eligibility.ShouldProceed)
        {
            return;
        }

        var desired = ComputeDesiredEntries(projectRoot, config, deps.ExePath);
        var delta = ComputeDelta(desired, state);
        if (delta.Count > 0)
        {
            announce($"{delta.Count} entr{(delta.Count == 1 ? "y" : "ies")} not yet covered -- run 'unbramble defender setup' to add.");
        }
    }

    /// <summary>`unbramble defender setup`: always re-evaluates and always re-prompts (bypasses
    /// the "already declined" suppression `MaybeOfferSetup` applies on a plain `init`), but still
    /// never prompts from a non-interactive caller.</summary>
    public static void RunSetup(
        string projectRoot, UnBrambleConfig config, Dependencies deps, bool interactive,
        Action<string> announce, Func<string?> readLine) =>
        Run(projectRoot, config, deps, interactive, forcePrompt: true, announce, readLine);

    /// <summary>`unbramble defender remove`: elevated removal of exactly the entries this tool
    /// itself recorded as applied -- never a blanket "clear all Defender exclusions."</summary>
    public static void RunRemove(string projectRoot, Dependencies deps, Action<string> announce)
    {
        var state = DefenderStateFile.TryRead(projectRoot);
        var toRemove = state?.Entries
            .Where(e => e.Outcome is "added" or "already-covered")
            .Select(e => new DefenderEntry(DefenderEntry.ParseType(e.Type), e.Value))
            .ToList();

        if (toRemove is null || toRemove.Count == 0)
        {
            announce("Defender exclusions: nothing recorded for this project -- nothing to remove.");
            return;
        }

        announce($"Removing {toRemove.Count} Defender exclusion entr{(toRemove.Count == 1 ? "y" : "ies")} added by unbramble:");
        foreach (var entry in toRemove)
        {
            announce($"  {entry.TypeString,-7} {entry.Value}");
        }

        var planPath = DefenderPlanFile.PathFor(projectRoot);
        var resultPath = DefenderResultFile.PathFor(projectRoot);
        DefenderResultFile.Delete(resultPath);
        DefenderPlanFile.Write(planPath, toRemove);

        var elevation = deps.Elevator.RunElevated(DefenderApply.BuildRemoveScript(planPath, resultPath));

        if (!AnnounceElevationFailureIfAny(elevation, announce))
        {
            return;
        }

        var resultRead = DefenderResultFile.TryRead(resultPath);
        if (resultRead is null)
        {
            announce("Windows Defender: the elevated removal step finished but no result could be read back -- check Windows Security > Virus & threat protection > Exclusions manually.");
            return;
        }

        foreach (var r in resultRead.Value.Results)
        {
            announce(FormatEntryLine(r.Entry, r.Outcome, error: null, gap: " "));
        }

        var allRemoved = resultRead.Value.Results.All(r => !DefenderDecisionText.IsFailure(r.Outcome));
        if (allRemoved)
        {
            DefenderStateFile.Delete(projectRoot);
            announce("Defender exclusions: removed. Re-offer any time with 'unbramble defender setup'.");
        }
        else
        {
            announce("Defender exclusions: some entries could not be removed (see above) -- this machine's " +
                "exclusions may be managed/tamper-protected. Recorded state left as-is.");
        }
    }

    private static void Run(
        string projectRoot, UnBrambleConfig config, Dependencies deps, bool interactive, bool forcePrompt,
        Action<string> announce, Func<string?> readLine)
    {
        var eligibility = EvaluateEligibility(projectRoot, deps);
        if (!eligibility.ShouldProceed)
        {
            if (eligibility.Note is not null)
            {
                var existing = DefenderStateFile.TryRead(projectRoot);
                if (existing is null)
                {
                    announce(eligibility.Note);
                    var decision = eligibility.Status switch
                    {
                        DefenderEligibility.SkippedDevDrive => DefenderDecision.SkippedDevDrive,
                        DefenderEligibility.SkippedThirdPartyAv => DefenderDecision.SkippedThirdParty,
                        DefenderEligibility.SkippedManaged => DefenderDecision.SkippedManaged,
                        _ => (DefenderDecision?)null,
                    };
                    if (decision is { } d)
                    {
                        DefenderStateFile.Write(projectRoot, d, exePath: null, entries: []);
                    }
                }
            }

            return;
        }

        var desired = ComputeDesiredEntries(projectRoot, config, deps.ExePath);
        var state = DefenderStateFile.TryRead(projectRoot);

        if (!forcePrompt && state?.Decision == "declined")
        {
            // Never re-nag after an explicit decline on a plain `init` -- only `defender setup`
            // (forcePrompt: true) or a fresh `--defender` flag reaches this again.
            return;
        }

        var delta = ComputeDelta(desired, state);
        if (delta.Count == 0 && state?.Decision == "applied")
        {
            announce("Defender exclusions: up to date.");
            return;
        }

        if (!interactive)
        {
            announce("Defender exclusions not set up -- run 'unbramble defender setup' in an interactive terminal.");
            return;
        }

        var isDelta = delta.Count != desired.Count && state?.Decision == "applied";
        PrintConsentPrompt(isDelta ? delta : desired, isDelta, deps.ExePath, announce);

        if (!IsAccept(readLine()))
        {
            announce("Skipped. Manual steps: see README. Re-offer any time with 'unbramble defender setup'.");
            DefenderStateFile.Write(projectRoot, DefenderDecision.Declined, deps.ExePath, entries: []);
            return;
        }

        ApplyAndRecord(projectRoot, deps, desired, delta, announce);
    }

    private static DefenderEligibilityResult EvaluateEligibility(string projectRoot, Dependencies deps)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new DefenderEligibilityResult(DefenderEligibility.SkippedNotWindows, null);
        }

        var isDevDrive = SafeCall(() => deps.DevDrive.IsDevDrive(projectRoot), fallback: false);
        var status = isDevDrive ? null : SafeCall(() => DefenderStatusReader.TryGetStatus(deps.PowerShell), fallback: (DefenderRuntimeStatus?)null);
        var tamperProtected = isDevDrive || status is null ? false : SafeCall(() => deps.Registry.ExclusionsAreTamperProtected(), fallback: false);

        return DefenderEligibilityCheck.Evaluate(isWindows: true, isDevDrive, status, tamperProtected);
    }

    private static T SafeCall<T>(Func<T> action, T fallback)
    {
        try
        {
            return action();
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static void ApplyAndRecord(
        string projectRoot, Dependencies deps, List<DefenderEntry> desired, List<DefenderEntry> delta, Action<string> announce)
    {
        var planPath = DefenderPlanFile.PathFor(projectRoot);
        var resultPath = DefenderResultFile.PathFor(projectRoot);
        DefenderResultFile.Delete(resultPath);
        DefenderPlanFile.Write(planPath, delta);

        var elevation = deps.Elevator.RunElevated(DefenderApply.BuildApplyScript(planPath, resultPath));

        if (elevation.Outcome == ElevatedLaunchOutcome.UserCancelled)
        {
            // "Declined for now," not a persisted decline -- don't nag again on THIS init
            // run, but a future run tries again since no state is written at all here.
            announce("Windows Defender setup: the administrator prompt was dismissed -- skipped for now.");
            return;
        }

        if (elevation.Outcome == ElevatedLaunchOutcome.Failed)
        {
            announce($"Windows Defender setup: could not start the elevated step ({elevation.ErrorMessage ?? "unknown error"}).");
            return;
        }

        var resultRead = DefenderResultFile.TryRead(resultPath);
        if (resultRead is null)
        {
            announce("Windows Defender setup: the elevated step finished but no result could be read back -- nothing recorded.");
            return;
        }

        var (success, results) = resultRead.Value;
        foreach (var r in results)
        {
            announce(FormatEntryLine(r.Entry, r.Outcome, r.Error, gap: " "));
        }

        // Merge with whatever was already recorded (a delta-only run must not forget the entries
        // a previous run already applied) -- previously-applied entries not in this run's results
        // are carried forward as-is.
        var previouslyApplied = DefenderStateFile.TryRead(projectRoot)?.Entries
            .Where(e => e.Outcome is "added" or "already-covered")
            .Where(e => results.All(r => !(r.Entry.TypeString == e.Type && string.Equals(r.Entry.Value, e.Value, StringComparison.OrdinalIgnoreCase))))
            .Select(e => new DefenderEntryResult(new DefenderEntry(DefenderEntry.ParseType(e.Type), e.Value), DefenderDecisionText.ParseOutcome(e.Outcome)))
            ?? [];

        var merged = results.Concat(previouslyApplied).ToList();
        var decision = success ? DefenderDecision.Applied : DefenderDecision.Blocked;
        DefenderStateFile.Write(projectRoot, decision, deps.ExePath, merged);

        announce(success
            ? "Windows Defender setup: done. Undo any time with 'unbramble defender remove'."
            : "Windows Defender setup: some entries could not be confirmed -- see above. This machine's exclusions may be managed/tamper-protected.");
    }

    /// <summary>True on <see cref="ElevatedLaunchOutcome.Completed"/>; announces and returns false
    /// for <see cref="ElevatedLaunchOutcome.UserCancelled"/>/<see cref="ElevatedLaunchOutcome.Failed"/>
    /// (used by <see cref="RunRemove"/>, which has no delta/merge bookkeeping of its own).</summary>
    private static bool AnnounceElevationFailureIfAny(ElevatedLaunchResult elevation, Action<string> announce)
    {
        if (elevation.Outcome == ElevatedLaunchOutcome.UserCancelled)
        {
            announce("Windows Defender: the administrator prompt was dismissed -- nothing removed.");
            return false;
        }

        if (elevation.Outcome == ElevatedLaunchOutcome.Failed)
        {
            announce($"Windows Defender: could not start the elevated step ({elevation.ErrorMessage ?? "unknown error"}).");
            return false;
        }

        return true;
    }

    /// <summary>Project root + full `unbramble.exe` path + every root-level junction/symlink
    /// target resolved OUTSIDE the project root -- process and path exclusions are shipped
    /// together as one unit. Targets that resolve inside the root are skipped -- the
    /// root's own path exclusion already covers them.</summary>
    public static List<DefenderEntry> ComputeDesiredEntries(string projectRoot, UnBrambleConfig config, string exePath)
    {
        var entries = new List<DefenderEntry>
        {
            new(DefenderEntryType.Process, exePath),
            new(DefenderEntryType.Path, Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot))),
        };

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectRoot));
        foreach (var link in RootLinkTargets.Enumerate(projectRoot, config))
        {
            var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(link.ResolvedTarget));
            if (!target.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                entries.Add(new DefenderEntry(DefenderEntryType.Path, target));
            }
        }

        return entries
            .GroupBy(e => (e.Type, Value: e.Value.ToLowerInvariant()))
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>Entries in <paramref name="desired"/> not already recorded as
    /// added/already-covered in <paramref name="state"/> -- everything else (never recorded,
    /// blocked, error) is treated as still needing a retry.</summary>
    public static List<DefenderEntry> ComputeDelta(List<DefenderEntry> desired, DefenderStateFileJson? state)
    {
        if (state is null)
        {
            return desired;
        }

        var covered = state.Entries
            .Where(e => e.Outcome is "added" or "already-covered")
            .Select(e => (Type: e.Type, Value: e.Value.ToLowerInvariant()))
            .ToHashSet();

        return desired.Where(e => !covered.Contains((e.TypeString, e.Value.ToLowerInvariant()))).ToList();
    }

    private static void PrintConsentPrompt(List<DefenderEntry> entries, bool isDelta, string exePath, Action<string> announce)
    {
        // Each paragraph below is ONE announce call, not a hand-broken run of them. The CLI's
        // writer (Program.WriteSetupLine) wraps whatever it's given to the real terminal width, so
        // a sentence pre-split across several calls at some fixed column would get re-wrapped
        // fragment-by-fragment -- each stub hanging-indented on its own, which reads worse than
        // either treatment alone.
        announce(string.Empty);
        var ansi = ConsoleCapabilities.SupportsAnsi;
        announce(
            AnsiStyle.Label("Windows Defender setup", ansi) +
            AnsiStyle.Muted(" (optional, recommended)", ansi));
        if (isDelta)
        {
            announce(
                "Defender exclusions are already set up for this project, but new locations appeared since then. " +
                "With your approval, unbramble will add exclusions for what's new:");
        }
        else
        {
            announce(
                "Defender's real-time scanning re-scans every file this tool reads, which can turn a ~2 minute " +
                "cold index into 15+ minutes. With your approval, unbramble will add Windows Defender exclusions " +
                "so these locations and this tool are skipped by real-time scanning:");
        }

        announce(string.Empty);
        foreach (var entry in entries)
        {
            var suffix = entry.Type == DefenderEntryType.Process ? "  (files this tool opens)" : "";
            announce(FormatEntryLine(entry, outcome: null, error: null, gap: "  ", suffix: suffix));
        }

        announce(string.Empty);
        announce(
            AnsiStyle.Caution("This is a real security trade-off: ", ansi) +
            "excluded locations aren't scanned when files are opened, by any program. Windows will show one " +
            "administrator (UAC) prompt. Undo any time with " +
            "'" + AnsiStyle.Command("unbramble defender remove", ansi) + "' or in " +
            AnsiStyle.Notice("Windows Security > Virus & threat protection > Exclusions", ansi) + ".");
        announce(string.Empty);
        announce(AnsiStyle.Label("Add these exclusions?", ansi) + " " + AnsiStyle.NoYesPrompt(ansi));
    }

    private static string FormatEntryLine(
        DefenderEntry entry,
        DefenderEntryOutcome? outcome,
        string? error,
        string gap,
        string suffix = "")
    {
        var ansi = ConsoleCapabilities.SupportsAnsi;
        var line = "  " +
            AnsiStyle.Label(entry.TypeString.PadRight(7), ansi) + gap +
            AnsiStyle.Notice(entry.Value, ansi) +
            AnsiStyle.Muted(suffix, ansi);

        if (outcome is not { } value)
        {
            return line;
        }

        var outcomeText = DefenderDecisionText.ToText(value);
        var styledOutcome = value switch
        {
            DefenderEntryOutcome.Added or DefenderEntryOutcome.AlreadyCovered or DefenderEntryOutcome.Removed =>
                AnsiStyle.Alive(outcomeText, ansi),
            DefenderEntryOutcome.Blocked => AnsiStyle.Caution(outcomeText, ansi),
            _ => AnsiStyle.Alarm(outcomeText, ansi),
        };

        return line + AnsiStyle.Muted("  -> ", ansi) + styledOutcome +
            (error is not null ? AnsiStyle.Alarm($" ({error})", ansi) : "");
    }

    /// <summary>Only an explicit "y" or "yes" accepts this security trade-off.</summary>
    private static bool IsAccept(string? answer)
    {
        if (answer is null)
        {
            return false;
        }

        var trimmed = answer.Trim();
        return trimmed.Equals("y", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}
