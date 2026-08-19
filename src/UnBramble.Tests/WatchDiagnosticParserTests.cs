using UnBramble.Core.Monitoring;

namespace UnBramble.Tests;

/// <summary>
/// Parses the exact stderr diagnostic line shapes emitted today by
/// <see cref="UnBramble.Core.UnBrambleEngine.EnableWatchCompilationCache"/>'s `onUnitDecision` sink
/// and <see cref="UnBramble.Core.Freshness.WatcherHost"/>'s `onDiagnostic` sink (lines copied
/// verbatim from the current source, not hand-written approximations). Also covers a handful of
/// unrelated/malformed lines to confirm the parser
/// degrades to "no match" rather than throwing -- these lines are free text owned by code this
/// feature doesn't control.
/// </summary>
public class WatchDiagnosticParserTests
{
    [Fact]
    public void TryParseCsCacheStep_BuildIncremental_ParsesUnitPhaseDetailAndMs()
    {
        var ok = WatchDiagnosticParser.TryParseCsCacheStep(
            "[cs-cache] Assembly-CSharp build: INCREMENTAL self-dirty=True dep-swaps=[] elapsed_ms=2", out var step);

        Assert.True(ok);
        Assert.Equal("Assembly-CSharp", step.Unit);
        Assert.Equal("build", step.Phase);
        Assert.Null(step.Sub);
        Assert.Equal("INCREMENTAL self-dirty=True dep-swaps=[]", step.Detail);
        Assert.Equal(2, step.Ms);
    }

    [Fact]
    public void TryParseCsCacheStep_BuildFullRebuild_ParsesReason()
    {
        var ok = WatchDiagnosticParser.TryParseCsCacheStep(
            "[cs-cache] Game build: FULL-REBUILD reason=cache-miss scripts=40 elapsed_ms=15", out var step);

        Assert.True(ok);
        Assert.Equal("Game", step.Unit);
        Assert.Equal("build", step.Phase);
        Assert.StartsWith("FULL-REBUILD", step.Detail, StringComparison.Ordinal);
        Assert.Equal(15, step.Ms);
    }

    [Fact]
    public void TryParseCsCacheStep_ExtractScoped_ParsesSubPhase()
    {
        var ok = WatchDiagnosticParser.TryParseCsCacheStep(
            "[cs-cache] Assembly-CSharp extract (scoped): files=1/315 symbols=27 elapsed_ms=50", out var step);

        Assert.True(ok);
        Assert.Equal("Assembly-CSharp", step.Unit);
        Assert.Equal("extract", step.Phase);
        Assert.Equal("scoped", step.Sub);
        Assert.Equal("files=1/315 symbols=27", step.Detail);
        Assert.Equal(50, step.Ms);
    }

    [Fact]
    public void TryParseCsCacheStep_DbScopeFullUnit_Parses()
    {
        var ok = WatchDiagnosticParser.TryParseCsCacheStep(
            "[cs-cache] Core db-scope (full-unit): changed=3/40 elapsed_ms=1", out var step);

        Assert.True(ok);
        Assert.Equal("db-scope", step.Phase);
        Assert.Equal("full-unit", step.Sub);
    }

    [Fact]
    public void TryParseCsAnalysisStep_Discovery_Parses()
    {
        var ok = WatchDiagnosticParser.TryParseCsAnalysisStep(
            "[cs-analysis] discovery: session-cache REUSED units=159 elapsed_ms=1", out var step);

        Assert.True(ok);
        Assert.Equal("discovery", step.Phase);
        Assert.Equal(1, step.Ms);
        Assert.Equal(159, step.Units);
    }

    [Fact]
    public void TryParseCsAnalysisStep_Ordering_Parses()
    {
        var ok = WatchDiagnosticParser.TryParseCsAnalysisStep("[cs-analysis] ordering: levels=2 elapsed_ms=0", out var step);

        Assert.True(ok);
        Assert.Equal("ordering", step.Phase);
        Assert.Equal(0, step.Ms);
        Assert.Null(step.Units);
    }

    [Fact]
    public void TryParseScanProgress_ParsesDirsFilesAndCurrentPath()
    {
        var ok = WatchDiagnosticParser.TryParseScanProgress(
            "[scan-progress] dirs=45231 files=112004 current=Assets/Foo/Bar.prefab", out var progress);

        Assert.True(ok);
        Assert.Equal(45231, progress.DirsVisited);
        Assert.Equal(112004, progress.FilesSeen);
        Assert.Equal("Assets/Foo/Bar.prefab", progress.CurrentPath);
    }

    [Fact]
    public void TryParseScanProgress_EmptyCurrentPath_Parses()
    {
        var ok = WatchDiagnosticParser.TryParseScanProgress("[scan-progress] dirs=0 files=0 current=", out var progress);

        Assert.True(ok);
        Assert.Equal(0, progress.DirsVisited);
        Assert.Equal(0, progress.FilesSeen);
        Assert.Equal(string.Empty, progress.CurrentPath);
    }

    [Fact]
    public void TryParseSummary_ApplyTargetedUpdate_ExtractsAllMsFieldsAndCounts()
    {
        var ok = WatchDiagnosticParser.TryParseSummary(
            "[cs-analysis] ApplyTargetedUpdate(paths=2): scan_ms=3 diff_ms=1415 reparse_ms=512 cs_ms=5734 total_ms=7665 " +
            "added=0 changed=2 removed=0 dirtyPaths=2",
            out var summary);

        Assert.True(ok);
        Assert.StartsWith("ApplyTargetedUpdate", summary.Kind, StringComparison.Ordinal);
        Assert.Equal(0, summary.Added);
        Assert.Equal(2, summary.Changed);
        Assert.Equal(0, summary.Removed);

        var byLabel = summary.Steps.ToDictionary(s => s.Label, s => s.Ms);
        Assert.Equal(3, byLabel["scan"]);
        Assert.Equal(1415, byLabel["diff"]);
        Assert.Equal(512, byLabel["reparse"]);
        Assert.Equal(5734, byLabel["cs"]);
        Assert.Equal(7665, byLabel["total"]);
    }

    [Fact]
    public void TryParseSummary_RunIndex_UsesSweepMsInsteadOfDiffMs()
    {
        var ok = WatchDiagnosticParser.TryParseSummary(
            "[cs-analysis] RunIndex(full=False): scan_ms=1 sweep_ms=2 reparse_ms=3 cs_ms=4 total_ms=10 " +
            "added=0 changed=1 removed=0 dirtyPaths=1",
            out var summary);

        Assert.True(ok);
        Assert.StartsWith("RunIndex", summary.Kind, StringComparison.Ordinal);
        var byLabel = summary.Steps.ToDictionary(s => s.Label, s => s.Ms);
        Assert.Equal(2, byLabel["sweep"]);
        Assert.Equal(1, summary.Changed);
    }

    [Fact]
    public void TryParseSummary_RunCsAnalysisSummary_ParsesHyphenatedLabels()
    {
        var ok = WatchDiagnosticParser.TryParseSummary(
            "[cs-analysis] RunCsAnalysis summary: units=159 dirty=1 semantic=157 syntactic=2 " +
            "mode-selection-total_ms=88 levels-total_ms=124 db-write_ms=5608 analyses-written=1 " +
            "symbols-written=27 refs-written=9 total_ms=5734",
            out var summary);

        Assert.True(ok);
        Assert.Equal("RunCsAnalysis summary", summary.Kind);
        var byLabel = summary.Steps.ToDictionary(s => s.Label, s => s.Ms);
        Assert.Equal(88, byLabel["mode-selection-total"]);
        Assert.Equal(124, byLabel["levels-total"]);
        Assert.Equal(5608, byLabel["db-write"]);
        Assert.Equal(5734, byLabel["total"]);
        Assert.Null(summary.Added);
    }

    [Fact]
    public void TryParseDebounceFired_NonZeroBatch_ParsesSize()
    {
        var ok = WatchDiagnosticParser.TryParseDebounceFired("[watch-fsw] debounce fired: batch-size=2", out var size);

        Assert.True(ok);
        Assert.Equal(2, size);
    }

    [Fact]
    public void IsResyncStarting_MatchesSelfHealOrErrorResyncStartLine()
    {
        Assert.True(WatchDiagnosticParser.IsResyncStarting("[watch-fsw] self-heal/error-resync sweep: starting RunIndex(full=false)"));
    }

    [Theory]
    [InlineData("[watch-fsw] Changed ACCEPTED path=Assets/Foo.cs pending=1 debounce-armed_ms=1000")]
    [InlineData("[watch-fsw] Changed REJECTED (out-of-scope: excluded dir 'Library') path=Library/x")]
    [InlineData("watch: promoted: now the active watcher for this project.")]
    [InlineData("")]
    [InlineData("not a diagnostic line at all")]
    public void UnrelatedOrMalformedLines_MatchNothing_NeverThrow(string line)
    {
        Assert.False(WatchDiagnosticParser.TryParseCsCacheStep(line, out _));
        Assert.False(WatchDiagnosticParser.TryParseCsAnalysisStep(line, out _));
        Assert.False(WatchDiagnosticParser.TryParseSummary(line, out _));
        Assert.False(WatchDiagnosticParser.TryParseDebounceFired(line, out _));
        Assert.False(WatchDiagnosticParser.TryParseScanProgress(line, out _));
        Assert.False(WatchDiagnosticParser.IsResyncStarting(line));
    }
}
