namespace UnBramble.Core.CSharp;

/// <summary>One declared symbol (maps to a `symbols` row). <see cref="DocId"/> is the stable key —
/// Roslyn's `ISymbol.GetDocumentationCommentId()` in semantic mode, a
/// best-effort syntactic equivalent in syntactic mode. <see cref="Attrs"/> is a space-joined
/// list of attribute simple names (types/methods only, "Attribute" suffix stripped), feeding
/// the attribute liveness screen. <see cref="EntryReason"/> discriminates
/// an unconditional entry point (`'attribute'`/`'main'`) from a conditional one (`'lifecycle'`
/// — a MonoBehaviour message name, only live if the file itself is reachable); null for
/// non-entry-point symbols.</summary>
public sealed record CsSymbolRow(
    string DocId,
    string Kind,
    string Name,
    long FileId,
    int Line,
    bool IsEntryPoint,
    string? Attrs = null,
    string? EntryReason = null);

/// <summary>One symbol-to-symbol reference (maps to a `symbol_refs` row). <see cref="TargetDocId"/>
/// is resolved against `symbols.doc_id` at QUERY time, never baked to a row id at parse time
/// (same lesson as path_refs). <see cref="SourceSymbolDocId"/> is the containing member's own
/// doc_id, if known — resolved to `source_symbol_id` at insert time (always within the same
/// assembly's freshly-extracted symbol set).</summary>
public sealed record CsSymbolRefRow(
    long SourceFileId,
    string? SourceSymbolDocId,
    string TargetDocId,
    string RefKind,
    int Line,
    string Confidence);

/// <summary>
/// One negative-evidence name hint sourced from C# analysis (maps to a `name_hints` row):
/// SendMessage-family string literals (`kind='cs-name-literal'`) or identifiers found inside
/// `#if`-disabled regions (`kind='cs-disabled'`). Never an edge:
/// no target, purely a name for the later liveness screen to match against.
/// </summary>
public sealed record CsNameHintRow(
    long SourceFileId,
    string Name,
    string Kind,
    int Line,
    string? TypeName);

/// <summary>One assembly's full (re)analysis result, ready to replace its stored rows.
/// <see cref="ModeReason"/> is <see cref="CSharp.CsModeReasons"/> for a Mode B unit, null for
/// Mode A — purely diagnostic, persisted alongside <see cref="Mode"/>.</summary>
public sealed record CsAssemblyAnalysis(
    string AssemblyName,
    long? AsmdefFileId,
    CsAnalysisMode Mode,
    IReadOnlyList<long> ScriptFileIds,
    IReadOnlyList<CsSymbolRow> Symbols,
    IReadOnlyList<CsSymbolRefRow> Refs,
    IReadOnlyList<CsNameHintRow> NameHints,
    string? ModeReason = null);
