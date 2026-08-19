using System.Collections.Concurrent;
using System.Diagnostics;
using UnBramble.Core.Config;
using UnBramble.Core.Model;

namespace UnBramble.Core.Scanning;

/// <summary>
/// A live snapshot of an in-progress <see cref="Scanner.Scan"/> walk, handed to the optional
/// <c>onProgress</c> callback (see <see cref="ScanHeartbeat"/>) on a short cadence. Total file/dir
/// counts are unknowable until the walk finishes, so this is deliberately just a running count +
/// "currently in" path -- never a fraction/percentage: an honest live count beats a fabricated
/// completion estimate.
/// </summary>
public readonly record struct ScanProgress(long DirsVisited, long FilesSeen, string CurrentPath);

/// <summary>
/// Diagnostic aid for a scan that appears to hang: never wrong, only sometimes slower -- makes
/// an unexpectedly slow scan observable in real time instead of a silent black box. Opt-in via
/// env var so it's zero-cost by default: set
/// <c>UNBRAMBLE_SCAN_HEARTBEAT=1</c> for a periodic "still scanning" line on stderr every ~3s, or
/// <c>=2</c> to additionally log every directory entered. Never writes to stdout (verb output
/// contracts own stdout) and never affects scan results — observability only. Generically useful
/// for any future real-project scan that looks stuck.
///
/// Also optionally drives an <see cref="onProgress"/>
/// callback on the same periodic timer, independent of the env-gated stderr output above -- this
/// is what lets `watch`'s cold-start scan surface a live "N files, M dirs" count through
/// <see cref="WatchStatusTracker"/> instead of staying silent until the whole walk finishes. When
/// a callback is supplied the timer runs faster (~600ms) than the stderr-only cadence (3s):
/// stderr is a human skimming a terminal, the tracker is feeding a live status file another
/// process can poll.
/// </summary>
internal sealed class ScanHeartbeat : IDisposable
{
    private static readonly TimeSpan StderrOnlyInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan WithProgressCallbackInterval = TimeSpan.FromMilliseconds(600);

    private readonly int _verbosity;
    private readonly Action<ScanProgress>? _onProgress;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly Timer? _timer;
    private long _dirsVisited;
    private long _filesSeen;
    private string _currentPath = string.Empty;

    private ScanHeartbeat(int verbosity, Action<ScanProgress>? onProgress)
    {
        _verbosity = verbosity;
        _onProgress = onProgress;
        var interval = onProgress is not null ? WithProgressCallbackInterval : StderrOnlyInterval;
        _timer = new Timer(_ => Tick(), null, interval, interval);
    }

    /// <param name="onProgress">
    /// Optional live-progress sink, independent of the <c>UNBRAMBLE_SCAN_HEARTBEAT</c> env var
    /// below -- when supplied, this instrument is created (and its timer runs) even if the env
    /// var is unset, but the env-gated stderr output stays exactly as opt-in as before.
    /// </param>
    public static ScanHeartbeat? CreateIfEnabled(Action<ScanProgress>? onProgress = null)
    {
        var raw = Environment.GetEnvironmentVariable("UNBRAMBLE_SCAN_HEARTBEAT");
        var verbosity = int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : 0;
        return verbosity > 0 || onProgress is not null ? new ScanHeartbeat(verbosity, onProgress) : null;
    }

    public void EnterDir(string path)
    {
        var count = Interlocked.Increment(ref _dirsVisited);
        Volatile.Write(ref _currentPath, path);
        if (_verbosity >= 2)
        {
            Console.Error.WriteLine($"[{_stopwatch.Elapsed:hh\\:mm\\:ss}] enter #{count}: {path}");
        }
    }

    /// <summary>Called once per directory (not once per file) with that directory's file count --
    /// alongside the existing per-directory <see cref="EnterDir"/> tracking, right after a
    /// directory's file candidates are enumerated in <see cref="Scanner"/>'s own Walk.</summary>
    public void AddFiles(int count)
    {
        if (count > 0)
        {
            Interlocked.Add(ref _filesSeen, count);
        }
    }

    private void Tick()
    {
        if (_verbosity >= 1)
        {
            Print();
        }

        _onProgress?.Invoke(new ScanProgress(Volatile.Read(ref _dirsVisited), Volatile.Read(ref _filesSeen), Volatile.Read(ref _currentPath)));
    }

    private void Print()
    {
        Console.Error.WriteLine(
            $"[{_stopwatch.Elapsed:hh\\:mm\\:ss}] still scanning: {Volatile.Read(ref _dirsVisited)} dirs visited, currently in: {Volatile.Read(ref _currentPath)}");
    }

    public void Dispose() => _timer?.Dispose();
}

/// <summary>
/// Walks a Unity project's configured roots (plus the hardcoded identity-only
/// Library/PackageCache root) and produces the current in-scope file inventory. Reads only
/// filesystem metadata and .meta guid lines — never touches asset content (that's a separate
/// parsing pass).
/// </summary>
public sealed class Scanner
{
    private const int MaxParallelRootLinks = 8;

    /// <summary>A single directory's own enumeration (EnumerateFileSystemInfos + per-entry
    /// attribute/stat reads) taking longer than this is recorded as a <see cref="SlowDirEntry"/>
    /// -- Windows Defender's real-time protection intercepts exactly this kind of per-file/per-dir
    /// I/O, so a cluster of slow entries under one root is the observable signature of a cold
    /// Defender verdict cache, not a code-level slowdown.</summary>
    internal const double SlowDirThresholdMs = 250;

    private sealed record DeferredLink(string RealPath, string ProjectPrefix, bool IdentityOnly);

    /// <summary>Per-root mutable dir/file counters, threaded down through one root's own
    /// <see cref="Walk"/> recursion. Never shared across roots and never touched from more than
    /// one thread at a time -- each top-level root (ordinary root, identity-only root, or one
    /// deferred-link job) owns exactly one instance for the lifetime of its own (single-threaded)
    /// recursive walk, so plain field increments are safe without Interlocked.</summary>
    private sealed class ScanCounters
    {
        public long Dirs;
        public long Files;
    }

    private sealed class ScanPartition
    {
        public List<ScannedFileEntry> Entries { get; } = [];
        public List<RootMapping> Roots { get; } = [];
        public List<string> Warnings { get; } = [];
        public List<DeferredLink> DeferredLinks { get; } = [];
        public ScanRootStat? RootStat { get; set; }
    }

    // Extend freely when a real project shows a text-serialized extension not covered here.
    // Public so callers outside the scanner (stats --json's refSourceExtensions field) can't
    // drift from what the scanner itself classifies as a reference source.
    public static readonly HashSet<string> ReferenceSourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".prefab", ".unity", ".asset", ".mat", ".anim", ".controller", ".overrideController",
        ".physicMaterial", ".physicsMaterial2D", ".mixer", ".flare", ".fontsettings", ".guiskin",
        ".renderTexture", ".spriteatlas", ".terrainlayer", ".brush", ".playable", ".mask",
        ".preset", ".shadervariants", ".lighting", ".giparams", ".cubemap", ".shadergraph",
        ".vfx", ".shadersubgraph", ".uxml", ".uss", ".tss", ".asmdef", ".asmref",
    };

    // internal (not private): the watcher needs the same literal to exclude
    // Library/PackageCache from the set of real roots it watches — refreshed on sweep only.
    internal const string PackageCacheProjectPrefix = "Library/PackageCache";

    /// <summary>
    /// True for a project-relative path under a package root (`Packages/` or `LocalPackages/` --
    /// see <see cref="UnBrambleConfig.DefaultRoots"/>), as opposed to `Assets/` or the
    /// identity-only `Library/PackageCache` (never a real reference source at all, see this
    /// class's own PackageCache scan above). Used to tell a plain project script's missing csproj
    /// apart from a package asmdef's: Unity's "open the project once" auto-regeneration only
    /// covers package-sourced assemblies when Preferences > External Tools > "Generate .csproj
    /// files for:" has the matching package category (Local/Embedded/Registry/Git) checked --
    /// unchecked by default, so a package asmdef can stay syntactic forever no matter how many
    /// times the project is reopened, which "open Unity once" alone doesn't explain.
    /// </summary>
    public static bool IsPackageSourcedPath(string projectRelativePath) =>
        projectRelativePath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)
        || projectRelativePath.StartsWith("LocalPackages/", StringComparison.OrdinalIgnoreCase);

    /// <param name="knownMeta">
    /// The store's current path -> (meta mtime, guid) snapshot, hoisted by the caller from the
    /// same `LoadAllFiles()` call <c>ApplySweep</c> would otherwise make on its own. When a path
    /// is present here AND its meta's mtime on disk still matches, the scanner reuses the
    /// recorded guid instead of re-reading the meta's content. Null (the default) degrades to
    /// reading every meta's content unconditionally, which is exactly correct for `--full`/a
    /// fresh `init` against an empty store.
    /// </param>
    /// <param name="onProgress">
    /// Optional live-progress sink: invoked roughly
    /// every 600ms while this walk is in flight with a running dirs/files-seen count and the
    /// directory currently being entered. Null (the default, every caller except `watch`'s
    /// engine) costs nothing beyond the env-gated <see cref="ScanHeartbeat"/> check it already had.
    /// </param>
    public ScanResult Scan(
        string projectRoot,
        UnBrambleConfig config,
        IReadOnlyDictionary<string, MetaSnapshot>? knownMeta = null,
        Action<ScanProgress>? onProgress = null)
    {
        var entries = new List<ScannedFileEntry>();
        var roots = new List<RootMapping>();
        var warnings = new List<string>();
        var visitedReal = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var deferredRootLinks = new List<DeferredLink>();
        var rootStats = new List<ScanRootStat>();
        var slowDirs = new ConcurrentBag<SlowDirEntry>();

        using var heartbeat = ScanHeartbeat.CreateIfEnabled(onProgress);

        foreach (var rootRelative in config.Roots)
        {
            ScanRoot(
                projectRoot,
                rootRelative,
                rootRelative,
                identityOnly: false,
                config,
                visitedReal,
                entries,
                roots,
                warnings,
                deferredRootLinks,
                heartbeat,
                knownMeta,
                rootStats,
                slowDirs);
        }

        ScanRoot(
            projectRoot,
            Path.Combine("Library", "PackageCache"),
            PackageCacheProjectPrefix,
            identityOnly: true,
            config,
            visitedReal,
            entries,
            roots,
            warnings,
            deferredRootLinks,
            heartbeat,
            knownMeta,
            rootStats,
            slowDirs);

        ScanDeferredRootLinks(
            deferredRootLinks,
            config,
            visitedReal,
            entries,
            roots,
            warnings,
            heartbeat,
            knownMeta,
            rootStats,
            slowDirs);

        // `visitedReal` above only guards
        // against re-walking the same REAL directory path twice. It does nothing for two
        // ENTRIES that resolve to the identical (case-insensitive) project-relative `Path` from
        // two different real files -- the case that matters in practice is two files in the same
        // real directory whose names differ only by case (e.g. "Thing.asset" and "thing.asset"),
        // which `Directory.EnumerateFileSystemInfos()` legitimately returns as two distinct
        // entries when that directory has Windows' per-directory case-sensitivity flag enabled
        // (a real, if unusual, NTFS/WSL-interop feature -- common in large third-party asset
        // trees synced with non-Windows-native tooling). `UnBrambleStore`'s `files.path` column
        // is `UNIQUE COLLATE NOCASE`; left unchecked, this collision previously surfaced as an
        // unhandled `SQLite Error 19: UNIQUE constraint failed: files.path` deep inside
        // sweep-diff apply, aborting the whole index with no indication of which path collided.
        // Resolved here, at the scan boundary, with the same "first claim wins" convention
        // `visitedReal` already uses for same-real-path duplicates -- deterministic because
        // `entries` is built in a fixed encounter order (config.Roots roots in listed order,
        // then Library/PackageCache, then deferred root-link jobs in their own stable sorted job
        // order, then nested links in their stable sorted order) -- but surfaced via `warnings`
        // (unlike `visitedReal`'s fully silent skip) since a path collision from two DIFFERENT
        // real files is a genuinely surprising topology a project maintainer would want to know
        // about and potentially fix at the source, not routine junction-aliasing noise.
        if (entries.Count > 0)
        {
            var firstIndexByPath = new Dictionary<string, int>(entries.Count, StringComparer.OrdinalIgnoreCase);
            var deduped = new List<ScannedFileEntry>(entries.Count);
            foreach (var entry in entries)
            {
                if (firstIndexByPath.TryGetValue(entry.Path, out var firstIndex))
                {
                    var first = deduped[firstIndex];
                    warnings.Add(
                        $"warning: duplicate project path '{entry.Path}' (also seen as '{first.Path}') reached from " +
                        "two different real files -- likely a case-only filename collision inside one directory; " +
                        $"kept the first-scanned copy (kind {first.Kind}), ignored the second (kind {entry.Kind})");
                    continue;
                }

                firstIndexByPath[entry.Path] = deduped.Count;
                deduped.Add(entry);
            }

            entries.Clear();
            entries.AddRange(deduped);
        }

        // Directory enumeration order and parallel completion order are both deliberately
        // excluded from the public result. Besides making repeated cold scans reproducible,
        // this keeps duplicate-realpath ownership deterministic when two root-level links
        // point at the same target.
        entries.Sort((left, right) => CompareStable(left.Path, right.Path));
        roots.Sort((left, right) =>
        {
            var comparison = CompareStable(left.RealPath, right.RealPath);
            return comparison != 0 ? comparison : CompareStable(left.ProjectPrefix, right.ProjectPrefix);
        });
        warnings.Sort(StringComparer.Ordinal);
        rootStats.Sort((left, right) => CompareStable(left.ProjectPrefix, right.ProjectPrefix));

        // Top-20 slowest directories only (index-history's own bounded-growth policy) -- the
        // full set is potentially unbounded on a genuinely slow cold scan and this is a
        // diagnostic sample, not a complete audit trail.
        var slowestDirs = slowDirs.OrderByDescending(d => d.ElapsedMs).Take(20).ToList();

        return new ScanResult(entries, roots, warnings, rootStats, slowestDirs);
    }

    /// <summary>
    /// Root-level junctions are the important real-project fan-out point: a real project's
    /// immediate Assets children can map to several large, disjoint package trees. The ordinary
    /// root is
    /// walked first so a directory reachable without a link retains precedence. Remaining
    /// distinct targets are then claimed in stable project-path order and scanned with a
    /// bounded degree of parallelism. Each worker owns private result lists; only the
    /// real-path visit set is shared.
    ///
    /// Reparse points discovered inside a parallel worker are recorded but not followed by
    /// that worker. They are handled serially after all partitions merge, which preserves
    /// deterministic alias ownership and makes cross-links/back-links cycle-safe even when
    /// two top-level targets refer into one another.
    /// </summary>
    private static void ScanDeferredRootLinks(
        List<DeferredLink> deferredRootLinks,
        UnBrambleConfig config,
        ConcurrentDictionary<string, byte> visitedReal,
        List<ScannedFileEntry> entries,
        List<RootMapping> roots,
        List<string> warnings,
        ScanHeartbeat? heartbeat,
        IReadOnlyDictionary<string, MetaSnapshot>? knownMeta,
        List<ScanRootStat> rootStats,
        ConcurrentBag<SlowDirEntry> slowDirs)
    {
        var jobs = new List<DeferredLink>();
        foreach (var link in deferredRootLinks
                     .OrderBy(link => link.ProjectPrefix, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(link => link.ProjectPrefix, StringComparer.Ordinal)
                     .ThenBy(link => link.RealPath, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(link => link.RealPath, StringComparer.Ordinal))
        {
            // Claim every job before any worker starts. This both collapses two links to the
            // same target deterministically and prevents an overlapping target from being
            // stolen through a nested path by a faster worker.
            if (visitedReal.TryAdd(Normalize(link.RealPath), 0))
            {
                jobs.Add(link);
            }
        }

        if (jobs.Count == 0)
        {
            return;
        }

        var partitions = new ScanPartition?[jobs.Count];
        Parallel.For(
            fromInclusive: 0,
            toExclusive: jobs.Count,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Min(MaxParallelRootLinks, jobs.Count) },
            index =>
            {
                var job = jobs[index];
                var partition = new ScanPartition();
                var counters = new ScanCounters();
                var jobStopwatch = Stopwatch.StartNew();
                Walk(
                    job.RealPath,
                    job.ProjectPrefix,
                    job.IdentityOnly,
                    config,
                    visitedReal,
                    partition.Entries,
                    partition.Roots,
                    partition.Warnings,
                    rootAlreadyClaimed: true,
                    deferReparseChildren: true,
                    deferReparseDescendants: true,
                    partition.DeferredLinks,
                    heartbeat,
                    knownMeta,
                    counters,
                    slowDirs);
                jobStopwatch.Stop();
                // Every deferred link is, by construction, a root-level reparse point (see this
                // method's own doc comment) -- IsJunction is unconditionally true here, and
                // ResolvedTarget is the real path it walked, which is frequently OUTSIDE the
                // project root entirely (the field an exclusion-path fix needs).
                partition.RootStat = new ScanRootStat(
                    job.ProjectPrefix, job.RealPath, IsJunction: true, counters.Dirs, counters.Files, jobStopwatch.Elapsed.TotalMilliseconds);
                partitions[index] = partition;
            });

        var nestedLinks = new List<DeferredLink>();
        foreach (var partition in partitions)
        {
            entries.AddRange(partition!.Entries);
            roots.AddRange(partition.Roots);
            warnings.AddRange(partition.Warnings);
            nestedLinks.AddRange(partition.DeferredLinks);
            if (partition.RootStat is { } stat)
            {
                rootStats.Add(stat);
            }
        }

        // Nested links are intentionally a serial tail. They are normally tiny compared with
        // the root-level package trees, and deterministic traversal is more valuable here than
        // racing aliases that may point at one another or back to an already-walked root.
        foreach (var link in nestedLinks
                     .OrderBy(link => link.ProjectPrefix, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(link => link.ProjectPrefix, StringComparer.Ordinal)
                     .ThenBy(link => link.RealPath, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(link => link.RealPath, StringComparer.Ordinal))
        {
            if (!visitedReal.TryAdd(Normalize(link.RealPath), 0))
            {
                continue;
            }

            var counters = new ScanCounters();
            var linkStopwatch = Stopwatch.StartNew();
            Walk(
                link.RealPath,
                link.ProjectPrefix,
                link.IdentityOnly,
                config,
                visitedReal,
                entries,
                roots,
                warnings,
                rootAlreadyClaimed: true,
                deferReparseChildren: false,
                deferReparseDescendants: false,
                deferredLinks: null,
                heartbeat,
                knownMeta,
                counters,
                slowDirs);
            linkStopwatch.Stop();
            rootStats.Add(new ScanRootStat(
                link.ProjectPrefix, link.RealPath, IsJunction: true, counters.Dirs, counters.Files, linkStopwatch.Elapsed.TotalMilliseconds));
        }
    }

    private static void ScanRoot(
        string projectRoot,
        string rootRelativeOnDisk,
        string projectPrefix,
        bool identityOnly,
        UnBrambleConfig config,
        ConcurrentDictionary<string, byte> visitedReal,
        List<ScannedFileEntry> entries,
        List<RootMapping> roots,
        List<string> warnings,
        List<DeferredLink> deferredRootLinks,
        ScanHeartbeat? heartbeat,
        IReadOnlyDictionary<string, MetaSnapshot>? knownMeta,
        List<ScanRootStat> rootStats,
        ConcurrentBag<SlowDirEntry> slowDirs)
    {
        var full = Path.Combine(projectRoot, rootRelativeOnDisk.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(full))
        {
            // Roots that don't exist on disk are silently skipped (LocalPackages usually won't exist).
            return;
        }

        var realPath = Path.GetFullPath(full);
        var isJunction = IsReparsePoint(realPath);
        string? resolvedTarget = null;
        if (isJunction)
        {
            var target = TryResolveLinkTarget(realPath);
            if (target is null)
            {
                warnings.Add($"warning: skipping dangling link '{projectPrefix}'");
                return;
            }

            realPath = target;
            resolvedTarget = target;
        }

        roots.Add(new RootMapping(Normalize(realPath), projectPrefix));

        var counters = new ScanCounters();
        var rootStopwatch = Stopwatch.StartNew();
        Walk(
            realPath,
            projectPrefix,
            identityOnly,
            config,
            visitedReal,
            entries,
            roots,
            warnings,
            rootAlreadyClaimed: false,
            deferReparseChildren: true,
            deferReparseDescendants: false,
            deferredRootLinks,
            heartbeat,
            knownMeta,
            counters,
            slowDirs);
        rootStopwatch.Stop();
        rootStats.Add(new ScanRootStat(
            projectPrefix, resolvedTarget, isJunction, counters.Dirs, counters.Files, rootStopwatch.Elapsed.TotalMilliseconds));
    }

    private static void Walk(
        string realDirPath,
        string projectPrefix,
        bool identityOnly,
        UnBrambleConfig config,
        ConcurrentDictionary<string, byte> visitedReal,
        List<ScannedFileEntry> entries,
        List<RootMapping> roots,
        List<string> warnings,
        bool rootAlreadyClaimed,
        bool deferReparseChildren,
        bool deferReparseDescendants,
        List<DeferredLink>? deferredLinks,
        ScanHeartbeat? heartbeat,
        IReadOnlyDictionary<string, MetaSnapshot>? knownMeta,
        ScanCounters? rootCounters,
        ConcurrentBag<SlowDirEntry> slowDirs)
    {
        if (!rootAlreadyClaimed && !visitedReal.TryAdd(Normalize(realDirPath), 0))
        {
            return;
        }

        heartbeat?.EnterDir(realDirPath);
        if (rootCounters is not null)
        {
            rootCounters.Dirs++;
        }

        // DirectoryInfo.EnumerateFileSystemInfos() surfaces attributes/timestamps/length from
        // the SAME FindFirstFile/FindNextFile enumeration record instead of a second per-entry
        // round trip (the old Directory.GetFileSystemEntries + File.GetAttributes + `new
        // FileInfo(...)` combo). The eager .ToList() here matches the old code's failure
        // granularity: a directory-level enumeration failure (e.g. permission denied on
        // realDirPath itself) is caught once, exactly like the old GetFileSystemEntries() call
        // was.
        //
        // Timed: a directory whose enumeration alone
        // exceeds SlowDirThresholdMs is recorded as a SlowDirEntry -- this is exactly the I/O
        // Windows Defender's real-time protection intercepts, so a cluster of these under one
        // root is the observable signature of a cold Defender verdict cache, distinguishable
        // from a genuine code regression (which would show up as a uniform shift instead).
        var enumStopwatch = Stopwatch.StartNew();
        List<FileSystemInfo> rawEntries;
        try
        {
            rawEntries = [.. new DirectoryInfo(realDirPath).EnumerateFileSystemInfos()];
            rawEntries.Sort((left, right) => CompareStable(left.Name, right.Name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"warning: could not enumerate '{projectPrefix}': {ex.Message}");
            return;
        }
        finally
        {
            enumStopwatch.Stop();
            if (enumStopwatch.Elapsed.TotalMilliseconds > SlowDirThresholdMs)
            {
                slowDirs.Add(new SlowDirEntry(projectPrefix, enumStopwatch.Elapsed.TotalMilliseconds));
            }
        }

        var rawNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirCandidates = new List<(string Name, string FullPath, bool IsReparse)>();
        var fileCandidates = new List<(string Name, string FullPath, long Mtime, long Length)>();
        var metaByOwner = new Dictionary<string, (string FullPath, long Mtime)>(StringComparer.OrdinalIgnoreCase);

        foreach (var info in rawEntries)
        {
            var name = info.Name;
            rawNames.Add(name);

            FileAttributes attributes;
            try
            {
                // Already part of the enumeration record on Windows -- no fresh syscall. (Not
                // true of LastWriteTimeUtc for DIRECTORY entries specifically -- see the comment
                // where a folder's ScannedFileEntry is built below.)
                attributes = info.Attributes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"warning: could not stat '{projectPrefix}/{name}': {ex.Message}");
                continue;
            }

            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            if (isDirectory)
            {
                if (HiddenAssetRules.IsHidden(name, isDirectory: true))
                {
                    continue;
                }

                if (config.ExcludeDirs.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                dirCandidates.Add((name, info.FullName, attributes.HasFlag(FileAttributes.ReparsePoint)));
            }
            else
            {
                if (HiddenAssetRules.IsHidden(name, isDirectory: false))
                {
                    continue;
                }

                // Files (unlike directories) don't have the parent-index staleness quirk --
                // LastWriteTimeUtc/Length straight from the enumeration record is reliable here.
                long mtimeTicks;
                try
                {
                    mtimeTicks = info.LastWriteTimeUtc.Ticks;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"warning: could not stat '{projectPrefix}/{name}': {ex.Message}");
                    continue;
                }

                if (name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    metaByOwner[name[..^".meta".Length]] = (info.FullName, mtimeTicks);
                }
                else
                {
                    long length;
                    try
                    {
                        length = ((FileInfo)info).Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        warnings.Add($"warning: could not stat '{projectPrefix}/{name}': {ex.Message}");
                        continue;
                    }

                    fileCandidates.Add((name, info.FullName, mtimeTicks, length));
                }
            }
        }

        heartbeat?.AddFiles(fileCandidates.Count);
        if (rootCounters is not null)
        {
            rootCounters.Files += fileCandidates.Count;
        }

        foreach (var (name, fullPath, isReparse) in dirCandidates)
        {
            var childPrefix = $"{projectPrefix}/{name}";

            if (metaByOwner.TryGetValue(name, out var meta))
            {
                var guid = ResolveMetaGuid(childPrefix, meta.FullPath, meta.Mtime, knownMeta, warnings);
                // NOT taken from the enumeration record on purpose, unlike everything else here
                // moved off a fresh stat: a subdirectory's LastWriteTimeUtc as reported through
                // its PARENT's own enumeration (FindFirstFile/FindNextFile) can lag the true
                // value by a small, variable amount on NTFS -- the directory's timestamp
                // propagates into the parent's directory-index entry somewhat lazily, while a
                // direct stat of the folder itself (this call) is always current. Content files
                // don't have this quirk (confirmed empirically against IncrementalSweepTests'
                // no-change-sweep assertions, which false-positived on folders only when this
                // used the enumeration-sourced value) -- only folders pay this one extra stat.
                var dirMtime = new DirectoryInfo(fullPath).LastWriteTimeUtc.Ticks;
                entries.Add(new ScannedFileEntry(childPrefix, guid, FileKind.Folder, dirMtime, 0, meta.Mtime, identityOnly));
            }

            if (isReparse)
            {
                var target = TryResolveLinkTarget(fullPath);
                if (target is null)
                {
                    warnings.Add($"warning: skipping dangling link '{childPrefix}'");
                    continue;
                }

                roots.Add(new RootMapping(Normalize(target), childPrefix));
                if (deferReparseChildren)
                {
                    deferredLinks!.Add(new DeferredLink(target, childPrefix, identityOnly));
                }
                else
                {
                    // Not a deferred (root-level) link -- an inline junction encountered mid-walk
                    // folds into whichever root/job is already in progress rather than getting
                    // its own ScanRootStat entry. Only root-level junctions (the important
                    // real-project fan-out point -- see this method's own doc comment) get
                    // separate accounting; this keeps rootCounters/slowDirs threading simple for
                    // the rare nested case without losing the root-level signal that matters.
                    Walk(
                        target,
                        childPrefix,
                        identityOnly,
                        config,
                        visitedReal,
                        entries,
                        roots,
                        warnings,
                        rootAlreadyClaimed: false,
                        deferReparseChildren: false,
                        deferReparseDescendants: false,
                        deferredLinks: null,
                        heartbeat,
                        knownMeta,
                        rootCounters,
                        slowDirs);
                }
            }
            else
            {
                Walk(
                    fullPath,
                    childPrefix,
                    identityOnly,
                    config,
                    visitedReal,
                    entries,
                    roots,
                    warnings,
                    rootAlreadyClaimed: false,
                    deferReparseChildren: deferReparseDescendants,
                    deferReparseDescendants,
                    deferredLinks,
                    heartbeat,
                    knownMeta,
                    rootCounters,
                    slowDirs);
            }
        }

        foreach (var (name, fullPath, mtime, length) in fileCandidates)
        {
            var childPrefix = $"{projectPrefix}/{name}";
            var extension = Path.GetExtension(name);
            var hasMeta = metaByOwner.TryGetValue(name, out var meta);
            var kind = Classify(childPrefix, extension, hasMeta);
            if (kind is null)
            {
                continue;
            }

            string? guid = null;
            long? metaMtime = null;
            if (hasMeta)
            {
                metaMtime = meta.Mtime;
                guid = ResolveMetaGuid(childPrefix, meta.FullPath, meta.Mtime, knownMeta, warnings);
            }

            entries.Add(new ScannedFileEntry(childPrefix, guid, kind.Value, mtime, length, metaMtime, identityOnly));
        }

        foreach (var (ownerName, meta) in metaByOwner)
        {
            if (!rawNames.Contains(ownerName))
            {
                warnings.Add($"warning: orphaned meta '{projectPrefix}/{Path.GetFileName(meta.FullPath)}' (owner not found on disk)");
            }
        }
    }

    /// <summary>
    /// Classifies and stats exactly one project-relative path (an owner path — content file or
    /// folder, never a `.meta` path itself), without walking any directory. Used by the
    /// watcher's targeted per-batch updates so a debounced
    /// batch of a handful of changed paths doesn't require rescanning the whole project. Mirrors
    /// <see cref="Walk"/>'s per-file/per-folder rules exactly (same hidden/exclude checks, same
    /// Classify/TryReadMetaGuid helpers) so the two paths can never classify the same file
    /// differently. Returns null if the path is gone, hidden/excluded, or not a recognized
    /// reference source/folder-with-meta — the caller's diff treats a null result for a
    /// previously-known path as a removal.
    /// </summary>
    public ScannedFileEntry? ScanSingleFile(string projectRoot, string projectRelativePath, UnBrambleConfig config, List<string> warnings)
    {
        var segments = projectRelativePath.Split('/');
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var segment = segments[i];
            if (HiddenAssetRules.IsHidden(segment, isDirectory: true) ||
                config.ExcludeDirs.Contains(segment, StringComparer.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        var leafName = segments[^1];
        if (leafName.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
        {
            // Callers always pass an owner path (asset/folder), never a .meta path directly —
            // an asset and its .meta are always reparsed as one unit via the owner path.
            return null;
        }

        var fullPath = Path.Combine(projectRoot, projectRelativePath.Replace('/', Path.DirectorySeparatorChar));

        bool isDirectory;
        try
        {
            if (Directory.Exists(fullPath))
            {
                isDirectory = true;
            }
            else if (File.Exists(fullPath))
            {
                isDirectory = false;
            }
            else
            {
                return null; // Gone -- the caller's diff treats this as a removal.
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"warning: could not stat '{projectRelativePath}': {ex.Message}");
            return null;
        }

        if (HiddenAssetRules.IsHidden(leafName, isDirectory))
        {
            return null;
        }

        if (isDirectory && config.ExcludeDirs.Contains(leafName, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var metaPath = fullPath + ".meta";
        var hasMeta = File.Exists(metaPath);
        string? guid = null;
        long? metaMtime = null;
        if (hasMeta)
        {
            guid = TryReadMetaGuid(metaPath, projectRelativePath, warnings);
            metaMtime = new FileInfo(metaPath).LastWriteTimeUtc.Ticks;
        }

        if (isDirectory)
        {
            // Folders are only graph nodes when meta'd, same rule as Walk().
            if (!hasMeta)
            {
                return null;
            }

            var dirInfo = new DirectoryInfo(fullPath);
            return new ScannedFileEntry(projectRelativePath, guid, FileKind.Folder, dirInfo.LastWriteTimeUtc.Ticks, 0, metaMtime, IdentityOnly: false);
        }

        var extension = Path.GetExtension(leafName);
        var kind = Classify(projectRelativePath, extension, hasMeta);
        if (kind is null)
        {
            return null;
        }

        var fileInfo = new FileInfo(fullPath);
        return new ScannedFileEntry(projectRelativePath, guid, kind.Value, fileInfo.LastWriteTimeUtc.Ticks, fileInfo.Length, metaMtime, IdentityOnly: false);
    }

    private static FileKind? Classify(string projectPath, string extension, bool hasMeta)
    {
        if (string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase))
        {
            return FileKind.Script;
        }

        if (string.Equals(extension, ".asset", StringComparison.OrdinalIgnoreCase) &&
            projectPath.StartsWith("ProjectSettings/", StringComparison.OrdinalIgnoreCase))
        {
            return FileKind.Settings;
        }

        if (ReferenceSourceExtensions.Contains(extension))
        {
            return FileKind.Asset;
        }

        if (hasMeta)
        {
            return FileKind.Asset;
        }

        return null;
    }

    /// <summary>
    /// The mtime-gate on top of <see cref="TryReadMetaGuid"/>.
    /// A meta's content is re-read only when its path is unknown to the store's snapshot or its
    /// mtime no longer matches what's recorded there -- the exact same trust boundary the rest
    /// of the freshness design already stands on (a file's guid content cannot have changed
    /// without its mtime moving). The "meta has no guid line" warning case is safe under this
    /// gate by construction: a recorded null guid is only ever reused when the meta's mtime
    /// genuinely still matches, i.e. the meta content provably has not changed since that null
    /// was last (correctly) derived and warned about.
    /// </summary>
    private static string? ResolveMetaGuid(
        string childProjectPath,
        string metaFullPath,
        long metaMtimeTicks,
        IReadOnlyDictionary<string, MetaSnapshot>? knownMeta,
        List<string> warnings)
    {
        if (knownMeta is not null &&
            knownMeta.TryGetValue(childProjectPath, out var known) &&
            known.MetaMtime == metaMtimeTicks)
        {
            return known.Guid;
        }

        return TryReadMetaGuid(metaFullPath, childProjectPath, warnings);
    }

    private static string? TryReadMetaGuid(string metaFullPath, string ownerProjectPath, List<string> warnings)
    {
        string content;
        try
        {
            content = File.ReadAllText(metaFullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"warning: could not read meta for '{ownerProjectPath}': {ex.Message}");
            return null;
        }

        var match = RegexPatterns.MetaGuid().Match(content);
        if (!match.Success)
        {
            warnings.Add($"warning: meta for '{ownerProjectPath}' has no guid line");
            return null;
        }

        return match.Groups[1].Value.ToLowerInvariant();
    }

    // internal (not private): RootLinkTargets (Windows-Defender-exclusion setup) reuses
    // these two exactly -- the same root-level reparse-point detection/resolution rules, so a
    // standalone "what junction targets exist" query (callable before any sweep) can never
    // classify a link differently than a real Scan() would.
    internal static bool IsReparsePoint(string path) =>
        File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    internal static string? TryResolveLinkTarget(string linkPath)
    {
        try
        {
            var target = Directory.ResolveLinkTarget(linkPath, returnFinalTarget: true);
            if (target is null || !Directory.Exists(target.FullName))
            {
                return null;
            }

            return target.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Normalize(string realPath) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(realPath));

    private static int CompareStable(string left, string right)
    {
        var comparison = StringComparer.OrdinalIgnoreCase.Compare(left, right);
        return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left, right);
    }
}
