using System.Text.Json.Serialization;

namespace UnBramble.Cli.Json;

/// <summary>
/// Shared "unbrambleSchema" envelope field: every `--json` answer carries this so a consumer
/// (the future MCP wrapper, or any cached agent knowledge of the shape) can detect a contract
/// change. Introduced now because
/// retrofitting versioning onto an already-shipped unversioned contract is a breaking change by
/// definition; every JSON envelope class below declares its own property with this constant so
/// System.Text.Json's source generator can see it without reflection (NativeAOT-safe).
/// </summary>
internal static class SchemaVersion
{
    public const int Current = 1;
}

public sealed class ResolveMatchJson
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("guid")]
    public string? Guid { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("identityOnly")]
    public bool IdentityOnly { get; init; }
}

public sealed class ResolveResultJson
{
    [JsonPropertyName("unbrambleSchema")]
    public int UnBrambleSchema { get; init; } = SchemaVersion.Current;

    [JsonPropertyName("query")]
    public required string Query { get; init; }

    [JsonPropertyName("matches")]
    public required List<ResolveMatchJson> Matches { get; init; }

    /// <summary>True when the query was a well-formed 32-hex guid that no indexed asset carries —
    /// a definite "not in this index" answer (deleted asset, or one from a package that isn't
    /// installed), distinct from a query string that simply matched nothing. Exit code is 0 in
    /// this case, matching how `who-uses` already answers an unmatched bare guid.</summary>
    [JsonPropertyName("unresolvedGuid")]
    public bool UnresolvedGuid { get; init; }
}

public sealed class FileCountsJson
{
    [JsonPropertyName("total")]
    public int Total { get; init; }

    [JsonPropertyName("assets")]
    public int Assets { get; init; }

    [JsonPropertyName("scripts")]
    public int Scripts { get; init; }

    [JsonPropertyName("folders")]
    public int Folders { get; init; }

    [JsonPropertyName("settings")]
    public int Settings { get; init; }
}

public sealed class DbInfoJson
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; }
}

public sealed class EdgeStatsJson
{
    [JsonPropertyName("guidTotal")]
    public int GuidTotal { get; init; }

    [JsonPropertyName("guidUnresolved")]
    public int GuidUnresolved { get; init; }

    [JsonPropertyName("guidBuiltin")]
    public int GuidBuiltin { get; init; }

    [JsonPropertyName("pathTotal")]
    public int PathTotal { get; init; }

    [JsonPropertyName("pathUnresolved")]
    public int PathUnresolved { get; init; }
}

public sealed class CsStatsJson
{
    [JsonPropertyName("types")]
    public int Types { get; init; }

    [JsonPropertyName("members")]
    public int Members { get; init; }

    [JsonPropertyName("refs")]
    public int Refs { get; init; }

    [JsonPropertyName("totalAssemblies")]
    public int TotalAssemblies { get; init; }

    [JsonPropertyName("syntacticAssemblies")]
    public int SyntacticAssemblies { get; init; }

    /// <summary>The name_hints row count — a cheap smoke signal that the name-hint capture paths aren't silently broken.</summary>
    [JsonPropertyName("nameHints")]
    public int NameHints { get; init; }
}

public sealed class StatsResultJson
{
    [JsonPropertyName("unbrambleSchema")]
    public int UnBrambleSchema { get; init; } = SchemaVersion.Current;

    [JsonPropertyName("project")]
    public required string Project { get; init; }

    [JsonPropertyName("unityVersion")]
    public required string UnityVersion { get; init; }

    [JsonPropertyName("files")]
    public required FileCountsJson Files { get; init; }

    [JsonPropertyName("identityOnly")]
    public int IdentityOnly { get; init; }

    [JsonPropertyName("guidLess")]
    public int GuidLess { get; init; }

    [JsonPropertyName("edges")]
    public required EdgeStatsJson Edges { get; init; }

    [JsonPropertyName("db")]
    public required DbInfoJson Db { get; init; }

    /// <summary>
    /// The exact extension list the binary treats as guid-reference sources (the internal
    /// Scanner list, exposed verbatim). scripts/rg-parity.ps1 reads this instead of
    /// hardcoding its own copy, so the script and binary can never drift apart.
    /// </summary>
    [JsonPropertyName("refSourceExtensions")]
    public required List<string> RefSourceExtensions { get; init; }

    [JsonPropertyName("cs")]
    public required CsStatsJson Cs { get; init; }

    /// <summary>Every syntactic-mode assembly, named with its reason — the full list (unlike the
    /// capped sample on a query answer; `stats` is the enumeration verb). Null when none.</summary>
    [JsonPropertyName("syntacticAssemblies")]
    public List<SyntacticAssemblyJson>? SyntacticAssemblies { get; init; }
}

/// <summary>Per-phase wall-clock breakdown, seconds each.</summary>
public sealed class PhaseTimingsJson
{
    [JsonPropertyName("scanSeconds")]
    public double ScanSeconds { get; init; }

    [JsonPropertyName("sweepDiffSeconds")]
    public double SweepDiffSeconds { get; init; }

    [JsonPropertyName("dirtyReparseSeconds")]
    public double DirtyReparseSeconds { get; init; }

    [JsonPropertyName("csAnalysisSeconds")]
    public double CsAnalysisSeconds { get; init; }
}

public sealed class IndexResultJson
{
    [JsonPropertyName("unbrambleSchema")]
    public int UnBrambleSchema { get; init; } = SchemaVersion.Current;

    [JsonPropertyName("project")]
    public required string Project { get; init; }

    [JsonPropertyName("unityVersion")]
    public required string UnityVersion { get; init; }

    [JsonPropertyName("elapsedSeconds")]
    public double ElapsedSeconds { get; init; }

    [JsonPropertyName("phaseTimings")]
    public required PhaseTimingsJson PhaseTimings { get; init; }

    [JsonPropertyName("added")]
    public int Added { get; init; }

    [JsonPropertyName("changed")]
    public int Changed { get; init; }

    [JsonPropertyName("removed")]
    public int Removed { get; init; }

    [JsonPropertyName("files")]
    public required FileCountsJson Files { get; init; }

    [JsonPropertyName("identityOnly")]
    public int IdentityOnly { get; init; }

    [JsonPropertyName("edges")]
    public required EdgeStatsJson Edges { get; init; }

    [JsonPropertyName("db")]
    public required DbInfoJson Db { get; init; }

    [JsonPropertyName("cs")]
    public required CsStatsJson Cs { get; init; }

    [JsonPropertyName("warnings")]
    public required List<string> Warnings { get; init; }
}

public sealed class QueryTargetJson
{
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("guid")]
    public string? Guid { get; init; }
}

/// <summary>
/// One edge in a who-uses/uses answer. "source" is always the file containing the physical
/// reference (always resolvable — source_file_id is never null by schema); "target"/
/// "targetKey" describe what that reference points at ("target" is null when unresolved).
/// For who-uses, "source" is the interesting varying value (the referencer); for uses,
/// "target"/"targetKey" are (the dependency) and "source" is the file at that walk depth.
/// </summary>
public sealed class EdgeResultJson
{
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("target")]
    public string? Target { get; init; }

    [JsonPropertyName("targetKey")]
    public required string TargetKey { get; init; }

    [JsonPropertyName("line")]
    public int Line { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("depth")]
    public int Depth { get; init; }

    [JsonPropertyName("resolved")]
    public bool Resolved { get; init; }

    [JsonPropertyName("builtin")]
    public bool Builtin { get; init; }

    [JsonPropertyName("classId")]
    public int? ClassId { get; init; }

    [JsonPropertyName("gameObject")]
    public string? GameObject { get; init; }

    [JsonPropertyName("methodName")]
    public string? MethodName { get; init; }

    /// <summary>Best-effort dotted serialized-field path of the referencing line
    /// ("m_Settings.m_VolumeProfile", "m_Materials[2]") — which FIELD holds the reference.
    /// Guid-kind YAML edges and event edges only; null elsewhere.</summary>
    [JsonPropertyName("propertyPath")]
    public string? PropertyPath { get; init; }

    [JsonPropertyName("via")]
    public string? Via { get; init; }

    /// <summary>
    /// The derived presentation label — "proven" | "advisory" | "speculative". "speculative" is
    /// only ever a who-uses-symbol name-match fallback lead (see
    /// <c>UnBrambleEngine.WhoUsesSymbol</c>), never a real resolved edge. For a transitive
    /// answer this is the chain-weakest label along the edge's min-depth path back to the seed,
    /// not just this edge's own single hop. Null only for an edge that stays in the unresolved
    /// bucket (unresolved is its own honest state, never a confidence tier).
    /// </summary>
    [JsonPropertyName("confidence")]
    public string? Confidence { get; init; }

    /// <summary>The referenced symbol's simple name, populated only for kind == "cs".</summary>
    [JsonPropertyName("targetSymbol")]
    public string? TargetSymbol { get; init; }

    /// <summary>The referencing symbol's simple name, if known, populated only for kind == "cs".</summary>
    [JsonPropertyName("sourceSymbol")]
    public string? SourceSymbol { get; init; }

    /// <summary>'call'|'type-ref'|'inherit'|'override'|'member-access', populated only for kind == "cs"; also reused for kind == "event" ('declared'|'overload'|'inherited'|'unityevent-local').</summary>
    [JsonPropertyName("refKind")]
    public string? RefKind { get; init; }

    /// <summary>
    /// True for a matched UnityEvent-bound method edge (kind == "event") — "wired in serialized
    /// data" rather than "called from code" (the resharper-unity explicit/implicit split). False
    /// for every other edge kind.
    /// </summary>
    [JsonPropertyName("implicit")]
    public bool Implicit { get; init; }

    /// <summary>Whether this edge's SOURCE file is forward-reachable from the liveness roots
    /// (Build Settings scenes, Resources/, StreamingAssets/, unconditional entry points,
    /// Addressables) via the asset/cs graph. True is a proven positive claim; false means "no
    /// chain found" — NEVER "unreachable" (syntactic assemblies under-report cs edges). who-uses
    /// answers only; omitted when not computed.</summary>
    [JsonPropertyName("buildReachable")]
    public bool? BuildReachable { get; init; }
}

/// <summary>One syntactic-mode assembly named in a <see cref="SyntacticAssembliesJson"/> sample.</summary>
public sealed class SyntacticAssemblyJson
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>"no-csproj" | "csproj-unusable" | "csproj-parse-failed" | null (unknown, e.g. a
    /// pre-v6 DB row that hasn't been re-analyzed yet).</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

/// <summary>Per-answer attribution for the `syntactic-assemblies-present` blind spot: names WHICH
/// assemblies (capped — see <see cref="Total"/> vs <see cref="Assemblies"/>.Count) and WHY,
/// plus a fixed remediation hint.</summary>
public sealed class SyntacticAssembliesJson
{
    [JsonPropertyName("total")]
    public int Total { get; init; }

    [JsonPropertyName("assemblies")]
    public required List<SyntacticAssemblyJson> Assemblies { get; init; }

    [JsonPropertyName("remediation")]
    public required string Remediation { get; init; }
}

public sealed class QueryResultJson
{
    [JsonPropertyName("unbrambleSchema")]
    public int UnBrambleSchema { get; init; } = SchemaVersion.Current;

    [JsonPropertyName("query")]
    public required string Query { get; init; }

    [JsonPropertyName("target")]
    public required QueryTargetJson Target { get; init; }

    /// <summary>Populated only for a symbol-argument who-uses query — the resolved doc_id,
    /// e.g. "M:Foo.Jump".</summary>
    [JsonPropertyName("symbol")]
    public string? Symbol { get; init; }

    [JsonPropertyName("results")]
    public required List<EdgeResultJson> Results { get; init; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    /// <summary>The weakest per-edge confidence label present in Results; null if every result
    /// is unresolved.</summary>
    [JsonPropertyName("confidence")]
    public string? Confidence { get; init; }

    /// <summary>The fixed, machine-readable blind-spot set that applies to this query shape.</summary>
    [JsonPropertyName("blindSpots")]
    public required List<string> BlindSpots { get; init; }

    /// <summary>Populated whenever the `syntactic-assemblies-present` blind spot fires — names a
    /// capped sample of the offending assemblies plus a remediation hint. Null when there are
    /// none.</summary>
    [JsonPropertyName("syntacticAssemblies")]
    public SyntacticAssembliesJson? SyntacticAssemblies { get; init; }

    /// <summary>True when this answer has no proven/advisory result (only speculative name-match
    /// leads, if any, or nothing at all) while syntactic assemblies exist for a `.cs`-shaped
    /// target — the tool cannot tell "genuinely no callers" apart from "callers live in a
    /// syntactic assembly and aren't in the semantic index".</summary>
    [JsonPropertyName("possibleFalseNegative")]
    public bool PossibleFalseNegative { get; init; }
}

public sealed class QueryBatchResultJson
{
    [JsonPropertyName("unbrambleSchema")]
    public int UnBrambleSchema { get; init; } = SchemaVersion.Current;

    [JsonPropertyName("query")]
    public required string Query { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("results")]
    public required List<QueryResultJson> Results { get; init; }
}

public sealed class UnresolvedRefJson
{
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("targetKey")]
    public required string TargetKey { get; init; }

    [JsonPropertyName("line")]
    public int Line { get; init; }

    [JsonPropertyName("classId")]
    public int? ClassId { get; init; }

    [JsonPropertyName("gameObject")]
    public string? GameObject { get; init; }

    [JsonPropertyName("component")]
    public string? Component { get; init; }

    [JsonPropertyName("componentScriptGuid")]
    public string? ComponentScriptGuid { get; init; }

    [JsonPropertyName("propertyPath")]
    public string? PropertyPath { get; init; }

    [JsonPropertyName("isScriptReference")]
    public bool IsScriptReference { get; init; }

    [JsonPropertyName("isPrefabOverride")]
    public bool IsPrefabOverride { get; init; }

    [JsonPropertyName("prefabSource")]
    public string? PrefabSource { get; init; }

    [JsonPropertyName("buildReachable")]
    public bool? BuildReachable { get; init; }
}

public sealed class UnresolvedGroupJson
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("targetKey")]
    public required string TargetKey { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("sources")]
    public required List<string> Sources { get; init; }

    [JsonPropertyName("fields")]
    public required List<string> Fields { get; init; }

    [JsonPropertyName("components")]
    public required List<string> Components { get; init; }

    [JsonPropertyName("gameObjects")]
    public required List<string> GameObjects { get; init; }

    [JsonPropertyName("prefabSources")]
    public required List<string> PrefabSources { get; init; }

    [JsonPropertyName("scriptReferences")]
    public int ScriptReferences { get; init; }

    [JsonPropertyName("prefabOverrides")]
    public int PrefabOverrides { get; init; }

    [JsonPropertyName("buildReachableSources")]
    public int BuildReachableSources { get; init; }
}

public sealed class UnresolvedResultJson
{
    [JsonPropertyName("unbrambleSchema")]
    public int UnBrambleSchema { get; init; } = SchemaVersion.Current;

    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("grouped")]
    public bool Grouped { get; init; }

    [JsonPropertyName("items")]
    public required List<UnresolvedRefJson> Items { get; init; }

    [JsonPropertyName("groups")]
    public required List<UnresolvedGroupJson> Groups { get; init; }
}

public sealed class AssetAuditTargetJson
{
    [JsonPropertyName("query")]
    public required string Query { get; init; }

    [JsonPropertyName("target")]
    public QueryTargetJson? Target { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("items")]
    public required List<UnresolvedRefJson> Items { get; init; }

    [JsonPropertyName("groups")]
    public required List<UnresolvedGroupJson> Groups { get; init; }
}

public sealed class AssetAuditResultJson
{
    [JsonPropertyName("unbrambleSchema")]
    public int UnBrambleSchema { get; init; } = SchemaVersion.Current;

    [JsonPropertyName("input")]
    public required string Input { get; init; }

    [JsonPropertyName("targetCount")]
    public int TargetCount { get; init; }

    [JsonPropertyName("resolvedTargetCount")]
    public int ResolvedTargetCount { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("grouped")]
    public bool Grouped { get; init; }

    [JsonPropertyName("results")]
    public required List<AssetAuditTargetJson> Results { get; init; }

    [JsonPropertyName("groups")]
    public required List<UnresolvedGroupJson> Groups { get; init; }
}

public sealed class GuidCollisionGroupJson
{
    [JsonPropertyName("guid")]
    public required string Guid { get; init; }

    [JsonPropertyName("paths")]
    public required List<string> Paths { get; init; }
}

/// <summary>`stats --collisions --json`: every guid currently claimed by more than one indexed
/// file. Derived live from the DB on each call.</summary>
public sealed class GuidCollisionsResultJson
{
    [JsonPropertyName("unbrambleSchema")]
    public int UnBrambleSchema { get; init; } = SchemaVersion.Current;

    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("groups")]
    public required List<GuidCollisionGroupJson> Groups { get; init; }
}

public sealed class CsRefEntryJson
{
    [JsonPropertyName("source")]
    public required string Source { get; init; }

    [JsonPropertyName("line")]
    public int Line { get; init; }

    [JsonPropertyName("containingSymbol")]
    public string? ContainingSymbol { get; init; }

    [JsonPropertyName("refKind")]
    public required string RefKind { get; init; }

    [JsonPropertyName("confidence")]
    public required string Confidence { get; init; }
}

public sealed class CsRefsResultJson
{
    [JsonPropertyName("unbrambleSchema")]
    public int UnBrambleSchema { get; init; } = SchemaVersion.Current;

    [JsonPropertyName("query")]
    public required string Query { get; init; }

    [JsonPropertyName("docId")]
    public required string DocId { get; init; }

    /// <summary>Counts <see cref="Results"/> only — the `symbol_refs` call sites. UnityEvent
    /// bindings are counted separately in <see cref="EventResults"/> rather than folded in here,
    /// so this number keeps meaning exactly what it always meant.</summary>
    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("results")]
    public required List<CsRefEntryJson> Results { get; init; }

    /// <summary>UnityEvent-bound referencers of the resolved symbol: real call sites wired in
    /// serialized data rather than in code, which `symbol_refs` structurally cannot contain.
    /// Same edge shape as a `kind: "event"` row in a who-uses answer.</summary>
    [JsonPropertyName("eventResults")]
    public required List<EdgeResultJson> EventResults { get; init; }

    /// <summary>The fixed, machine-readable blind-spot set that applies to this query shape —
    /// same enum and same meaning as a who-uses/uses answer's.</summary>
    [JsonPropertyName("blindSpots")]
    public required List<string> BlindSpots { get; init; }

    /// <summary>Populated whenever the `syntactic-assemblies-present` blind spot fires; null when
    /// there are none. See <see cref="QueryResultJson.SyntacticAssemblies"/>.</summary>
    [JsonPropertyName("syntacticAssemblies")]
    public SyntacticAssembliesJson? SyntacticAssemblies { get; init; }
}

public sealed class CsSymbolCandidateJson
{
    [JsonPropertyName("docId")]
    public required string DocId { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("source")]
    public required string Source { get; init; }
}

public sealed class CsSymbolAmbiguousJson
{
    [JsonPropertyName("unbrambleSchema")]
    public int UnBrambleSchema { get; init; } = SchemaVersion.Current;

    [JsonPropertyName("query")]
    public required string Query { get; init; }

    [JsonPropertyName("candidates")]
    public required List<CsSymbolCandidateJson> Candidates { get; init; }
}

/// <summary>
/// `who-uses`/`uses --json`'s target-not-found/ambiguous error, mirroring
/// <see cref="CsSymbolAmbiguousJson"/>'s shape (same "query" + "candidates" contract) so the two
/// error surfaces never drift -- an empty <see cref="Candidates"/> list means no match at all,
/// a non-empty list means the target argument was ambiguous. Candidates reuse
/// <see cref="ResolveMatchJson"/>, the same shape `resolve --json` already emits for matches.
/// </summary>
public sealed class TargetNotFoundJson
{
    [JsonPropertyName("unbrambleSchema")]
    public int UnBrambleSchema { get; init; } = SchemaVersion.Current;

    [JsonPropertyName("query")]
    public required string Query { get; init; }

    [JsonPropertyName("candidates")]
    public required List<ResolveMatchJson> Candidates { get; init; }
}

/// <summary>
/// `who-uses &lt;arg&gt;`'s disambiguation error when an argument resolves as BOTH a path/guid
/// target and a C# symbol — never guessed, both interpretations listed so the caller can pick
/// via `--symbol` or a doc-id prefix.
/// </summary>
public sealed class AmbiguousPathOrSymbolJson
{
    [JsonPropertyName("unbrambleSchema")]
    public int UnBrambleSchema { get; init; } = SchemaVersion.Current;

    [JsonPropertyName("query")]
    public required string Query { get; init; }

    [JsonPropertyName("pathInterpretation")]
    public required QueryTargetJson PathInterpretation { get; init; }

    [JsonPropertyName("symbolInterpretation")]
    public required string SymbolInterpretation { get; init; }

    [JsonPropertyName("hint")]
    public required string Hint { get; init; }
}

/// <summary>`dead-candidates --json`'s root-set summary.</summary>
public sealed class LivenessRootSummaryJson
{
    [JsonPropertyName("projectSettingsFileCount")]
    public int ProjectSettingsFileCount { get; init; }

    [JsonPropertyName("resourcesFileCount")]
    public int ResourcesFileCount { get; init; }

    [JsonPropertyName("streamingAssetsFileCount")]
    public int StreamingAssetsFileCount { get; init; }

    [JsonPropertyName("entryPointFileCount")]
    public int EntryPointFileCount { get; init; }

    [JsonPropertyName("addressables")]
    public required string Addressables { get; init; }

    [JsonPropertyName("allowlistCount")]
    public int AllowlistCount { get; init; }
}

public sealed class DeadCandidateEntryJson
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("label")]
    public string Label { get; init; } = "proven";

    [JsonPropertyName("reasons")]
    public required List<string> Reasons { get; init; }
}

public sealed class AdvisoryDeadEntryJson
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}

/// <summary>
/// `unbramble dead-candidates --json`'s result envelope.
/// <see cref="Available"/> false means every gate-dependent field is default/empty and
/// <see cref="UnavailableReasons"/> lists every failed gate (never just the first).
/// </summary>
public sealed class DeadCandidatesResultJson
{
    [JsonPropertyName("unbrambleSchema")]
    public int UnBrambleSchema { get; init; } = SchemaVersion.Current;

    [JsonPropertyName("available")]
    public bool Available { get; init; }

    [JsonPropertyName("unavailableReasons")]
    public required List<string> UnavailableReasons { get; init; }

    [JsonPropertyName("roots")]
    public LivenessRootSummaryJson? Roots { get; init; }

    [JsonPropertyName("totalAssemblies")]
    public int TotalAssemblies { get; init; }

    [JsonPropertyName("syntacticAssemblies")]
    public int SyntacticAssemblies { get; init; }

    [JsonPropertyName("conventionExcludedCount")]
    public int ConventionExcludedCount { get; init; }

    [JsonPropertyName("provenDead")]
    public required List<DeadCandidateEntryJson> ProvenDead { get; init; }

    [JsonPropertyName("advisoryDead")]
    public required List<AdvisoryDeadEntryJson> AdvisoryDead { get; init; }

    /// <summary>The unconditional blind-spot statement — on every answer, available or
    /// not.</summary>
    [JsonPropertyName("blindSpots")]
    public required List<string> BlindSpots { get; init; }
}

[JsonSerializable(typeof(ResolveResultJson))]
[JsonSerializable(typeof(StatsResultJson))]
[JsonSerializable(typeof(IndexResultJson))]
[JsonSerializable(typeof(QueryResultJson))]
[JsonSerializable(typeof(QueryBatchResultJson))]
[JsonSerializable(typeof(UnresolvedResultJson))]
[JsonSerializable(typeof(AssetAuditTargetJson))]
[JsonSerializable(typeof(AssetAuditResultJson))]
[JsonSerializable(typeof(GuidCollisionsResultJson))]
[JsonSerializable(typeof(CsRefsResultJson))]
[JsonSerializable(typeof(CsSymbolAmbiguousJson))]
[JsonSerializable(typeof(TargetNotFoundJson))]
[JsonSerializable(typeof(AmbiguousPathOrSymbolJson))]
[JsonSerializable(typeof(DeadCandidatesResultJson))]
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class CliJsonContext : JsonSerializerContext
{
}
