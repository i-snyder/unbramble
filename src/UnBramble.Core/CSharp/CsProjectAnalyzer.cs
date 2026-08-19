using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace UnBramble.Core.CSharp;

/// <summary>
/// Builds a <see cref="CSharpCompilation"/> for one assembly unit in Mode A (semantic): sources
/// from the scanner's own `.cs` rows, defines/metadata references from
/// <see cref="CsModeDecision"/>, plus dependency assemblies referenced as **compilation
/// references**, not their stale DLLs, via
/// <paramref name="dependencyCompilations"/> — the caller builds dependencies first, in
/// topological order, and threads their compilations in here so cross-assembly symbol
/// resolution (the Game→Core call in the fixture) is real semantic resolution, not name
/// matching. Exposed as its own step (rather than folded into <see cref="SemanticCsExtractor"/>)
/// so tests can drive it directly with injected BCL metadata references, forcing Mode A without
/// a generated .csproj on disk.
/// </summary>
public static class CsProjectAnalyzer
{
    /// <summary>
    /// Below this many scripts, parsing them
    /// on a <see cref="Parallel.For(int, int, Action{int})"/> costs more in task-scheduling
    /// overhead than it saves — most Unity asmdef units are small (the "wide and shallow" real-
    /// project shape), so this keeps the common small-assembly case on the cheap sequential path
    /// while still parallelizing the rare huge one (predefined Assembly-CSharp, or any unit with
    /// thousands of scripts).
    /// </summary>
    private const int ParallelParseThreshold = 8;

    /// <param name="metadataReferenceCache">
    /// Path-keyed (case-insensitive), shared across every <see cref="BuildCompilation"/> call in
    /// one C# analysis pass: Unity-generated csprojs
    /// overwhelmingly reference the same few hundred DLLs (engine modules, `Library/
    /// ScriptAssemblies`, packages), and <see cref="MetadataReference"/> objects are immutable
    /// and explicitly documented as safe to share across compilations, so loading each distinct
    /// DLL once instead of once per referencing assembly is a pure win with no correctness
    /// surface. <see cref="ConcurrentDictionary{TKey,TValue}"/>: multiple units in the same
    /// dependency level can call <see cref="BuildCompilation"/> concurrently, all racing to
    /// populate this same cache -- <c>GetOrAdd</c> makes that race benign (worst case a DLL is
    /// loaded twice instead of once; never a torn read). Defaults to a fresh, call-local
    /// dictionary when omitted (tests that don't care about cross-call caching). Per-path
    /// missing-file degradation is unchanged: a path that fails to load is simply not cached and
    /// not added to the reference list.
    /// </param>
    public static (CSharpCompilation Compilation, Dictionary<SyntaxTree, long> FileIdByTree) BuildCompilation(
        AssemblyUnit unit,
        CsModeDecision modeDecision,
        IReadOnlyDictionary<string, CSharpCompilation> dependencyCompilations,
        Func<string, string> toFullPath,
        ConcurrentDictionary<string, PortableExecutableReference>? metadataReferenceCache = null)
    {
        metadataReferenceCache ??= new ConcurrentDictionary<string, PortableExecutableReference>(StringComparer.OrdinalIgnoreCase);

        PortableExecutableReference? Resolve(string referencePath)
        {
            try
            {
                // GetOrAdd's factory can legitimately run more than once under a race (two
                // threads missing the cache for the same path at once) -- whichever result
                // "wins" is stored and both callers get the same instance back; the only cost is
                // an occasional redundant load, never a correctness issue (MetadataReference
                // instances are immutable and interchangeable for the same file).
                return metadataReferenceCache.GetOrAdd(referencePath, static path => MetadataReference.CreateFromFile(path));
            }
            catch (IOException)
            {
                // Honest degradation, not a crash: a stale HintPath in the generated csproj
                // just means that one reference is missing from the compilation. Deliberately
                // NOT cached (as a failure) -- GetOrAdd never stores a thrown factory's result,
                // so a transient failure can't poison every later assembly's reference list for
                // the rest of this run.
                return null;
            }
        }

        var result = BuildCompilationCore(unit, modeDecision, dependencyCompilations, toFullPath, Resolve);
        return (result.Compilation, result.FileIdByTree);
    }

    /// <summary>
    /// Watch-only cache-aware variant (see <see cref="CsCompilationCache"/>): identical build
    /// logic to <see cref="BuildCompilation"/> -- same parsing, same reference resolution rules,
    /// same compilation options -- so a cache MISS/invalidation (the "do the expensive-but-
    /// correct thing" fallback) produces byte-for-byte the same <see cref="CSharpCompilation"/> a
    /// non-cached pass would. The only difference is the additional outputs
    /// (<see cref="CsCompilationBuildResult.TreeByPath"/>, <see cref="CsCompilationBuildResult.DepRefs"/>)
    /// a later incremental pass needs to target a <see cref="Compilation.ReplaceSyntaxTree"/> /
    /// <see cref="Compilation.ReplaceReference"/> call at the exact right object, plus routing
    /// metadata-reference loads through <paramref name="cache"/>'s persistent (path, mtime, size)
    /// cache instead of the pass-local path-only one -- Unity can rewrite
    /// `Library/ScriptAssemblies/*.dll` without the referencing csproj's own mtime moving, so a
    /// path-only cache surviving across watch passes would risk silently pinning a stale DLL.
    /// </summary>
    public static CsCompilationBuildResult BuildCompilationForCache(
        AssemblyUnit unit,
        CsModeDecision modeDecision,
        IReadOnlyDictionary<string, CSharpCompilation> dependencyCompilations,
        Func<string, string> toFullPath,
        CsCompilationCache cache)
    {
        PortableExecutableReference? Resolve(string referencePath)
        {
            try
            {
                return cache.GetOrLoadMetadataReference(referencePath);
            }
            catch (IOException)
            {
                return null;
            }
        }

        return BuildCompilationCore(unit, modeDecision, dependencyCompilations, toFullPath, Resolve);
    }

    private static CsCompilationBuildResult BuildCompilationCore(
        AssemblyUnit unit,
        CsModeDecision modeDecision,
        IReadOnlyDictionary<string, CSharpCompilation> dependencyCompilations,
        Func<string, string> toFullPath,
        Func<string, PortableExecutableReference?> resolveReference)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: modeDecision.Defines);

        // CSharpSyntaxTree.ParseText is thread-safe and CPU-bound -- parse every script in
        // this unit in parallel (bounded, above the small-unit threshold), writing into an
        // index-aligned array so the resulting `trees` list keeps the EXACT order `unit.Scripts`
        // was already enumerated in. That's not cosmetic: SemanticCsExtractor.Extract iterates
        // `compilation.SyntaxTrees` in this same order, and downstream `symbols`/`symbol_refs`
        // insertion order (docIdToRowId's "first declaration wins" partial-class-collision rule,
        // UnBrambleStore.ReplaceAssemblyAnalyses) depends on it staying deterministic regardless
        // of parse scheduling. A file that fails to read is skipped (its slot stays null and is
        // filtered out below).
        var scripts = unit.Scripts;
        var parsed = new (SyntaxTree Tree, long FileId)?[scripts.Count];

        void ParseOne(int i)
        {
            var (fileId, path) = scripts[i];
            string text;
            try
            {
                text = File.ReadAllText(toFullPath(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            var tree = CSharpSyntaxTree.ParseText(text, parseOptions, path: toFullPath(path));
            parsed[i] = (tree, fileId);
        }

        if (scripts.Count >= ParallelParseThreshold)
        {
            Parallel.For(0, scripts.Count, ParseOne);
        }
        else
        {
            for (var i = 0; i < scripts.Count; i++)
            {
                ParseOne(i);
            }
        }

        var fileIdByTree = new Dictionary<SyntaxTree, long>();
        var treeByPath = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
        var trees = new List<SyntaxTree>(scripts.Count);
        foreach (var entry in parsed)
        {
            if (entry is not { } e)
            {
                continue;
            }

            trees.Add(e.Tree);
            fileIdByTree[e.Tree] = e.FileId;
        }

        // Second pass over scripts (not `parsed`, so a path is only ever recorded against the
        // one tree that actually parsed for it) -- kept separate from the loop above so
        // BuildCompilation's own fileIdByTree/trees construction stays untouched.
        for (var i = 0; i < scripts.Count; i++)
        {
            if (parsed[i] is { } e)
            {
                treeByPath[scripts[i].Path] = e.Tree;
            }
        }

        var references = new List<MetadataReference>();
        foreach (var referencePath in modeDecision.MetadataReferencePaths)
        {
            var reference = resolveReference(referencePath);
            if (reference is null)
            {
                // Honest degradation, not a crash: a stale HintPath in the generated csproj
                // just means that one reference is missing from the compilation.
                continue;
            }

            references.Add(reference);
        }

        var depRefs = new Dictionary<string, CompilationReference>(StringComparer.Ordinal);
        foreach (var dependencyName in unit.References)
        {
            if (dependencyCompilations.TryGetValue(dependencyName, out var dependencyCompilation))
            {
                var compilationReference = dependencyCompilation.ToMetadataReference();
                references.Add(compilationReference);
                depRefs[dependencyName] = compilationReference;
            }
        }

        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Disable);
        var compilation = CSharpCompilation.Create(unit.Name, trees, references, options);
        return new CsCompilationBuildResult(compilation, fileIdByTree, treeByPath, depRefs);
    }
}

/// <summary>
/// Additive-output shape of <see cref="CsProjectAnalyzer.BuildCompilationForCache"/> (internal,
/// watch-cache-only) -- everything <see cref="CsProjectAnalyzer.BuildCompilation"/> already
/// returns, plus what a later incremental pass needs to target
/// <see cref="Microsoft.CodeAnalysis.Compilation.ReplaceSyntaxTree"/> /
/// <see cref="Microsoft.CodeAnalysis.Compilation.ReplaceReference"/> at the exact right object:
/// the syntax tree currently backing each script path, and the exact
/// <see cref="CompilationReference"/> instance used for each in-project dependency.
/// </summary>
public sealed record CsCompilationBuildResult(
    CSharpCompilation Compilation,
    Dictionary<SyntaxTree, long> FileIdByTree,
    Dictionary<string, SyntaxTree> TreeByPath,
    Dictionary<string, CompilationReference> DepRefs);
