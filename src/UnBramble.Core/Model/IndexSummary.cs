namespace UnBramble.Core.Model;

/// <summary>
/// Per-phase wall-clock breakdown of one <see cref="UnBramble.Core.UnBrambleEngine.RunIndex"/> call:
/// an actual measurement, printed by `index`/`init --verbose` and always present in `--json`
/// output. The four phases sum to slightly less than <see cref="IndexSummary.Elapsed"/> (the
/// remainder is <see cref="UnBrambleEngine.ResetData"/> on `--full`, <c>ReplaceRoots</c>, and
/// <see cref="UnBramble.Core.Store.UnBrambleStore.GetStats"/> — none of them expected to be
/// significant).
/// </summary>
/// <param name="Scan">Filesystem walk (<c>Scanner.Scan</c>), including the hoisted store snapshot load that feeds the meta mtime-gate.</param>
/// <param name="SweepDiff">In-memory diff against the store plus the write transaction (<c>UnBrambleStore.ApplySweep</c>).</param>
/// <param name="DirtyReparse">Reference-source reparse of dirty files (<c>ReparseDirtyFiles</c>).</param>
/// <param name="CsAnalysis">C# assembly discovery/compilation/extraction (<c>RunCsAnalysis</c>) — near-zero when the dirty-path skip gate fires.</param>
public sealed record IndexPhaseTimings(TimeSpan Scan, TimeSpan SweepDiff, TimeSpan DirtyReparse, TimeSpan CsAnalysis);

public sealed record IndexSummary(
    string ProjectRoot,
    string UnityVersion,
    string DbPath,
    TimeSpan Elapsed,
    int Added,
    int Changed,
    int Removed,
    bool IsFirstRun,
    StatsResult Stats,
    IReadOnlyList<string> Warnings,
    IndexPhaseTimings PhaseTimings);
