namespace UnBramble.Core.Query;

/// <summary>One candidate/match for a `cs-refs` symbol-name lookup.</summary>
public sealed record CsSymbolMatch(string DocId, string Kind, string Name, string SourcePath);

/// <summary>
/// Outcome of resolving a `cs-refs` query argument: exact doc-id, then Type.Member, then fuzzy
/// with candidate listing on ambiguity — never runs a query on a guess (same discipline as
/// <see cref="TargetResolution"/>). DocId is null when not found or ambiguous.
/// </summary>
public sealed record CsSymbolResolution(string? DocId, IReadOnlyList<CsSymbolMatch> Candidates);

/// <summary>One row of a `cs-refs` answer: a file:line that references the resolved symbol.</summary>
public sealed record CsRefEntry(string SourcePath, int Line, string? ContainingSymbol, string RefKind, string Confidence);

/// <summary>
/// A full `cs-refs` answer. <see cref="Refs"/> is the `symbol_refs` call-site set (the verb's
/// original and only content); <see cref="EventRefs"/> is the UnityEvent-bound referencer set,
/// which is a genuine call site of the method — just wired in serialized data rather than in code
/// — and whose earlier absence made `cs-refs` able to report "0 referencers" for a live method.
/// <see cref="BlindSpots"/>/<see cref="SyntacticAssemblies"/> carry the same caveat material every
/// who-uses/uses answer already carried, so this verb's zero is qualified like every other.
/// </summary>
public sealed record CsRefsAnswer(
    IReadOnlyList<CsRefEntry> Refs,
    IReadOnlyList<EdgeResult> EventRefs,
    IReadOnlyList<string> BlindSpots,
    SyntacticAssemblySummary? SyntacticAssemblies);

/// <summary>One row of a speculative name-match fallback: a syntactic-confidence `symbol_refs`
/// row whose text-derived <see cref="TargetDocId"/> trailing identifier matched the queried
/// symbol's simple name, not a real join. <see cref="TargetDocId"/> is kept (unlike
/// <see cref="CsRefEntry"/>, which already knows its one resolved target) because the caller
/// displays the raw text the match was found against, honest about what actually matched.</summary>
public sealed record CsNameMatchEntry(string SourcePath, int Line, string? ContainingSymbol, string RefKind, string TargetDocId);

/// <summary>
/// A resolved symbol's declaring-file context. Kind is
/// 'type'|'method'|'field'|'property'|'event' (the `symbols.kind` value); used to seed
/// a symbol query's file-level walk at FilePath and to decide the basename rule (Kind == "type"
/// and Name matches FilePath's basename -> proven attachment; anything else -> advisory).
/// </summary>
public sealed record CsSymbolInfo(string Kind, string Name, long FileId, string FilePath, string? FileGuid);

/// <summary>Aggregate C# stats for the `stats` verb (NameHints is the name_hints row count, a
/// cheap smoke signal that capture isn't silently broken).</summary>
public sealed record CsStats(int Types, int Members, int Refs, int TotalAssemblies, int SyntacticAssemblies, int NameHints = 0)
{
    public static readonly CsStats Empty = new(0, 0, 0, 0, 0, 0);
}
