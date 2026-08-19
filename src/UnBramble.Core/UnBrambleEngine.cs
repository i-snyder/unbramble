using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using UnBramble.Core.Addressables;
using UnBramble.Core.CSharp;
using UnBramble.Core.Config;
using UnBramble.Core.Exceptions;
using UnBramble.Core.Freshness;
using UnBramble.Core.Liveness;
using UnBramble.Core.Model;
using UnBramble.Core.Monitoring;
using UnBramble.Core.Parsing;
using UnBramble.Core.ProjectDetection;
using UnBramble.Core.Query;
using UnBramble.Core.Scanning;
using UnBramble.Core.Store;

namespace UnBramble.Core;

/// <summary>
/// Top-level facade over project detection, the Force Text gate, the scanner, and the
/// store. UnBramble.Cli calls this and does nothing else but parse args, format output, and
/// map results to exit codes.
/// </summary>
public sealed class UnBrambleEngine : IDisposable
{
    /// <summary>First-pass default transitive walk depth cap.</summary>
    public const int DefaultDepthCap = 12;

    private readonly UnBrambleStore _store;

    // Watch-only persistent Roslyn compilation cache (see CsCompilationCache's own doc comment).
    // Null by default -- every one-shot CLI verb (init/index/stats/who-uses/etc.) and the pull-
    // path stat-sweep never call EnableWatchCompilationCache, so RunCsAnalysis/ProcessUnit stay
    // on the existing from-scratch BuildCompilation path, byte-for-byte unchanged, for every
    // caller except the `watch` command's own long-lived engine instance.
    private CsCompilationCache? _watchCompilationCache;

    // Set exactly once, by EnableWatchCompilationCache, the moment it creates a BRAND-NEW cache
    // instance -- consumed (read, then reset) by the very next RunCsAnalysis call, forcing that
    // one pass to run in full even when the skip-gate (see RunCsAnalysis's own doc comment)
    // would otherwise fire on a no-change sweep. This is the watch-startup pre-warm fix: without
    // it, `watch`'s Promote() catch-up sweep (RunIndex(full:false), which already runs before
    // the Promoted event -- see WatcherHost.Promote's doc comment on promotion order) is a
    // guaranteed skip whenever the project was already indexed before `watch` started (the
    // common case -- `existingNames` already covers every unit, nothing is dirty), so
    // _watchCompilationCache stayed completely empty until the user's FIRST real edit, which
    // then paid to build every Semantic-mode unit's compilation from scratch synchronously
    // (measured 500+s on the real target project) before that edit's own feedback could appear.
    // Forcing ONE full pass here reuses RunCsAnalysis/ProcessUnit/ProcessSemanticUnitWithWatchCache
    // completely unchanged -- the pre-warmed state is byte-for-byte what a lazy first-edit warm-up
    // would have produced, because it's the identical code path, just run one pass earlier, with
    // an empty dirty set (so no extraction/DB write happens -- only the Compilation objects get
    // built and cached; see ProcessUnit's own comment on why Semantic-mode compilation is built
    // unconditionally, dirty or not). Never set for any caller that doesn't opt into the watch
    // cache (every one-shot CLI verb), so RunCsAnalysis's existing skip gate is completely
    // unaffected for them by construction.
    private bool _watchCachePrewarmPending;

    // Session-scoped cached project model (unit discovery + per-unit mode decisions) -- see
    // CsSessionModelCache's own doc comment for what it caches and exactly when it invalidates.
    // Unconditional (NOT watch-only, unlike _watchCompilationCache above): a one-shot verb's
    // engine runs a single pass, so the cache is populated once and never consulted again --
    // behavior identical by construction -- while a long-lived watch engine (or a multi-pass
    // test) gets O(change) steady-state passes. Both RunIndex (pull) and ApplyTargetedUpdate
    // (push) reach it through the same RunCsAnalysis, so the two paths cannot diverge on what
    // model they see.
    private readonly CsSessionModelCache _sessionModel = new();

    // Watch-cache diagnostics only (null unless a caller opts in via EnableWatchCompilationCache's
    // onUnitDecision parameter): one line per unit per phase (compilation-cache decision, and
    // separately, extraction) so a real-world slow pass can be diagnosed from stderr output alone
    // instead of guessing from wall-clock/CPU-time shape. See ProcessSemanticUnitWithWatchCache's
    // and ProcessUnit's own call sites for exactly what gets logged.
    private Action<string>? _watchCacheDiagnostics;

    public string ProjectRoot { get; }

    public string UnityVersion { get; }

    public UnBrambleConfig Config { get; }

    public string DbPath { get; }

    public bool WasCreated { get; }

    public bool SchemaWasReset { get; }

    public IReadOnlyList<string> OpenWarnings { get; }

    private UnBrambleEngine(
        string projectRoot,
        string unityVersion,
        UnBrambleConfig config,
        string dbPath,
        UnBrambleStore store,
        IReadOnlyList<string> openWarnings)
    {
        ProjectRoot = projectRoot;
        UnityVersion = unityVersion;
        Config = config;
        DbPath = dbPath;
        _store = store;
        WasCreated = store.WasCreated;
        SchemaWasReset = store.SchemaWasReset;
        OpenWarnings = openWarnings;
    }

    /// <exception cref="ProjectNotFoundException">No Unity project found walking up from <paramref name="startPath"/>.</exception>
    /// <exception cref="ForceTextNotEnabledException">The project is not using (or provably using) Force Text serialization.</exception>
    public static UnBrambleEngine Open(string startPath)
    {
        var root = ProjectDetector.FindProjectRoot(startPath) ?? throw new ProjectNotFoundException(startPath);
        ForceTextGate.Assert(root);

        var config = UnBrambleConfigLoader.Load(root, out var configWarnings);
        var unityVersion = ProjectDetector.ReadUnityVersion(root);
        var dbPath = Path.Combine(root, config.DbPath.Replace('/', Path.DirectorySeparatorChar));

        var store = UnBrambleStore.OpenOrCreate(dbPath, unityVersion, root);

        var warnings = new List<string>(configWarnings);
        if (store.SchemaWasReset)
        {
            warnings.Add($"Schema version mismatch: rebuilding index from scratch (schema v{UnBrambleStore.CurrentSchemaVersion}).");
        }

        return new UnBrambleEngine(root, unityVersion, config, dbPath, store, warnings);
    }

    /// <summary>
    /// Opts this engine instance into the watch-only persistent Roslyn compilation cache (see
    /// <see cref="CsCompilationCache"/>) for every subsequent <see cref="RunIndex"/>/<see
    /// cref="ApplyTargetedUpdate"/> call. Intended to be called exactly once, by the `watch` CLI
    /// command, before <see cref="Freshness.WatcherHost.Start"/> runs its event loop -- a
    /// long-lived engine is the only scenario where holding onto compilations between calls is
    /// even meaningful (a one-shot CLI invocation never gets a second pass to reuse anything).
    /// Idempotent as far as the cache object itself: a second call never replaces an
    /// already-created cache, so it's safe to call defensively.
    ///
    /// Also arms a one-shot pre-warm (<see cref="_watchCachePrewarmPending"/>): the very next
    /// <see cref="RunCsAnalysis"/> call -- in practice, <see cref="Freshness.WatcherHost.Promote"/>'s
    /// own catch-up <see cref="RunIndex"/> sweep, which already runs before that host declares
    /// itself promoted -- builds every Semantic-mode unit's compilation into the cache even if
    /// nothing is actually dirty, instead of leaving the cache empty until the user's first real
    /// edit pays that cost synchronously. See that field's own doc comment for the full reasoning.
    /// </summary>
    /// <param name="onUnitDecision">
    /// Optional diagnostics sink: one call per Semantic-mode unit per pass for each of two
    /// phases -- the compilation-cache decision (full rebuild and why, vs. incremental edit/
    /// reference-swap/reuse-as-is), and, separately, whenever that unit is actually extracted
    /// (semantic-model walk), with the tree count and elapsed time. Both a cheap-looking cache
    /// decision AND a large extraction cost can be true for the SAME unit at once -- extraction
    /// is, by design, scoped to the whole unit, not just the changed file(s) within it (see
    /// ProcessUnit's own doc comment) -- so a real diagnosis needs to see both numbers side by
    /// side, not just which cache branch fired. Null (the default) emits nothing and costs
    /// nothing beyond a null check.
    /// </param>
    public void EnableWatchCompilationCache(Action<string>? onUnitDecision = null)
    {
        if (_watchCompilationCache is null)
        {
            _watchCompilationCache = new CsCompilationCache();
            _watchCachePrewarmPending = true;
        }

        if (onUnitDecision is not null)
        {
            _watchCacheDiagnostics = onUnitDecision;
        }
    }

    /// <summary>
    /// Runs a stat-sweep of the project against the store and applies the diff. `full`
    /// drops all data tables first (equivalent to sweeping against an empty store). Times each
    /// of the four phases separately so cost can be measured directly instead of guessed as a
    /// single opaque total.
    /// </summary>
    /// <param name="onScanProgress">
    /// Optional live scan-progress sink for the CALLER's own reporting (the CLI's stderr
    /// progress lines), independent of the watch-diagnostics channel below. Added after a real
    /// incident (real-project validation): a cold sweep of a large real project spends
    /// minutes in the scan phase at near-zero CPU with, previously, zero output of any kind --
    /// observed live as "the query hung" and killed. When both this and the watch diagnostics
    /// sink are present, both are invoked.
    /// </param>
    /// <param name="onPhase">
    /// Optional coarse phase-boundary sink (same incident as <paramref name="onScanProgress"/>):
    /// one call per phase transition after the scan, labeling what the sweep is about to do
    /// next -- the post-scan phases (diff apply, dirty reparse, C# analysis) can themselves run
    /// for minutes on a cold rebuild of a large project, and were previously just as silent.
    /// </param>
    public IndexSummary RunIndex(bool full, Action<ScanProgress>? onScanProgress = null, Action<string>? onPhase = null)
    {
        using var writerLease = IndexWriterLock.Acquire(ProjectRoot, onPhase);
        return RunIndexOwned(full, onScanProgress, onPhase);
    }

    private IndexSummary RunIndexOwned(bool full, Action<ScanProgress>? onScanProgress, Action<string>? onPhase)
    {
        var stopwatch = Stopwatch.StartNew();

        var isFirstRun = WasCreated;
        if (full)
        {
            _store.ResetData();
        }

        // Hoisted once: this PRE-sweep file snapshot feeds the scanner's meta mtime-gate
        // and is reused as ApplySweep's own "existing" comparison side, instead of
        // ApplySweep re-issuing the identical full-table load a moment later. (RunCsAnalysis
        // below deliberately does NOT reuse this snapshot -- see its own doc comment for why a
        // pre-diff snapshot would be the wrong, stale input there.)
        var scanStopwatch = Stopwatch.StartNew();
        var existingFiles = _store.LoadAllFiles();
        var knownMeta = BuildMetaSnapshot(existingFiles);
        var scanner = new Scanner();
        // Live scan progress: routed through the same
        // _watchCacheDiagnostics sink as every other watch-cache/analysis diagnostic line --
        // WatchStatusTracker is the one channel that parses this stream, so a brand-new tracker
        // reference here would fork that architecture for no reason. Null (every caller that
        // hasn't opted into EnableWatchCompilationCache -- i.e. every one-shot CLI verb) costs
        // nothing beyond the delegate-null checks Scanner.Scan/ScanHeartbeat already had.
        Action<ScanProgress>? diagnosticsProgress = _watchCacheDiagnostics is null
            ? null
            : progress => _watchCacheDiagnostics!($"[scan-progress] dirs={progress.DirsVisited} files={progress.FilesSeen} current={progress.CurrentPath}");
        var scan = scanner.Scan(ProjectRoot, Config, knownMeta, onProgress: (diagnosticsProgress, onScanProgress) switch
        {
            (null, null) => null,
            ({ } diag, null) => diag,
            (null, { } caller) => caller,
            ({ } diag, { } caller) => progress => { diag(progress); caller(progress); },
        });
        scanStopwatch.Stop();

        onPhase?.Invoke($"scan complete ({scan.Entries.Count:N0} entries); applying inventory diff");
        var sweepStopwatch = Stopwatch.StartNew();
        var diff = _store.ApplySweep(scan, existingFiles);
        // Skip the roots rewrite entirely when the sweep found zero file changes AND the
        // scanned roots are identical to what's already stored -- the common no-change-sweep
        // case pays for neither the diff-writes' empty transaction (already skipped inside
        // ApplyDiff) nor this one.
        if (diff.Added != 0 || diff.Changed != 0 || diff.Removed != 0 || !RootsMatch(_store.GetRoots(), scan.Roots))
        {
            _store.ReplaceRoots(scan.Roots);
        }

        sweepStopwatch.Stop();

        if (diff.DirtyPaths.Count > 0)
        {
            onPhase?.Invoke($"parsing {diff.DirtyPaths.Count:N0} changed files for references");
        }

        var reparseStopwatch = Stopwatch.StartNew();
        var parseWarnings = ReparseDirtyFiles(diff.DirtyPaths);
        reparseStopwatch.Stop();

        onPhase?.Invoke("running C# analysis");
        var csStopwatch = Stopwatch.StartNew();
        var csWarnings = RunCsAnalysis(diff);
        csStopwatch.Stop();

        stopwatch.Stop();

        var warnings = new List<string>(OpenWarnings.Count + scan.Warnings.Count + diff.Warnings.Count + parseWarnings.Count + csWarnings.Count);
        warnings.AddRange(OpenWarnings);
        warnings.AddRange(scan.Warnings);
        warnings.AddRange(diff.Warnings);
        warnings.AddRange(parseWarnings);
        warnings.AddRange(csWarnings);

        var stats = _store.GetStats();
        var phaseTimings = new IndexPhaseTimings(scanStopwatch.Elapsed, sweepStopwatch.Elapsed, reparseStopwatch.Elapsed, csStopwatch.Elapsed);

        // These four phase timings already existed but were previously only reachable via
        // the returned IndexSummary -- WatcherHost's own callers (Promote's catch-up sweep,
        // RunFullResync's self-heal/error-resync sweep) both discard RunIndex's return value
        // entirely, so this was invisible for exactly the callers most likely to be running a
        // slow, large-project sweep unattended. Surfaced here instead of duplicating stopwatches
        // at each WatcherHost call site.
        _watchCacheDiagnostics?.Invoke(
            $"[cs-analysis] RunIndex(full={full}): scan_ms={scanStopwatch.ElapsedMilliseconds} sweep_ms={sweepStopwatch.ElapsedMilliseconds} " +
            $"reparse_ms={reparseStopwatch.ElapsedMilliseconds} cs_ms={csStopwatch.ElapsedMilliseconds} total_ms={stopwatch.ElapsedMilliseconds} " +
            $"added={diff.Added} changed={diff.Changed} removed={diff.Removed} dirtyPaths={diff.DirtyPaths.Count}");

        AppendIndexHistory(full, scan, diff, phaseTimings, stopwatch.Elapsed);

        return new IndexSummary(ProjectRoot, UnityVersion, DbPath, stopwatch.Elapsed, diff.Added, diff.Changed, diff.Removed, isFirstRun, stats, warnings, phaseTimings);
    }

    /// <summary>
    /// Durable evidence for exactly this pass: appends one line to
    /// `.unbramble/index-history.log` via <see cref="IndexHistoryLog"/>. Deliberately separate
    /// from <see cref="Monitoring.WatchStatusFile"/> -- that file only ever retains the LAST
    /// completed pass and gets overwritten within seconds by an auto-spawned watcher's first
    /// no-op sweep, permanently losing a slow cold pass's own numbers. This file is
    /// append-only, so that data survives. Best-effort: any failure inside
    /// <see cref="IndexHistoryLog.Append"/> is already swallowed there, so this call can never
    /// change RunIndex's own outcome.
    /// </summary>
    private void AppendIndexHistory(bool full, Model.ScanResult scan, Store.SweepDiff diff, IndexPhaseTimings phaseTimings, TimeSpan totalElapsed)
    {
        long dirsVisited = 0;
        long filesSeen = 0;
        foreach (var r in scan.RootStats)
        {
            dirsVisited += r.Dirs;
            filesSeen += r.Files;
        }

        var entry = new IndexHistoryEntry(
            TimestampUtc: DateTime.UtcNow,
            Pid: Environment.ProcessId,
            Full: full,
            // Cheap-only signal, per this feature's own scope (no dedicated trigger-source
            // plumbing exists yet): a watch-mode engine is the only caller that ever opts into
            // the persistent compilation cache, so its presence doubles as "this pass ran inside
            // `watch`" versus every one-shot CLI verb.
            TriggerSource: _watchCompilationCache is not null ? "watch" : "cli",
            ScanMs: phaseTimings.Scan.TotalMilliseconds,
            SweepMs: phaseTimings.SweepDiff.TotalMilliseconds,
            ReparseMs: phaseTimings.DirtyReparse.TotalMilliseconds,
            CsMs: phaseTimings.CsAnalysis.TotalMilliseconds,
            TotalMs: totalElapsed.TotalMilliseconds,
            Added: diff.Added,
            Changed: diff.Changed,
            Removed: diff.Removed,
            DirtyCount: diff.DirtyPaths.Count,
            FilesSeen: filesSeen,
            DirsVisited: dirsVisited,
            Roots: [.. scan.RootStats.Select(r => new IndexHistoryRootEntry(r.ProjectPrefix, r.ResolvedTarget, r.IsJunction, r.Dirs, r.Files, r.ElapsedMs))],
            SlowDirs: [.. scan.SlowDirs.Select(d => new IndexHistorySlowDirEntry(d.ProjectPrefix, d.ElapsedMs))]);

        IndexHistoryLog.Append(ProjectRoot, entry);
    }

    private static Dictionary<string, MetaSnapshot> BuildMetaSnapshot(Dictionary<string, FileRow> existingFiles)
    {
        var snapshot = new Dictionary<string, MetaSnapshot>(existingFiles.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, row) in existingFiles)
        {
            snapshot[path] = new MetaSnapshot(row.MetaMtime, row.Guid);
        }

        return snapshot;
    }

    /// <summary>Cheap in-memory comparison so <see cref="RunIndex"/> can skip
    /// <see cref="UnBrambleStore.ReplaceRoots"/>'s delete+rewrite when nothing actually
    /// changed. Deduplicates the scanned side by real path first, same first-wins rule
    /// <c>ReplaceRoots</c> itself applies, so this can't ever produce a false "changed" purely
    /// from scan-order duplicates.</summary>
    private static bool RootsMatch(IReadOnlyList<RootMapping> stored, IReadOnlyList<RootMapping> scanned)
    {
        var scannedDeduped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in scanned)
        {
            scannedDeduped.TryAdd(mapping.RealPath, mapping.ProjectPrefix);
        }

        if (stored.Count != scannedDeduped.Count)
        {
            return false;
        }

        foreach (var s in stored)
        {
            if (!scannedDeduped.TryGetValue(s.RealPath, out var prefix) || !string.Equals(prefix, s.ProjectPrefix, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reparses reference-derived rows (refs/path_refs/gameobjects/component_gameobject) for
    /// every file the sweep flagged as new or changed. Identity-only (PackageCache) files and
    /// .cs files are never reference sources — a .cs file's sibling .meta is skipped too, so a
    /// script can never appear as a `refs`/`path_refs` source. Folders have no content file to
    /// parse but their .meta (importer refs) is still parsed like any other asset's.
    ///
    /// Per-file parsing is verified independent
    /// (<see cref="ReferenceParser"/> is stateless — no instance fields beyond static lookup
    /// tables, no cross-file state) so it runs on a bounded pool of parallel workers
    /// (<see cref="Environment.ProcessorCount"/>-wide). Results funnel through a bounded
    /// <see cref="BlockingCollection{T}"/> to exactly ONE writer — this method's own calling
    /// thread, which is the only thread that ever touches <c>_store</c> for the life of this
    /// engine call (<see cref="Freshness.WatcherHost"/>'s <c>_dbGate</c> already treats a whole
    /// <see cref="RunIndex"/>/<see cref="ApplyTargetedUpdate"/> call as atomic from any other
    /// thread's perspective, so no additional locking is needed here). The writer applies
    /// chunked <see cref="UnBrambleStore.ReplaceFileReferences"/> transactions instead of the old
    /// single call over every dirty file's rows materialized at once — that both bounds peak
    /// memory on a huge cold-init diff and keeps each transaction's WAL tail short.
    /// Deletions-before-insertions holds per chunk by construction, same as before parallelism:
    /// <see cref="UnBrambleStore.ReplaceFileReferences"/> deletes then (re)inserts the SAME file
    /// ids within one call, and no file id is ever split across two chunks (each dirty file is
    /// parsed and queued exactly once) — the cross-file rename-sensitive delete ordering this
    /// could otherwise threaten already happened earlier, inside <c>ApplyDiff</c>'s own
    /// transaction, untouched by this method. Parse warnings are collected into a
    /// <see cref="ConcurrentBag{T}"/> (order is a race across workers) and sorted before being
    /// returned so the reported warning order stays stable run-to-run, matching the store's own
    /// query layer, which reads every table back through an explicit <c>ORDER BY</c> for exactly
    /// this reason.
    /// </summary>
    private List<string> ReparseDirtyFiles(IReadOnlyList<string> dirtyPaths)
    {
        if (dirtyPaths.Count == 0)
        {
            return [];
        }

        var rows = _store.LoadFileRows(dirtyPaths);
        var parser = new ReferenceParser();
        var warningBag = new ConcurrentBag<string>();

        const int ChunkSize = 1500;
        using var pending = new BlockingCollection<(long FileId, ParsedFileRefs Refs)>(boundedCapacity: ChunkSize * 4);

        var producer = Task.Run(() =>
        {
            try
            {
                Parallel.ForEach(
                    rows,
                    new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                    row => pending.Add(ParseOneDirtyFile(row, parser, warningBag)));
            }
            finally
            {
                // Runs even if a worker throws (Parallel.ForEach surfaces that as an
                // AggregateException from producer.GetAwaiter().GetResult() below) so the
                // consumer loop below can never hang waiting on a producer that already died.
                pending.CompleteAdding();
            }
        });

        var chunk = new List<(long FileId, ParsedFileRefs Refs)>(ChunkSize);
        foreach (var item in pending.GetConsumingEnumerable())
        {
            chunk.Add(item);
            if (chunk.Count >= ChunkSize)
            {
                _store.ReplaceFileReferences(chunk);
                chunk.Clear();
            }
        }

        if (chunk.Count > 0)
        {
            _store.ReplaceFileReferences(chunk);
        }

        // Draining is already complete by this point; this just re-raises any worker exception
        // (as an AggregateException) instead of silently swallowing it.
        producer.GetAwaiter().GetResult();

        return [.. warningBag.OrderBy(w => w, StringComparer.Ordinal)];
    }

    private (long FileId, ParsedFileRefs Refs) ParseOneDirtyFile(FileRow row, ReferenceParser parser, ConcurrentBag<string> warnings)
    {
        if (row.IdentityOnly || row.Kind == FileKind.Script)
        {
            return (row.Id, ParsedFileRefs.Empty);
        }

        var fullPath = ToFullPath(row.Path);
        var contentRefs = ParsedFileRefs.Empty;
        if (row.Kind != FileKind.Folder)
        {
            try
            {
                if (File.Exists(fullPath))
                {
                    contentRefs = parser.ParseContentSource(fullPath, row.Path, row.Guid);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"warning: could not parse '{row.Path}': {ex.Message}");
            }
        }

        IReadOnlyList<GuidRefRow> metaRefs = [];
        if (row.MetaMtime is not null)
        {
            try
            {
                metaRefs = parser.ParseMetaOwnerRefs(fullPath + ".meta", row.Guid);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"warning: could not parse meta for '{row.Path}': {ex.Message}");
            }
        }

        var combined = metaRefs.Count == 0
            ? contentRefs
            : contentRefs with { GuidRefs = [.. contentRefs.GuidRefs, .. metaRefs] };

        return (row.Id, combined);
    }

    /// <summary>
    /// C# semantic graph analysis. Assembly discovery is cheap (string/JSON only) and reruns
    /// every sweep the phase isn't skipped for; actual Roslyn compilation + extraction only runs
    /// for assemblies that are "dirty": a script or asmdef among <paramref name="diff"/>'s dirty
    /// paths belongs to them (or is new to the DB), or they transitively depend on an assembly
    /// that is (incremental scope is coarse but correct — never wrong, only sometimes slower).
    /// Non-dirty Semantic-mode assemblies are still (re)compiled in-memory-only so a
    /// dirty dependent's cross-assembly symbol resolution stays real semantic resolution against
    /// current sources, not stale DLLs — only DB writes are skipped for them.
    ///
    /// The whole phase above is skipped entirely — no
    /// discovery, no compilation, no DB touch — when ALL of: (a) no dirty path is
    /// `.cs`/`.asmdef`/`.asmref` (<see cref="CsRelevantPaths.IsCsRelevant"/>); (b) no such path
    /// was REMOVED either (<see cref="SweepDiff.RemovedCsRelevant"/> — a removal never appears
    /// in DirtyPaths, so this is the only signal for it); (c) every persisted assembly's mode
    /// fingerprint (csproj path + mtime, or its recorded absence) still matches disk, checked as
    /// a plain stat (<see cref="CsAnalysisFingerprintsMatch"/>), which also subsumes detecting a
    /// csproj appearing/disappearing/changing outside any tracked `files` row. On any doubt this
    /// resolves to running the phase, never skipping it — "never wrong, only sometimes slower"
    /// only licenses the skip when EVERY condition is verifiably true. Both <see cref="RunIndex"/>
    /// (the pull path) and <see cref="ApplyTargetedUpdate"/> (the watcher push path) call this
    /// SAME method with the SAME gate, so the two can never diverge on when it's safe to skip.
    ///
    /// Deliberately re-loads <see cref="UnBrambleStore.LoadAllFiles"/> itself (not threaded in from
    /// the caller's own pre-diff snapshot) when the gate doesn't fire AND the session model can't
    /// prove its cached unit list is still valid: <see cref="AssemblyUnitDiscovery.Discover"/>
    /// needs the POST-diff file inventory (current ids, including anything the diff just
    /// inserted/updated/removed), and the caller's snapshot was necessarily taken BEFORE that
    /// diff committed. The skip gate already eliminates this load entirely on the dominant
    /// no-change sweep; the session model (<see cref="CsSessionModelCache"/>) additionally
    /// eliminates it on the dominant DIRTY steady-state pass —
    /// a content edit to existing `.cs` files with nothing added/removed/rebuilt and no
    /// asmdef/asmref touched — where the diff itself proves membership and ids are stable.
    /// </summary>
    private List<string> RunCsAnalysis(SweepDiff diff)
    {
        var csAnalysisStopwatch = _watchCacheDiagnostics is null ? null : Stopwatch.StartNew();

        // Consumed exactly once: see _watchCachePrewarmPending's own doc comment for why this
        // can force an otherwise-skippable pass to run in full, and why that's the watch-startup
        // compilation-cache pre-warm fix, not a correctness change.
        var forcePrewarm = _watchCachePrewarmPending;
        _watchCachePrewarmPending = false;

        var dirtyPaths = diff.DirtyPaths;
        var anyCsRelevantDirty = dirtyPaths.Any(CsRelevantPaths.IsCsRelevant);
        var wouldSkip = !anyCsRelevantDirty && !diff.RemovedCsRelevant && CsAnalysisFingerprintsMatch();
        if (wouldSkip && forcePrewarm)
        {
            _watchCacheDiagnostics?.Invoke("[cs-analysis] RunCsAnalysis: skip-gate bypassed once to pre-warm the watch compilation cache");
        }
        else if (wouldSkip)
        {
            _watchCacheDiagnostics?.Invoke($"[cs-analysis] RunCsAnalysis: SKIPPED elapsed_ms={csAnalysisStopwatch!.ElapsedMilliseconds}");
            return [];
        }

        // Session-model reuse (see CsSessionModelCache):
        // the full file-inventory load + Discover below is O(project) -- every row re-loaded and
        // re-walked, every asmdef re-read from disk, on EVERY pass -- and is provably redundant
        // whenever this pass's own diff shows unit membership and file ids are stable. The
        // data_version capture deliberately happens BEFORE LoadAllFiles (see SetUnits' param doc).
        var discoveryStopwatch = _watchCacheDiagnostics is null ? null : Stopwatch.StartNew();
        var dataVersion = _store.GetDataVersion();
        var structureDirty = diff.Added != 0 || diff.Removed != 0 || diff.AnyGuidRebuild
            || dirtyPaths.Any(CsSessionModelCache.IsUnitStructurePath);

        var discoveryReused = _sessionModel.TryGetUnits(dataVersion, structureDirty, out var units, out var discoveryWarnings);
        if (!discoveryReused)
        {
            var files = _store.LoadAllFiles().Values
                .Select(r => new CsFileEntry(r.Id, r.Path, r.Guid, r.Kind, r.IdentityOnly))
                .ToList();

            (units, discoveryWarnings) = AssemblyUnitDiscovery.Discover(files, ToFullPath);
            _sessionModel.SetUnits(units, discoveryWarnings, dataVersion);
        }

        discoveryStopwatch?.Stop();
        if (discoveryStopwatch is not null)
        {
            _watchCacheDiagnostics!($"[cs-analysis] discovery: {(discoveryReused ? "session-cache REUSED" : "rebuilt")} units={units.Count} elapsed_ms={discoveryStopwatch.ElapsedMilliseconds}");
        }

        if (units.Count == 0)
        {
            return [.. discoveryWarnings];
        }

        var existingNames = new HashSet<string>(_store.GetAssemblyNames(), StringComparer.Ordinal);
        var dirtyPathSet = new HashSet<string>(dirtyPaths, StringComparer.OrdinalIgnoreCase);
        var byName = units.ToDictionary(u => u.Name, StringComparer.Ordinal);

        var dirty = new HashSet<string>(StringComparer.Ordinal);
        foreach (var unit in units)
        {
            var touched = !existingNames.Contains(unit.Name)
                || (unit.AsmdefPath is not null && dirtyPathSet.Contains(unit.AsmdefPath))
                || unit.Scripts.Any(s => dirtyPathSet.Contains(s.Path));
            if (touched)
            {
                dirty.Add(unit.Name);
            }
        }

        // Snapshot BEFORE the transitive-dependents BFS below mutates `dirty` in place: the
        // watch-compilation-cache path (ProcessSemanticUnitWithWatchCache) needs to tell "this
        // unit's OWN .cs files changed" (selfDirty -- needs ReplaceSyntaxTree/Add/Remove) apart
        // from "this unit is only in `dirty` because a dependency changed" (needs ReplaceReference
        // only, no tree edits of its own) -- a distinction the pre-cache code never needed since
        // it always rebuilt every Semantic-mode unit from scratch regardless of either.
        var selfDirty = new HashSet<string>(dirty, StringComparer.Ordinal);

        // Transitive dependents: a Semantic-mode assembly compiles against its dependencies'
        // real compilations, so a dependency's source change can change a
        // dependent's own resolved symbol data even though none of the dependent's own files
        // changed.
        var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var unit in units)
        {
            foreach (var referenceName in unit.References)
            {
                if (!byName.ContainsKey(referenceName))
                {
                    continue;
                }

                if (!dependents.TryGetValue(referenceName, out var list))
                {
                    list = [];
                    dependents[referenceName] = list;
                }

                list.Add(unit.Name);
            }
        }

        var queue = new Queue<string>(dirty);
        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (!dependents.TryGetValue(name, out var deps))
            {
                continue;
            }

            foreach (var dependent in deps)
            {
                if (dirty.Add(dependent))
                {
                    queue.Enqueue(dependent);
                }
            }
        }

        var orderingStopwatch = _watchCacheDiagnostics is null ? null : Stopwatch.StartNew();
        var (order, orderWarnings) = AssemblyUnitDiscovery.TopologicalOrder(units);
        var levels = GroupIntoDependencyLevels(order);
        if (orderingStopwatch is not null)
        {
            _watchCacheDiagnostics!($"[cs-analysis] ordering: levels={levels.Count} elapsed_ms={orderingStopwatch.ElapsedMilliseconds}");
        }

        // Aggregated across every unit's mode-selection call below (Interlocked since units
        // within a level run concurrently via Parallel.ForEach) -- NOT covered by either the
        // per-unit cs-cache build/extract diagnostics (which only start timing AFTER mode
        // selection has already run) or by discoveryStopwatch above (mode selection is per-UNIT,
        // not part of file-inventory discovery). The pre-session-cache behavior
        // (CsModeSelector.Determine XML-parsing every unit's FULL generated
        // csproj, every unit, every pass) was a leading real-project cost; with the session model
        // this now measures one stat per unit on the steady-state path and only pays the parse
        // when a csproj actually moved.
        long modeSelectionTotalTicks = 0;

        var compilationCache = new ConcurrentDictionary<string, Microsoft.CodeAnalysis.CSharp.CSharpCompilation>(StringComparer.Ordinal);
        // Shared across every BuildCompilation call in
        // this pass, so a DLL referenced by many assemblies (engine modules, common packages) is
        // loaded from disk once instead of once per referencing assembly. Concurrent: a
        // level can run several units' BuildCompilation calls at once, all reading/populating
        // this same cache.
        var metadataReferenceCache = new ConcurrentDictionary<string, PortableExecutableReference>(StringComparer.OrdinalIgnoreCase);
        var analysesBag = new ConcurrentBag<CsAssemblyAnalysis>();
        var syntacticNamesBag = new ConcurrentBag<string>();
        // Set once (Interlocked, not a bag -- only ever need to know "at least one") when a
        // syntactic unit's asmdef is package-sourced (Packages/, LocalPackages/): Unity's "open
        // the project once" auto-regeneration does NOT cover those by default (see
        // Scanner.IsPackageSourcedPath's own doc comment), so the sweep-level warning below needs
        // a different remediation hint than a plain Assets/ script's.
        var anyPackageSourcedSyntactic = 0;
        // Refreshed for every unit encountered this pass, dirty or not (see
        // UpdateAssemblyModeFingerprints' own doc comment for why non-dirty units still need
        // their fingerprint kept current).
        var fingerprintsBag = new ConcurrentBag<UnBrambleStore.AssemblyModeFingerprint>();

        // Watch-compilation-cache only (null in every other caller): names of units whose
        // Compilation object actually changed identity THIS pass -- either freshly rebuilt, or
        // incrementally derived via ReplaceSyntaxTree/AddSyntaxTrees/RemoveSyntaxTrees/
        // ReplaceReference. A dependent checks this (never the OLD `dirty` set, which conflates
        // "changed" with "needs re-extraction") to decide whether IT needs a ReplaceReference
        // swap of its own -- populated strictly level-by-level, so by the time any unit in level
        // N asks about a dependency (always in a strictly lower level, by construction of
        // GroupIntoDependencyLevels), that dependency's entry here is already final for this pass.
        var changedThisPass = _watchCompilationCache is null
            ? null
            : new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);

        void ProcessUnit(AssemblyUnit unit)
        {
            var modeSelectionStopwatch = _watchCacheDiagnostics is null ? null : Stopwatch.StartNew();
            // Session-cached: the full csproj XML parse
            // only runs when the csproj's mtime/existence moved since the last pass -- otherwise
            // this is one stat. See CsSessionModelCache.GetModeDecision.
            var modeDecision = _sessionModel.GetModeDecision(ProjectRoot, unit.Name);
            if (modeSelectionStopwatch is not null)
            {
                Interlocked.Add(ref modeSelectionTotalTicks, modeSelectionStopwatch.ElapsedTicks);
            }

            fingerprintsBag.Add(new UnBrambleStore.AssemblyModeFingerprint(unit.Name, modeDecision.CsprojMtimeTicks));
            if (modeDecision.Mode == CsAnalysisMode.Syntactic)
            {
                syntacticNamesBag.Add(unit.Name);
                if (unit.AsmdefPath is not null && Scanner.IsPackageSourcedPath(unit.AsmdefPath))
                {
                    Interlocked.Exchange(ref anyPackageSourcedSyntactic, 1);
                }
            }

            if (modeDecision.Mode == CsAnalysisMode.Semantic)
            {
                // Hazard (preserve exactly): every Semantic-mode unit is compiled here
                // regardless of `dirty` -- a dirty dependent's cross-assembly resolution must
                // stay real semantic resolution against CURRENT sources, not a stale DLL. Level
                // grouping only changes WHEN/on WHICH thread this runs, never WHETHER it runs:
                // a unit's dependencies are, by construction of GroupIntoDependencyLevels, all
                // in strictly lower levels and therefore already fully present in
                // compilationCache by the time any unit in this unit's level starts.
                //
                // "Compiled" no longer means "rebuilt from scratch" when the watch compilation
                // cache is enabled: see ProcessSemanticUnitWithWatchCache for the cheap-derivation
                // path this delegates to instead. When the cache is null (every caller except the
                // `watch` command), this is the exact same BuildCompilation call as before --
                // byte-for-byte unchanged.
                CSharpCompilation compilation;
                Dictionary<SyntaxTree, long> fileIdByTree;
                bool scopedExtractionEligible;
                if (_watchCompilationCache is { } watchCache)
                {
                    var built = ProcessSemanticUnitWithWatchCache(watchCache, unit, modeDecision, compilationCache, selfDirty.Contains(unit.Name), dirtyPathSet, changedThisPass!);
                    compilation = built.Compilation;
                    fileIdByTree = built.FileIdByTree;

                    // Eligibility: per-file extraction/DB-write scoping is only attempted when this unit's
                    // compilation was cheaply DERIVED (not rebuilt from scratch -- a full rebuild
                    // means the environment itself may have moved, so the dirty files' declaration
                    // shapes staying identical proves nothing about the OTHER, never-re-extracted
                    // files' rows) and changed ONLY via this unit's own self-dirty file edits
                    // (no dependency reference swap this pass -- ApplyDependencyReferenceSwaps can
                    // change how ANY tree in this unit resolves a symbol from the swapped dependency,
                    // and precisely which trees are affected isn't tracked, so the safe default for
                    // that case stays full-unit extraction). Ineligibility here no longer means a
                    // full-unit DB WRITE though: the full-extraction fallback below now diffs its
                    // own result per file (exact row content, every file re-extracted -- see
                    // ScopeFullUnitAnalysisToChangedFiles) and narrows the write the same way.
                    scopedExtractionEligible = !built.FullRebuild && !built.HasDependencySwaps && selfDirty.Contains(unit.Name);
                }
                else
                {
                    (compilation, fileIdByTree) = CsProjectAnalyzer.BuildCompilation(unit, modeDecision, compilationCache, ToFullPath, metadataReferenceCache);
                    scopedExtractionEligible = false;
                }

                compilationCache[unit.Name] = compilation;

                if (dirty.Contains(unit.Name))
                {
                    var scriptFileIds = unit.Scripts.Select(s => s.FileId).ToList();
                    var extractStopwatch = _watchCacheDiagnostics is null ? null : Stopwatch.StartNew();

                    CsAssemblyAnalysis? scoped = scopedExtractionEligible
                        ? TryExtractScoped(unit, compilation, fileIdByTree, dirtyPathSet, extractStopwatch)
                        : null;

                    if (scoped is { } scopedAnalysis)
                    {
                        analysesBag.Add(scopedAnalysis);
                    }
                    else
                    {
                        // Unconditional on the FULL compilation whenever this unit is dirty and
                        // scoping either wasn't eligible or was tried and had to escalate (this
                        // unit's own declaration shape moved -- see TryExtractScoped). This is the
                        // pre-existing, by-design safe default: "actually-affected set" here means
                        // affected FILES within the unit whenever that's provably safe,
                        // and affected UNITS otherwise -- never the reverse. For a large unit
                        // (Unity's own Assembly-CSharp catch-all in particular, which can hold a
                        // disproportionate share of a real project's scripts), this walk's own cost
                        // can dominate regardless of how cheaply the compilation itself was derived --
                        // logged separately from the cache decision above for exactly that reason.
                        var (symbols, refs, nameHints) = SemanticCsExtractor.Extract(compilation, fileIdByTree);
                        if (extractStopwatch is not null)
                        {
                            _watchCacheDiagnostics!($"[cs-cache] {unit.Name} extract (full-unit): trees={fileIdByTree.Count} symbols={symbols.Count} elapsed_ms={extractStopwatch.ElapsedMilliseconds}");
                        }

                        var fullAnalysis = new CsAssemblyAnalysis(unit.Name, unit.AsmdefFileId, CsAnalysisMode.Semantic, scriptFileIds, symbols, refs, nameHints);

                        // A FULL extraction
                        // (whatever forced it -- csproj-mtime/shape-hash full rebuild, dependency
                        // reference swap, or a self-edit that escalated above) does NOT imply
                        // every FILE's rows changed: after e.g. Unity regenerating a csproj mid-
                        // session, most of a 300-file unit's files extract to byte-identical rows.
                        // ScopeFullUnitAnalysisToChangedFiles diffs the fresh result per file
                        // against what's persisted (EXACT row content, not the declaration-shape
                        // proxy -- see its doc comment for why shape alone would be unsafe here)
                        // and narrows the DB write to just the files that genuinely differ. Watch
                        // only, same as every other cache-era behavior change: with the cache
                        // null this is byte-for-byte the pre-existing full write.
                        analysesBag.Add(_watchCompilationCache is null
                            ? fullAnalysis
                            : ScopeFullUnitAnalysisToChangedFiles(fullAnalysis));
                    }
                }
            }
            else if (dirty.Contains(unit.Name))
            {
                var extractStopwatch = _watchCacheDiagnostics is null ? null : Stopwatch.StartNew();
                var (symbols, refs, nameHints) = SyntacticCsExtractor.Extract(unit, ToFullPath);
                if (extractStopwatch is not null)
                {
                    _watchCacheDiagnostics!($"[cs-cache] {unit.Name} extract (syntactic): scripts={unit.Scripts.Count} symbols={symbols.Count} elapsed_ms={extractStopwatch.ElapsedMilliseconds}");
                }

                var scriptFileIds = unit.Scripts.Select(s => s.FileId).ToList();
                analysesBag.Add(new CsAssemblyAnalysis(unit.Name, unit.AsmdefFileId, CsAnalysisMode.Syntactic, scriptFileIds, symbols, refs, nameHints, modeDecision.SyntacticReason));
            }
        }

        // Process one dependency level at a time, in level order -- this is the barrier
        // that guarantees every unit's dependencies finished (and populated compilationCache)
        // before any unit that needs them starts. Units WITHIN a level have no dependency
        // relationship to each other (by construction), so they run concurrently; a
        // single-unit level just runs inline (no Parallel.ForEach scheduling overhead for the
        // common narrow-level or degraded-to-sequential case).
        var levelsStopwatch = _watchCacheDiagnostics is null ? null : Stopwatch.StartNew();
        var levelParallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
        foreach (var level in levels)
        {
            if (level.Count == 1)
            {
                ProcessUnit(level[0]);
            }
            else
            {
                Parallel.ForEach(level, levelParallelOptions, ProcessUnit);
            }
        }

        levelsStopwatch?.Stop();

        // Deterministic output: cross-assembly ordering of `analyses`/`fingerprints`
        // has no correctness dependency of its own (each assembly's DB write is independently
        // keyed by name/file id -- see ReplaceAssemblyAnalyses' per-analysis-scoped
        // docIdToRowId), but sorting removes any doubt and keeps two runs over identical
        // unchanged source producing the exact same write order regardless of which worker
        // thread finished first.
        var analyses = analysesBag.OrderBy(a => a.AssemblyName, StringComparer.Ordinal).ToList();
        var fingerprints = fingerprintsBag.OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
        var syntacticNames = syntacticNamesBag.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();

        var dbWriteStopwatch = _watchCacheDiagnostics is null ? null : Stopwatch.StartNew();
        _store.ReplaceAssemblyAnalyses(analyses);
        _store.UpdateAssemblyModeFingerprints(fingerprints);
        dbWriteStopwatch?.Stop();

        var warnings = new List<string>(discoveryWarnings);
        warnings.AddRange(orderWarnings);
        if (syntacticNames.Count > 0)
        {
            // One line, not the full remediation walkthrough — this fires on EVERY sweep-running
            // invocation (it otherwise repeats in full even on asset-graph
            // queries where C# analysis wasn't in play). The per-assembly reasons and the
            // package-vs-project remediation detail live in `stats` and the query footer's
            // --verbose diagnoses, both of which this points at.
            warnings.Add(
                $"C# analysis: syntactic mode for {syntacticNames.Count} assembl{(syntacticNames.Count == 1 ? "y" : "ies")} (no usable generated .csproj) — " +
                "C# edges are degraded; per-assembly reasons + remediation: `unbramble stats`.");
        }

        if (csAnalysisStopwatch is not null)
        {
            var modeSelectionMs = TimeSpan.FromTicks(Interlocked.Read(ref modeSelectionTotalTicks)).TotalMilliseconds;
            var totalSymbols = analyses.Sum(a => a.Symbols.Count);
            var totalRefs = analyses.Sum(a => a.Refs.Count);
            _watchCacheDiagnostics!(
                $"[cs-analysis] RunCsAnalysis summary: units={units.Count} dirty={dirty.Count} semantic={units.Count - syntacticNames.Count} " +
                $"syntactic={syntacticNames.Count} mode-selection-total_ms={modeSelectionMs:F0} levels-total_ms={levelsStopwatch!.ElapsedMilliseconds} " +
                $"db-write_ms={dbWriteStopwatch!.ElapsedMilliseconds} analyses-written={analyses.Count} symbols-written={totalSymbols} refs-written={totalRefs} " +
                $"total_ms={csAnalysisStopwatch.ElapsedMilliseconds}");
        }

        return warnings;
    }

    /// <summary>Result of <see cref="ProcessSemanticUnitWithWatchCache"/>. <see cref="FullRebuild"/>
    /// and <see cref="HasDependencySwaps"/> let <c>ProcessUnit</c> decide whether PER-FILE
    /// extraction scoping is even eligible for this unit this
    /// pass -- see that call site's own comment for exactly why both must be false.</summary>
    private sealed record CsSemanticUnitBuildResult(
        CSharpCompilation Compilation,
        Dictionary<SyntaxTree, long> FileIdByTree,
        bool FullRebuild,
        bool HasDependencySwaps);

    /// <summary>
    /// Watch-compilation-cache path for one Semantic-mode unit (only ever called when <see
    /// cref="_watchCompilationCache"/> is non-null): cheaply DERIVES this pass's compilation from
    /// the previous one instead of rebuilding from scratch, whenever that's safe, falling back to
    /// a full <see cref="CsProjectAnalyzer.BuildCompilationForCache"/> rebuild -- "the existing
    /// expensive-but-correct thing" -- the moment there's any doubt:
    ///
    ///   1. Cache miss, or this unit's csproj mtime / structural shape hash no longer matches what
    ///      the cached compilation was built against (see <see cref="CsCompilationCache.ComputeUnitShapeHash"/>)
    ///      → full rebuild. Defines or the reference set may have changed; old parsed trees are
    ///      not safe to reuse.
    ///   2. Otherwise, this unit's OWN `.cs` files changed (<paramref name="unitSelfDirty"/>) →
    ///      <see cref="CsCompilationCache.ApplyIncrementalEdits"/> (ReplaceSyntaxTree/Add/Remove,
    ///      scoped to only the dirty files -- everything else in this unit's tree is untouched).
    ///   3. Either way, any dependency whose OWN compilation changed identity this pass (per
    ///      <paramref name="changedThisPass"/>, populated strictly level-by-level -- a dependency
    ///      is always in an already-finished, strictly lower level by construction of
    ///      GroupIntoDependencyLevels) gets its reference swapped via <see
    ///      cref="CsCompilationCache.ApplyDependencyReferenceSwaps"/>. This is what satisfies the
    ///      existing "recompile dependents" correctness rule under caching: a dependent that picks
    ///      up an already-updated dependency's compilation via ReplaceReference sees current
    ///      symbols by construction, the same as if it had been rebuilt from scratch against that
    ///      dependency.
    ///   4. Neither self-dirty nor any dependency changed → the cached compilation is returned
    ///      completely as-is; the ONLY work this call does is two dictionary reads. Extraction
    ///      itself is (and always was, cache or not) separately gated on `dirty.Contains(unit.Name)`
    ///      back in ProcessUnit -- caching adds nothing new there, it only removes the wasted
    ///      recompilation this unit doesn't need either.
    /// </summary>
    private CsSemanticUnitBuildResult ProcessSemanticUnitWithWatchCache(
        CsCompilationCache watchCache,
        AssemblyUnit unit,
        CsModeDecision modeDecision,
        ConcurrentDictionary<string, CSharpCompilation> compilationCache,
        bool unitSelfDirty,
        HashSet<string> dirtyPathSet,
        ConcurrentDictionary<string, bool> changedThisPass)
    {
        var buildStopwatch = _watchCacheDiagnostics is null ? null : Stopwatch.StartNew();
        var shapeHash = CsCompilationCache.ComputeUnitShapeHash(unit, modeDecision);
        var cached = watchCache.TryGetUnit(unit.Name);

        // Individually named (not just a bool) so the diagnostic below can say WHICH check
        // tripped -- "cache miss" (never built this process), a raw csproj-mtime move (which
        // fires even if Unity rewrote the file with byte-identical DefineConstants/Reference
        // content -- see CsprojMtimeReason's own note below), or an actual content-level
        // structural change (shapeHash, which DOES compare Defines/MetadataReferencePaths
        // content, not just the csproj's timestamp).
        string? fullRebuildReason = cached is null
            ? "cache-miss"
            : cached.CsprojMtimeTicks != modeDecision.CsprojMtimeTicks
                ? $"csproj-mtime-changed(old={FormatTicks(cached.CsprojMtimeTicks)},new={FormatTicks(modeDecision.CsprojMtimeTicks)})"
                : !string.Equals(cached.UnitShapeHash, shapeHash, StringComparison.Ordinal)
                    ? "shape-hash-changed"
                    : null;

        if (fullRebuildReason is not null)
        {
            var built = CsProjectAnalyzer.BuildCompilationForCache(unit, modeDecision, compilationCache, ToFullPath, watchCache);
            var freshUnit = new CachedUnit(built.Compilation, built.FileIdByTree, built.TreeByPath, built.DepRefs, modeDecision.CsprojMtimeTicks, shapeHash);
            watchCache.SetUnit(unit.Name, freshUnit);
            changedThisPass[unit.Name] = true;
            if (buildStopwatch is not null)
            {
                _watchCacheDiagnostics!($"[cs-cache] {unit.Name} build: FULL-REBUILD reason={fullRebuildReason} scripts={unit.Scripts.Count} elapsed_ms={buildStopwatch.ElapsedMilliseconds}");
            }

            return new CsSemanticUnitBuildResult(freshUnit.Compilation, freshUnit.FileIdByTree, FullRebuild: true, HasDependencySwaps: false);
        }

        var current = cached!;
        var compilation = current.Compilation;
        var fileIdByTree = current.FileIdByTree;
        var treeByPath = current.TreeByPath;
        var depRefs = current.DepRefs;
        var changed = false;

        if (unitSelfDirty)
        {
            var parseOptions = new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: modeDecision.Defines);
            var edited = CsCompilationCache.ApplyIncrementalEdits(current, unit, ToFullPath, parseOptions, dirtyPathSet);
            compilation = edited.Compilation;
            fileIdByTree = edited.FileIdByTree;
            treeByPath = edited.TreeByPath;
            changed = true;
        }

        var changedDeps = unit.References.Where(changedThisPass.ContainsKey).ToList();
        if (changedDeps.Count > 0)
        {
            var (swapped, newDepRefs) = CsCompilationCache.ApplyDependencyReferenceSwaps(compilation, depRefs, changedDeps, compilationCache);
            compilation = swapped;
            depRefs = newDepRefs;
            changed = true;
        }

        watchCache.SetUnit(unit.Name, new CachedUnit(compilation, fileIdByTree, treeByPath, depRefs, current.CsprojMtimeTicks, current.UnitShapeHash));
        if (changed)
        {
            changedThisPass[unit.Name] = true;
        }

        if (buildStopwatch is not null)
        {
            var decision = changed ? $"INCREMENTAL self-dirty={unitSelfDirty} dep-swaps=[{string.Join(",", changedDeps)}]" : "REUSED (no-op)";
            _watchCacheDiagnostics!($"[cs-cache] {unit.Name} build: {decision} elapsed_ms={buildStopwatch.ElapsedMilliseconds}");
        }

        return new CsSemanticUnitBuildResult(compilation, fileIdByTree, FullRebuild: false, HasDependencySwaps: changedDeps.Count > 0);
    }

    private static string FormatTicks(long? ticks) => ticks?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null";

    /// <summary>
    /// Attempts a per-file-scoped re-extraction of just <paramref name="unit"/>'s own dirty `.cs`
    /// files instead of every file in the unit -- the fix for a dirty unit's DB write cost
    /// (delete-then-reinsert of EVERY file's symbols/refs/name_hints, not just the edited one)
    /// dominating a watch push's wall-clock even when the compilation cache and mode-selection
    /// session cache have both already made everything upstream of it cheap.
    /// Returns null (caller must fall back to full-unit extraction) whenever this unit isn't
    /// eligible or the attempt itself proves scoping unsafe; a non-null result is always exactly
    /// as correct as a full-unit extraction would have been for this pass.
    ///
    /// Safety argument ("never wrong, only sometimes slower"): Roslyn's `GetDocumentationCommentId`
    /// already encodes a method's full parameter-type signature into its doc id string, so ANY
    /// edit that could change how some OTHER file in this unit resolves a reference into the
    /// changed file(s) -- adding/removing/renaming a member, changing a signature -- necessarily
    /// changes at least one declared (kind, doc_id) pair for the changed file(s). A base-type/
    /// interface swap doesn't necessarily change the file's OWN doc_id set but does change its
    /// own emitted inherit/override ref targets, which is why <see
    /// cref="UnBrambleStore.GetDeclarationShapes"/> folds both into one comparable token set. If
    /// the changed file(s)' BEFORE (as currently persisted) and AFTER (freshly re-extracted,
    /// scoped to just those trees) shapes are identical, no other file in the unit could possibly
    /// resolve anything differently, so replacing ONLY the changed files' rows (via <see
    /// cref="CsAssemblyAnalysis.ScriptFileIds"/> naturally scoping <see
    /// cref="UnBrambleStore.ReplaceAssemblyAnalyses"/>'s existing per-file-id delete-then-insert) is
    /// exactly as correct as a full-unit replace. If the shape moved, this method has already done
    /// the (cheap, single-file) extraction work for nothing beyond one extra store read -- the
    /// caller re-extracts the whole unit from scratch, same as if scoping had never been attempted.
    /// </summary>
    private CsAssemblyAnalysis? TryExtractScoped(
        AssemblyUnit unit,
        CSharpCompilation compilation,
        Dictionary<SyntaxTree, long> fileIdByTree,
        HashSet<string> dirtyPathSet,
        Stopwatch? extractStopwatch)
    {
        var changedScripts = unit.Scripts.Where(s => dirtyPathSet.Contains(s.Path)).ToList();
        if (changedScripts.Count == 0)
        {
            // Shouldn't happen when the caller's eligibility check passed (selfDirty implies at
            // least one of this unit's own paths is dirty), but never assume -- fall back.
            return null;
        }

        var changedFileIds = new HashSet<long>(changedScripts.Select(s => s.FileId));
        var scopedFileIdByTree = fileIdByTree
            .Where(kv => changedFileIds.Contains(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        if (scopedFileIdByTree.Count != changedFileIds.Count)
        {
            // Every changed path should have a tree in fileIdByTree by construction (it's either
            // freshly parsed by ApplyIncrementalEdits this same pass, or was already present) --
            // if a path is somehow missing (unreadable file, race), don't guess: fall back.
            return null;
        }

        var (afterSymbols, afterRefs, afterNameHints) = SemanticCsExtractor.Extract(compilation, scopedFileIdByTree);

        var beforeShapes = _store.GetDeclarationShapes(changedFileIds);
        foreach (var fileId in changedFileIds)
        {
            var afterTokens = BuildShapeTokens(fileId, afterSymbols, afterRefs);
            var beforeTokens = beforeShapes.TryGetValue(fileId, out var t) ? t : [];
            if (!afterTokens.SetEquals(beforeTokens))
            {
                if (extractStopwatch is not null)
                {
                    _watchCacheDiagnostics!($"[cs-cache] {unit.Name} extract (scoped): ESCALATED (declaration shape changed, file_id={fileId}) elapsed_ms={extractStopwatch.ElapsedMilliseconds}");
                }

                return null;
            }
        }

        if (extractStopwatch is not null)
        {
            _watchCacheDiagnostics!($"[cs-cache] {unit.Name} extract (scoped): files={changedFileIds.Count}/{unit.Scripts.Count} symbols={afterSymbols.Count} elapsed_ms={extractStopwatch.ElapsedMilliseconds}");
        }

        return new CsAssemblyAnalysis(unit.Name, unit.AsmdefFileId, CsAnalysisMode.Semantic, [.. changedFileIds], afterSymbols, afterRefs, afterNameHints);
    }

    /// <summary>In-memory equivalent of <see cref="UnBrambleStore.GetDeclarationShapes"/>'s token
    /// format, computed from a just-extracted (not yet persisted) result so the two are directly
    /// comparable.</summary>
    private static HashSet<string> BuildShapeTokens(long fileId, IReadOnlyList<CsSymbolRow> symbols, IReadOnlyList<CsSymbolRefRow> refs)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            if (symbol.FileId == fileId)
            {
                tokens.Add($"S:{symbol.Kind}:{symbol.DocId}");
            }
        }

        foreach (var r in refs)
        {
            if (r.SourceFileId == fileId && r.RefKind is "inherit" or "override")
            {
                tokens.Add($"R:{r.RefKind}:{r.TargetDocId}");
            }
        }

        return tokens;
    }

    /// <summary>
    /// Narrows a FULL-unit extraction's DB
    /// write to only the files whose persisted rows actually differ from the fresh result: a
    /// Unity-regenerated csproj mid-watch (FULL-REBUILD reason=csproj-mtime-changed) correctly
    /// rebuilds the compilation from scratch AND correctly falls back to full-unit extraction
    /// (TryExtractScoped's declaration-shape reasoning cannot cover an environment change --
    /// see below), but a full-unit delete-reinsert of every file's rows (~500s+ live on
    /// Assembly-CSharp) is unnecessary since a csproj regeneration almost never changes what
    /// most of the unit's files extract to.
    ///
    /// Safety argument ("never wrong, only sometimes slower"): unlike TryExtractScoped, which
    /// avoids re-extracting untouched files and therefore needs the declaration-shape PROOF that
    /// their rows couldn't have changed, this runs strictly AFTER every file in the unit has
    /// been freshly re-extracted against the current (possibly fully rebuilt) compilation --
    /// the fresh analysis is already exactly what a full write would persist. Skipping a file
    /// whose fresh fingerprint (exact row content, every column ReplaceAssemblyAnalyses writes
    /// -- see <see cref="CsFileRowFingerprint"/>) equals its persisted fingerprint therefore
    /// leaves the DB byte-identical to the full write, with the skipped files' row ids stable
    /// instead of churned. The declaration-shape proxy would NOT be safe here (a DefineConstants
    /// change can flip an `#if` inside a method body, changing call refs while the shape stays
    /// identical); exact content equality needs no such reasoning. Any anomaly (a row keyed to a
    /// file id outside ScriptFileIds) falls back to the full write rather than guessing.
    ///
    /// The exact-content baseline also catches a subtlety NO tree-level argument could: a
    /// touched `.cs` file's `name_hints` rows are deleted by ReplaceFileReferences earlier in
    /// this same pass (the file-level parse pipeline), and it is the cs full write that restores
    /// them — so a touched file with any hints (e.g. an `#if`-disabled region) fingerprint-
    /// differs against its half-deleted persisted state and gets correctly rewritten, where
    /// "its tree is byte-identical, skip it" would have silently dropped its hints.
    /// </summary>
    private CsAssemblyAnalysis ScopeFullUnitAnalysisToChangedFiles(CsAssemblyAnalysis full)
    {
        var scopeStopwatch = _watchCacheDiagnostics is null ? null : Stopwatch.StartNew();

        var freshFingerprints = CsFileRowFingerprint.ComputeForAnalysis(full);
        if (freshFingerprints is null)
        {
            return full;
        }

        var persistedFingerprints = _store.GetFileRowFingerprints(full.ScriptFileIds);
        var changedFileIds = new List<long>(full.ScriptFileIds.Count);
        foreach (var fileId in full.ScriptFileIds)
        {
            if (!persistedFingerprints.TryGetValue(fileId, out var before)
                || !string.Equals(before, freshFingerprints[fileId], StringComparison.Ordinal))
            {
                changedFileIds.Add(fileId);
            }
        }

        if (scopeStopwatch is not null)
        {
            _watchCacheDiagnostics!($"[cs-cache] {full.AssemblyName} db-scope (full-unit): changed={changedFileIds.Count}/{full.ScriptFileIds.Count} elapsed_ms={scopeStopwatch.ElapsedMilliseconds}");
        }

        if (changedFileIds.Count == full.ScriptFileIds.Count)
        {
            return full;
        }

        var changedSet = new HashSet<long>(changedFileIds);
        return new CsAssemblyAnalysis(
            full.AssemblyName,
            full.AsmdefFileId,
            full.Mode,
            changedFileIds,
            [.. full.Symbols.Where(s => changedSet.Contains(s.FileId))],
            [.. full.Refs.Where(r => changedSet.Contains(r.SourceFileId))],
            [.. full.NameHints.Where(h => changedSet.Contains(h.SourceFileId))],
            full.ModeReason);
    }

    /// <summary>
    /// Groups <paramref name="order"/> (already
    /// dependency-first from <see cref="AssemblyUnitDiscovery.TopologicalOrder"/>) into
    /// dependency LEVELS — every unit in level N has every in-project reference it has fully
    /// contained in levels 0..N-1, so every unit within one level is safe to build concurrently
    /// with every other unit in that same level, and processing levels strictly in order (0,
    /// 1, 2, ...) guarantees a unit's dependencies are always fully compiled before it starts.
    ///
    /// This does NOT trust <c>TopologicalOrder</c>'s ordering property blindly: level assignment
    /// walks <paramref name="order"/> once, and a unit's level is <c>1 + max(dependency levels)</c>
    /// only for references whose level has ALREADY been assigned by the time this unit is
    /// reached — if any in-project reference hasn't been assigned yet (which can only happen if
    /// <paramref name="order"/> doesn't actually have every dependency before its dependents,
    /// e.g. <c>TopologicalOrder</c>'s own documented cycle fallback, which returns the original
    /// undordered unit list plus a warning instead of a real topological order), grouping
    /// degrades to one unit per "level" in <paramref name="order"/>'s own sequence — i.e. fully
    /// sequential processing. "Never wrong,
    /// only sometimes slower": a degenerate/cyclic asmdef graph falls back to serial rather than
    /// risking a dependency being read before it's built.
    /// </summary>
    private static List<List<AssemblyUnit>> GroupIntoDependencyLevels(IReadOnlyList<AssemblyUnit> order)
    {
        var byName = order.ToDictionary(u => u.Name, StringComparer.Ordinal);
        var levelOf = new Dictionary<string, int>(StringComparer.Ordinal);
        var safeToParallelize = true;

        foreach (var unit in order)
        {
            var level = 0;
            foreach (var referenceName in unit.References)
            {
                if (!byName.ContainsKey(referenceName))
                {
                    continue; // Reference outside this project (e.g. a built-in Unity module) -- doesn't gate scheduling.
                }

                if (!levelOf.TryGetValue(referenceName, out var dependencyLevel))
                {
                    // order didn't actually place this dependency before its dependent --
                    // can't trust level grouping for anything in this pass.
                    safeToParallelize = false;
                    continue;
                }

                level = Math.Max(level, dependencyLevel + 1);
            }

            levelOf[unit.Name] = level;
        }

        if (!safeToParallelize)
        {
            return [.. order.Select(u => new List<AssemblyUnit> { u })];
        }

        return [.. order
            .GroupBy(u => levelOf[u.Name])
            .OrderBy(g => g.Key)
            .Select(g => g.ToList())];
    }

    /// <summary>
    /// Checks whether every persisted assembly's
    /// recorded mode fingerprint still matches disk, as a plain
    /// <see cref="File.Exists(string)"/>/<see cref="File.GetLastWriteTimeUtc(string)"/> stat per
    /// assembly — never a parse. An empty fingerprint set (nothing recorded yet — a fresh store,
    /// or a project with no C# assemblies ever discovered) always returns false: <see
    /// cref="RunCsAnalysis"/> must not skip on an unknown assembly set.
    /// </summary>
    private bool CsAnalysisFingerprintsMatch()
    {
        var fingerprints = _store.GetAssemblyModeFingerprints();
        if (fingerprints.Count == 0)
        {
            return false;
        }

        foreach (var fp in fingerprints)
        {
            var csprojPath = Path.Combine(ProjectRoot, fp.Name + ".csproj");
            long? currentMtimeTicks = File.Exists(csprojPath) ? File.GetLastWriteTimeUtc(csprojPath).Ticks : null;
            if (currentMtimeTicks != fp.CsprojMtimeTicks)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Targeted incremental update for a known set of owner project-relative paths (the
    /// watcher's push path): each path is stat'd
    /// directly via <see cref="Scanner.ScanSingleFile"/> — no full <see cref="Scanner.Scan"/> —
    /// and diffed against its existing row. New/changed files are reparsed, missing ones
    /// cascade-delete. Never touches the `roots` table (unlike <see cref="RunIndex"/>): a
    /// targeted batch can't discover new junctions, which is exactly why the periodic self-heal
    /// full sweep exists on top of this.
    /// </summary>
    public SweepDiff ApplyTargetedUpdate(IReadOnlyCollection<string> ownerProjectPaths)
    {
        var totalStopwatch = Stopwatch.StartNew();
        if (ownerProjectPaths.Count == 0)
        {
            return new SweepDiff(0, 0, 0, [], []);
        }

        using var writerLease = IndexWriterLock.Acquire(ProjectRoot, message => _watchCacheDiagnostics?.Invoke(message));

        var scanStopwatch = Stopwatch.StartNew();
        var scanner = new Scanner();
        var scanWarnings = new List<string>();
        var entries = new List<ScannedFileEntry>();
        foreach (var path in ownerProjectPaths)
        {
            var entry = scanner.ScanSingleFile(ProjectRoot, path, Config, scanWarnings);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        scanStopwatch.Stop();

        var diffStopwatch = Stopwatch.StartNew();
        var diff = _store.ApplyTargetedDiff(ownerProjectPaths, entries);
        diffStopwatch.Stop();

        var reparseStopwatch = Stopwatch.StartNew();
        var parseWarnings = ReparseDirtyFiles(diff.DirtyPaths);
        reparseStopwatch.Stop();

        // Same RunCsAnalysis call, same gate, as RunIndex's pull path -- see that method's doc
        // comment for why the two must never diverge.
        var csStopwatch = Stopwatch.StartNew();
        var csWarnings = RunCsAnalysis(diff);
        csStopwatch.Stop();

        var warnings = new List<string>(scanWarnings.Count + diff.Warnings.Count + parseWarnings.Count + csWarnings.Count);
        warnings.AddRange(scanWarnings);
        warnings.AddRange(diff.Warnings);
        warnings.AddRange(parseWarnings);
        warnings.AddRange(csWarnings);

        // Unlike RunIndex, this push-path method previously had NO phase timing at all -- added
        // here (not just gated behind the diagnostics null-check for the stopwatches themselves,
        // since Stopwatch.StartNew() is cheap enough to not bother skipping) so a real-time
        // watcher batch can be measured on its own terms, separate from a self-heal RunIndex
        // sweep picking up the same change later.
        _watchCacheDiagnostics?.Invoke(
            $"[cs-analysis] ApplyTargetedUpdate(paths={ownerProjectPaths.Count}): scan_ms={scanStopwatch.ElapsedMilliseconds} diff_ms={diffStopwatch.ElapsedMilliseconds} " +
            $"reparse_ms={reparseStopwatch.ElapsedMilliseconds} cs_ms={csStopwatch.ElapsedMilliseconds} total_ms={totalStopwatch.ElapsedMilliseconds} " +
            $"added={diff.Added} changed={diff.Changed} removed={diff.Removed} dirtyPaths={diff.DirtyPaths.Count}");

        return diff with { Warnings = warnings };
    }

    /// <summary>
    /// The pull path: every query verb (who-uses,
    /// uses, resolve, stats) calls this before answering. Sweeps unless a fresh watcher
    /// heartbeat shows a live watcher already owns freshness — `init`/`index` never call this;
    /// they always sweep unconditionally, no flag exists to skip a sweep without a live
    /// heartbeat.
    ///
    /// A stale/missing heartbeat does NOT mean nobody is sweeping — a just-promoted watcher
    /// (<see cref="Freshness.WatcherHost.Promote"/>) runs its whole catch-up sweep, which on a
    /// large project can take minutes, BEFORE writing its first heartbeat (load-bearing order,
    /// see that method's own doc comment: nothing may trust a heartbeat written before the sweep
    /// it vouches for is real). Racing a second, independent <see cref="RunIndex"/> against that
    /// in-progress sweep from a different OS process was a real bug found against a ~115k-file
    /// project: both sides wrap their whole sweep in one SQLite transaction (see
    /// <c>UnBrambleStore</c>'s sweep-apply method), so two of them overlapping is exactly the
    /// writer-vs-writer contention <c>PRAGMA busy_timeout</c> exists to smooth over, except a
    /// multi-minute sweep can outlast any reasonable timeout, surfacing as a bare "database is
    /// locked" error to whichever CLI invocation loses the race. <see cref="Freshness.WatcherLock"/>
    /// identifies a watcher that may be inside its pre-heartbeat promotion sweep; the separate
    /// <see cref="IndexWriterLock"/> serializes every finite mutation, including ordinary query
    /// sweeps and explicit indexes. Together they close both watcher-vs-query and query-vs-query
    /// races without making an explicit writer wait for a watcher process's whole lifetime.
    /// </summary>
    /// <param name="onScanProgress">Optional live-progress sink for the inline sweep, forwarded
    /// to <see cref="RunIndex"/> -- see that method's own param doc. Never invoked on the fresh-
    /// heartbeat fast path (no sweep runs at all).</param>
    /// <param name="onPhase">Optional phase-boundary sink for the inline sweep, forwarded to
    /// <see cref="RunIndex"/>, and also used to announce entering the concurrent-sweep wait below.
    /// Never invoked on the fresh-heartbeat fast path.</param>
    /// <param name="waitForConcurrentSweep">
    /// True (every caller except `stats`) blocks until the other writer's heartbeat goes fresh (or
    /// takes over itself if that writer disappears without ever leaving one, e.g. a crash) --
    /// same "always answer from a fresh index" guarantee EnsureFresh has always made, just without
    /// the duplicate-sweep race. False (`stats` only, by request: a status command should report
    /// current state immediately, not stall behind someone else's multi-minute first index) returns
    /// <see cref="FreshnessOutcome.SkippedConcurrentSweep"/> the instant contention is detected.
    /// </param>
    public FreshnessOutcome EnsureFresh(Action<ScanProgress>? onScanProgress = null, Action<string>? onPhase = null, bool waitForConcurrentSweep = true)
    {
        var heartbeat = HeartbeatFile.TryRead(ProjectRoot);
        var heartbeatIsFresh = heartbeat is { } hb && HeartbeatFreshness.IsFresh(hb.UtcTimestamp, DateTime.UtcNow, HeartbeatFreshness.DefaultStaleThreshold);

        // A fresh heartbeat is only TRUSTED when its writer stamped the schema version this
        // binary's store shape expects, and this very open didn't just reset the store. Found by
        // reasoning through the first real schema-bump upgrade, not live (yet): a still-running
        // watcher from the previous binary keeps heartbeating right through the upgrade, while
        // this binary's open just dropped and recreated every table -- trusting that heartbeat
        // would answer from an empty store, silently. The one thing freshness must never be.
        if (heartbeatIsFresh && heartbeat!.Value.Schema == UnBrambleStore.CurrentSchemaVersion && !SchemaWasReset)
        {
            // Auto-spawn telemetry only (docs/architecture.md, "Auto-spawn watcher") -- this
            // marker plays no role in the freshness decision above (already made) or in any
            // future call's decision (which only ever reads the heartbeat file). It exists
            // purely so a live `--auto` watcher can tell whether it's still earning its keep
            // (AutoIdleGate) -- best-effort, and never allowed to affect this or any other
            // query's correctness.
            AutoWatchMarkers.TouchLastQuery(ProjectRoot, DateTime.UtcNow);
            return FreshnessOutcome.SkippedFreshHeartbeat(DateTime.UtcNow - heartbeat.Value.UtcTimestamp);
        }

        var watcherProbe = Freshness.WatcherLock.TryAcquire(ProjectRoot);
        if (watcherProbe is not null)
        {
            // No watcher is currently active -- release immediately (this call isn't becoming a
            // watcher), then claim the finite writer lease. Another one-shot query/index may
            // already own that second lock even though the watcher lock was free.
            watcherProbe.Dispose();
            var writerLease = IndexWriterLock.TryAcquire(ProjectRoot);
            if (writerLease is not null)
            {
                using (writerLease)
                {
                    return FreshnessOutcome.Swept(RunIndexOwned(full: false, onScanProgress, onPhase));
                }
            }
        }

        if (heartbeatIsFresh)
        {
            // The lock holder is alive but its heartbeat failed the schema check above: an
            // older-binary watcher. Waiting on it can never end (it will never write a matching
            // heartbeat, and a manual `watch` never exits on its own) -- so sweep alongside it.
            // Safe: the watcher lock only prevents DUPLICATE work, not unsafe concurrency
            // (SQLite WAL + busy_timeout serialize the writes). Its old-shaped writes can only
            // degrade optional metadata until it's retired, never edges.
            onPhase?.Invoke("freshness: a watcher from an older unbramble version is still running -- its heartbeat is ignored; sweeping now (run 'unbramble stop' to retire it)");
            return FreshnessOutcome.Swept(RunIndex(full: false, onScanProgress, onPhase));
        }

        if (!waitForConcurrentSweep)
        {
            return FreshnessOutcome.SkippedConcurrentSweep();
        }

        onPhase?.Invoke("freshness: another process already owns the index (likely a watcher's first sweep) -- waiting for it instead of racing a duplicate sweep");
        return WaitForConcurrentSweep(onScanProgress, onPhase);
    }

    /// <summary>
    /// Polls for the other writer's heartbeat to go fresh, re-probing <see
    /// cref="Freshness.WatcherLock"/> on every tick so a writer that disappears mid-sweep without
    /// ever leaving a fresh heartbeat (crash, killed process) doesn't strand this call waiting on a
    /// heartbeat that will never come -- the moment the lock is free, this takes over the sweep
    /// itself instead. Unbounded: the other side's sweep can legitimately take minutes on a large
    /// project (<see cref="EnsureFresh"/>'s own doc comment), and "never wrong, only sometimes
    /// slower" already tolerates that same latency when this process is the one doing the sweeping.
    /// </summary>
    private FreshnessOutcome WaitForConcurrentSweep(Action<ScanProgress>? onScanProgress, Action<string>? onPhase)
    {
        while (true)
        {
            Thread.Sleep(ConcurrentSweepPollInterval);

            var heartbeat = HeartbeatFile.TryRead(ProjectRoot);
            if (heartbeat is { } h && HeartbeatFreshness.IsFresh(h.UtcTimestamp, DateTime.UtcNow, HeartbeatFreshness.DefaultStaleThreshold))
            {
                // Same schema-stamp trust rule as EnsureFresh: a fresh heartbeat from an
                // older-binary writer will NEVER become trustworthy, so stop waiting on it and
                // sweep alongside it (see EnsureFresh's own old-watcher branch for why that's
                // safe).
                if (h.Schema != UnBrambleStore.CurrentSchemaVersion)
                {
                    onPhase?.Invoke("freshness: the concurrent writer is an older unbramble version -- its heartbeat is ignored; sweeping now (run 'unbramble stop' to retire it)");
                    return FreshnessOutcome.Swept(RunIndex(full: false, onScanProgress, onPhase));
                }

                AutoWatchMarkers.TouchLastQuery(ProjectRoot, DateTime.UtcNow);
                return FreshnessOutcome.SkippedFreshHeartbeat(DateTime.UtcNow - h.UtcTimestamp);
            }

            var watcherProbe = Freshness.WatcherLock.TryAcquire(ProjectRoot);
            if (watcherProbe is not null)
            {
                watcherProbe.Dispose();
                var writerLease = IndexWriterLock.TryAcquire(ProjectRoot);
                if (writerLease is not null)
                {
                    using (writerLease)
                    {
                        return FreshnessOutcome.Swept(RunIndexOwned(full: false, onScanProgress, onPhase));
                    }
                }
            }
        }
    }

    private static readonly TimeSpan ConcurrentSweepPollInterval = TimeSpan.FromMilliseconds(250);

    private string ToFullPath(string projectRelativePath) =>
        Path.Combine(ProjectRoot, projectRelativePath.Replace('/', Path.DirectorySeparatorChar));

    public StatsResult GetStats() => _store.GetStats();

    /// <summary>Every current guid collision (one group per guid claimed by multiple files) —
    /// `stats --collisions`' data source; see the store method's own doc comment.</summary>
    public IReadOnlyList<UnBrambleStore.GuidCollisionGroup> GetGuidCollisionGroups() => _store.GetGuidCollisionGroups();

    /// <summary>Every syntactic-mode assembly, named with its <see cref="CsModeReasons"/> reason —
    /// the `stats` verb's full enumeration, unlike <see cref="SyntacticAssemblySummary"/>'s capped
    /// per-query sample.</summary>
    public IReadOnlyList<SyntacticAssemblyDetail> GetSyntacticAssemblyDetails() =>
        [.. _store.GetSyntacticAssemblyDetails().Select(EnrichSyntacticDetail)];

    /// <summary>
    /// Adds the two package-only diagnostics (see <see cref="SyntacticAssemblyDetail"/>'s own doc
    /// comment) on top of the store's plain name/reason/package-sourced row -- both require extra
    /// filesystem/DB work, so they're computed here (the engine layer, which owns both
    /// <see cref="ProjectRoot"/> and <see cref="_store"/>) rather than unconditionally in the store.
    /// </summary>
    private SyntacticAssemblyDetail EnrichSyntacticDetail(UnBrambleStore.SyntacticAssemblyDetail d)
    {
        if (!d.IsPackageSourced)
        {
            return new SyntacticAssemblyDetail(d.Name, d.Reason, d.IsPackageSourced);
        }

        var neverCompiled = !File.Exists(Path.Combine(ProjectRoot, "Library", "ScriptAssemblies", d.Name + ".dll"));
        int? externalReferencers = neverCompiled ? _store.CountExternalReferencers(d.Name) : null;
        return new SyntacticAssemblyDetail(d.Name, d.Reason, d.IsPackageSourced, neverCompiled, externalReferencers);
    }

    public IReadOnlyList<ResolveMatch> Resolve(string query) => _store.Resolve(NormalizeQueryInput(query));

    /// <summary>
    /// True when <paramref name="query"/> is a well-formed 32-hex guid. Lets `resolve` tell
    /// "a real guid this project's index doesn't contain" (a definite ANSWER — a deleted asset,
    /// or one belonging to a package that isn't installed) apart from "that string matched
    /// nothing" (a failed lookup), instead of collapsing both into the not-found error path.
    /// This is the same distinction <see cref="UnBrambleStore.ResolveQueryTarget"/> already draws
    /// for who-uses/uses, which answers an unmatched bare guid gracefully; the two verbs
    /// must agree, since an agent probing an unknown guid's
    /// identity naturally reaches for `resolve` FIRST and hit the error path.
    /// </summary>
    public static bool IsBareGuid(string query) => RegexPatterns.BareGuid().IsMatch(query);

    public IReadOnlyList<FileRecord> GetAllFiles() => _store.GetAllFiles();

    public IReadOnlyList<RootMapping> GetRoots() => _store.GetRoots();

    /// <summary>Resolves a who-uses/uses target argument. See <see cref="UnBrambleStore.ResolveQueryTarget"/>.
    /// Accepts an ABSOLUTE path inside the project too (real-project validation passed
    /// `D:/…/&lt;project&gt;/Assets/Editor/X.cs` and got "no match" against the store's
    /// project-relative paths): a fully-qualified input under <see cref="ProjectRoot"/> is
    /// rewritten to the store's project-relative forward-slash form before resolution; anything
    /// else passes through unchanged.</summary>
    public TargetResolution ResolveQueryTarget(string input) => _store.ResolveQueryTarget(MakeProjectRelativeIfInsideRoot(NormalizeQueryInput(input)));

    /// <summary>CLI paths are documented and stored with forward slashes, but Windows callers
    /// naturally paste backslash paths. Normalize both relative and absolute query inputs at the
    /// engine boundary so every query surface gets the same behavior.</summary>
    private static string NormalizeQueryInput(string input) => input.Replace('\\', '/');

    private string MakeProjectRelativeIfInsideRoot(string input)
    {
        if (!Path.IsPathFullyQualified(input.Replace('/', Path.DirectorySeparatorChar)))
        {
            return input;
        }

        string full;
        try
        {
            full = Path.GetFullPath(input.Replace('/', Path.DirectorySeparatorChar));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return input;
        }

        var root = Path.TrimEndingDirectorySeparator(ProjectRoot);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return input;
        }

        return full[(root.Length + 1)..].Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>Resolves a `cs-refs` symbol-name/doc-id argument. See <see cref="UnBrambleStore.ResolveCsSymbol"/>.</summary>
    public CsSymbolResolution ResolveCsSymbol(string query) => _store.ResolveCsSymbol(query);

    /// <summary>Symbol-level reverse lookup for a resolved doc_id (used by `cs-refs`).</summary>
    public IReadOnlyList<CsRefEntry> GetCsRefs(string docId) => _store.GetCsRefsByDocId(docId);

    /// <summary>
    /// Every UnityEvent binding that targets <paramref name="docId"/>'s member: the guid-carrying
    /// cascade's matched links plus the guid-less same-asset ("unityevent-local") annotations.
    /// ONE accessor, shared by <see cref="WhoUsesSymbol"/> and <see cref="GetCsRefsAnswer"/>, so
    /// the two symbol-level surfaces can never drift in which serialized bindings they count as
    /// referencers -- they DID drift, and it was the worst-shaped kind of drift: `cs-refs` read
    /// `symbol_refs` alone and so reported a flat "0 referencers" for a method whose only caller
    /// was a Button.onClick binding, which is exactly the answer an agent reads as "safe to
    /// delete". Cross-checking with a grep for `m_MethodName` exposes the missing edge.
    /// </summary>
    private List<EdgeResult> SymbolEventReferencers(string docId) =>
        [.. _store.GetEventLinksTargetingDocId(docId), .. _store.GetLocalEventBindingsTargetingDocId(docId)];

    /// <summary>
    /// The full `cs-refs` answer for a resolved doc_id: its `symbol_refs` call sites, its
    /// UnityEvent-bound referencers (see <see cref="SymbolEventReferencers"/>), and the same
    /// blind-spot/syntactic-assembly caveat material every who-uses/uses answer carries. `cs-refs`
    /// had NO caveat footer at all before this, which made its "0 referencers" the only
    /// unqualified zero the tool could print.
    ///
    /// Deliberately NOT the same thing as <see cref="WhoUsesSymbol"/>: no declaring-file asset
    /// context and no speculative name-match fallback, because `cs-refs` answers the narrower
    /// "which call sites reference this symbol" question. The CLI points at `who-uses` for the
    /// wider one rather than quietly widening this verb.
    /// </summary>
    public CsRefsAnswer GetCsRefsAnswer(string docId)
    {
        var refs = _store.GetCsRefsByDocId(docId);
        var eventRefs = SymbolEventReferencers(docId);
        var anySyntactic = _store.GetCsStats().SyntacticAssemblies > 0;
        var blindSpots = BlindSpots.ForQuery(
            anySyntactic, truncated: false, IsAnyCsprojStale(), _store.HasDisabledRegionNameHint(SimpleNameFromDocId(docId)));
        var summary = anySyntactic ? BuildSyntacticSummary() : null;
        return new CsRefsAnswer(refs, eventRefs, blindSpots, summary);
    }

    public IReadOnlyList<UnresolvedRefEntry> GetUnresolvedRefs(long? sourceFileId = null) => _store.GetUnresolvedRefs(sourceFileId);

    /// <summary>Reverse dependency query (blast radius). Direct, or transitive up to depthCap.
    /// Direct queries on a `.cs` target merge in C#-kind edges: files whose `symbol_refs`
    /// target a symbol declared in this file, alongside the existing guid/path asset-graph
    /// edges. Transitive queries cross the seam too — the reverse closure walks
    /// `unified_walk_edges`, replacing the earlier direct-only merge. Every result carries a
    /// derived confidence label and the answer carries its weakest-link confidence plus blind
    /// spots.</summary>
    public QueryAnswer WhoUses(QueryTarget target, bool transitive, int depthCap)
    {
        if (!transitive)
        {
            var direct = DirectReferencers(target);
            if (target.FileId is { } directFileId && target.Path is not null &&
                target.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                var csEdges = _store.GetCsReferencersOfFile(directFileId);
                if (csEdges.Count > 0)
                {
                    direct = [.. direct, .. csEdges];
                }

                // Matched
                // UnityEvent links surface in "who-uses <file>.cs output" too, not just symbol-
                // argument queries — every method symbol declared in this file that some event
                // binding matched (proven or advisory), reusing GetCsReferencersOfFile's exact
                // depth-1 merge idiom.
                var eventEdges = _store.GetEventLinksTargetingFile(directFileId);
                if (eventEdges.Count > 0)
                {
                    direct = [.. direct, .. eventEdges];
                }
            }

            direct = [.. direct, .. DllReferencers(target)];

            return Finalize(target, direct, transitive: false, truncated: false, transitiveUnavailable: false, forward: false);
        }

        if (target.FileId is not { } fileId)
        {
            // External/unresolved guid target: still answer with direct guid hits, but no
            // transitive walk is possible without a file id to walk from.
            return Finalize(target, DirectReferencers(target), transitive: false, truncated: false, transitiveUnavailable: true, forward: false);
        }

        var closure = _store.GetReverseClosure(fileId, depthCap);
        var results = _store.GetTransitiveWhoUsesEdges(fileId, closure);
        var truncated = _store.WalkHitDepthCap(forward: false, fileId, depthCap);

        if (target.Path is not null && target.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            // Same merge as the direct branch above, generalized to a transitive answer: an
            // event-matched method's binding is always a depth-1 fact layered atop whatever else
            // the transitive walk separately discovered. When the same
            // source file is ALSO reached deeper in the closure via a fully-proven chain,
            // Finalize's chain-weakest aggregation correctly takes the worst of
            // both alternates for that node — this is exactly how an advisory event hop taints
            // an otherwise-proven transitive answer.
            var transitiveEventEdges = _store.GetEventLinksTargetingFile(fileId);
            if (transitiveEventEdges.Count > 0)
            {
                results = [.. results, .. transitiveEventEdges];
            }
        }

        results = [.. results, .. DllReferencers(target)];

        return Finalize(target, results, transitive: true, truncated, transitiveUnavailable: false, forward: false);
    }

    /// <summary>
    /// The `.asmdef`s that name this target in `precompiledReferences` — a depth-1 fact merged
    /// into who-uses answers exactly like the UnityEvent-link merge above, because these edges
    /// deliberately live outside `all_refs` (see the `dll_refs` DDL comment). Gated on `.dll`
    /// because a precompiledReferences entry can name nothing else, which keeps the lookup off
    /// every other query. Unresolved guid targets have no path to take a file name from and
    /// correctly return nothing.
    /// </summary>
    private IReadOnlyList<EdgeResult> DllReferencers(QueryTarget target) =>
        target.Path is not null && target.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? _store.GetDllReferencersOfFile(target.Path)
            : [];

    /// <summary>The forward counterpart of <see cref="DllReferencers"/>: the plugin assemblies an
    /// `.asmdef` names in `precompiledReferences`. Gated on `.asmdef` for the same reason — no
    /// other file kind can hold one.</summary>
    private IReadOnlyList<EdgeResult> DllDependencies(QueryTarget target, long fileId) =>
        target.Path is not null && target.Path.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase)
            ? _store.GetDllDependenciesOfFile(fileId)
            : [];

    /// <summary>Forward dependency query. Direct, or transitive up to depthCap (unresolved
    /// edges surfaced at every depth). Direct queries on a `.cs` source merge in C#-kind
    /// outgoing edges (the symmetric counterpart of `who-uses`' merge). Transitive
    /// queries cross the seam via `unified_walk_edges`, same as who-uses.</summary>
    public QueryAnswer Uses(QueryTarget target, bool transitive, int depthCap)
    {
        if (target.FileId is not { } fileId)
        {
            // uses enumerates outgoing refs FROM a real file; an external/unresolved guid
            // target has no outgoing edges of its own to show.
            return Finalize(target, [], transitive: false, truncated: false, transitiveUnavailable: true, forward: true);
        }

        if (!transitive)
        {
            var direct = _store.GetDirectDependencies(fileId);
            if (target.Path is not null && target.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                var csEdges = _store.GetCsDependenciesOfFile(fileId);
                if (csEdges.Count > 0)
                {
                    direct = [.. direct, .. csEdges];
                }
            }

            direct = [.. direct, .. DllDependencies(target, fileId)];

            return Finalize(target, direct, transitive: false, truncated: false, transitiveUnavailable: false, forward: true);
        }

        var closure = _store.GetForwardClosure(fileId, depthCap);
        var results = _store.GetTransitiveUsesEdges(fileId, closure);
        var truncated = _store.WalkHitDepthCap(forward: true, fileId, depthCap);
        results = [.. results, .. DllDependencies(target, fileId)];
        return Finalize(target, results, transitive: true, truncated, transitiveUnavailable: false, forward: true);
    }

    /// <summary>
    /// Unified symbol-argument who-uses: `docId`'s symbol-level referencers (depth 0, today's
    /// `cs-refs` data) plus the asset-level
    /// referencers of its declaring file F, surfaced automatically as file-level context. The
    /// depth-1+ portion is exactly a normal file-level `WhoUses(F, ...)` — reusing it is what
    /// keeps the seam a query-time join rather than a parallel code path — except its direct
    /// (depth-1) guid/path edges are relabeled per the basename rule: S is a type whose name
    /// equals F's basename (Unity's attachment convention) -> proven; anything else (a
    /// mismatched type, or any member) -> advisory, because attaching F to an asset doesn't
    /// prove the attachment reaches a member `S` specifically. Nothing is ever omitted — an
    /// agent asking "who uses Foo.Jump" still sees Foo.cs's prefab attachments, just labeled
    /// advisory rather than silently dropped.
    /// </summary>
    public QueryAnswer WhoUsesSymbol(string docId, bool transitive, int depthCap)
    {
        var symbolEdges = _store.GetCsRefsByDocId(docId).Select(r => ToSymbolEdgeResult(docId, r)).ToList();

        // Matched UnityEvent links (both the
        // guid-carrying cascade and the guid-less same-asset "unityevent-local" annotation) are
        // depth-0 symbol-level referencers of the method they bind, exactly like a symbol_refs
        // row -- surfaced here so "who-uses Foo.Jump" shows its Button.onClick binding, not just
        // its C# callers.
        symbolEdges.AddRange(SymbolEventReferencers(docId));

        var info = _store.GetSymbolInfo(docId);
        if (info is null)
        {
            // Should not happen for a docId that just came out of ResolveCsSymbol, but never
            // crash on a stale/edge-case lookup -- degrade to the symbol-level section alone.
            var anySyntacticNoFile = _store.GetCsStats().SyntacticAssemblies > 0;
            var speculativeNoFile = anySyntacticNoFile
                ? _store.GetSyntacticNameMatchRefs(docId, SimpleNameFromDocId(docId), docId.StartsWith("T:", StringComparison.Ordinal), excludeSourceFileId: null)
                : [];
            var combinedNoFile = MergeSpeculative(symbolEdges, speculativeNoFile);
            var summaryNoFile = anySyntacticNoFile ? BuildSyntacticSummary() : null;
            return new QueryAnswer(
                new QueryTarget(null, null, null), combinedNoFile, Truncated: false, TransitiveUnavailable: true,
                EdgeConfidence.AnswerLevel(combinedNoFile),
                BlindSpots.ForQuery(
                    anySyntacticNoFile, truncated: false, IsAnyCsprojStale(),
                    _store.HasDisabledRegionNameHint(SimpleNameFromDocId(docId))),
                summaryNoFile,
                PossibleFalseNegative: HasNoNonSpeculativeResult(combinedNoFile) && summaryNoFile is not null);
        }

        var fileTarget = new QueryTarget(info.FileId, info.FilePath, info.FileGuid);
        var fileAnswer = WhoUses(fileTarget, transitive, depthCap);

        var isMainType = info.Kind == "type" &&
            string.Equals(info.Name, Path.GetFileNameWithoutExtension(info.FilePath), StringComparison.OrdinalIgnoreCase);
        var contextLabel = isMainType ? EdgeConfidence.Proven : EdgeConfidence.Advisory;

        // Depth-1 cs-kind edges from WhoUses(F) are direct referencers of F via SOME symbol
        // declared in F, not necessarily S — when it IS S, it's already covered by symbolEdges
        // (depth 0) and would otherwise show up twice; when it's some other member, it isn't
        // "asset-level context" at all. Drop depth-1 cs
        // edges from the file-context section entirely; depth-1 guid/path edges are the actual
        // "asset-level referencers of F" and get the basename relabel. Depth 2+ (only reached
        // with --transitive) keeps every kind unchanged — blast radius continues across both
        // graphs, and there is no depth-0 overlap that deep.
        //
        // Depth-1 event-kind edges from WhoUses(F) are the exact same story —
        // WhoUses(F)'s own merge (the "who-uses <file>.cs" surfacing) returns every
        // matched event binding for ANY method declared in F, not necessarily S specifically.
        // When it IS S, GetEventLinksTargetingDocId already put it in symbolEdges above (depth
        // 0); dropping it here avoids the same double-count the cs case already guards against.
        var fileContext = fileAnswer.Results
            .Where(r => r.Depth != 1 || r.Kind is not ("cs" or "event"))
            .Select(r => r.Depth == 1 && r.Kind is "guid" or "path" ? r with { ConfidenceLabel = contextLabel } : r);

        var combined = symbolEdges.Concat(fileContext).ToList();
        var anySyntactic = _store.GetCsStats().SyntacticAssemblies > 0;

        // Speculative name-match fallback: a syntactic assembly's text-derived symbol_refs can
        // never join this docId directly (see GetSyntacticNameMatchRefs' doc comment), so a
        // proven/advisory-only answer can silently omit real call sites. Runs whenever any
        // syntactic assembly exists; purely additive after MergeSpeculative's dedup against what
        // the exact join already found.
        var isTypeQuery = info.Kind == "type";
        var speculative = anySyntactic
            ? _store.GetSyntacticNameMatchRefs(docId, info.Name, isTypeQuery, info.FileId)
            : [];
        combined = MergeSpeculative(combined, speculative);

        var answerConfidence = EdgeConfidence.AnswerLevel(combined);
        // The precise case (see the class doc comment on BlindSpots):
        // a symbol query has exactly one resolved symbol name to check, so this is the tight
        // check, not the file-declared-symbols fallback Finalize uses for plain file queries.
        var blindSpots = BlindSpots.ForQuery(
            anySyntactic, fileAnswer.Truncated, IsAnyCsprojStale(), _store.HasDisabledRegionNameHint(info.Name));
        var summary = anySyntactic ? BuildSyntacticSummary() : null;
        var possibleFalseNegative = HasNoNonSpeculativeResult(combined) && summary is not null;

        return new QueryAnswer(fileTarget, combined, fileAnswer.Truncated, fileAnswer.TransitiveUnavailable, answerConfidence, blindSpots, summary, possibleFalseNegative);
    }

    /// <summary>True when every labeled edge (if any) is speculative — i.e. the answer has no real
    /// proven/advisory result to fall back on, only name-match leads (or nothing at all). Backs
    /// <see cref="QueryAnswer.PossibleFalseNegative"/>: a query that found a genuine caller isn't
    /// the "silently missing" case even if speculative leads also happened to turn up.</summary>
    private static bool HasNoNonSpeculativeResult(IReadOnlyList<EdgeResult> results) =>
        !results.Any(r => r.ConfidenceLabel is not null && r.ConfidenceLabel != EdgeConfidence.Speculative);

    /// <summary>Merges speculative name-match rows into an already-computed edge list, skipping
    /// any match whose (SourcePath, Line) is already present — a real, already-resolved edge at
    /// that exact call site makes the text-match redundant, never a second lead.</summary>
    private static List<EdgeResult> MergeSpeculative(IReadOnlyList<EdgeResult> existing, IReadOnlyList<CsNameMatchEntry> matches)
    {
        var result = new List<EdgeResult>(existing);
        var seen = new HashSet<(string SourcePath, int Line)>(existing.Select(r => (r.SourcePath, r.Line)));
        foreach (var m in matches)
        {
            if (!seen.Add((m.SourcePath, m.Line)))
            {
                continue;
            }

            result.Add(ToSpeculativeEdgeResult(m));
        }

        return result;
    }

    private static EdgeResult ToSpeculativeEdgeResult(CsNameMatchEntry m)
    {
        var targetSymbol = m.TargetDocId.Length > 2 && m.TargetDocId[1] == ':' ? m.TargetDocId[2..] : m.TargetDocId;
        var edge = new EdgeResult(
            m.SourcePath, TargetPath: null, m.TargetDocId, m.Line, "cs", Depth: 0, Resolved: true, Builtin: false,
            ClassId: null, GameObject: null, MethodName: null, Via: null,
            Confidence: "syntactic", TargetSymbol: targetSymbol, SourceSymbol: m.ContainingSymbol, RefKind: m.RefKind);
        return edge with { ConfidenceLabel = EdgeConfidence.Speculative };
    }

    /// <summary>Caps the named-assembly sample for <see cref="QueryAnswer.SyntacticAssemblies"/>
    /// at a handful — the footer/JSON attribution names a few offenders plus a remediation hint,
    /// not necessarily every syntactic assembly in a large project (that's what `stats` is for).
    /// Null when there are none, so callers can gate on "is there anything to attribute" with a
    /// single null check.</summary>
    private const int MaxNamedSyntacticAssemblies = 5;

    private SyntacticAssemblySummary? BuildSyntacticSummary()
    {
        var details = _store.GetSyntacticAssemblyDetails();
        if (details.Count == 0)
        {
            return null;
        }

        var sample = details.Take(MaxNamedSyntacticAssemblies)
            .Select(EnrichSyntacticDetail)
            .ToList();
        return new SyntacticAssemblySummary(details.Count, sample);
    }

    private static EdgeResult ToSymbolEdgeResult(string targetDocId, CsRefEntry r)
    {
        var targetSymbol = targetDocId.Length > 2 && targetDocId[1] == ':' ? targetDocId[2..] : targetDocId;
        var edge = new EdgeResult(
            r.SourcePath, TargetPath: null, targetDocId, r.Line, "cs", Depth: 0, Resolved: true, Builtin: false,
            ClassId: null, GameObject: null, MethodName: null, Via: null,
            Confidence: r.Confidence, TargetSymbol: targetSymbol, SourceSymbol: r.ContainingSymbol, RefKind: r.RefKind);
        return edge with { ConfidenceLabel = EdgeConfidence.Derive(edge) };
    }

    private IReadOnlyList<EdgeResult> DirectReferencers(QueryTarget target)
    {
        if (target.FileId is { } fileId)
        {
            return _store.GetDirectReferencers(fileId);
        }

        return target.Guid is { } guid ? _store.GetDirectReferencersByGuidLiteral(guid) : [];
    }

    /// <summary>
    /// Applies the unified output contract's answer-construction step: per-edge confidence labels (chain-weakest for transitive
    /// answers, single-hop otherwise — see <see cref="ApplyChainConfidence"/>), answer-level
    /// weakest-link confidence, and the blind-spots footer. One shared path for who-uses and
    /// uses so the contract can never drift between the two verbs.
    /// </summary>
    private QueryAnswer Finalize(QueryTarget target, IReadOnlyList<EdgeResult> results, bool transitive, bool truncated, bool transitiveUnavailable, bool forward)
    {
        // A direct (depth-1-only) answer has no
        // "chain" at all — every result already IS its own single hop — so each edge keeps its
        // own EdgeConfidence.Derive label. Without this, file-level merges (an
        // event edge and a guid edge can legitimately share one SourcePath at depth 1, e.g.
        // Level.unity's m_Script attachment AND its separate UnityEvent binding to Foo.Jump)
        // would wrongly pull the PROVEN guid edge down to the event edge's advisory label
        // through ApplyChainConfidence's per-node "worst of alternates" grouping — a rule meant
        // for genuine alternate ROUTES to the same node in a multi-depth transitive walk, not for
        // two independent single-hop facts that happen to share a source file.
        IReadOnlyList<EdgeResult> labeled = transitive
            ? ApplyChainConfidence(results, forward)
            : [.. results.Select(r => r with { ConfidenceLabel = EdgeConfidence.Derive(r) })];
        // Asset-only answers cannot change when Roslyn coverage changes. Besides keeping the
        // caveat contract relevant, this gate avoids project-wide C# diagnostics dominating a
        // direct indexed GUID lookup (1.5s on a large project for a query whose SQL took milliseconds).
        var csRelevant = target.Path?.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) == true ||
            labeled.Any(result => result.Kind is "cs" or "event");
        var anySyntactic = csRelevant && _store.GetCsStats().SyntacticAssemblies > 0;
        var confidence = EdgeConfidence.AnswerLevel(labeled);
        var blindSpots = BlindSpots.ForQuery(
            anySyntactic,
            truncated,
            csRelevant && IsAnyCsprojStale(),
            csRelevant && HasDisabledRegionHint(target));
        var summary = anySyntactic ? BuildSyntacticSummary() : null;
        var possibleFalseNegative = labeled.Count == 0 && summary is not null
            && target.Path is not null && target.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
        return new QueryAnswer(target, labeled, truncated, transitiveUnavailable, confidence, blindSpots, summary, possibleFalseNegative);
    }

    /// <summary>
    /// The `disabled-region-refs-possible` blind-spot trigger for a plain file/guid-shaped
    /// who-uses/uses query -- <c>Finalize</c>'s only caller. There is no single "the queried symbol" here
    /// (unlike <see cref="WhoUsesSymbol"/>, which checks the one resolved symbol's own name), so
    /// the fallback this task's judgment call landed on is: any symbol DECLARED in the queried
    /// `.cs` file. That is deliberately coarser (a collision on any of the file's members fires
    /// it, not just ones actually implicated by this query) but stays a real, targeted signal
    /// rather than an always-on flag -- a non-`.cs` target (an asset, a guid with no file) can
    /// never have a disabled-region collision by construction (disabled-region tokens only ever
    /// come from C# source), so those correctly always evaluate to false.
    /// </summary>
    private bool HasDisabledRegionHint(QueryTarget target)
    {
        if (target.FileId is not { } fileId || target.Path is null ||
            !target.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return _store.HasDisabledRegionNameHintForFile(fileId, target.Path);
    }

    /// <summary>Extracts the simple (unqualified, unprefixed) name from a doc_id, e.g.
    /// "M:Foo.Jump" -&gt; "Jump", "T:Foo" -&gt; "Foo" -- the same basis disabled-region name_hints
    /// tokens are captured on (bare identifiers), used by the `WhoUsesSymbol` info-null
    /// degraded path where a <see cref="CsSymbolInfo"/> (and therefore its own `Name`) isn't
    /// available.</summary>
    private static string SimpleNameFromDocId(string docId)
    {
        var afterPrefix = docId.Length > 2 && docId[1] == ':' ? docId[2..] : docId;
        var dot = afterPrefix.LastIndexOf('.');
        return dot >= 0 ? afterPrefix[(dot + 1)..] : afterPrefix;
    }

    /// <summary>
    /// Computes each edge's presentation confidence label: for a direct query (no `Via`), that's
    /// just its own single-hop label. For a
    /// transitive answer, it's the weakest label along the edge's min-depth path back to the
    /// seed — "computed during the display pass from the labels of the edges on their min-depth
    /// path". Processed depth-ascending so each node's chain value is available before any
    /// deeper node that cites it as `Via`; a node reached by more than one displayed edge (an
    /// asset route and a cs route both landing on the same file) takes the WORST of its
    /// alternates, the conservative direction. Terminal dead-end edges (unresolved forward
    /// refs surfaced at every depth by `uses`) have no walkable node key and just keep their own
    /// single-hop label (null for a plain unresolved ref, proven for a builtin).
    /// </summary>
    private static IReadOnlyList<EdgeResult> ApplyChainConfidence(IReadOnlyList<EdgeResult> results, bool forward)
    {
        string? NodeKeyOf(EdgeResult r) => forward ? r.TargetPath : r.SourcePath;

        var chainWeakest = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var edge in results.Where(r => r.Depth >= 1).OrderBy(r => r.Depth))
        {
            var nodeKey = NodeKeyOf(edge);
            if (nodeKey is null)
            {
                continue;
            }

            var ownLabel = EdgeConfidence.Derive(edge);
            var predecessorLabel = edge.Via is not null && chainWeakest.TryGetValue(edge.Via, out var pl) ? pl : null;
            var candidate = EdgeConfidence.Weakest(ownLabel, predecessorLabel);

            if (!chainWeakest.TryGetValue(nodeKey, out var existing) || EdgeConfidence.IsWeakerThan(candidate, existing))
            {
                chainWeakest[nodeKey] = candidate;
            }
        }

        return [.. results.Select(r =>
        {
            var nodeKey = NodeKeyOf(r);
            var label = nodeKey is not null && chainWeakest.TryGetValue(nodeKey, out var v) ? v : EdgeConfidence.Derive(r);
            return r with { ConfidenceLabel = label };
        })];
    }

    // ---- Liveness / dead-candidates --------------------------------------------------------

    /// <summary>
    /// The preflight gates. Gates 1 (Force Text) and 2 (freshness) are satisfied by
    /// construction by the time this can run at all (<see cref="Open"/> asserts Force Text;
    /// every caller runs <see cref="EnsureFresh"/> first, same as every other query verb) --
    /// only gates 3-5 can actually fail, and this lists every failure, not just the first.
    /// </summary>
    public LivenessGateResult CheckLivenessGates()
    {
        var reasons = new List<string>();

        var syntacticNames = _store.GetSyntacticAssemblyNames();
        if (syntacticNames.Count > 0)
        {
            reasons.Add(
                $"liveness unavailable: syntactic-mode assembly present ({string.Join(", ", syntacticNames)}) " +
                "— open the project in the Unity Editor once (or open a .cs file in your IDE while Unity is running) to generate the missing .csproj files for full semantic analysis");
        }

        var addressables = AddressablesDetector.Detect(ProjectRoot);
        if (addressables.IsGated)
        {
            reasons.Add(addressables.Reason!);
        }
        else if (addressables.Status == AddressablesStatus.DetectedConfirmedVersion && _store.GetAddressablesRootFileId() is null)
        {
            // Confirmed-but-missing-settings-asset is its own gate condition,
            // not a silent zero-roots degradation (see AddressablesReasons.SettingsAssetMissing's
            // doc comment). Wired into the SAME preflight-gate collection as every other gate so
            // it's reported alongside any other failures, not as a separate code path.
            reasons.Add(AddressablesReasons.SettingsAssetMissing);
        }

        var staleReason = CheckCsprojFreshness();
        if (staleReason is not null)
        {
            reasons.Add(staleReason);
        }

        return reasons.Count == 0 ? LivenessGateResult.Ok : new LivenessGateResult(false, reasons);
    }

    /// <summary>
    /// For every Mode-A (semantic) assembly,
    /// its generated csproj's mtime must be >= the mtimes of Packages/manifest.json,
    /// ProjectSettings/ProjectVersion.txt, ProjectSettings/ProjectSettings.asset, AND
    /// ProjectSettings/EditorUserBuildSettings.asset -- the latter two are where scripting
    /// define symbols and the active build target actually live, so a manifest-only check
    /// misses exactly the changes that alter defines. Files absent in a given project are
    /// skipped in the comparison, not treated as failures. Returns null when fresh (or when
    /// there is nothing to compare -- no semantic assemblies, or none of the four files exist).
    /// </summary>
    private string? CheckCsprojFreshness()
    {
        var semanticNames = _store.GetSemanticAssemblyNames();
        if (semanticNames.Count == 0)
        {
            return null;
        }

        string[] configFiles =
        [
            Path.Combine(ProjectRoot, "Packages", "manifest.json"),
            Path.Combine(ProjectRoot, "ProjectSettings", "ProjectVersion.txt"),
            Path.Combine(ProjectRoot, "ProjectSettings", "ProjectSettings.asset"),
            Path.Combine(ProjectRoot, "ProjectSettings", "EditorUserBuildSettings.asset"),
        ];

        DateTime? newestConfigMtimeUtc = null;
        foreach (var configFile in configFiles)
        {
            if (!File.Exists(configFile))
            {
                continue;
            }

            var mtime = File.GetLastWriteTimeUtc(configFile);
            if (newestConfigMtimeUtc is null || mtime > newestConfigMtimeUtc.Value)
            {
                newestConfigMtimeUtc = mtime;
            }
        }

        if (newestConfigMtimeUtc is null)
        {
            return null;
        }

        foreach (var assemblyName in semanticNames)
        {
            var csprojPath = Path.Combine(ProjectRoot, assemblyName + ".csproj");
            if (!File.Exists(csprojPath))
            {
                // A missing expected csproj for an assembly the DB recorded
                // as semantic-mode is itself a staleness condition, not a case to skip. The
                // generic freshness sweep (EnsureFresh, called before this gate) doesn't catch
                // this on its own: the generated csproj lives at the project root, outside
                // Assets/Packages, so it isn't tracked as a `files` row the sweep watches for
                // deletion -- CsModeSelector only re-observes it on a full reindex of that
                // assembly, which nothing forces here. Treating "recorded semantic, csproj now
                // absent" as stale (same message family, same gate) closes that gap without
                // requiring a broader watch-list restructuring: the DB's mode claim can no
                // longer be verified against reality, which is exactly what this gate exists to
                // catch. (Previously this silently skipped the assembly and let a deleted-csproj
                // Mode-A assembly pass the gate as if nothing had changed.)
                return "liveness unavailable: generated csproj missing for a semantic-mode assembly — reopen the project in Unity (or your IDE while Unity is running) to resync it";
            }

            if (File.GetLastWriteTimeUtc(csprojPath) < newestConfigMtimeUtc.Value)
            {
                return "liveness unavailable: generated csproj older than project configuration — reopen the project in Unity (or your IDE while Unity is running) to resync it";
            }
        }

        return null;
    }

    /// <summary>
    /// The `who-uses`/`uses` counterpart of <see cref="CheckCsprojFreshness"/>: same
    /// extended-mtime check, reused rather than duplicated, but surfaced as the `csproj-stale`
    /// <see cref="BlindSpots"/> flag instead of a disqualifying gate — for `who-uses`, stale
    /// csprojs only add the `csproj-stale` blind-spot flag; for liveness claims they are
    /// disqualifying, because wrong defines compile out code and its refs. Covers both the
    /// "older than config" and the "missing entirely for a recorded semantic assembly" cases <see
    /// cref="CheckCsprojFreshness"/> itself treats as one staleness condition.
    /// </summary>
    private bool IsAnyCsprojStale() => CheckCsprojFreshness() is not null;

    /// <summary>
    /// `unbramble dead-candidates`: root
    /// materialization, the file-granular fixed point, screens-as-liveness-seeds,
    /// referenced-by-convention exclusions, and the allowlist — one pass, run
    /// after <see cref="EnsureFresh"/> and the preflight gates.
    /// </summary>
    /// <summary>
    /// Screen-free, ungated forward reachability from the liveness roots, as a PATH set — the
    /// input to who-uses' build-reachable tag (proven referencers
    /// can still be irrelevant test/dead content, and telling them apart required knowing which
    /// ones the build can actually reach).
    ///
    /// Deliberately NOT dead-candidates: none of its gates apply, because this only ever backs a
    /// POSITIVE claim ("a reference chain from a build root reaches this file") plus its honest
    /// absence ("no such chain found"). Positive reachability over the asset graph is provable
    /// regardless of syntactic assemblies — missing cs edges can only under-report reachability,
    /// never invent it, which is the safe direction for a tag whose absent case already reads as
    /// "not proven" rather than "unreachable". For the same reason the liveness SCREENS don't
    /// run here (they exist to keep maybe-live files off the dead list — seeding them here would
    /// invent reachability), and the Addressables root is seeded whenever Addressables is
    /// detected at all, version-confirmed or not (an unconfirmed version can only cost coverage,
    /// never correctness of the positive claim). Same asymmetric-risk rule as everywhere else,
    /// pointed in the tag's direction.
    /// </summary>
    /// <summary>
    /// Seeds every real file→file edge that lives outside `unified_walk_edges` into the liveness
    /// workspace. ONE helper for both fixed points (`dead-candidates`' and the build-reachable
    /// tag's) so the two can never propagate over different edge sets — which would break the
    /// coherence guarantee that a file `dead-candidates` proves dead can never be the target of an
    /// edge from a live file, since who-uses would be reading an edge the other never walked.
    /// </summary>
    private void SeedExtraWalkEdges()
    {
        _store.SeedExtraWalkEdges(_store.GetMatchedEventLinkFileEdges());
        _store.SeedExtraWalkEdges(_store.GetDllRefFileEdges());
    }

    public HashSet<string> ComputeBuildReachablePaths()
    {
        var configKey = string.Join("\n", Config.Liveness.Allowlist);
        if (_store.TryGetBuildReachabilityCache(configKey, out var cached))
        {
            return cached;
        }

        HashSet<string> result = new(StringComparer.OrdinalIgnoreCase);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var graphGeneration = _store.GetBuildReachabilityGraphGeneration();
            var seeds = GetBuildReachabilitySeeds();

            _store.InitLivenessWorkspace();
            _store.SeedLiveFiles(seeds);
            SeedExtraWalkEdges();
            _store.PropagateLiveFilesToFixedPoint();
            result = new HashSet<string>(_store.GetLiveFilePaths(), StringComparer.OrdinalIgnoreCase);

            if (_store.ReplaceBuildReachabilityCacheFromWorkspace(configKey, graphGeneration))
            {
                return result;
            }
        }

        // A continuously changing project can defeat both publication attempts. The complete
        // workspace result is still useful for this query; leave the cache invalid so the next
        // process recomputes rather than trusting a snapshot that lost its generation race.
        return result;
    }

    /// <summary>Targeted form of <see cref="ComputeBuildReachablePaths()"/> for interactive
    /// answers. It proves each requested path by walking the identical edge set backward to the
    /// identical roots. For a large candidate set, the full forward fixed point is cheaper.</summary>
    public HashSet<string> ComputeBuildReachablePaths(IEnumerable<string> candidatePaths)
    {
        var candidates = candidatePaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (candidates.Count == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var reachable = ComputeBuildReachablePaths();
        reachable.IntersectWith(candidates);
        return reachable;
    }

    private HashSet<long> GetBuildReachabilitySeeds()
    {
        var seeds = new HashSet<long>();
        seeds.UnionWith(_store.GetProjectSettingsFileIds());
        seeds.UnionWith(_store.GetResourcesFileIds());
        seeds.UnionWith(_store.GetStreamingAssetsFileIds());
        seeds.UnionWith(_store.GetUnconditionalEntryPointFileIds());

        var addressables = AddressablesDetector.Detect(ProjectRoot);
        if (addressables.Status != AddressablesStatus.NotDetected && _store.GetAddressablesRootFileId() is { } arId)
        {
            seeds.Add(arId);
        }

        var allowlistGlobs = Config.Liveness.Allowlist;
        if (allowlistGlobs.Length > 0)
        {
            seeds.UnionWith(_store.GetAllFileRowsForLiveness()
                .Where(f => !f.IdentityOnly && !f.IsFolder && GlobMatcher.MatchesAny(f.Path, allowlistGlobs))
                .Select(f => f.Id));
        }

        return seeds;
    }

    public DeadCandidatesResult RunDeadCandidates(Action<ScanProgress>? onScanProgress = null, Action<string>? onPhase = null)
    {
        EnsureFresh(onScanProgress, onPhase);

        var gate = CheckLivenessGates();
        if (!gate.Available)
        {
            return DeadCandidatesResult.Unavailable(gate.Reasons);
        }

        var allFiles = _store.GetAllFileRowsForLiveness();

        var neverDeadIds = new HashSet<long>();
        var candidateFiles = new Dictionary<long, string>();
        var conventionExcludedCount = 0;
        foreach (var file in allFiles)
        {
            if (file.IdentityOnly || file.IsFolder || file.Path.StartsWith("ProjectSettings/", StringComparison.OrdinalIgnoreCase))
            {
                neverDeadIds.Add(file.Id);
                continue;
            }

            if (ConventionExclusions.IsExcluded(file.Path))
            {
                neverDeadIds.Add(file.Id);
                conventionExcludedCount++;
                continue;
            }

            candidateFiles[file.Id] = file.Path;
        }

        var projectSettingsIds = _store.GetProjectSettingsFileIds();
        var resourcesIds = _store.GetResourcesFileIds();
        var streamingAssetsIds = _store.GetStreamingAssetsFileIds();
        var entryPointIds = _store.GetUnconditionalEntryPointFileIds();

        var addressables = AddressablesDetector.Detect(ProjectRoot);
        long? addressablesRootId = addressables.Status == AddressablesStatus.DetectedConfirmedVersion
            ? _store.GetAddressablesRootFileId()
            : null;

        var allowlistGlobs = Config.Liveness.Allowlist;
        var allowlistIds = allowlistGlobs.Length == 0
            ? []
            : allFiles.Where(f => !f.IdentityOnly && !f.IsFolder && GlobMatcher.MatchesAny(f.Path, allowlistGlobs))
                      .Select(f => f.Id).ToList();

        _store.InitLivenessWorkspace();

        var seeds = new HashSet<long>();
        seeds.UnionWith(projectSettingsIds);
        seeds.UnionWith(resourcesIds);
        seeds.UnionWith(streamingAssetsIds);
        seeds.UnionWith(entryPointIds);
        seeds.UnionWith(allowlistIds);
        if (addressablesRootId is { } arId)
        {
            seeds.Add(arId);
        }

        _store.SeedLiveFiles(seeds);
        SeedExtraWalkEdges();

        // Screen inputs, loaded once (they don't depend on the current live set themselves --
        // only which of their SOURCE files are currently live does, checked per pass below).
        var nameHintRows = _store.GetNameHintRows("cs-name-literal", "anim-event", "unityevent-local");
        var disabledRegionRows = _store.GetNameHintRows("cs-disabled");
        // unity-callback-guard: a type flagged by
        // SemanticCsExtractor.FindExternalCallbackContract with a 'cs-unity-callback' name hint
        // is invoked by Unity/a package through a mechanism this project's C# graph cannot see,
        // so it must be screened rather than proven dead regardless of what else does or doesn't
        // reference it -- checked as a short-circuit ahead of ComputeScreenReason below, same as
        // the other screens.
        var callbackFileIds = _store.GetNameHintRows("cs-unity-callback")
            .Select(row => row.SourceFileId)
            .ToHashSet();
        var unresolvedPathRefRows = _store.GetUnresolvedPathRefRows();
        var unresolvedSymbolTrailingIdentifiers = _store.GetUnresolvedSymbolRefTrailingIdentifiers();
        var unmatchedEventRawNameRows = _store.GetUnmatchedEventRawNameRows();
        var symbolNamesByFile = _store.GetCsSymbolNamesByFile()
            .GroupBy(r => r.FileId)
            .ToDictionary(g => g.Key, g => g.ToList());
        var symbolAttrsByFile = _store.GetCsSymbolAttrsByFile()
            .GroupBy(r => r.FileId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Attrs).ToList());
        var inheritEdges = _store.GetInheritEdges();

        var screenedReasons = new Dictionary<long, string>();

        // The outer loop: propagate to an inner fixed point, then re-evaluate screens
        // against whatever is now live; a newly-screened file is seeded and can unlock further
        // propagation/screening next iteration. LiveFiles and the screened set only grow, so
        // this terminates (bounded by the candidate count).
        var changed = true;
        while (changed)
        {
            changed = false;

            _store.PropagateLiveFilesToFixedPoint();

            var liveIds = new HashSet<long>(_store.GetLiveFileIds());

            foreach (var (fileId, path) in candidateFiles)
            {
                if (liveIds.Contains(fileId) || screenedReasons.ContainsKey(fileId))
                {
                    continue;
                }

                var reason = callbackFileIds.Contains(fileId)
                    ? ScreenReasons.UnityCallbackGuard
                    : ComputeScreenReason(
                        fileId, path, liveIds,
                        nameHintRows, disabledRegionRows, unresolvedPathRefRows,
                        unresolvedSymbolTrailingIdentifiers, unmatchedEventRawNameRows,
                        symbolNamesByFile, symbolAttrsByFile, inheritEdges);

                if (reason is not null)
                {
                    screenedReasons[fileId] = reason;
                    _store.SeedLiveFiles([fileId]);
                    changed = true;
                }
            }
        }

        var finalLiveIds = new HashSet<long>(_store.GetLiveFileIds());

        var provenDead = candidateFiles
            .Where(kv => !finalLiveIds.Contains(kv.Key))
            .Select(kv => new DeadCandidateEntry(kv.Value, "no reachable reference from any liveness root"))
            .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var advisoryDead = screenedReasons
            .Select(kv => new AdvisoryDeadEntry(candidateFiles[kv.Key], kv.Value))
            .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var addressablesStatusText = addressables.Status switch
        {
            AddressablesStatus.DetectedConfirmedVersion => $"detected, confirmed ({addressables.ResolvedVersion})",
            _ => "not detected",
        };

        var roots = new LivenessRootSummary(
            projectSettingsIds.Count, resourcesIds.Count, streamingAssetsIds.Count,
            entryPointIds.Count, addressablesStatusText, allowlistIds.Count);

        // Gate 3 already required zero syntactic assemblies to reach this point -- every
        // analyzed assembly is semantic-mode by construction here. Gate 5 (CheckCsprojFreshness)
        // likewise already required every semantic assembly's generated csproj to be fresh, so
        // csprojStale is always false here too -- a stale csproj disqualifies dead-candidates
        // entirely (CheckLivenessGates above) rather than merely flagging the answer.
        // disabledRegionNameHint stays false too: dead-candidates already has its own dedicated
        // disabled-region SCREEN (ComputeScreenReason below) that consumes the exact same
        // cs-disabled name_hints rows to keep a candidate off the proven-dead list outright --
        // this answer-level flag is who-uses/uses' substitute for not having that screen.
        var semanticAssemblyCount = _store.GetSemanticAssemblyNames().Count;
        var blindSpots = BlindSpots.ForQuery(anySyntacticAssembly: false, truncated: false, csprojStale: false, disabledRegionNameHint: false);

        return new DeadCandidatesResult(
            true, [], roots,
            semanticAssemblyCount, 0, conventionExcludedCount, provenDead, advisoryDead, blindSpots);
    }

    /// <summary>
    /// The screen cascade for one currently-dead candidate, evaluated against the CURRENT
    /// live set (mutates across outer-loop passes as more files become live). Returns the first
    /// matching screen's reason, or null if none apply. Order is arbitrary;
    /// since every screen leads to the identical outcome (seed + advisory), the order only
    /// affects which single reason string is reported when more than one would fire.
    /// </summary>
    private static string? ComputeScreenReason(
        long candidateId, string candidatePath, HashSet<long> liveIds,
        IReadOnlyList<UnBrambleStore.NameHintScreenRow> nameHintRows,
        IReadOnlyList<UnBrambleStore.NameHintScreenRow> disabledRegionRows,
        IReadOnlyList<UnBrambleStore.SourceKeyedRow> unresolvedPathRefRows,
        IReadOnlyList<UnBrambleStore.SourceKeyedRow> unresolvedSymbolTrailingIdentifiers,
        IReadOnlyList<UnBrambleStore.SourceKeyedRow> unmatchedEventRawNameRows,
        Dictionary<long, List<UnBrambleStore.CsSymbolNameRow>> symbolNamesByFile,
        Dictionary<long, List<string>> symbolAttrsByFile,
        IReadOnlyList<(long SourceFileId, long TargetFileId)> inheritEdges)
    {
        // Path-ref name collision applies to ANY candidate (asset or script). This screen
        // carries NO "sourced from a live file" qualifier -- unlike name-hint collision and
        // disabled-region below, which do require the source file to be live: a broken path
        // ref sitting in an otherwise-dead file is still real evidence someone once intended to
        // reference this candidate, so the screen catches that even when the
        // referencing file's own liveness can't be established.
        var candidateFileName = Path.GetFileName(candidatePath);
        foreach (var row in unresolvedPathRefRows)
        {
            if (string.Equals(FinalPathSegment(row.Key), candidateFileName, StringComparison.OrdinalIgnoreCase))
            {
                return ScreenReasons.PathRefNameCollision;
            }
        }

        if (!candidatePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        symbolNamesByFile.TryGetValue(candidateId, out var ownSymbols);

        // Zero-symbols screen (see LivenessModels.ScreenReasons.NoExtractedSymbols's
        // doc comment): a .cs candidate with NO rows in `symbols` at all -- e.g. a whole-file
        // platform #if wrapping the entire class for a platform never active under the current
        // defines -- has nothing of its own for ANY of the six screens below to match against
        // (they all key off the candidate's own declared symbol names/attrs/base list). Checked
        // before those screens run so the reason reported is the honest one ("could not be
        // analyzed") rather than accidentally falling through all of them to provenDead. Gate 3
        // already guarantees every assembly reaching this point is semantic-mode --
        // a syntactic assembly makes the whole command unavailable before candidates are ever
        // evaluated -- so this applies uniformly to every .cs candidate here without needing to
        // re-check assembly mode itself.
        if (ownSymbols is null || ownSymbols.Count == 0)
        {
            return ScreenReasons.NoExtractedSymbols;
        }

        var anySymbolNames = new HashSet<string>(ownSymbols.Select(s => s.Name), StringComparer.Ordinal);
        var methodNames = new HashSet<string>(
            ownSymbols.Where(s => s.Kind == "method").Select(s => s.Name), StringComparer.Ordinal);

        // Same as the path-ref screen above: syntactic-text collision has no liveness qualifier
        // either. Not conditioning on the source file's liveness here (unlike
        // name-hint-collision/disabled-region-screen, which do require it) matters as
        // defense-in-depth: a downgraded/unresolvable call sitting anywhere in the project, live
        // or not, is still evidence against proving this candidate dead.
        foreach (var row in unresolvedSymbolTrailingIdentifiers)
        {
            if (anySymbolNames.Contains(row.Key))
            {
                return ScreenReasons.SyntacticTextCollision;
            }
        }

        foreach (var row in nameHintRows)
        {
            if (liveIds.Contains(row.SourceFileId) && methodNames.Contains(row.Name))
            {
                return ScreenReasons.NameHintCollision;
            }
        }

        foreach (var row in unmatchedEventRawNameRows)
        {
            if (liveIds.Contains(row.SourceFileId) && methodNames.Contains(row.Key))
            {
                return ScreenReasons.UnityEventNameCollision;
            }
        }

        if (symbolAttrsByFile.TryGetValue(candidateId, out var attrsList))
        {
            foreach (var attrs in attrsList)
            {
                foreach (var token in attrs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!InertAttributes.Names.Contains(token))
                    {
                        return ScreenReasons.AttributeScreen;
                    }
                }
            }
        }

        foreach (var row in disabledRegionRows)
        {
            if (liveIds.Contains(row.SourceFileId) && anySymbolNames.Contains(row.Name))
            {
                return ScreenReasons.DisabledRegionScreen;
            }
        }

        foreach (var (sourceFileId, targetFileId) in inheritEdges)
        {
            if (sourceFileId == candidateId && liveIds.Contains(targetFileId))
            {
                return ScreenReasons.InterfaceDispatchGuard;
            }
        }

        return null;
    }

    private static string FinalPathSegment(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx < 0 ? path : path[(idx + 1)..];
    }

    public void Dispose() => _store.Dispose();
}
