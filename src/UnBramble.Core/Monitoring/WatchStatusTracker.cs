using UnBramble.Core.Freshness;

namespace UnBramble.Core.Monitoring;

/// <summary>
/// Consumes the two diagnostic channels a `watch` process already exposes -- the strongly-typed
/// <see cref="WatcherEvent"/> lifecycle stream (<see cref="WatcherHost"/>'s `onEvent`) and the
/// free-text diagnostic lines (<see cref="WatcherHost"/>'s `onDiagnostic` plus
/// <see cref="UnBrambleEngine.EnableWatchCompilationCache"/>'s `onUnitDecision`, parsed via
/// <see cref="WatchDiagnosticParser"/>) -- and folds them into one queryable
/// <see cref="WatchStatusSnapshot"/>, written to <see cref="WatchStatusFile"/> after every
/// meaningful update.
///
/// Thread-safety: `watch`'s own callbacks can fire from multiple threads at once (FSW event
/// threads vs. the debounce/self-heal timer threads -- see <see cref="WatcherHost"/>'s own gate
/// doc comments), so every mutation AND the resulting disk write happen inside one lock. This
/// mirrors why <see cref="WatcherHost"/> itself needed a `_heartbeatGate` to rule out
/// concurrent-writer bugs -- holding the lock across the write is the simplest way to rule that
/// class of bug out here too, and the write itself is small and infrequent enough that it's not
/// worth splitting into two locks the way the heartbeat gate did.
/// </summary>
public sealed class WatchStatusTracker
{
    private const int SchemaVersion = 1;

    private readonly string _projectRoot;
    private readonly int _pid;
    private readonly DateTime _startedUtc;
    private readonly object _gate = new();

    // Processing/"startup" (NOT Waiting), so a monitor started concurrently
    // with host.Start()
    // is even called, specifically so a slow cold start is visible instead of a blank screen)
    // never has a real chance to render this pre-first-event default. Waiting's own status line
    // asserts "another watcher process is already active" -- true only once a genuine
    // WatcherEvent.LockWaiting has actually fired; rendering that BEFORE this process has even
    // attempted to acquire the lock would be exactly the kind of false/misleading signal this
    // whole feature exists to eliminate. The render thread's first frame can win a race against
    // Promote()'s first event by a few milliseconds (WatcherLock.TryAcquire/StartFileSystemWatchers
    // do real, non-instant I/O), long enough to paint one full frame (up to PollInterval) of this
    // default before it's overwritten.
    private WatchActivityState _state = WatchActivityState.Processing;
    private DateTime _lastUpdatedUtc;
    private CurrentActivity? _current;
    private WatchTotals _totals = WatchTotals.Empty;
    private LastPassSummary? _lastPass;

    // Set by a SelfHealSweep/ErrorResync WatcherEvent, consumed by the very next
    // "[watch-fsw] self-heal/error-resync sweep: starting ..." diagnostic line -- the event
    // fires immediately before that line is emitted (see WatcherHost.OnSelfHealTick/OnWatcherError),
    // so this is never held across two separate passes.
    private string? _pendingResyncKind;

    // Extraction progress within the CURRENT pass: _passUnitTotal is set from the discovery
    // line's `units=N` field (RunCsAnalysis's unit count for this pass -- known upfront, unlike
    // the scan's file/dir count), and _passExtractCount counts `[cs-cache] <unit> extract ...`
    // lines seen since. Reset at the start of each new pass (a fresh discovery line resets both)
    // and cleared in FinalizePass so a pass that never emits a discovery line (e.g. a skip-gate
    // no-op) can't leak a stale total into whatever pass runs next.
    private int? _passUnitTotal;
    private int _passExtractCount;

    public WatchStatusTracker(string projectRoot, int pid, DateTime? nowUtc = null)
    {
        _projectRoot = projectRoot;
        _pid = pid;
        _startedUtc = nowUtc ?? DateTime.UtcNow;
        _lastUpdatedUtc = _startedUtc;
        _current = new CurrentActivity("startup", _startedUtc, _startedUtc, null, null, "starting…");
    }

    /// <summary>Whether this tracker persists to <see cref="WatchStatusFile"/> on every update
    /// (true by default). Tests that only care about the in-memory snapshot can disable this to
    /// avoid touching disk.</summary>
    public bool WriteToDisk { get; init; } = true;

    public WatchStatusSnapshot Snapshot()
    {
        lock (_gate)
        {
            return BuildSnapshotLocked();
        }
    }

    public void OnWatcherEvent(WatcherEvent watcherEvent)
    {
        // Always a forced (immediate) write: these are inherently low-frequency, boundary-level
        // transitions (promotion, a batch/sweep completing, stop) -- never the rapid per-unit
        // steps within a single pass that ForceWrite=false throttling below exists to absorb.
        Mutate(() =>
        {
            switch (watcherEvent)
            {
                case WatcherEvent.LockWaiting:
                    _state = WatchActivityState.Waiting;
                    _current = null;
                    break;
                case WatcherEvent.PromotionCatchupSweepStarted:
                    // Fires immediately before WatcherHost.Promote's blocking catch-up
                    // RunIndex(full:false) call -- the expensive part of a cold start (can be
                    // minutes on a large project). Entering Processing here, with a synthetic
                    // "initial-index" activity, is what gives `monitor` a
                    // live "it's working" signal for that whole window instead of a blank/stale
                    // screen until the first pass summary line arrives. Scan progress
                    // and extraction progress diagnostic lines refine `_current` further
                    // as they arrive; WatcherEvent.Promoted (below) or the pass's own summary
                    // line (FinalizePass) clears it on completion, whichever fires first.
                    _state = WatchActivityState.Processing;
                    _current = new CurrentActivity("initial-index", DateTime.UtcNow, DateTime.UtcNow, null, "scanning", "starting…");
                    break;
                case WatcherEvent.Promoted:
                    _state = WatchActivityState.Watching;
                    _current = null;
                    break;
                case WatcherEvent.BatchApplied:
                    _totals = _totals with { BatchesApplied = _totals.BatchesApplied + 1 };
                    break;
                case WatcherEvent.SelfHealSweep:
                    _totals = _totals with { SelfHealSweeps = _totals.SelfHealSweeps + 1 };
                    _pendingResyncKind = "self-heal-sweep";
                    break;
                case WatcherEvent.SelfHealSweepSkipped:
                    _totals = _totals with { SelfHealSweepsSkipped = _totals.SelfHealSweepsSkipped + 1 };
                    break;
                case WatcherEvent.ErrorResync:
                    _totals = _totals with { ErrorResyncs = _totals.ErrorResyncs + 1 };
                    _pendingResyncKind = "error-resync";
                    break;
                case WatcherEvent.Stopped:
                    _state = WatchActivityState.Stopped;
                    _current = null;
                    break;
                default:
                    // PromotionBufferingStarted / PromotionCatchupSweepStarted/Completed /
                    // PromotionBufferDrained / PromotionHeartbeatStarted: promotion sub-steps.
                    // No dedicated display for these yet -- the eventual RunIndex summary line
                    // from the catch-up sweep still lands in LastPass via OnDiagnosticLine.
                    break;
            }
        }, force: true);
    }

    /// <summary>
    /// Boundary lines (a batch/sweep starting, a pass finalizing) force an immediate write, same
    /// as <see cref="OnWatcherEvent"/>. Per-unit step lines (one per Semantic-mode assembly per
    /// pass -- potentially many on a large project's real-project validation runs) are throttled
    /// (<see cref="MinStepWriteInterval"/>) rather than writing to
    /// disk on every single one: the in-memory snapshot is still updated immediately either way
    /// (so the worker's current state is never stale), only the
    /// separate-process `unbramble monitor` file round-trip is coalesced.
    /// </summary>
    public void OnDiagnosticLine(string line)
    {
        if (WatchDiagnosticParser.TryParseDebounceFired(line, out var batchSize))
        {
            if (batchSize > 0)
            {
                Mutate(() =>
                {
                    _current = new CurrentActivity("batch", DateTime.UtcNow, DateTime.UtcNow, null, null, null);
                    _state = WatchActivityState.Processing;
                }, force: true);
            }

            return;
        }

        if (WatchDiagnosticParser.IsResyncStarting(line))
        {
            Mutate(() =>
            {
                var kind = _pendingResyncKind ?? "resync";
                _pendingResyncKind = null;
                _current = new CurrentActivity(kind, DateTime.UtcNow, DateTime.UtcNow, null, null, null);
                _state = WatchActivityState.Processing;
            }, force: true);
            return;
        }

        if (WatchDiagnosticParser.TryParseCsCacheStep(line, out var unitStep))
        {
            Mutate(() =>
            {
                // "unit k/N (<name>)" once the pass's discovery line has told us N --
                // reuses lines already emitted for the compilation-cache diagnostics, no new
                // engine instrumentation. Falls back to the pre-existing "extract (sub): detail"
                // rendering when the total isn't known (e.g. a synthetic line fed directly to
                // the tracker in a test, with no preceding discovery line).
                if (unitStep.Phase == "extract" && _passUnitTotal is { } total)
                {
                    _passExtractCount++;
                    AdvanceCurrentStep(unit: null, "extracting", $"unit {_passExtractCount}/{total} ({unitStep.Unit})");
                }
                else
                {
                    var phase = unitStep.Sub is null ? unitStep.Phase : $"{unitStep.Phase} ({unitStep.Sub})";
                    AdvanceCurrentStep(unitStep.Unit, phase, unitStep.Detail);
                }

                if (unitStep.Phase == "build")
                {
                    _totals = ClassifyBuildOutcome(unitStep.Detail);
                }
            }, force: false);
            return;
        }

        if (WatchDiagnosticParser.TryParseCsAnalysisStep(line, out var analysisStep))
        {
            Mutate(() =>
            {
                if (analysisStep.Phase == "discovery" && analysisStep.Units is { } units)
                {
                    _passUnitTotal = units;
                    _passExtractCount = 0;
                }

                AdvanceCurrentStep(unit: null, analysisStep.Phase, analysisStep.Detail);
            }, force: false);
            return;
        }

        if (WatchDiagnosticParser.TryParseScanProgress(line, out var scanProgress))
        {
            // Deliberately no percentage/fraction (unlike extraction, above): the scan's total
            // file/dir count is unknown until the walk finishes, so an honest live count +
            // elapsed beats a fabricated completion estimate (see ScanProgress's own doc comment).
            Mutate(() => AdvanceCurrentStep(unit: null, "scanning", $"{scanProgress.FilesSeen:N0} files, {scanProgress.DirsVisited:N0} dirs"), force: false);
            return;
        }

        if (WatchDiagnosticParser.TryParseSummary(line, out var summary))
        {
            Mutate(() => FinalizePass(summary), force: true);
        }

        // Any other line (e.g. raw "[watch-fsw] ... ACCEPTED/REJECTED ..." per-event tracing)
        // carries nothing this tracker surfaces -- ignored, not an error.
    }

    /// <summary>
    /// Bumps <see cref="_lastUpdatedUtc"/> and force-writes the status file, touching no other
    /// state. Called on the same ~5s cadence as <see cref="Freshness.WatcherHost"/>'s freshness
    /// heartbeat idle timer (<see cref="Freshness.WatcherEvent.HeartbeatIdle"/>) so a live but
    /// fully idle watcher keeps advancing LastUpdatedUtc instead of freezing at whenever the last
    /// real mutation happened -- the root cause of the false "heartbeat stale... watcher may have
    /// exited" warning shown to a user watching an idle-but-alive watcher. Deliberately routed
    /// through the presentational status-file channel only
    /// -- see <see cref="WatchStatusFile"/>'s own doc comment on why that file (unlike the
    /// freshness heartbeat itself) is safe to write on this cadence/this early.
    /// </summary>
    public void OnIdleTick() => Mutate(() => { }, force: true);

    private WatchTotals ClassifyBuildOutcome(string detail)
    {
        if (detail.StartsWith("FULL-REBUILD", StringComparison.Ordinal))
        {
            return _totals with { CacheFullRebuilds = _totals.CacheFullRebuilds + 1 };
        }

        if (detail.StartsWith("INCREMENTAL", StringComparison.Ordinal))
        {
            return _totals with { CacheIncrementalBuilds = _totals.CacheIncrementalBuilds + 1 };
        }

        if (detail.StartsWith("REUSED", StringComparison.Ordinal))
        {
            return _totals with { CacheReusedNoOp = _totals.CacheReusedNoOp + 1 };
        }

        return _totals;
    }

    private void AdvanceCurrentStep(string? unit, string phase, string detail)
    {
        var kind = _current?.Kind ?? "batch";
        var startedUtc = _current?.StartedUtc ?? DateTime.UtcNow;
        _current = new CurrentActivity(kind, startedUtc, DateTime.UtcNow, unit, phase, detail);
        if (_state != WatchActivityState.Processing)
        {
            _state = WatchActivityState.Processing;
        }
    }

    private void FinalizePass(WatchDiagnosticParser.Summary summary)
    {
        var totalMs = summary.Steps.FirstOrDefault(s => s.Label == "total")?.Ms ?? 0;
        var kind = _current?.Kind ?? InferPassKindFromSummaryHeader(summary.Kind);

        _lastPass = new LastPassSummary(kind, DateTime.UtcNow, totalMs, summary.Steps, summary.Added, summary.Changed, summary.Removed);

        if (summary.Added is { } added)
        {
            _totals = _totals with { FilesAdded = _totals.FilesAdded + added };
        }

        if (summary.Changed is { } changed)
        {
            _totals = _totals with { FilesChanged = _totals.FilesChanged + changed };
        }

        if (summary.Removed is { } removed)
        {
            _totals = _totals with { FilesRemoved = _totals.FilesRemoved + removed };
        }

        _current = null;
        _passUnitTotal = null;
        _passExtractCount = 0;
        if (_state == WatchActivityState.Processing)
        {
            _state = WatchActivityState.Watching;
        }
    }

    private static string InferPassKindFromSummaryHeader(string header) =>
        header.StartsWith("ApplyTargetedUpdate", StringComparison.Ordinal) ? "batch" : "sweep";

    // Per-unit step lines can arrive far faster than a human-readable dashboard needs to
    // refresh (a large project's pass can emit dozens of [cs-cache] lines within milliseconds).
    // Throttling non-boundary writes to this cadence keeps `monitor`'s view responsive (well
    // under one debounce interval of visible lag) without hammering disk once per line on a big
    // multi-unit pass.
    private static readonly TimeSpan MinStepWriteInterval = TimeSpan.FromMilliseconds(150);

    private DateTime? _lastWrittenUtc;

    /// <summary>
    /// Applies a mutation under <see cref="_gate"/> and, if <see cref="WriteToDisk"/>, persists
    /// the resulting snapshot -- immediately when <paramref name="force"/> (a boundary event), or
    /// throttled to <see cref="MinStepWriteInterval"/> otherwise. The in-memory snapshot
    /// (<see cref="Snapshot"/>) always reflects the mutation immediately either way -- only the
    /// disk write is ever skipped, never the state update itself.
    /// </summary>
    private void Mutate(Action apply, bool force)
    {
        WatchStatusSnapshot? toWrite = null;
        lock (_gate)
        {
            apply();
            _lastUpdatedUtc = DateTime.UtcNow;

            if (WriteToDisk && (force || _lastWrittenUtc is null || _lastUpdatedUtc - _lastWrittenUtc.Value >= MinStepWriteInterval))
            {
                _lastWrittenUtc = _lastUpdatedUtc;
                toWrite = BuildSnapshotLocked();
            }
        }

        if (toWrite is not null)
        {
            WatchStatusFile.Write(_projectRoot, toWrite);
        }
    }

    private WatchStatusSnapshot BuildSnapshotLocked() =>
        new(SchemaVersion, _pid, _projectRoot, _state, _startedUtc, _lastUpdatedUtc, _current, _totals, _lastPass);
}
