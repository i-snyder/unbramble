namespace UnBramble.Core.Liveness;

/// <summary>
/// Screen/exclusion reason strings, including the unmatched-UnityEvent-name variant of the
/// name-hint screen — see <see cref="UnBramble.Core.UnBrambleEngine"/>'s screen implementation
/// for the exact rule per reason. Stable, machine-readable identifiers — surfaced verbatim in
/// `--json` output.
/// </summary>
public static class ScreenReasons
{
    public const string PathRefNameCollision = "path-ref-name-collision";
    public const string SyntacticTextCollision = "syntactic-text-collision";
    public const string NameHintCollision = "name-hint-collision";
    public const string UnityEventNameCollision = "unityevent-name-collision";
    public const string AttributeScreen = "attribute-screen";
    public const string DisabledRegionScreen = "disabled-region-screen";
    public const string InterfaceDispatchGuard = "interface-dispatch-guard";

    /// <summary>
    /// A type that implements a metadata-only (no source) interface — Unity's own callback interfaces
    /// (`ISerializationCallbackReceiver` etc., `UnityEngine`/`UnityEditor` namespaces) plus any
    /// other externally-defined interface, conservatively — or derives from a known Unity
    /// callback base class (`AssetPostprocessor`, `AssetModificationProcessor`,
    /// `BuildPlayerProcessor`) is invoked by Unity/the package through a mechanism this project's
    /// C# graph cannot see: no ordinary inbound call edge ever reaches such a type, so without
    /// this screen it was silently proven dead despite being live. See
    /// <see cref="UnBramble.Core.CSharp.SemanticCsExtractor"/>'s `FindExternalCallbackContract`
    /// for where the `cs-unity-callback` name hint backing this screen is emitted.
    /// </summary>
    public const string UnityCallbackGuard = "unity-callback-guard";

    /// <summary>
    /// A `.cs` candidate in a semantic-mode assembly (every assembly reaching evaluation is
    /// already guaranteed semantic) with literally ZERO rows in `symbols` -- e.g. a whole-file
    /// platform `#if` wrapping the entire class, for a platform never active under the current
    /// defines. None of the six screens above can ever fire for such a candidate because every
    /// one of them matches something against the candidate's OWN declared
    /// symbols/attributes/base list, and a zero-symbol file has nothing to match against -- not
    /// "genuinely unreferenced", but "could not be analyzed under the current defines at all".
    /// Per the asymmetric-risk invariant that labels can only be downgraded by ambiguity, never
    /// upgraded, such a file must never reach provenDead; it is screened (seed + advisory)
    /// exactly like the six named screens.
    /// </summary>
    public const string NoExtractedSymbols = "no-extracted-symbols";
}

/// <summary>
/// Curated inert-attribute allowlist: data-only attributes that do not, by themselves, imply
/// reflection-driven discovery. Anything else on a candidate's type/method screens it. Shipped
/// as a reviewed constant, same discipline as <see cref="ConventionExclusions"/>.
/// </summary>
public static class InertAttributes
{
    public static readonly HashSet<string> Names = new(StringComparer.Ordinal)
    {
        "Serializable", "Obsolete", "SerializeField", "Header", "Tooltip", "Range",
    };
}

/// <summary>Result of the liveness preflight gate check. <see cref="Reasons"/> is empty iff <see cref="Available"/>.</summary>
public sealed record LivenessGateResult(bool Available, IReadOnlyList<string> Reasons)
{
    public static readonly LivenessGateResult Ok = new(true, []);
}

/// <summary>Root-set summary for the human/`--json` output header.</summary>
public sealed record LivenessRootSummary(
    int ProjectSettingsFileCount,
    int ResourcesFileCount,
    int StreamingAssetsFileCount,
    int EntryPointFileCount,
    string AddressablesStatusText,
    int AllowlistCount);

/// <summary>One proven-dead file: every supporting fact is a resolved
/// edge, so the file is emitted as the default `dead-candidates` output.</summary>
public sealed record DeadCandidateEntry(string Path, string Reason);

/// <summary>One screened ("maybe live") file: caught by a screen or
/// the allowlist, seeded into LiveFiles, shown only under `--include-advisory`.</summary>
public sealed record AdvisoryDeadEntry(string Path, string Reason);

/// <summary>Full `dead-candidates` answer. <see cref="Available"/> false
/// means every other field is default/empty and <see cref="UnavailableReasons"/> lists every
/// failed preflight gate.</summary>
public sealed record DeadCandidatesResult(
    bool Available,
    IReadOnlyList<string> UnavailableReasons,
    LivenessRootSummary? Roots,
    int TotalAssemblies,
    int SyntacticAssemblies,
    int ConventionExcludedCount,
    IReadOnlyList<DeadCandidateEntry> ProvenDead,
    IReadOnlyList<AdvisoryDeadEntry> AdvisoryDead,
    IReadOnlyList<string> BlindSpots)
{
    public static DeadCandidatesResult Unavailable(IReadOnlyList<string> reasons) =>
        new(false, reasons, null, 0, 0, 0, [], [], []);
}
