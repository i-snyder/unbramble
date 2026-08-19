using System.Collections.Concurrent;

namespace UnBramble.Core.CSharp;

/// <summary>
/// Session-scoped cached C# project model: caches, for the lifetime of one
/// <see cref="UnBramble.Core.UnBrambleEngine"/> instance, the two
/// pieces of per-pass work in <c>RunCsAnalysis</c> that used to be re-derived from scratch on
/// EVERY pass regardless of what changed — (a) the full file-inventory load +
/// <see cref="AssemblyUnitDiscovery.Discover"/> (which re-reads every asmdef/asmref from disk and
/// iterates every file row: O(project), a real cost on a large real project), and (b)
/// <see cref="CsModeSelector.Determine"/> per unit (a full XML parse of each unit's generated
/// csproj, including its complete Compile/Reference item lists — the single largest untimed
/// per-pass cost on the real target project). With this cache, a steady-state watch push
/// (one changed `.cs` file) pays one PRAGMA read, one stat per unit, and nothing else here.
///
/// One instance lives unconditionally on the engine (not watch-only): both
/// <c>RunIndex</c> (the pull path) and <c>ApplyTargetedUpdate</c> (the watcher push path) flow
/// through the same <c>RunCsAnalysis</c> on the same engine and therefore consult this SAME
/// provider — preserving the existing invariant that the two paths can never diverge. A one-shot
/// CLI verb constructs a fresh engine, runs one pass, and exits, so for every caller except a
/// long-lived `watch` (or a test that deliberately reuses one engine across passes) this cache is
/// populated once and never consulted again — behavior is unchanged by construction.
///
/// Invalidation ("never wrong, only sometimes slower" — every doubt resolves to rebuilding):
/// <list type="bullet">
/// <item><b>Unit discovery</b> is reused only when the pass's own <see cref="Store.SweepDiff"/>
/// proves membership and file ids are stable: zero added rows, zero removed rows, zero
/// guid-change rebuilds (<see cref="Store.SweepDiff.AnyGuidRebuild"/> — a rebuild keeps the path
/// but re-inserts the row under a NEW file id, which would silently poison every cached
/// <see cref="AssemblyUnit"/> script/asmdef id), and no dirty `.asmdef`/`.asmref` path (their
/// CONTENT defines the unit graph). A pure content edit to an existing `.cs` file — the
/// steady-state watch case — changes none of these.</item>
/// <item><b>Cross-process writes</b>: the discovery cache additionally records SQLite's
/// <c>PRAGMA data_version</c> as of when it was built. That value moves if and only if some OTHER
/// connection committed to the DB (our own engine's writes never move it for us), so a concurrent
/// `unbramble index` from another process — which can insert/delete file rows behind this engine's
/// back without any diff this engine ever sees — forces a full re-discovery on the next pass
/// instead of trusting stale ids.</item>
/// <item><b>Mode decisions</b> are keyed per assembly on the generated csproj's current mtime
/// ticks (or its absence), checked as a plain stat each pass — the same fingerprint basis
/// the dirty-path skip gate and <see cref="CsCompilationCache"/>'s rebuild gate already use, so
/// all three share one blind-spot family (an mtime-identical csproj rewrite) rather than adding
/// a new one.
/// A referenced DLL appearing on disk without its csproj mtime moving is picked up on the next
/// csproj regeneration rather than immediately — same honest degradation as
/// <c>BuildCompilation</c>'s existing stale-HintPath skip.</item>
/// </list>
/// </summary>
public sealed class CsSessionModelCache
{
    private IReadOnlyList<AssemblyUnit>? _units;
    private IReadOnlyList<string>? _discoveryWarnings;
    private long _unitsDataVersion;

    // Written from Parallel.ForEach workers (units within one dependency level run
    // concurrently), hence concurrent. Entries for assemblies that stop existing linger
    // harmlessly (bounded by the historical unit-name set of one process lifetime).
    private readonly ConcurrentDictionary<string, CsModeDecision> _modeByName = new(StringComparer.Ordinal);

    /// <summary>True when a dirty path's CONTENT can change the unit graph itself (which units
    /// exist, their references, their names) — `.asmdef`/`.asmref`. A dirty `.cs` is deliberately
    /// NOT structure-relevant: an in-place content edit keeps its path, its file id, and therefore
    /// its unit assignment (adds/removes/renames/guid-rebuilds are separately covered by the diff
    /// counts and <see cref="Store.SweepDiff.AnyGuidRebuild"/>).</summary>
    public static bool IsUnitStructurePath(string path) =>
        path.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".asmref", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the cached unit list when it is provably still valid (see class doc comment for
    /// the exact conditions); false means the caller must re-load the file inventory, re-run
    /// <see cref="AssemblyUnitDiscovery.Discover"/>, and store the result via
    /// <see cref="SetUnits"/>.
    /// </summary>
    public bool TryGetUnits(
        long currentDataVersion,
        bool structureDirty,
        out IReadOnlyList<AssemblyUnit> units,
        out IReadOnlyList<string> warnings)
    {
        if (_units is null || structureDirty || currentDataVersion != _unitsDataVersion)
        {
            units = [];
            warnings = [];
            return false;
        }

        units = _units;
        warnings = _discoveryWarnings!;
        return true;
    }

    /// <param name="dataVersion">The store's <c>PRAGMA data_version</c> captured BEFORE the file
    /// inventory backing <paramref name="units"/> was loaded — capturing before (not after) means
    /// a foreign commit racing the load at worst forces one unnecessary re-discovery next pass,
    /// never a reuse of ids the load didn't actually see.</param>
    public void SetUnits(IReadOnlyList<AssemblyUnit> units, IReadOnlyList<string> warnings, long dataVersion)
    {
        _units = units;
        _discoveryWarnings = warnings;
        _unitsDataVersion = dataVersion;
    }

    /// <summary>
    /// Mtime-gated wrapper around <see cref="CsModeSelector.Determine"/>: one
    /// <see cref="File.Exists(string)"/> + <see cref="File.GetLastWriteTimeUtc(string)"/> stat per
    /// call; the full csproj XML parse only runs when the csproj's mtime (or existence) no longer
    /// matches what the cached decision was computed from. The cached decision's OWN
    /// <see cref="CsModeDecision.CsprojMtimeTicks"/> (captured inside Determine, not our stat) is
    /// what's compared, so a csproj rewritten between our stat and Determine's read self-corrects
    /// on the very next pass.
    /// </summary>
    public CsModeDecision GetModeDecision(string projectRoot, string assemblyName)
    {
        var csprojPath = Path.Combine(projectRoot, assemblyName + ".csproj");
        long? currentMtimeTicks = File.Exists(csprojPath) ? File.GetLastWriteTimeUtc(csprojPath).Ticks : null;

        if (_modeByName.TryGetValue(assemblyName, out var cached) && cached.CsprojMtimeTicks == currentMtimeTicks)
        {
            return cached;
        }

        var fresh = CsModeSelector.Determine(projectRoot, assemblyName);
        _modeByName[assemblyName] = fresh;
        return fresh;
    }
}
