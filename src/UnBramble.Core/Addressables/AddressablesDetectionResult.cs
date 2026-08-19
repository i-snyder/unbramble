namespace UnBramble.Core.Addressables;

/// <summary>
/// Tri-state Addressables detection outcome. Deliberately NOT a plain bool: confirmation is
/// backed by a small set of real-project captures (see <see cref="AddressablesDetector"/>'s
/// class doc), each scoped to one version combination — that is not proof the serialized shape
/// generalizes to every Addressables version, so a project using an unconfirmed version must be
/// told apart from one that's out of scope entirely.
/// </summary>
public enum AddressablesStatus
{
    /// <summary>Neither detection signal fired (no `com.unity.addressables` manifest dependency,
    /// no file under `Assets/AddressableAssetsData/`). No liveness gate applies.</summary>
    NotDetected,

    /// <summary>Addressables is present, but its resolved package version is outside
    /// <see cref="AddressablesDetector.ConfirmedVersionRangeLabel"/> — or could not be
    /// determined at all, which is treated the same way (the safe default, not an error). The
    /// hard "liveness unavailable" gate applies.</summary>
    DetectedUnconfirmedVersion,

    /// <summary>Addressables is present and its resolved package version falls inside a
    /// confirmed range. Lifting the liveness gate on this status is the consuming preflight's
    /// job — this type only reports the detection outcome.</summary>
    DetectedConfirmedVersion,
}

/// <summary>
/// Result of <see cref="AddressablesDetector.Detect"/>. <see cref="ResolvedVersion"/> is the
/// version string the detector actually used to decide confirmed-vs-not (preferring
/// `Packages/packages-lock.json`'s resolved version over `Packages/manifest.json`'s requested
/// version — see the detector's own doc comment for why); null when Addressables was detected
/// only via the `Assets/AddressableAssetsData/` file-presence signal with no corresponding
/// manifest/lock entry, or when detection didn't fire at all. <see cref="Reason"/> is the exact
/// gate-message string the `dead-candidates` preflight should reuse verbatim; null exactly
/// when <see cref="Status"/> is not a gating status (<c>NotDetected</c> or
/// <c>DetectedConfirmedVersion</c>).
/// </summary>
public sealed record AddressablesDetectionResult(AddressablesStatus Status, string? ResolvedVersion, string? Reason)
{
    public bool IsGated => Status == AddressablesStatus.DetectedUnconfirmedVersion;
}

/// <summary>
/// Exact reason-string constants/builders for the Addressables liveness gate: `dead-candidates`
/// prints `liveness unavailable: unconfirmed root coverage (Addressables detected)`. This type
/// only builds and tests these strings against the detector; the `dead-candidates` preflight is
/// the actual consumer — it must reuse these verbatim rather than re-deriving its own wording,
/// so the gate messages can never drift apart.
/// </summary>
public static class AddressablesReasons
{
    private const string Prefix = "liveness unavailable: unconfirmed root coverage (Addressables detected";

    /// <summary>
    /// Addressables was detected, but no resolvable package version could be found at all — a
    /// manifest dependency entry with a git/local-path reference instead of a version, a
    /// manifest entry present but no matching packages-lock.json entry, or Addressables-only-
    /// via-the-data-folder with no manifest entry to look a version up from. This is the SAFE
    /// DEFAULT, not an error — treated identically to a confirmed-absent
    /// out-of-range version, never surfaced as a crash or a distinct failure mode.
    /// </summary>
    public const string VersionUndetermined = Prefix + ", version could not be determined)";

    /// <summary>
    /// Addressables was detected and a version WAS resolved, but it falls outside
    /// <see cref="AddressablesDetector.ConfirmedVersionRangeLabel"/>. Distinguished from
    /// <see cref="VersionUndetermined"/> in the message itself because a known-but-unconfirmed
    /// version is actionable information a project owner can act on (e.g. downgrade, or wait
    /// for the confirmed range to widen) in a way an undetermined version isn't.
    /// </summary>
    public static string VersionOutsideConfirmedRange(string resolvedVersion) =>
        $"{Prefix}, version {resolvedVersion} outside the confirmed range ({AddressablesDetector.ConfirmedVersionRangeLabel}))";

    /// <summary>
    /// Addressables was detected AND its version is confirmed, but the
    /// expected settings asset (<c>Assets/AddressableAssetsData/AddressableAssetSettings.asset</c>)
    /// is not present in the index -- Unity's Addressables settings location is user-configurable,
    /// so a real project can have it elsewhere, or the file could exist on disk but not be
    /// indexed. Without this file, zero Addressables roots can be materialized, which inverts
    /// the whole point of the hard gate: with root coverage unconfirmed, every
    /// absence-of-reference claim in the project is unsound -- silently proceeding with no
    /// Addressables roots would mean every group/addressable-target file loses its liveness
    /// protection without any indication. This is therefore its own gate condition (distinct
    /// from <see cref="VersionUndetermined"/>/<see cref="VersionOutsideConfirmedRange"/>, which
    /// are both about the VERSION being unconfirmed, not the root file being absent).
    /// </summary>
    public const string SettingsAssetMissing = Prefix +
        ", confirmed version but the Addressables settings asset was not found in the index " +
        "(expected Assets/AddressableAssetsData/AddressableAssetSettings.asset) — reindex, or " +
        "verify the project's actual Addressables settings location)";
}
