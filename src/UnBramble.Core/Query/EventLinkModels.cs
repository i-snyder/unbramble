namespace UnBramble.Core.Query;

/// <summary>
/// One UnityEvent persistent-call entry's matching-cascade outcome, evaluated at query time
/// against `refs`/`symbols`/`assemblies` — never stored. Every guid-carrying call entry (a
/// `refs` row with `method_name IS NOT NULL`) produces exactly one MATCHED row per resolved
/// member (several for an ambiguous overload set) or exactly one UNMATCHED row — never both,
/// never zero.
///
/// This is the ground truth `expected-events.json` compares against. <see cref="UnBramble.Core.Store.UnBrambleStore"/>
/// projects only the MATCHED rows into <see cref="EdgeResult"/> (kind="event") for who-uses
/// output — an unmatched row has no target file/symbol to attach an edge to, so it never
/// appears as a distinct output row; the underlying guid-kind `refs` edge (to the call's
/// containing asset) that carried this call all along keeps surfacing exactly as it always has
/// (its `methodName` annotation), so the call is never dropped or guessed at even in the
/// unmatched case, without needing a second edge shape.
/// </summary>
public sealed record EventLinkResult(
    string SourceFilePath,
    int Line,
    string? GameObjectName,
    string RawMethodName,
    string? RawTargetTypeName,
    string? TargetDocId,
    string? TargetFilePath,
    long? TargetFileId,
    string MatchKind,   // "declared" | "overload" | "inherited" | "unmatched"
    string Confidence,  // "proven" | "advisory"
    // The m_Target ref's property path ("m_OnClick.m_PersistentCalls.m_Calls[0].m_Target") —
    // this is what finally names WHICH UnityEvent field the binding lives on, which the parser
    // itself never captured. Display metadata only, same rules as EdgeResult.PropertyPath.
    string? PropertyPath = null)
{
    public bool IsMatched => TargetDocId is not null;
}
