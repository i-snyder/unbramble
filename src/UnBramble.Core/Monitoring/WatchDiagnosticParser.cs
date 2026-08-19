using System.Text.RegularExpressions;
using UnBramble.Core.Scanning;

namespace UnBramble.Core.Monitoring;

/// <summary>
/// Pure, side-effect-free parsing of the stderr diagnostic lines `watch` already emits (see
/// <see cref="UnBrambleEngine.EnableWatchCompilationCache"/>'s `onUnitDecision` sink and
/// <see cref="Freshness.WatcherHost"/>'s `onDiagnostic` sink) into structured pieces
/// <see cref="WatchStatusTracker"/> can fold into a <see cref="WatchStatusSnapshot"/>.
///
/// Deliberately tolerant: every `TryParse*` method returns false (never throws) on a line that
/// doesn't match -- these lines are free text owned by other code that can change shape over
/// time, and a status DASHBOARD going stale/incomplete on an unrecognized line is a
/// cosmetic regression, never a correctness one. Every regex is anchored and specific rather
/// than greedy, so an unrelated line (e.g. a `[watch-fsw] ... REJECTED ...` line) simply matches
/// nothing and is ignored by the caller.
/// </summary>
public static class WatchDiagnosticParser
{
    // "[cs-cache] Assembly-CSharp build: INCREMENTAL self-dirty=True dep-swaps=[] elapsed_ms=2"
    // "[cs-cache] Assembly-CSharp extract (scoped): files=1/315 symbols=27 elapsed_ms=50"
    // "[cs-cache] Core db-scope (full-unit): changed=3/40 elapsed_ms=1"
    private static readonly Regex CsCacheStepRegex = new(
        @"^\[cs-cache\]\s+(?<unit>\S+)\s+(?<phase>build|extract|db-scope)(?:\s+\((?<sub>[^)]*)\))?:\s+(?<detail>.*?)\s+elapsed_ms=(?<ms>\d+(?:\.\d+)?)\s*$",
        RegexOptions.Compiled);

    // "[cs-analysis] discovery: session-cache REUSED units=159 elapsed_ms=1"
    // "[cs-analysis] ordering: levels=2 elapsed_ms=0"
    private static readonly Regex CsAnalysisStepRegex = new(
        @"^\[cs-analysis\]\s+(?<phase>discovery|ordering):\s+(?<detail>.*?)\s+elapsed_ms=(?<ms>\d+(?:\.\d+)?)\s*$",
        RegexOptions.Compiled);

    // Top-level pass summaries -- all three share the same "<field>_ms=<n> ... added=<n>
    // changed=<n> removed=<n>" shape, just different headers:
    //   "[cs-analysis] RunIndex(full=False): scan_ms=1 sweep_ms=2 reparse_ms=3 cs_ms=4 total_ms=10 added=0 changed=1 removed=0 dirtyPaths=1"
    //   "[cs-analysis] ApplyTargetedUpdate(paths=2): scan_ms=3 diff_ms=1415 reparse_ms=512 cs_ms=5734 total_ms=7665 added=0 changed=2 removed=0 dirtyPaths=2"
    //   "[cs-analysis] RunCsAnalysis summary: units=159 dirty=1 semantic=157 syntactic=2 mode-selection-total_ms=88 levels-total_ms=124 db-write_ms=5608 analyses-written=1 symbols-written=27 refs-written=9 total_ms=5734"
    private static readonly Regex SummaryHeaderRegex = new(
        @"^\[cs-analysis\]\s+(?<kind>RunIndex\(full=\w+\)|ApplyTargetedUpdate\(paths=\d+\)|RunCsAnalysis summary):\s*(?<rest>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex MsFieldRegex = new(@"(?<label>[A-Za-z][A-Za-z0-9-]*)_ms=(?<ms>\d+(?:\.\d+)?)", RegexOptions.Compiled);
    private static readonly Regex AddedRegex = new(@"(?<![A-Za-z-])added=(?<n>\d+)", RegexOptions.Compiled);
    private static readonly Regex ChangedRegex = new(@"(?<![A-Za-z-])changed=(?<n>\d+)", RegexOptions.Compiled);
    private static readonly Regex RemovedRegex = new(@"(?<![A-Za-z-])removed=(?<n>\d+)", RegexOptions.Compiled);
    private static readonly Regex UnitsFieldRegex = new(@"(?<![A-Za-z-])units=(?<n>\d+)", RegexOptions.Compiled);

    // "[watch-fsw] debounce fired: batch-size=2"
    private static readonly Regex DebounceFiredRegex = new(
        @"^\[watch-fsw\]\s+debounce fired:\s+batch-size=(?<n>\d+)\s*$", RegexOptions.Compiled);

    // "[watch-fsw] self-heal/error-resync sweep: starting RunIndex(full=false)"
    private static readonly Regex ResyncStartingRegex = new(
        @"^\[watch-fsw\]\s+self-heal/error-resync sweep:\s+starting\b", RegexOptions.Compiled);

    // "[scan-progress] dirs=45231 files=112004 current=Assets/Foo/Bar.prefab" -- emitted by
    // UnBrambleEngine.RunIndex's onProgress callback into Scanner.Scan.
    // "current=" is deliberately the last field, unanchored on the right
    // (`.*$`), so a path containing spaces is captured whole rather than truncated.
    private static readonly Regex ScanProgressRegex = new(
        @"^\[scan-progress\]\s+dirs=(?<dirs>\d+)\s+files=(?<files>\d+)\s+current=(?<current>.*)$", RegexOptions.Compiled);

    public readonly record struct UnitStep(string Unit, string Phase, string? Sub, string Detail, double Ms);

    /// <param name="Units">
    /// The `units=N` field, when present on this line -- currently only the discovery phase's
    /// line carries it ("session-cache REUSED units=159" / "rebuilt units=159"). Null for
    /// ordering (and any future phase that doesn't carry a unit count).
    /// </param>
    public readonly record struct AnalysisStep(string Phase, string Detail, double Ms, int? Units);

    public readonly record struct Summary(string Kind, IReadOnlyList<PassStep> Steps, int? Added, int? Changed, int? Removed);

    /// <summary>Per-unit cache/extract/db-scope step from an `EnableWatchCompilationCache` diagnostic line.</summary>
    public static bool TryParseCsCacheStep(string line, out UnitStep step)
    {
        var m = CsCacheStepRegex.Match(line);
        if (!m.Success)
        {
            step = default;
            return false;
        }

        step = new UnitStep(
            m.Groups["unit"].Value,
            m.Groups["phase"].Value,
            m.Groups["sub"].Success ? m.Groups["sub"].Value : null,
            m.Groups["detail"].Value,
            double.Parse(m.Groups["ms"].Value, System.Globalization.CultureInfo.InvariantCulture));
        return true;
    }

    /// <summary>Whole-pass discovery/ordering step (not scoped to one unit).</summary>
    public static bool TryParseCsAnalysisStep(string line, out AnalysisStep step)
    {
        var m = CsAnalysisStepRegex.Match(line);
        if (!m.Success)
        {
            step = default;
            return false;
        }

        var detail = m.Groups["detail"].Value;
        int? units = UnitsFieldRegex.Match(detail) is { Success: true } u
            ? int.Parse(u.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture)
            : null;

        step = new AnalysisStep(
            m.Groups["phase"].Value,
            detail,
            double.Parse(m.Groups["ms"].Value, System.Globalization.CultureInfo.InvariantCulture),
            units);
        return true;
    }

    /// <summary>Live scan progress -- a running
    /// dirs/files-seen count and the directory currently being entered, with deliberately no
    /// percentage/fraction: the total is unknown until the walk finishes.</summary>
    public static bool TryParseScanProgress(string line, out ScanProgress progress)
    {
        var m = ScanProgressRegex.Match(line);
        if (!m.Success)
        {
            progress = default;
            return false;
        }

        progress = new ScanProgress(
            long.Parse(m.Groups["dirs"].Value, System.Globalization.CultureInfo.InvariantCulture),
            long.Parse(m.Groups["files"].Value, System.Globalization.CultureInfo.InvariantCulture),
            m.Groups["current"].Value);
        return true;
    }

    /// <summary>
    /// A top-level pass summary (RunIndex/ApplyTargetedUpdate/RunCsAnalysis summary): every
    /// `..._ms=N` field on the line, in the order it appears, plus added/changed/removed when
    /// present. Field NAMES are not hardcoded (RunIndex uses `sweep_ms`, ApplyTargetedUpdate
    /// uses `diff_ms` for the analogous phase) -- reading whatever `_ms=` fields are actually on
    /// the line is what keeps this tolerant of future line-shape changes.
    /// </summary>
    public static bool TryParseSummary(string line, out Summary summary)
    {
        var header = SummaryHeaderRegex.Match(line);
        if (!header.Success)
        {
            summary = default;
            return false;
        }

        var rest = header.Groups["rest"].Value;
        var steps = new List<PassStep>();
        foreach (Match m in MsFieldRegex.Matches(rest))
        {
            steps.Add(new PassStep(m.Groups["label"].Value, double.Parse(m.Groups["ms"].Value, System.Globalization.CultureInfo.InvariantCulture)));
        }

        int? added = AddedRegex.Match(rest) is { Success: true } a ? int.Parse(a.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
        int? changed = ChangedRegex.Match(rest) is { Success: true } c ? int.Parse(c.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture) : null;
        int? removed = RemovedRegex.Match(rest) is { Success: true } r ? int.Parse(r.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture) : null;

        summary = new Summary(header.Groups["kind"].Value, steps, added, changed, removed);
        return true;
    }

    /// <summary>True (with the parsed batch size) for the `[watch-fsw] debounce fired: batch-size=N` line
    /// that marks the start of a real (non-empty) batch about to be applied.</summary>
    public static bool TryParseDebounceFired(string line, out int batchSize)
    {
        var m = DebounceFiredRegex.Match(line);
        if (!m.Success)
        {
            batchSize = 0;
            return false;
        }

        batchSize = int.Parse(m.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>True for the `[watch-fsw] self-heal/error-resync sweep: starting ...` line that
    /// marks the start of a full resync (self-heal or FSW-error-triggered).</summary>
    public static bool IsResyncStarting(string line) => ResyncStartingRegex.IsMatch(line);
}
