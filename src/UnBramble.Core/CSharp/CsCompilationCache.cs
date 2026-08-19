using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace UnBramble.Core.CSharp;

/// <summary>
/// One Semantic-mode assembly unit's persistent, incrementally-updated Roslyn state, held by
/// <see cref="CsCompilationCache"/> for the lifetime of a `watch` process. A <see
/// cref="Microsoft.CodeAnalysis.Compilation"/> is immutable but cheaply DERIVED --
/// <c>ReplaceSyntaxTree</c> only re-parses the one changed file (unrelated trees are shared by
/// reference), and <c>ReplaceReference</c> lets a dependent pick up a dependency's new symbols by
/// swapping one reference object, with no re-parse of the dependent's own source. This record is
/// what makes those cheap derivations possible pass-to-pass: without <see cref="TreeByPath"/> and
/// <see cref="DepRefs"/>, a later pass would have no way to name "the exact old object" either API
/// requires as its first argument.
/// </summary>
/// <param name="Compilation">The current compilation for this unit -- either freshly built from
/// scratch, or derived from a prior pass's compilation via tree/reference swaps.</param>
/// <param name="FileIdByTree">Same shape as <see cref="CsProjectAnalyzer.BuildCompilation"/>'s own
/// output -- fed directly to <see cref="SemanticCsExtractor.Extract"/> when this unit is
/// (re)extracted.</param>
/// <param name="TreeByPath">The syntax tree currently backing each of this unit's script paths --
/// lets an incremental pass find "the old tree for this path" to hand to
/// <c>ReplaceSyntaxTree</c>/<c>RemoveSyntaxTrees</c> without re-deriving it.</param>
/// <param name="DepRefs">The exact <see cref="CompilationReference"/> instance currently embedded
/// in <see cref="Compilation"/>'s reference list for each in-project dependency this unit
/// references -- <c>ReplaceReference</c> requires the literal old object, not just the dependency
/// name.</param>
/// <param name="CsprojMtimeTicks">This unit's generated csproj mtime fingerprint as of when
/// <see cref="Compilation"/> was last (re)built -- reuses <see cref="CsModeDecision.CsprojMtimeTicks"/>,
/// the same fingerprint the skip-gate already keys on. A mismatch against the CURRENT mode
/// decision means defines/references may have changed, so cached syntax trees are no longer safe
/// to reuse for anything short of a full rebuild.</param>
/// <param name="UnitShapeHash">See <see cref="CsCompilationCache.ComputeUnitShapeHash"/> --
/// everything about this unit's compilation STRUCTURE other than its own `.cs` file membership
/// (which the incremental add/remove path already handles safely and correctly on its own).</param>
public sealed record CachedUnit(
    CSharpCompilation Compilation,
    Dictionary<SyntaxTree, long> FileIdByTree,
    Dictionary<string, SyntaxTree> TreeByPath,
    Dictionary<string, CompilationReference> DepRefs,
    long? CsprojMtimeTicks,
    string UnitShapeHash);

/// <summary>Result of <see cref="CsCompilationCache.ApplyIncrementalEdits"/>.</summary>
public sealed record IncrementalEditResult(
    CSharpCompilation Compilation,
    Dictionary<SyntaxTree, long> FileIdByTree,
    Dictionary<string, SyntaxTree> TreeByPath);

/// <summary>(path, mtime, size) key for <see cref="CsCompilationCache"/>'s persistent PE-reference
/// cache -- see that class's own doc comment for why path alone is not a safe cache key here.</summary>
internal readonly record struct PeReferenceCacheKey(string Path, long MtimeTicks, long Length);

/// <summary>
/// Watch-only persistent Roslyn compilation cache (see UnBrambleEngine's own doc comments on
/// <c>RunCsAnalysis</c>/<c>ProcessUnit</c> for the surrounding wiring). Holds, per Semantic-mode
/// assembly unit, for the lifetime of one `watch` process: the unit's current
/// <see cref="CSharpCompilation"/> plus enough bookkeeping (<see cref="CachedUnit.TreeByPath"/>,
/// <see cref="CachedUnit.DepRefs"/>) to cheaply DERIVE the next pass's compilation instead of
/// rebuilding it from scratch -- the fix for `watch` reacting to a single `.cs` save by rebuilding
/// every semantic-mode assembly from nothing, every time.
///
/// Absent from every one-shot CLI verb (init/index/stats/who-uses/etc.) and from the pull-path
/// stat-sweep every query already performs -- <see cref="UnBrambleEngine"/> only ever constructs one
/// of these when the `watch` command explicitly opts in
/// (<see cref="UnBrambleEngine.EnableWatchCompilationCache"/>), so RunCsAnalysis's existing
/// from-scratch <see cref="CsProjectAnalyzer.BuildCompilation"/> path stays byte-for-byte
/// unreachable-and-therefore-unchanged for every other caller.
///
/// Thread-safety: <see cref="TryGetUnit"/>/<see cref="SetUnit"/> and
/// <see cref="GetOrLoadMetadataReference"/> can all be called concurrently -- a dependency level's
/// units run via <c>Parallel.ForEach</c> in <c>UnBrambleEngine.RunCsAnalysis</c>, each writing its
/// own unit's entry while (by construction of <c>GroupIntoDependencyLevels</c>) only ever READING
/// entries for units in strictly lower, already-completed levels.
/// </summary>
public sealed class CsCompilationCache
{
    private readonly ConcurrentDictionary<string, CachedUnit> _units = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<PeReferenceCacheKey, PortableExecutableReference> _peCache = new();

    public CachedUnit? TryGetUnit(string unitName) =>
        _units.TryGetValue(unitName, out var unit) ? unit : null;

    public void SetUnit(string unitName, CachedUnit unit) => _units[unitName] = unit;

    /// <summary>
    /// Persistent metadata-reference cache keyed by (path, mtime, size) rather than path alone --
    /// Unity can rewrite `Library/ScriptAssemblies/*.dll` (a dependency's build output) without
    /// the REFERENCING assembly's own generated csproj mtime changing at all, so a cache that
    /// survives across watch passes keyed on path alone would risk silently pinning a stale DLL
    /// forever once loaded once. Keying on the file's own current mtime+size means a rewritten
    /// DLL is detected and reloaded on the very next pass that touches it, while an unchanged DLL
    /// (the overwhelming common case: engine modules, most packages) is loaded from disk exactly
    /// once for the life of the watch process instead of once per save.
    /// </summary>
    public PortableExecutableReference GetOrLoadMetadataReference(string path)
    {
        var info = new FileInfo(path);
        var key = new PeReferenceCacheKey(path, info.LastWriteTimeUtc.Ticks, info.Length);
        return _peCache.GetOrAdd(key, static k => MetadataReference.CreateFromFile(k.Path));
    }

    /// <summary>
    /// Fingerprints everything about a unit's compilation STRUCTURE that the cheap incremental
    /// path (<see cref="ApplyIncrementalEdits"/>/<see cref="ApplyDependencyReferenceSwaps"/>)
    /// canNOT safely absorb, so a change here forces the safe-default full rebuild instead:
    /// the resolved in-project reference names (an asmdef reference add/remove changes which
    /// dependency compilations this unit's own compilation must carry), whether engine references
    /// are suppressed, the owning asmdef's identity, and Mode A's own defines/metadata-reference
    /// paths (already gated separately by <see cref="CachedUnit.CsprojMtimeTicks"/>, included here
    /// too as a second, cheap belt-and-suspenders check).
    ///
    /// Deliberately does NOT include the unit's own script path list: which `.cs` files currently
    /// belong to this unit is exactly what <see cref="ApplyIncrementalEdits"/>'s own add/remove
    /// diffing (`unit.Scripts` vs. the previous pass's <see cref="CachedUnit.TreeByPath"/>) already
    /// detects and handles precisely as correctly as a full rebuild would -- via Roslyn's own
    /// <c>AddSyntaxTrees</c>/<c>RemoveSyntaxTrees</c>, not a guess. Folding script membership into
    /// this hash too would force a full rebuild on every file add/remove, defeating exactly the
    /// optimization this cache exists for, with no correctness upside.
    /// </summary>
    public static string ComputeUnitShapeHash(AssemblyUnit unit, CsModeDecision modeDecision) => string.Join(
        '|',
        unit.Name,
        unit.AsmdefPath ?? "",
        unit.NoEngineReferences,
        string.Join(',', unit.References),
        string.Join(',', modeDecision.Defines),
        string.Join(',', modeDecision.MetadataReferencePaths));

    /// <summary>
    /// Applies this unit's own dirty `.cs` files (added, changed, or removed since
    /// <paramref name="previous"/> was built) to <paramref name="previous"/>'s compilation via
    /// <c>ReplaceSyntaxTree</c> (changed)/<c>AddSyntaxTrees</c> (added)/<c>RemoveSyntaxTrees</c>
    /// (removed) -- every file NOT in <paramref name="dirtyPaths"/> and still present in
    /// <paramref name="unit"/>'s script list is left completely untouched, its parsed
    /// <see cref="SyntaxTree"/> carried over by reference from <paramref name="previous"/>. Only
    /// called once the caller has already established the cheap path is safe (cache hit, csproj
    /// mtime and shape hash both still match) -- this method itself does no such gating.
    /// </summary>
    public static IncrementalEditResult ApplyIncrementalEdits(
        CachedUnit previous,
        AssemblyUnit unit,
        Func<string, string> toFullPath,
        CSharpParseOptions parseOptions,
        IReadOnlyCollection<string> dirtyPaths)
    {
        var compilation = previous.Compilation;
        var fileIdByTree = new Dictionary<SyntaxTree, long>(previous.FileIdByTree);
        var treeByPath = new Dictionary<string, SyntaxTree>(previous.TreeByPath, StringComparer.OrdinalIgnoreCase);
        var dirtySet = dirtyPaths as HashSet<string> ?? new HashSet<string>(dirtyPaths, StringComparer.OrdinalIgnoreCase);
        var currentPaths = new HashSet<string>(unit.Scripts.Select(s => s.Path), StringComparer.OrdinalIgnoreCase);

        // Removed: tracked by the previous pass, no longer in this unit's current script list.
        foreach (var oldPath in treeByPath.Keys.ToList())
        {
            if (currentPaths.Contains(oldPath))
            {
                continue;
            }

            var oldTree = treeByPath[oldPath];
            compilation = compilation.RemoveSyntaxTrees(oldTree);
            fileIdByTree.Remove(oldTree);
            treeByPath.Remove(oldPath);
        }

        // Added/changed: anything new to this unit's script list, or present in both and named
        // in dirtyPaths. A file that's neither new nor dirty is left completely alone -- its old
        // SyntaxTree object stays exactly where it was in `compilation`/`treeByPath`.
        foreach (var (fileId, path) in unit.Scripts)
        {
            var isNew = !treeByPath.ContainsKey(path);
            if (!isNew && !dirtySet.Contains(path))
            {
                continue;
            }

            string text;
            try
            {
                text = File.ReadAllText(toFullPath(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable: same degradation BuildCompilation's own ParseOne applies (skip it).
                // If a tree already existed for this path (it was readable last pass, not now),
                // drop it too rather than leaving a stale tree keyed to a file that's gone dark.
                if (treeByPath.TryGetValue(path, out var unreadableOldTree))
                {
                    compilation = compilation.RemoveSyntaxTrees(unreadableOldTree);
                    fileIdByTree.Remove(unreadableOldTree);
                    treeByPath.Remove(path);
                }

                continue;
            }

            var newTree = CSharpSyntaxTree.ParseText(text, parseOptions, path: toFullPath(path));

            if (treeByPath.TryGetValue(path, out var oldTree))
            {
                compilation = compilation.ReplaceSyntaxTree(oldTree, newTree);
                fileIdByTree.Remove(oldTree);
            }
            else
            {
                compilation = compilation.AddSyntaxTrees(newTree);
            }

            fileIdByTree[newTree] = fileId;
            treeByPath[path] = newTree;
        }

        return new IncrementalEditResult(compilation, fileIdByTree, treeByPath);
    }

    /// <summary>
    /// Picks up a changed dependency's new symbols by swapping the ONE
    /// <see cref="CompilationReference"/> object that stood for it (found via
    /// <paramref name="previousDepRefs"/>, the exact instance <c>ApplyDependencyReferenceSwaps</c>
    /// or the last full build recorded) -- no re-parse of this unit's own source, only lazy
    /// re-binding, inside <paramref name="compilation"/>, of whatever actually references the
    /// dependency's changed surface. A dependency this unit references for the first time this
    /// pass (no prior entry in <paramref name="previousDepRefs"/>) is added via
    /// <c>AddReferences</c> instead -- shouldn't normally occur outside a shape-hash-triggered
    /// full rebuild, but handled rather than assumed impossible.
    /// </summary>
    public static (CSharpCompilation Compilation, Dictionary<string, CompilationReference> DepRefs) ApplyDependencyReferenceSwaps(
        CSharpCompilation compilation,
        IReadOnlyDictionary<string, CompilationReference> previousDepRefs,
        IReadOnlyCollection<string> changedDependencyNames,
        IReadOnlyDictionary<string, CSharpCompilation> currentDependencyCompilations)
    {
        var depRefs = new Dictionary<string, CompilationReference>(previousDepRefs, StringComparer.Ordinal);

        foreach (var depName in changedDependencyNames)
        {
            if (!currentDependencyCompilations.TryGetValue(depName, out var depCompilation))
            {
                continue;
            }

            var newReference = depCompilation.ToMetadataReference();
            compilation = depRefs.TryGetValue(depName, out var oldReference)
                ? compilation.ReplaceReference(oldReference, newReference)
                : compilation.AddReferences(newReference);

            depRefs[depName] = newReference;
        }

        return (compilation, depRefs);
    }
}
