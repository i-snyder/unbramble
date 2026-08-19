using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace UnBramble.Core.CSharp;

/// <summary>
/// Canonical per-file fingerprint over EXACTLY the row content <see
/// cref="UnBramble.Core.Store.UnBrambleStore.ReplaceAssemblyAnalyses"/> persists for one `.cs` file
/// (every `symbols` column including which assembly the row is bound to, every `symbol_refs`
/// column with `source_symbol_id` compared as its doc_id, every `name_hints` column). After a
/// FULL-unit extraction that was forced for environmental reasons (csproj mtime moved, defines/
/// reference shape-hash changed, dependency reference swap, or a self-edit that escalated),
/// comparing each file's freshly-extracted fingerprint against its persisted one lets the DB
/// write be scoped to only the files whose rows actually differ, instead of delete-reinserting
/// every file in the unit.
///
/// This is deliberately an EXACT-content comparison, not the cheaper declaration-shape proxy
/// <see cref="UnBramble.Core.Store.UnBrambleStore.GetDeclarationShapes"/> uses: declaration shape is
/// only a valid skip criterion in the self-edit case, where it proves something about OTHER
/// (untouched) files. After a full rebuild the changed input is the environment itself — a
/// DefineConstants change can flip an `#if` inside a method BODY (changing a file's call refs
/// and name hints while its declared symbol set and inherit/override refs stay identical), and a
/// reference change can re-resolve a call's `target_doc_id` the same way — so shape equality
/// does NOT imply row equality there. Exact content equality trivially does: skipping a write
/// whose rows are byte-identical to what's already persisted leaves the DB in exactly the state
/// a full rewrite would have (with untouched files' row ids stable rather than churned).
/// "Never wrong, only sometimes slower" — any ambiguity below (unreadable value, NULL vs
/// missing) is encoded so it can only make fingerprints DIFFER, i.e. force a rewrite.
/// </summary>
public static class CsFileRowFingerprint
{
    // Both are impossible in C# identifiers, doc ids, paths, attribute lists, and confidence
    // strings, so tokens can never collide across field boundaries or with a real value.
    private const char FieldSeparator = '\u001F';
    private const string NullMarker = "\u0001";

    public static string SymbolToken(string assemblyName, string kind, string docId, string name, string? line, bool isEntryPoint, string? attrs, string? entryReason) =>
        Join("S", assemblyName, kind, docId, name, line ?? NullMarker, isEntryPoint ? "1" : "0", attrs ?? NullMarker, entryReason ?? NullMarker);

    public static string RefToken(string? sourceSymbolDocId, string targetDocId, string refKind, string line, string confidence) =>
        Join("R", sourceSymbolDocId ?? NullMarker, targetDocId, refKind, line, confidence);

    public static string NameHintToken(string name, string kind, string line, string? typeName) =>
        Join("H", name, kind, line, typeName ?? NullMarker);

    public static string FormatLine(long line) => line.ToString(CultureInfo.InvariantCulture);

    /// <summary>Order-insensitive but duplicate-sensitive (a multiset, not a set — two identical
    /// rows persisted twice must not compare equal to one): tokens are sorted ordinally, then
    /// hashed. An empty token list has a well-defined fingerprint too, so "file has no rows" on
    /// both sides compares equal (nothing to write) rather than needing a special case.</summary>
    public static string Compute(List<string> tokens)
    {
        tokens.Sort(StringComparer.Ordinal);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', tokens)));
        return Convert.ToHexString(bytes);
    }

    /// <summary>Fingerprints a freshly-extracted (not yet persisted) full-unit analysis, one
    /// entry per <see cref="CsAssemblyAnalysis.ScriptFileIds"/> id — directly comparable against
    /// <see cref="UnBramble.Core.Store.UnBrambleStore.GetFileRowFingerprints"/>' persisted side.
    /// Returns null if any row's file id falls outside ScriptFileIds (shouldn't happen by
    /// construction; the caller must fall back to the full write rather than guess).</summary>
    public static Dictionary<long, string>? ComputeForAnalysis(CsAssemblyAnalysis analysis)
    {
        var tokensByFile = new Dictionary<long, List<string>>(analysis.ScriptFileIds.Count);
        foreach (var fileId in analysis.ScriptFileIds)
        {
            tokensByFile[fileId] = [];
        }

        foreach (var s in analysis.Symbols)
        {
            if (!tokensByFile.TryGetValue(s.FileId, out var tokens))
            {
                return null;
            }

            tokens.Add(SymbolToken(analysis.AssemblyName, s.Kind, s.DocId, s.Name, FormatLine(s.Line), s.IsEntryPoint, s.Attrs, s.EntryReason));
        }

        foreach (var r in analysis.Refs)
        {
            if (!tokensByFile.TryGetValue(r.SourceFileId, out var tokens))
            {
                return null;
            }

            tokens.Add(RefToken(r.SourceSymbolDocId, r.TargetDocId, r.RefKind, FormatLine(r.Line), r.Confidence));
        }

        foreach (var h in analysis.NameHints)
        {
            if (!tokensByFile.TryGetValue(h.SourceFileId, out var tokens))
            {
                return null;
            }

            tokens.Add(NameHintToken(h.Name, h.Kind, FormatLine(h.Line), h.TypeName));
        }

        var result = new Dictionary<long, string>(tokensByFile.Count);
        foreach (var (fileId, tokens) in tokensByFile)
        {
            result[fileId] = Compute(tokens);
        }

        return result;
    }

    private static string Join(params string[] fields) => string.Join(FieldSeparator, fields);
}
