using System.Text.Json;

namespace UnBramble.Core.Addressables;

/// <summary>
/// Detects whether a target project uses Unity Addressables, and whether its package version
/// falls inside a range this codebase's real-capture confirmation work has actually verified.
/// Pure Core infrastructure — stateless, no store/engine dependency — `dead-candidates`'s
/// preflight gate is this detector's consumer.
///
/// <para><b>What "confirmed" means here, precisely:</b> each entry in <see cref="ConfirmedRanges"/>
/// is backed by an independent real-project capture — not a public-source read alone, not a
/// version-number assumption. That is a finite set of data points, not proof the serialized shape
/// generalizes across every Addressables version; newer/older releases may serialize
/// `AddressableAssetGroup`/`AddressableAssetSettings` differently (field renames happen). The
/// confirmed set below is therefore deliberately a small, explicit list of (major, minor) pairs
/// — never "any 1.x" or "any 2.x" — and widening it further requires the SAME real-capture
/// discipline used for every entry already in the list: capture real output, verify field names,
/// extend the fixture — not just adding a tuple.</para>
///
/// <para><b>Confirmed range 1: Addressables 1.21.x.</b> The original confirmation —
/// one real (old) Unity project, Unity 2022.3.10f1, Addressables 1.21.21 — captured verbatim,
/// sanitized into this repo's fixture. The group's serialized shape as captured (`m_GUID`,
/// `m_Address`, `m_ReadOnly`, `m_SerializedLabels`, `FlaggedDuringContentUpdateRestriction` on
/// `AddressableAssetEntry`) was cross-checked against Addressables' own public source at the exact
/// matching tag (github.com/needle-mirror/com.unity.addressables, tag 1.21.21,
/// Editor/Settings/AddressableAssetEntry.cs) and matches field-for-field.</para>
///
/// <para><b>Confirmed range 2: Addressables 2.3.x.</b> A second real-project capture, against a
/// large production Unity project (Unity 6000.0.68f1). That project's `Packages/manifest.json`
/// requests Addressables 2.8.1, but `Packages/packages-lock.json` — the file Unity Package
/// Manager actually resolved to, which is what <see cref="ResolveVersion"/> deliberately prefers
/// — showed 2.3.16 resolved (the manifest had been hand-edited without reopening the project in
/// Unity since, so the resolve never actually ran). The `Assets/AddressableAssetsData/*.asset`
/// files captured from that project were therefore actually written by the 2.3.16 resolve, not
/// 2.8.1, and this range's real-capture evidence is attributed to 2.3.16 accordingly. The captured
/// `AddressableAssetSettings.asset` (`m_GroupAssets`: an ordinary guid-ref list, same shape as
/// 1.21.21's) and three real group `.asset` files use the IDENTICAL entry field set as 1.21.21 —
/// `m_GUID`, `m_Address`, `m_ReadOnly`, `m_SerializedLabels`, `FlaggedDuringContentUpdateRestriction`
/// — zero renames. Cross-checked against public source at tag 2.3.16: `AddressableAssetEntry.cs`
/// and `AddressableAssetGroup.cs` still declare every one of those fields `[SerializeField]` with
/// identical names; diffed directly against the 1.21.21 source, the only changes are non-
/// serialized logic (removal of `IsInResources`/editor-scene-list special-casing).</para>
///
/// <para><b>Confirmed range 3: Addressables 2.8.x.</b> NOT independently real-capture-confirmed —
/// no real project running an actually-resolved 2.8.1 was captured. Included because the public
/// source at tag 2.8.1 was diffed directly against tag 2.3.16 (confirmed range 2, above):
/// `AddressableAssetEntry.cs` is byte-identical between the two tags; `AddressableAssetGroup.cs`
/// differs only in non-serialization logic (an added `AllowNestedFolders` property, an
/// accessibility keyword, a sort-call addition) — zero changes to any `[SerializeField]`
/// declaration, and `AddressableAssetSettings.cs`'s `m_GroupAssets` field is unchanged too. This
/// is a byte-level source diff chained off a real capture, not an independent real-project capture
/// at 2.8.1 itself — one rung below ranges 1 and 2 on the confirmation ladder, included anyway
/// because the evidence is concrete (not an assumption) and the captured project's manifest.json
/// already requests 2.8.1, meaning the next time that project is reopened in Unity this range
/// becomes the operative one.</para>
/// </summary>
public static class AddressablesDetector
{
    /// <summary>
    /// A single confirmed Addressables (major, minor) version combination — see the class doc for
    /// each entry's provenance. Patch-level is deliberately unconstrained within a range (nothing
    /// in any confirmed shape has been found to be patch-sensitive). <paramref name="VersionLabel"/>
    /// is the bare "major.minor.x" portion (no "Addressables " prefix — <see cref="ConfirmedVersionRangeLabel"/>
    /// adds that once, for the whole joined list, not per entry).
    /// </summary>
    public readonly record struct ConfirmedRange(int Major, int Minor, string VersionLabel, string UnityVersionLabel);

    /// <summary>
    /// Every version range this detector currently confirms. See the class doc for each entry's
    /// real-capture (or, for the 2.8.x entry, source-diff) provenance. Deliberately a short,
    /// explicit list rather than open-ended major/minor bounds — widening it requires the same
    /// discipline as every existing entry, not just appending a tuple with no capture behind it.
    /// </summary>
    public static readonly IReadOnlyList<ConfirmedRange> ConfirmedRanges =
    [
        new ConfirmedRange(1, 21, "1.21.x", "Unity 2022.3.x"),
        new ConfirmedRange(2, 3, "2.3.x", "Unity 6000.0.x"),
        new ConfirmedRange(2, 8, "2.8.x", "Unity 6000.0.x"),
    ];

    /// <summary>Human-readable label for <see cref="AddressablesReasons"/> messages and docs —
    /// "Addressables " followed by every confirmed range's bare version label, joined. See
    /// <see cref="ConfirmedRanges"/> for the underlying set and each entry's provenance.</summary>
    public static string ConfirmedVersionRangeLabel =>
        "Addressables " + string.Join(", ", ConfirmedRanges.Select(r => r.VersionLabel));

    /// <summary>Confirmed Unity editor version context (not itself gated on — see the detector's
    /// class doc; recorded here only so each confirmed combination is discoverable in one place).
    /// Distinct Unity major versions are
    /// de-duplicated and joined.</summary>
    public static string ConfirmedUnityVersionRangeLabel =>
        string.Join(", ", ConfirmedRanges.Select(r => r.UnityVersionLabel).Distinct());

    public const string AddressablesPackageId = "com.unity.addressables";

    private const string AddressableAssetsDataRelativePath = "Assets/AddressableAssetsData";

    /// <summary>
    /// Runs both detection signals -- (a) `com.unity.addressables` appears
    /// in `Packages/manifest.json` dependencies, or (b) any file exists under
    /// `Assets/AddressableAssetsData/`; either alone triggers -- and, if detected, resolves a
    /// package version and classifies it against the confirmed range. Never throws on a missing
    /// or malformed manifest/lock file — a project without Addressables at all is the overwhelmingly
    /// common case this must handle silently, not loudly.
    /// </summary>
    public static AddressablesDetectionResult Detect(string projectRoot)
    {
        var manifestDependsOnAddressables = ManifestListsAddressables(projectRoot);
        var dataFolderPresent = AnyFileUnderAddressablesDataFolder(projectRoot);

        if (!manifestDependsOnAddressables && !dataFolderPresent)
        {
            return new AddressablesDetectionResult(AddressablesStatus.NotDetected, null, null);
        }

        var resolvedVersion = ResolveVersion(projectRoot);
        var versionMatch = resolvedVersion is not null ? RegexPatterns.LeadingMajorMinorVersion().Match(resolvedVersion) : null;

        if (versionMatch is { Success: true } m)
        {
            var major = int.Parse(m.Groups[1].Value);
            var minor = int.Parse(m.Groups[2].Value);
            if (ConfirmedRanges.Any(r => r.Major == major && r.Minor == minor))
            {
                return new AddressablesDetectionResult(AddressablesStatus.DetectedConfirmedVersion, resolvedVersion, null);
            }
        }

        // Three distinct "unconfirmed" causes, all treated the SAME way for gating purposes (an
        // undeterminable version is the SAFE DEFAULT, not a distinct error class) but told
        // apart in the message: no version signal at all
        // (null), a version-shaped string outside the confirmed range, or a non-version-shaped
        // string (a git/file package reference — "file:../local-pkg" is not itself a version,
        // reporting it as "version file:../local-pkg outside the confirmed range" would be
        // actively misleading, so a failed version-shape match is VersionUndetermined too, not
        // VersionOutsideConfirmedRange with garbage in the version slot).
        var reason = versionMatch is { Success: true }
            ? AddressablesReasons.VersionOutsideConfirmedRange(resolvedVersion!)
            : AddressablesReasons.VersionUndetermined;

        return new AddressablesDetectionResult(AddressablesStatus.DetectedUnconfirmedVersion, resolvedVersion, reason);
    }

    /// <summary>Detection rule (a): a `com.unity.addressables` KEY in `Packages/manifest.json`'s
    /// `dependencies` object, regardless of whether its value is a resolvable version (a git/
    /// file reference still counts as "depends on Addressables" for detection purposes — version
    /// resolution is a separate, later question, see <see cref="ResolveVersion"/>).</summary>
    private static bool ManifestListsAddressables(string projectRoot)
    {
        var manifest = TryReadManifest(projectRoot);
        return manifest?.Dependencies?.ContainsKey(AddressablesPackageId) == true;
    }

    /// <summary>Detection rule (b): any file (not just the directory existing) anywhere under
    /// `Assets/AddressableAssetsData/`, recursively — catches vendored/unusual manifests where
    /// the package reference itself is missing but the generated data tree is present.</summary>
    private static bool AnyFileUnderAddressablesDataFolder(string projectRoot)
    {
        var full = Path.Combine(projectRoot, AddressableAssetsDataRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(full))
        {
            return false;
        }

        try
        {
            return Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Can't enumerate -> can't prove a file exists there. Not a crash-worthy condition
            // for a pure detector; the caller's own scan (Scanner) will separately warn about
            // the same directory if it matters for indexing.
            return false;
        }
    }

    /// <summary>
    /// Resolves the Addressables package version, preferring `Packages/packages-lock.json`'s
    /// RESOLVED version (see <see cref="PackagesLockJson"/>'s doc comment for why) and falling
    /// back to `Packages/manifest.json`'s requested version string if the lock file is absent,
    /// unreadable, or has no matching entry. Returns null — "could not be determined" — for a
    /// git/file/URL package reference with no parseable leading version, or when Addressables
    /// wasn't found in either file at all (the data-folder-only detection path has no version
    /// signal to read).
    /// </summary>
    private static string? ResolveVersion(string projectRoot)
    {
        var lockEntry = TryReadPackagesLock(projectRoot);
        var lockVersion = lockEntry?.Dependencies?.GetValueOrDefault(AddressablesPackageId)?.Version;
        if (!string.IsNullOrWhiteSpace(lockVersion))
        {
            return lockVersion;
        }

        var manifest = TryReadManifest(projectRoot);
        var manifestVersion = manifest?.Dependencies?.GetValueOrDefault(AddressablesPackageId);
        return string.IsNullOrWhiteSpace(manifestVersion) ? null : manifestVersion;
    }

    private static PackageManifestJson? TryReadManifest(string projectRoot)
    {
        var path = Path.Combine(projectRoot, "Packages", "manifest.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, PackageManifestJsonContext.Default.PackageManifestJson);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static PackagesLockJson? TryReadPackagesLock(string projectRoot)
    {
        var path = Path.Combine(projectRoot, "Packages", "packages-lock.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, PackageManifestJsonContext.Default.PackagesLockJson);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
