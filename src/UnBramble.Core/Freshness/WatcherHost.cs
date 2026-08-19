using UnBramble.Core.Model;
using UnBramble.Core.Scanning;

namespace UnBramble.Core.Freshness;

/// <summary>Lifecycle/progress events a <see cref="WatcherHost"/> raises — consumed by the CLI for human-readable logging and by tests to assert ordering.</summary>
public enum WatcherEvent
{
    /// <summary>Lock refused; will retry on <see cref="WatcherOptions.LockRetryInterval"/>.</summary>
    LockWaiting,

    /// <summary>Promotion step 1: FileSystemWatchers are live and buffering events.</summary>
    PromotionBufferingStarted,

    /// <summary>Promotion step 2 start: the catch-up stat-sweep.</summary>
    PromotionCatchupSweepStarted,

    /// <summary>Promotion step 2 end.</summary>
    PromotionCatchupSweepCompleted,

    /// <summary>Promotion step 3: whatever buffered during steps 1-2 has been applied.</summary>
    PromotionBufferDrained,

    /// <summary>Promotion step 4: the first heartbeat write, and periodic heartbeat/self-heal timers, are starting.</summary>
    PromotionHeartbeatStarted,

    /// <summary>All four promotion steps are complete; this host is now the active watcher.</summary>
    Promoted,

    /// <summary>A debounced batch of changed paths was applied.</summary>
    BatchApplied,

    /// <summary>The periodic full-sweep backstop ran — raised after the sweep COMPLETES, so an
    /// observer may rely on the swept state being committed.</summary>
    SelfHealSweep,

    /// <summary>The periodic self-heal timer fired but was skipped: no FileSystemWatcher
    /// callback (accepted or rejected) had been observed since the previous tick, so there is
    /// nothing this full sweep could catch up on. See <see cref="WatcherHost.OnSelfHealTick"/>'s
    /// doc comment.</summary>
    SelfHealSweepSkipped,

    /// <summary>FileSystemWatcher.Error (buffer overflow) triggered an immediate full resync —
    /// raised after that resync COMPLETES, same contract as <see cref="SelfHealSweep"/>.</summary>
    ErrorResync,

    /// <summary>Stop()/Dispose() completed.</summary>
    Stopped,

    /// <summary>The periodic heartbeat-idle timer ticked (~every <see
    /// cref="WatcherOptions.HeartbeatIdleCadence"/>, same cadence as the freshness heartbeat
    /// write it accompanies -- see <see cref="WatcherHost.StartHeartbeatIdleTimer"/>). Carries no
    /// state of its own; exists purely so a live-but-idle watcher's <see
    /// cref="Monitoring.WatchStatusTracker"/> keeps advancing LastUpdatedUtc instead of freezing,
    /// which previously made an idle watcher falsely look "heartbeat stale" to `monitor`/
    /// `monitor`. Deliberately separate from the
    /// freshness heartbeat itself -- see <see cref="WriteHeartbeat"/>'s own doc comment on why
    /// that one must not be written any earlier/more often than it already is.</summary>
    HeartbeatIdle,

    /// <summary>
    /// `--auto`-mode-only (see <see cref="WatcherHost.OnSelfHealTick"/>): this host's own idle
    /// TTL (<see cref="AutoIdleGate"/>) elapsed with no fast-path query activity, so it's about
    /// to stop itself. Raised from inside a timer callback -- the CLI worker
    /// reacts by setting its own stop signal from OUTSIDE that callback (the same mechanism
    /// Ctrl+C already uses) rather than this class calling <see cref="WatcherHost.Stop"/> on
    /// itself re-entrantly. Never raised for a normal (non-`--auto`) `watch` invocation, which
    /// ignores the idle marker entirely and runs until Ctrl+C as before.
    /// </summary>
    AutoIdleTimeout,
}

/// <summary>
/// Push-path freshness: one <see cref="FileSystemWatcher"/> per
/// real root from the persisted `roots` table, ~1s-debounced targeted reparse, a heartbeat
/// file, a single-active-writer lock with a load-bearing promotion order, and a self-heal
/// backstop. Explicit Start/Stop lifecycle (not baked into the CLI) so it's trivial to host
/// in-process later (an MCP server) and so tests can drive it directly, in-process, with
/// injected timing and an event hook instead of racing real wall-clock timers.
///
/// Governing invariant: never wrong, only sometimes slower. This class only ever *adds*
/// freshness on top of the pull-path stat-sweep (<see cref="UnBrambleEngine.RunIndex"/>) that
/// every CLI invocation already performs when the heartbeat is stale — a watcher that does
/// nothing at all is still correct, just slower to reflect changes.
/// </summary>
public sealed class WatcherHost : IDisposable
{
    private readonly UnBrambleEngine _engine;
    private readonly WatcherOptions _options;
    private readonly Action<WatcherEvent>? _onEvent;

    // Diagnostics only (null unless a caller opts in): fine-grained tracing of the raw FSW
    // event -> path-translation -> scope-check -> debounce -> batch pipeline, one line per
    // decision point, including every REJECTION that the pre-existing code (OnFsEvent,
    // TranslateToProjectPath, IsInWatchScope) previously handled by silently returning --
    // there was no way to tell "no event arrived at all" apart from "an event arrived and was
    // silently dropped" without this. See ProcessSemanticUnitWithWatchCache and friends in
    // UnBrambleEngine for the analogous [cs-cache]/[cs-analysis] diagnostics on the compile/
    // extract side; this is the same idea applied to the push-path event pipeline.
    private readonly Action<string>? _onDiagnostic;

    // Serializes every call into _engine (RunIndex / ApplyTargetedUpdate): FSW event handlers,
    // the debounce timer, the heartbeat/self-heal timers, and the promotion sequence can all
    // run on different ThreadPool threads, but UnBrambleStore's single SqliteConnection is not
    // safe for concurrent use from multiple threads at once.
    private readonly object _dbGate = new();

    // Guards _pendingPaths, _buffering, and _debounceTimer together so "add to the pending set"
    // and "decide whether to (re)schedule the debounce timer" stay one atomic step.
    private readonly object _pendingGate = new();
    private HashSet<string> _pendingPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _buffering;

    private List<(RootMapping Root, FileSystemWatcher Watcher)>? _watchers;
    private Timer? _debounceTimer;
    private Timer? _heartbeatIdleTimer;
    private Timer? _selfHealTimer;
    private Timer? _lockRetryTimer;
    private FileStream? _lockHandle;
    private volatile bool _stopRequested;

    // WriteHeartbeat
    // is called from at least four independently-scheduled contexts within this one process --
    // the debounce timer (ProcessBatch), the self-heal timer (both its real-sweep path via
    // RunFullResync and its skip path), the separate heartbeat-idle timer (StartHeartbeatIdleTimer,
    // firing every HeartbeatIdleCadence -- 5s by default), and Promote -- with no mutual exclusion
    // between them. HeartbeatFile.Write's atomic-write pattern (unique temp file, then
    // File.Move(..., overwrite: true) to the SAME shared destination path) is only safe against a
    // READER racing a writer, not two WRITERS racing each other: two concurrent File.Move calls
    // targeting the same destination can throw (observed live as UnauthorizedAccessException),
    // which is fatal to the whole process when raised from an unguarded Timer callback. Serializing
    // every call through this one gate eliminates the race at its source (within this process);
    // HeartbeatFile.Write itself additionally tolerates a transient failure as defense-in-depth
    // (see its own doc comment) in case of a genuinely external actor (AV scanner, etc.).
    private readonly object _heartbeatGate = new();

    // On a large real target project, real-time FileSystemWatcher detection through
    // every watched root (including junction-resolved ones) worked correctly for every edit
    // tested -- the periodic self-heal sweep never had to close a real detection gap in that run.
    // A full NTFS-USN-journal change-detection rebuild is NOT justified by that evidence; what IS
    // still real and measured is that OnSelfHealTick fires a
    // full RunIndex(full:false) walk of the ENTIRE watched tree unconditionally on a timer,
    // forever, even across a fully idle window where not one FileSystemWatcher callback (accepted
    // OR rejected) occurred -- there is provably nothing for a full walk to catch up on in that
    // case. This counter is the small, directly-evidenced fix: incremented once per raw FSW
    // callback in OnFsEvent (every Created/Changed/Deleted/Renamed invocation, REGARDLESS of
    // whether OnFsEvent goes on to accept or reject it -- even a rejected event, e.g. under an
    // excluded dir, is still real filesystem churn that could sit adjacent to the documented
    // renamed-directory-descendants gap RunFullResync exists to close), and atomically read-and-
    // reset by OnSelfHealTick. "Never wrong, only sometimes slower" still holds: any doubt (any
    // activity at all since the last tick, however small or seemingly irrelevant) always runs the
    // full sweep; FSW.Error's own resync path (OnWatcherError -> RunFullResync) is untouched by
    // this counter entirely and always fires unconditionally, exactly as before.
    private long _fsActivitySinceLastSelfHeal;

    // `--auto`-mode-only (docs/architecture.md, "Auto-spawn watcher"). _autoModeStartedUtc is
    // this host's construction time, not its promotion time -- used as AutoIdleGate's fallback
    // reference so a freshly spawned watcher gets a full idle-TTL window even if it never
    // observes a single fast-path query (see that method's own doc comment). _autoIdleExitRaised
    // guards against raising WatcherEvent.AutoIdleTimeout more than once: the self-heal timer
    // keeps ticking on its own cadence until the CLI actually calls Stop() in response to the
    // first one, and re-raising on every subsequent tick in that window would be noise.
    private readonly bool _autoMode;
    private readonly TimeSpan _autoIdleTtl;
    private readonly DateTime _autoModeStartedUtc = DateTime.UtcNow;
    private volatile bool _autoIdleExitRaised;

    /// <summary>True once this host has completed promotion and is the active writer.</summary>
    public bool IsActive { get; private set; }

    public WatcherHost(
        UnBrambleEngine engine,
        WatcherOptions? options = null,
        Action<WatcherEvent>? onEvent = null,
        Action<string>? onDiagnostic = null,
        bool autoMode = false,
        TimeSpan? autoIdleTtl = null)
    {
        _engine = engine;
        _options = options ?? WatcherOptions.Default;
        _onEvent = onEvent;
        _onDiagnostic = onDiagnostic;
        _autoMode = autoMode;
        _autoIdleTtl = autoIdleTtl ?? AutoIdleGate.DefaultIdleTtl;
    }

    /// <summary>
    /// Attempts the lock immediately. If won, promotes synchronously (including the catch-up
    /// sweep) before returning — the same blocking behavior `index`/`init` already have.
    /// If lost, schedules passive retries on <see cref="WatcherOptions.LockRetryInterval"/> and
    /// returns immediately.
    /// </summary>
    public void Start()
    {
        _stopRequested = false;
        TryAcquireOrScheduleRetry();
    }

    /// <summary>
    /// Detached-worker entry point (Program's hidden `watch-worker`, docs/architecture.md
    /// "Auto-spawn watcher"): attempts the watcher lock exactly ONCE and returns immediately --
    /// never schedules the passive retry loop <see cref="Start"/> does. An auto-spawned watcher
    /// exists to fill the gap until a real watcher owns freshness; if one already does (this
    /// returns false), queuing up behind it would let a burst of stale-heartbeat queries each
    /// spawn a process that all sit waiting on the same lock -- the crash-loop cooldown
    /// (<see cref="AutoSpawnPolicy"/>) only bounds how often a NEW spawn is attempted, not how
    /// long an already-spawned-but-waiting process would otherwise stick around. Returns true
    /// once this host has fully promoted (same synchronous, blocking-through-the-catch-up-sweep
    /// behavior <see cref="Start"/> has when it wins the lock immediately).
    /// </summary>
    public bool TryStartOnceForAuto()
    {
        _stopRequested = false;
        var handle = WatcherLock.TryAcquire(_engine.ProjectRoot);
        if (handle is null)
        {
            return false;
        }

        _lockHandle = handle;
        Promote();
        return true;
    }

    /// <summary>
    /// Stops everything (watchers, timers, lock) idempotently. Safe to call more than once, and
    /// safe to call when never promoted.
    ///
    /// Every timer callback below (debounce, lock-retry, heartbeat-idle, self-heal) can still be
    /// mid-execution on a ThreadPool thread at the moment Stop() is called, and every one of them
    /// eventually touches <see cref="_dbGate"/>/<see cref="_engine"/> (a SqliteConnection) or the
    /// heartbeat/lock files on disk. A plain <see cref="Timer.Dispose()"/> only cancels FUTURE
    /// callbacks -- it does not wait for one that is already running. If Stop() returned while a
    /// callback was still in flight, a caller (a test's `finally { host.Stop(); }`) could go on to
    /// dispose the engine/SqliteConnection or delete a temp fixture directory out from under that
    /// callback; the callback would then throw on a background thread with nothing to catch it,
    /// which the CLR treats as fatal and aborts the whole process instead of merely failing a
    /// test. <see cref="Timer.Dispose(WaitHandle)"/> blocks until any in-flight callback has
    /// actually finished, so every timer is torn down through <see cref="DisposeTimerAndWait"/>
    /// instead of a bare Dispose().
    /// </summary>
    public void Stop()
    {
        _stopRequested = true;

        // Stop the FileSystemWatchers first so no new FS event can arrive and re-arm the
        // debounce timer while the timers below are being torn down.
        if (_watchers is { } watchers)
        {
            foreach (var (_, watcher) in watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }

            _watchers = null;
        }

        StopDebounceTimerAndWait();
        StopLockRetryTimerAndWait();
        DisposeTimerAndWait(ref _heartbeatIdleTimer);
        DisposeTimerAndWait(ref _selfHealTimer);

        _lockHandle?.Dispose();
        _lockHandle = null;

        IsActive = false;
        _onEvent?.Invoke(WatcherEvent.Stopped);
    }

    /// <summary>
    /// _debounceTimer is created/re-armed under <see cref="_pendingGate"/> (see OnFsEvent), so
    /// capturing-and-nulling it must happen under that same lock. The wait for the callback to
    /// finish must NOT happen while holding the lock: the callback (ProcessBatch) itself takes
    /// _pendingGate at its start, so blocking on it while holding the lock would deadlock.
    /// </summary>
    private void StopDebounceTimerAndWait()
    {
        Timer? timer;
        lock (_pendingGate)
        {
            timer = _debounceTimer;
            _debounceTimer = null;
        }

        DisposeTimerAndWait(timer);
    }

    /// <summary>
    /// _lockRetryTimer re-arms itself: TryAcquireOrScheduleRetry creates a fresh one-shot Timer
    /// each time it loses the race for the lock. A callback can read <see cref="_stopRequested"/>
    /// as false a moment before Stop() flips it true, and then go on to create a replacement
    /// timer after this method has already captured/waited on the one it knew about. Loop until a
    /// capture-and-wait round observes no replacement (bounded: once _stopRequested is true, the
    /// next callback to run always bails out before creating another one, so this converges in at
    /// most a couple of iterations).
    /// </summary>
    private void StopLockRetryTimerAndWait()
    {
        while (true)
        {
            var timer = _lockRetryTimer;
            if (timer is null)
            {
                return;
            }

            DisposeTimerAndWait(timer);

            if (ReferenceEquals(_lockRetryTimer, timer))
            {
                _lockRetryTimer = null;
                return;
            }
        }
    }

    /// <summary>Disposes a <see cref="Timer"/> and blocks until any callback that was already in flight has fully completed.</summary>
    private static void DisposeTimerAndWait(Timer? timer)
    {
        if (timer is null)
        {
            return;
        }

        using var noMoreCallbacksPending = new ManualResetEvent(initialState: false);

        // Returns false only if the timer was already disposed by a previous call (nothing to
        // wait for); otherwise it always eventually signals the handle, immediately if no
        // callback was running, or once the in-flight one returns.
        if (timer.Dispose(noMoreCallbacksPending))
        {
            noMoreCallbacksPending.WaitOne();
        }
    }

    private static void DisposeTimerAndWait(ref Timer? timer)
    {
        var captured = timer;
        timer = null;
        DisposeTimerAndWait(captured);
    }

    public void Dispose() => Stop();

    /// <summary>Test-only hook mirroring what a real <see cref="FileSystemWatcher.Error"/> (buffer overflow) triggers — simulating an actual OS-level overflow in a test is flaky by nature, so tests invoke this directly instead.</summary>
    public void SimulateWatcherError() => OnWatcherError();

    // ---- Lock acquisition / promotion --------------------------------------------------

    private void TryAcquireOrScheduleRetry()
    {
        if (_stopRequested)
        {
            return;
        }

        var handle = WatcherLock.TryAcquire(_engine.ProjectRoot);
        if (handle is null)
        {
            _onEvent?.Invoke(WatcherEvent.LockWaiting);
            _lockRetryTimer = new Timer(_ => TryAcquireOrScheduleRetry(), null, _options.LockRetryInterval, Timeout.InfiniteTimeSpan);
            return;
        }

        _lockHandle = handle;
        Promote();
    }

    /// <summary>
    /// Sweeping before watching would leave an unwatched gap that the heartbeat then
    /// falsely vouches for. Order is load-bearing and must not be reordered:
    ///   1. start FileSystemWatchers, buffering events (not yet applying them);
    ///   2. catch-up stat-sweep (a full <see cref="UnBrambleEngine.RunIndex"/>);
    ///   3. drain whatever buffered during steps 1-2;
    ///   4. begin writing the heartbeat (only now may other processes' pull-sweeps trust it).
    /// </summary>
    private void Promote()
    {
        lock (_pendingGate)
        {
            _buffering = true;
            _pendingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        StartFileSystemWatchers();
        _onEvent?.Invoke(WatcherEvent.PromotionBufferingStarted);

        _onEvent?.Invoke(WatcherEvent.PromotionCatchupSweepStarted);
        lock (_dbGate)
        {
            _engine.RunIndex(full: false);
        }

        _onEvent?.Invoke(WatcherEvent.PromotionCatchupSweepCompleted);

        HashSet<string> buffered;
        lock (_pendingGate)
        {
            buffered = _pendingPaths;
            _pendingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _buffering = false;
        }

        if (buffered.Count > 0)
        {
            lock (_dbGate)
            {
                _engine.ApplyTargetedUpdate(buffered);
            }
        }

        _onEvent?.Invoke(WatcherEvent.PromotionBufferDrained);

        _onEvent?.Invoke(WatcherEvent.PromotionHeartbeatStarted);
        WriteHeartbeat();
        StartHeartbeatIdleTimer();
        StartSelfHealTimer();

        IsActive = true;
        _onEvent?.Invoke(WatcherEvent.Promoted);
    }

    // ---- FileSystemWatchers ---------------------------------------------------------------

    private void StartFileSystemWatchers()
    {
        // One FSW per real root from the PERSISTED roots table (never recomputed — a
        // recomputed mapping could disagree with what the index actually stored), excluding
        // the identity-only Library/PackageCache root: it refreshes on sweep/explicit index
        // only, never watched in real time.
        var roots = _engine.GetRoots()
            .Where(r => !r.ProjectPrefix.Equals(Scanner.PackageCacheProjectPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var watchers = new List<(RootMapping, FileSystemWatcher)>(roots.Count);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root.RealPath))
            {
                continue;
            }

            var watcher = new FileSystemWatcher(root.RealPath)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                    | NotifyFilters.Size | NotifyFilters.CreationTime,
            };

            var capturedRoot = root;
            watcher.Created += (_, e) => OnFsEvent(capturedRoot, e.FullPath, "Created");
            watcher.Changed += (_, e) => OnFsEvent(capturedRoot, e.FullPath, "Changed");
            watcher.Deleted += (_, e) => OnFsEvent(capturedRoot, e.FullPath, "Deleted");
            watcher.Renamed += (_, e) =>
            {
                OnFsEvent(capturedRoot, e.OldFullPath, "Renamed(old)");
                OnFsEvent(capturedRoot, e.FullPath, "Renamed(new)");
            };
            watcher.Error += (_, e) => OnWatcherError(e.GetException());

            watcher.EnableRaisingEvents = true;
            watchers.Add((root, watcher));

            _onDiagnostic?.Invoke($"[watch-fsw] watching root prefix={root.ProjectPrefix} realPath={root.RealPath}");
        }

        _watchers = watchers;
    }

    private void OnFsEvent(RootMapping root, string realFullPath, string eventKind)
    {
        // Counts EVERY raw callback, before any accept/reject decision below -- see the field's
        // own doc comment for why a rejected event still counts as "activity happened."
        Interlocked.Increment(ref _fsActivitySinceLastSelfHeal);

        var projectPath = TranslateToProjectPath(root, realFullPath);
        if (projectPath is null)
        {
            // Previously a silent early return -- indistinguishable from "no event arrived at
            // all". Logs the RAW path FSW reported alongside the root it's being matched
            // against so a symlink/junction-reached file (a real production project is confirmed
            // symlink-heavy -- see the real-project validation notes) whose reported
            // path doesn't share the expected prefix is visible directly, not just inferred
            // from its absence.
            _onDiagnostic?.Invoke($"[watch-fsw] {eventKind} REJECTED (path-translate-failed) root={root.RealPath} raw={realFullPath}");
            return;
        }

        if (!IsInWatchScope(projectPath, out var scopeRejectReason))
        {
            _onDiagnostic?.Invoke($"[watch-fsw] {eventKind} REJECTED (out-of-scope: {scopeRejectReason}) path={projectPath}");
            return;
        }

        // An asset and its .meta are always reparsed as one unit: touching either enqueues the
        // owner path, and Scanner.ScanSingleFile always re-reads both sides regardless of which
        // one triggered the event.
        var ownerPath = projectPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
            ? projectPath[..^".meta".Length]
            : projectPath;

        lock (_pendingGate)
        {
            _pendingPaths.Add(ownerPath);
            var pendingCount = _pendingPaths.Count;
            if (!_buffering)
            {
                _debounceTimer ??= new Timer(_ => ProcessBatch(), null, Timeout.Infinite, Timeout.Infinite);
                _debounceTimer.Change(_options.DebounceInterval, Timeout.InfiniteTimeSpan);
                _onDiagnostic?.Invoke($"[watch-fsw] {eventKind} ACCEPTED path={ownerPath} pending={pendingCount} debounce-armed_ms={_options.DebounceInterval.TotalMilliseconds:F0}");
            }
            else
            {
                // Buffered during promotion (see Promote's own doc comment) -- not lost, just
                // held until the buffer-drain step, which is a SEPARATE, non-debounced
                // ApplyTargetedUpdate call, not this class's usual debounce/ProcessBatch path.
                _onDiagnostic?.Invoke($"[watch-fsw] {eventKind} ACCEPTED-BUFFERED path={ownerPath} pending={pendingCount} (still promoting)");
            }
        }
    }

    private static string? TranslateToProjectPath(RootMapping root, string realFullPath)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.RealPath));
        string normalizedFull;
        try
        {
            normalizedFull = Path.GetFullPath(realFullPath);
        }
        catch (ArgumentException)
        {
            return null;
        }

        if (!normalizedFull.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var relative = normalizedFull[normalizedRoot.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var forwardRelative = relative.Replace(Path.DirectorySeparatorChar, '/');
        return forwardRelative.Length == 0 ? root.ProjectPrefix : $"{root.ProjectPrefix}/{forwardRelative}";
    }

    /// <summary>
    /// Same in-scope rules as the scanner (hidden names, excludeDirs), applied to every path
    /// segment, plus an explicit guard on Library/ (structurally unreachable here — Library is
    /// never a watched root — kept as defense-in-depth anyway). Extension
    /// filtering is deliberately NOT duplicated here: <see cref="Scanner.ScanSingleFile"/> (the
    /// scanner's own classifier) is the single source of truth for "is this a real reference
    /// source," and harmlessly no-ops on a path that fails its own classification — an event for
    /// an out-of-scope extension costs one wasted stat, never a wrong answer.
    /// </summary>
    private bool IsInWatchScope(string projectPath, out string? rejectReason)
    {
        if (projectPath.StartsWith("Library/", StringComparison.OrdinalIgnoreCase))
        {
            rejectReason = "under Library/";
            return false;
        }

        foreach (var segment in projectPath.Split('/'))
        {
            if (HiddenAssetRules.IsHidden(segment, isDirectory: true) || HiddenAssetRules.IsHidden(segment, isDirectory: false))
            {
                rejectReason = $"hidden segment '{segment}'";
                return false;
            }

            if (_engine.Config.ExcludeDirs.Contains(segment, StringComparer.OrdinalIgnoreCase))
            {
                rejectReason = $"excluded dir '{segment}'";
                return false;
            }
        }

        rejectReason = null;
        return true;
    }

    private void OnWatcherError(Exception? exception = null)
    {
        _onDiagnostic?.Invoke($"[watch-fsw] FileSystemWatcher.Error: {exception?.GetType().Name} {exception?.Message ?? "(no exception captured)"}");
        // After the resync, same completion contract (and the same latent race) as
        // OnSelfHealTick's SelfHealSweep -- see that method's own comment.
        RunFullResync();
        _onEvent?.Invoke(WatcherEvent.ErrorResync);
    }

    // ---- Debounce / batch processing -------------------------------------------------------

    private void ProcessBatch()
    {
        if (_stopRequested)
        {
            return;
        }

        HashSet<string> batch;
        lock (_pendingGate)
        {
            batch = _pendingPaths;
            _pendingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        _onDiagnostic?.Invoke($"[watch-fsw] debounce fired: batch-size={batch.Count}");

        if (batch.Count == 0)
        {
            return;
        }

        lock (_dbGate)
        {
            _engine.ApplyTargetedUpdate(batch);
        }

        WriteHeartbeat();
        _onEvent?.Invoke(WatcherEvent.BatchApplied);
    }

    // ---- Heartbeat / self-heal --------------------------------------------------------------

    /// <summary>Serializes every heartbeat write from this process against every other one --
    /// see <see cref="_heartbeatGate"/>'s own doc comment for why that's required, not optional.</summary>
    private void WriteHeartbeat()
    {
        lock (_heartbeatGate)
        {
            HeartbeatFile.Write(_engine.ProjectRoot, Environment.ProcessId, DateTime.UtcNow);
        }
    }

    // Heartbeat refresh: "after each processed batch" is already covered by ProcessBatch/
    // Promote/RunFullResync all calling WriteHeartbeat() directly; this periodic timer covers
    // "at least every 5s when idle" on top of that. Also raises HeartbeatIdle on the same tick:
    // the freshness heartbeat itself must not be written any earlier/more often than
    // this (see WriteHeartbeat's doc comment -- a pull-path sweep in another process would wrongly
    // trust an early one), but the presentational status-file channel has no such constraint, so
    // this is a second, parallel signal riding the same timer purely to keep
    // WatchStatusTracker.LastUpdatedUtc from freezing while idle.
    private void StartHeartbeatIdleTimer() =>
        _heartbeatIdleTimer = new Timer(
            _ =>
            {
                WriteHeartbeat();
                _onEvent?.Invoke(WatcherEvent.HeartbeatIdle);
            },
            null,
            _options.HeartbeatIdleCadence,
            _options.HeartbeatIdleCadence);

    private void StartSelfHealTimer() =>
        _selfHealTimer = new Timer(_ => OnSelfHealTick(), null, _options.SelfHealSweepInterval, _options.SelfHealSweepInterval);

    /// <summary>
    /// See <see cref="_fsActivitySinceLastSelfHeal"/>'s own doc comment for the full reasoning:
    /// skips
    /// this tick's full <see cref="RunFullResync"/> walk when zero FileSystemWatcher callbacks
    /// (accepted or rejected) were observed since the previous tick -- provably nothing for a full
    /// walk to catch up on. The read-and-reset is atomic (<see cref="Interlocked.Exchange(ref long, long)"/>)
    /// so a callback racing this check either lands before the exchange (counted, resync runs) or
    /// after it (counted toward the NEXT tick instead) -- never silently lost. The heartbeat is
    /// still refreshed on a skip: a live, idle watcher is still fresh, just with nothing to sweep.
    /// </summary>
    private void OnSelfHealTick()
    {
        if (_stopRequested)
        {
            return;
        }

        // `--auto`-mode idle-TTL self-termination check (docs/architecture.md, "Auto-spawn
        // watcher"): piggybacks on this existing periodic tick rather than a new timer -- a
        // normal (non-`--auto`) watch process never reads AutoWatchMarkers at all, so this is a
        // no-op for it beyond one branch check. Raising the event does NOT call Stop() on this
        // object from inside its own timer callback (see AutoIdleTimeout's own doc comment for
        // why that would be unsafe); it's the CLI's job to react from outside this callback, the
        // same way Ctrl+C already does. This tick still returns without running the ordinary
        // self-heal-or-skip logic below -- a host that's about to shut down has nothing useful
        // left to sweep for.
        if (_autoMode && !_autoIdleExitRaised
            && AutoIdleGate.ShouldExit(AutoWatchMarkers.TryReadLastQuery(_engine.ProjectRoot), _autoModeStartedUtc, DateTime.UtcNow, _autoIdleTtl))
        {
            _autoIdleExitRaised = true;
            _onDiagnostic?.Invoke("[watch-auto] idle TTL exceeded with no fast-path query activity -- self-terminating");
            _onEvent?.Invoke(WatcherEvent.AutoIdleTimeout);
            return;
        }

        if (Interlocked.Exchange(ref _fsActivitySinceLastSelfHeal, 0) == 0)
        {
            _onDiagnostic?.Invoke("[watch-fsw] self-heal tick: SKIPPED (no FSW activity since last tick)");
            _onEvent?.Invoke(WatcherEvent.SelfHealSweepSkipped);
            WriteHeartbeat();
            return;
        }

        // Raised AFTER the resync completes, not before it starts: the event's contract is "the
        // backstop ran", and its consumers (tests polling for it, then asserting on post-sweep
        // DB state) read it exactly that way -- raising it first was a real, load-sensitive race
        // (observed as a repeated full-suite-only flake: the poll won against the sweep's own
        // commit and asserted against a DB the sweep hadn't written yet).
        RunFullResync();
        _onEvent?.Invoke(WatcherEvent.SelfHealSweep);
    }

    /// <summary>
    /// Full stat-sweep backstop, used by both self-heal paths (periodic, and FSW.Error):
    /// events can be lost or hard to fully account for (a renamed/deleted directory only fires
    /// one event for the directory itself, none for descendants — a documented, accepted gap),
    /// so assume nothing and re-derive the whole inventory.
    /// </summary>
    private void RunFullResync()
    {
        // Brackets the RunIndex call so a self-heal/error-resync sweep is directly visible as a
        // span in the log, correlated against the [cs-analysis] RunIndex(full=False) summary
        // line RunIndex itself emits from inside this same lock -- lets a reader match "this
        // self-heal sweep took N ms wall-clock" against "and here's exactly where that N ms
        // went" without guessing which RunIndex call in the log belongs to which sweep.
        var stopwatch = _onDiagnostic is null ? null : System.Diagnostics.Stopwatch.StartNew();
        _onDiagnostic?.Invoke("[watch-fsw] self-heal/error-resync sweep: starting RunIndex(full=false)");

        lock (_dbGate)
        {
            _engine.RunIndex(full: false);
        }

        if (stopwatch is not null)
        {
            _onDiagnostic!($"[watch-fsw] self-heal/error-resync sweep: finished elapsed_ms={stopwatch.ElapsedMilliseconds}");
        }

        WriteHeartbeat();
    }
}
