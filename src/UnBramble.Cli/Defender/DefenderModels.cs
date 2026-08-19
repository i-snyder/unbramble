namespace UnBramble.Cli.Defender;

/// <summary>Matches the plan's `{type: "process"|"path", value: string}` shape exactly (plan file,
/// result file, and state file all reuse this same two-value vocabulary).</summary>
public enum DefenderEntryType
{
    Process,
    Path,
}

/// <summary>One thing this feature can ask Windows Defender to exclude: either the running
/// `unbramble.exe`'s own full path (a process exclusion -- covers every file it opens, regardless
/// of location) or a directory (a path exclusion -- the project root, or a root-level junction
/// target resolved outside the root).</summary>
public sealed record DefenderEntry(DefenderEntryType Type, string Value)
{
    public string TypeString => Type == DefenderEntryType.Process ? "process" : "path";

    public static DefenderEntryType ParseType(string s) => s switch
    {
        "process" => DefenderEntryType.Process,
        _ => DefenderEntryType.Path,
    };
}

/// <summary>Per-entry outcome after the elevated `defender-apply` child ran its check-then-add
/// script and read `Get-MpPreference` back to verify -- a change can appear to succeed but
/// actually be blocked, so a clean exit code alone is never trusted.</summary>
public enum DefenderEntryOutcome
{
    /// <summary>Added by this run and confirmed present on read-back.</summary>
    Added,

    /// <summary>Already covered by an existing exclusion before this run touched anything (path:
    /// prefix match; process: exact match) -- Add-MpPreference was never called for it.</summary>
    AlreadyCovered,

    /// <summary>Add-MpPreference reported success but the entry was NOT present on read-back --
    /// the tamper-protection "appeared to succeed but was blocked" case.</summary>
    Blocked,

    /// <summary>Add-MpPreference itself threw.</summary>
    Error,

    /// <summary>`defender remove` only: confirmed absent on read-back after a
    /// `Remove-MpPreference` attempt.</summary>
    Removed,
}

public sealed record DefenderEntryResult(DefenderEntry Entry, DefenderEntryOutcome Outcome, string? Error = null);

/// <summary>The state file's `decision` field.</summary>
public enum DefenderDecision
{
    Applied,
    Declined,
    Blocked,
    SkippedDevDrive,
    SkippedThirdParty,
    SkippedManaged,
}

public static class DefenderDecisionText
{
    public static string ToText(DefenderDecision d) => d switch
    {
        DefenderDecision.Applied => "applied",
        DefenderDecision.Declined => "declined",
        DefenderDecision.Blocked => "blocked",
        DefenderDecision.SkippedDevDrive => "skipped-devdrive",
        DefenderDecision.SkippedThirdParty => "skipped-thirdparty",
        DefenderDecision.SkippedManaged => "skipped-managed",
        _ => "declined",
    };

    public static DefenderDecision? Parse(string? s) => s switch
    {
        "applied" => DefenderDecision.Applied,
        "declined" => DefenderDecision.Declined,
        "blocked" => DefenderDecision.Blocked,
        "skipped-devdrive" => DefenderDecision.SkippedDevDrive,
        "skipped-thirdparty" => DefenderDecision.SkippedThirdParty,
        "skipped-managed" => DefenderDecision.SkippedManaged,
        _ => null,
    };

    public static string ToText(DefenderEntryOutcome o) => o switch
    {
        DefenderEntryOutcome.Added => "added",
        DefenderEntryOutcome.AlreadyCovered => "already-covered",
        DefenderEntryOutcome.Blocked => "blocked",
        DefenderEntryOutcome.Removed => "removed",
        _ => "error",
    };

    public static DefenderEntryOutcome ParseOutcome(string? s) => s switch
    {
        "added" => DefenderEntryOutcome.Added,
        "already-covered" => DefenderEntryOutcome.AlreadyCovered,
        "blocked" => DefenderEntryOutcome.Blocked,
        "removed" => DefenderEntryOutcome.Removed,
        _ => DefenderEntryOutcome.Error,
    };

    /// <summary>True for any outcome that represents a real problem the caller should surface
    /// distinctly (as opposed to "this entry ended up in the state this run wanted").</summary>
    public static bool IsFailure(DefenderEntryOutcome o) => o is DefenderEntryOutcome.Blocked or DefenderEntryOutcome.Error;
}
