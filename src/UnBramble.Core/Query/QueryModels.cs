namespace UnBramble.Core.Query;

/// <summary>
/// One dependency edge as returned to a CLI verb: SourcePath is always a real, resolvable
/// file (source_file_id is NOT NULL by schema); TargetPath is null when the edge is
/// unresolved (broken/external) or a builtin. TargetKey is the raw guid/path key and is
/// always present, even when unresolved — that's the whole point of surfacing it.
/// </summary>
public sealed record EdgeResult(
    string SourcePath,
    string? TargetPath,
    string TargetKey,
    int Line,
    string Kind,
    int Depth,
    bool Resolved,
    bool Builtin,
    int? ClassId,
    string? GameObject,
    string? MethodName,
    string? Via,
    // The raw C# analysis mode ('semantic'|'syntactic'), populated only for
    // Kind == "cs" — an input to EdgeConfidence.Derive, not itself the presentation label.
    string? Confidence = null,
    string? TargetSymbol = null,
    string? SourceSymbol = null,
    string? RefKind = null,
    // The derived proven/advisory/speculative presentation label, populated for every kind
    // (not just cs). Computed post-retrieval by EdgeConfidence/EdgeConfidenceChain — never
    // stored, never read from the DB directly. Null exactly when the edge is unresolved and
    // not builtin (the unresolved bucket is its own honest state, not a confidence tier).
    string? ConfidenceLabel = null,
    // True for a matched (proven or advisory) UnityEvent-bound method edge — "wired in
    // serialized data" rather than "called from code" (the resharper-unity explicit/implicit
    // split). A property of the matched event edge itself (provenance), not yet fed into any
    // liveness fixed point. False for every other edge kind.
    bool Implicit = false,
    // Best-effort dotted serialized-field path of the referencing line ("m_Settings.
    // m_VolumeProfile", "m_Materials[2]") — WHICH field holds the reference, not just which
    // line. Guid-kind YAML edges (and event edges, where it names the owning UnityEvent field)
    // only; null elsewhere. Display metadata: never queried, joined, or fed into confidence.
    string? PropertyPath = null,
    // Whether the SOURCE file is forward-reachable from the liveness roots via the asset/cs
    // graph (UnBrambleEngine.ComputeBuildReachablePaths — screen-free, ungated). True is a
    // positive proven claim; false means "no chain found", NEVER "unreachable" (missing cs
    // edges under syntactic assemblies can only under-report). Populated for who-uses answers
    // only; null when not computed.
    bool? BuildReachable = null);

/// <summary>
/// A query verb's resolved target. FileId is null when the input is a bare guid that
/// resolves to no file row — still a valid, answerable target (direct refs by literal guid),
/// just one that can't be transitively walked (no file id to walk from).
/// </summary>
public sealed record QueryTarget(long? FileId, string? Path, string? Guid);

/// <summary>Outcome of resolving a who-uses/uses target argument. Target is null when not found or ambiguous.</summary>
public sealed record TargetResolution(QueryTarget? Target, IReadOnlyList<Model.ResolveMatch> Candidates);

/// <summary>One unresolved serialized reference. Owner fields are best-effort display metadata:
/// they classify a finding without making the caller reopen Unity YAML, but never affect whether
/// the edge is considered unresolved.</summary>
public sealed record UnresolvedRefEntry(
    string SourcePath,
    string Kind,
    string TargetKey,
    int Line,
    string? Context,
    int? ClassId = null,
    string? GameObject = null,
    string? PropertyPath = null,
    string? Component = null,
    string? ComponentScriptGuid = null,
    bool IsScriptReference = false,
    bool IsPrefabOverride = false,
    string? PrefabSource = null,
    bool? BuildReachable = null);

public sealed record EdgeStats(
    int GuidTotal, int GuidUnresolved, int GuidBuiltin,
    int PathTotal, int PathUnresolved)
{
    public int TotalUnresolved => GuidUnresolved + PathUnresolved;
}

/// <summary>One syntactic-mode assembly named in a <see cref="SyntacticAssemblySummary"/> sample —
/// Reason is a <c>CsModeReasons</c> value (null if unknown, e.g. a pre-v6 DB row).
/// <see cref="IsPackageSourced"/> (see <see cref="UnBramble.Core.Scanning.Scanner.IsPackageSourcedPath"/>)
/// is true when the asmdef lives under `Packages/`/`LocalPackages/`, false for a plain
/// project script or a predefined assembly (no asmdef at all) — the CLI uses it to swap in the
/// package-specific remediation hint instead of the generic "open Unity once" one, since Unity's
/// csproj auto-regeneration doesn't cover packages by default.
///
/// <see cref="NeverCompiledByUnity"/> and <see cref="ExternalReferencerCount"/> are only ever
/// populated for a package-sourced assembly (null otherwise — not worth the extra query for a
/// plain project script, which Unity compiles regardless of whether its IDE csproj exists).
/// Found live: a package dropped under `LocalPackages/` but never added to
/// `Packages/manifest.json` is invisible to Unity's Package Manager, so it's never compiled
/// (<see cref="NeverCompiledByUnity"/> true) no matter how many csproj-generation checkboxes are
/// checked — a completely different failure mode than "just needs regenerating," which the
/// generic package hint alone doesn't distinguish. <see cref="ExternalReferencerCount"/> (guid/
/// path/C#-symbol edges from OUTSIDE the package into it — see
/// <see cref="UnBramble.Core.Store.UnBrambleStore.CountExternalReferencers"/>) then tells apart
/// "looks orphaned" (0) from "still referenced, so this is a BROKEN dependency, not dead code"
/// (&gt;0) — only meaningful once <see cref="NeverCompiledByUnity"/> is true, so it's left null
/// otherwise rather than spending the extra query on the common "just needs a fresh csproj"
/// case.</summary>
public sealed record SyntacticAssemblyDetail(
    string Name,
    string? Reason,
    bool IsPackageSourced = false,
    bool? NeverCompiledByUnity = null,
    int? ExternalReferencerCount = null);

/// <summary>Per-answer attribution for the `syntactic-assemblies-present` blind spot: names WHICH
/// assemblies (capped at a handful) and WHY, instead of leaving the caller to go find out.
/// <see cref="Total"/> is the real count; <see cref="Sample"/> may be shorter than
/// <see cref="Total"/> when capped.</summary>
public sealed record SyntacticAssemblySummary(int Total, IReadOnlyList<SyntacticAssemblyDetail> Sample);

/// <summary>Full answer for a who-uses/uses invocation, direct or transitive. Confidence and
/// BlindSpots are the answer-level fields of the unified output contract: Confidence is the
/// weakest per-edge ConfidenceLabel present (null if every result is unresolved); BlindSpots is
/// the fixed, machine-readable caveat set that applies to this query shape. SyntacticAssemblies
/// and PossibleFalseNegative are the attribution/false-negative-warning fields layered on top of
/// the `syntactic-assemblies-present` blind spot — see <see cref="UnBramble.Core.UnBrambleEngine"/>'s
/// Finalize/WhoUsesSymbol for how they're populated.</summary>
public sealed record QueryAnswer(
    QueryTarget Target,
    IReadOnlyList<EdgeResult> Results,
    bool Truncated,
    bool TransitiveUnavailable,
    string? Confidence = null,
    IReadOnlyList<string>? BlindSpots = null,
    SyntacticAssemblySummary? SyntacticAssemblies = null,
    bool PossibleFalseNegative = false)
{
    public IReadOnlyList<string> BlindSpots { get; init; } = BlindSpots ?? [];
}
