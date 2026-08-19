namespace UnBramble.Core.Query;

/// <summary>
/// Derives the proven/advisory/speculative confidence label at the presentation layer, from
/// stored facts already on an <see cref="EdgeResult"/> — NOT a new stored column. A
/// labeling-rule fix here never requires reindexing.
/// </summary>
public static class EdgeConfidence
{
    public const string Proven = "proven";
    public const string Advisory = "advisory";

    /// <summary>
    /// Never returned by <see cref="Derive"/> — that method only labels a real, resolved edge, and
    /// a speculative row isn't one. Populated instead by <c>UnBrambleEngine.WhoUsesSymbol</c>'s
    /// name-match fallback, which stamps it directly onto rows found by trailing-identifier text
    /// match against syntactic-confidence <c>symbol_refs</c> — a lead, not a resolved reference.
    /// </summary>
    public const string Speculative = "speculative";

    /// <summary>
    /// Single-hop derivation for one edge. Labels can only be
    /// <b>downgraded</b> by ambiguity, never upgraded by heuristics: "proven" requires
    /// serialized identity (guid), deterministic exact path match, or Roslyn semantic
    /// resolution — nothing name-shaped. Returns null for an edge that stays in
    /// the unresolved bucket — unresolved is its own honest state; dressing it in a confidence
    /// would imply an edge exists (the table's last row).
    /// </summary>
    public static string? Derive(EdgeResult edge)
    {
        // Builtin guids resolve to no `files` row (Resolved is false) but their identity is
        // completely known — Unity's own reserved guid families — so they are proven, checked
        // before the general Resolved gate.
        if (edge.Builtin)
        {
            return Proven;
        }

        if (!edge.Resolved)
        {
            return null;
        }

        return edge.Kind switch
        {
            "guid" => Proven,          // 32-hex identity equality; machine-checkable.
            "path" => Proven,          // resolution is deterministic exact normalized-path matching; no fuzz.
            "cs" => edge.Confidence == "semantic" ? Proven : Advisory,
            // The matching cascade already computed proven-vs-advisory itself (identity chain +
            // assembly mode, or a weaker rule capped at advisory) and stamped it directly onto
            // edge.Confidence as "proven"/"advisory" — reusing the raw-confidence field the
            // same way "cs" reuses semantic/syntactic, rather than adding a new field.
            "event" => edge.Confidence == Proven ? Proven : Advisory,
            // An asmdef precompiledReferences entry names its target by FILE NAME, and Unity
            // itself resolves it that way — so a name matching exactly one file in the project is
            // deterministic and machine-checkable, on the same footing as an exact normalized-path
            // match. Two or more files sharing the name is the ambiguous case (Unity errors on it;
            // this tool cannot tell which was meant), which the store already stamped as advisory
            // — read back here the same way "cs" and "event" read their own raw-confidence field.
            "dll" => edge.Confidence == Proven ? Proven : Advisory,
            _ => Advisory,             // room for future kinds without a crash.
        };
    }

    /// <summary>Ordinal rank for comparison; higher = stronger. Unknown/null labels rank as
    /// "strongest" (the identity element for <see cref="Weakest"/>) so a missing comparison
    /// value never wrongly downgrades a chain.</summary>
    private static int Rank(string? label) => label switch
    {
        Proven => 2,
        Advisory => 1,
        Speculative => 0,
        _ => 2,
    };

    /// <summary>The weaker of two labels — a chain is as strong as its weakest link. A null
    /// argument contributes nothing (returns the other side unchanged).</summary>
    public static string? Weakest(string? a, string? b)
    {
        if (a is null)
        {
            return b;
        }

        if (b is null)
        {
            return a;
        }

        return Rank(a) <= Rank(b) ? a : b;
    }

    private static bool IsWeaker(string? candidate, string? existing) => Rank(candidate) < Rank(existing);

    /// <summary>True if <paramref name="candidate"/> is strictly weaker than <paramref name="baseline"/>.
    /// A null baseline (nothing recorded yet) is treated as beaten by any real label — see
    /// <see cref="UnBramble.Core.UnBrambleEngine"/>'s per-node chain aggregation, which only calls
    /// this after already special-casing "no value recorded yet".</summary>
    public static bool IsWeakerThan(string? candidate, string? baseline) =>
        baseline is null ? candidate is not null : IsWeaker(candidate, baseline);

    /// <summary>
    /// Answer-level confidence: the weakest label among all labeled (resolved)
    /// edges in the answer. Unresolved edges (null label) are ignored entirely, not treated as
    /// weakest — the unresolved bucket stays separate and always surfaced on its own terms;
    /// confidence never becomes a place to hide that breakage. Null if the
    /// answer contains no labeled edge at all (e.g. only unresolved/broken refs).
    ///
    /// A <see cref="Speculative"/> label is excluded from this weakest-link computation unless it
    /// is the ONLY tier present: a name-match fallback lead sitting alongside a genuine proven/
    /// advisory result must never drag a real answer's confidence down to "speculative" — that
    /// would bury the one thing the caller can actually rely on under the tier meant to flag
    /// "unverified lead". When every labeled edge is speculative, the answer level truthfully
    /// becomes speculative too, rather than reporting nothing.
    /// </summary>
    public static string? AnswerLevel(IEnumerable<EdgeResult> results)
    {
        string? weakestNonSpeculative = null;
        var anyNonSpeculative = false;
        var anySpeculative = false;
        foreach (var label in results.Select(r => r.ConfidenceLabel))
        {
            if (label is null)
            {
                continue;
            }

            if (label == Speculative)
            {
                anySpeculative = true;
                continue;
            }

            anyNonSpeculative = true;
            weakestNonSpeculative = weakestNonSpeculative is null
                ? label
                : (IsWeaker(label, weakestNonSpeculative) ? label : weakestNonSpeculative);
        }

        if (anyNonSpeculative)
        {
            return weakestNonSpeculative;
        }

        return anySpeculative ? Speculative : null;
    }
}
