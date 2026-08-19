namespace UnBramble.Core.Query;

/// <summary>
/// The fixed, machine-readable blind-spot enum: structural static-analysis limits the tool
/// states on every answer rather than hoping a consumer reads the docs. Additive-only.
///
/// `AddressablesUnconfirmed` remains reserved (no `who-uses`/`uses` caller passes it yet).
/// `CsprojStale` is wired: `UnBrambleEngine.CheckCsprojFreshness`'s extended-mtime check (the
/// same one `dead-candidates`' preflight gate uses) also feeds `who-uses`/`uses` — for
/// `who-uses`, stale csprojs only add the `csproj-stale` blind-spot flag (stale data can still
/// be shown there, just flagged; `dead-candidates` disqualifies instead, because wrong `#if`
/// defines silently prune real edges).
///
/// `DisabledRegionRefsPossible`: the wrong-defines channel surfaced as a per-answer flag rather
/// than only as `dead-candidates`' disabled-region SCREEN. A real project had a method called
/// from a live subclass AND from a second subclass wrapped in `#if UNITY_ANDROID ||
/// UNITY_IPHONE` -- under the desktop/editor analysis defines that second call site is
/// invisible to Roslyn, so `who-uses` silently omitted a real caller with no signal that
/// anything was missed. Unlike the two unconditional flags above, this one is
/// query-shape-conditional: it fires only when the query's resolved symbol's (or, for a plain
/// file-shaped query, the queried file's declared symbols') simple name collides with a
/// `name_hints` row of `kind='cs-disabled'` (identifier tokens harvested from inside
/// `#if`-disabled text spans) -- see
/// `UnBrambleStore.HasDisabledRegionNameHint`/`HasDisabledRegionNameHintForFile`.
/// </summary>
public static class BlindSpots
{
    public const string StringPathLoading = "string-path-loading";
    public const string Reflection = "reflection";
    public const string SyntacticAssembliesPresent = "syntactic-assemblies-present";
    public const string AddressablesUnconfirmed = "addressables-unconfirmed"; // reserved; no who-uses/uses trigger yet.
    public const string DepthTruncated = "depth-truncated";
    public const string CsprojStale = "csproj-stale";
    public const string DisabledRegionRefsPossible = "disabled-region-refs-possible";

    /// <summary>
    /// Computes the blind spots that apply to a who-uses/uses answer. `string-path-loading` and
    /// `reflection` are unconditional: any static-analysis edge set can miss a
    /// `Resources.Load("Foo" + var)` or reflection-driven reference regardless of what this
    /// particular query touched, so they are always present, not query-shape-conditional — an
    /// answer that could be wrong for a reason the tool knows about says so on every answer, not
    /// just in docs. `syntactic-assemblies-present` is project-wide (any assembly
    /// in syntactic mode makes the whole project's cs edges structurally blinder — not
    /// just the assembly containing the query target, since a transitive walk can cross into any
    /// assembly). `depth-truncated` is per-answer (only for transitive queries that hit the cap).
    /// `csprojStale` is likewise project-wide (any Mode-A assembly's generated csproj stale
    /// relative to the extended mtime set taints the whole project's cs edges the same way a
    /// syntactic assembly does — see `UnBrambleEngine.CheckCsprojFreshness`, the shared source of
    /// truth this flag and the `dead-candidates` preflight gate both read).
    /// `disabledRegionNameHint` is per-answer and precise: true only when the caller already
    /// determined the queried symbol's (or file's) name collides with a disabled-region
    /// identifier — see the class doc comment.
    /// </summary>
    public static IReadOnlyList<string> ForQuery(
        bool anySyntacticAssembly, bool truncated, bool csprojStale, bool disabledRegionNameHint = false)
    {
        var result = new List<string> { StringPathLoading, Reflection };
        if (anySyntacticAssembly)
        {
            result.Add(SyntacticAssembliesPresent);
        }

        if (truncated)
        {
            result.Add(DepthTruncated);
        }

        if (csprojStale)
        {
            result.Add(CsprojStale);
        }

        if (disabledRegionNameHint)
        {
            result.Add(DisabledRegionRefsPossible);
        }

        return result;
    }
}
