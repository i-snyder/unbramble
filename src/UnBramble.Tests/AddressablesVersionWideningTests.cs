using UnBramble.Core;
using UnBramble.Core.Addressables;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// Widens <see cref="AddressablesDetector"/>'s confirmed-version gate beyond its original
/// 1.21.x-only range, using the same real-capture discipline <see cref="AddressablesDetector"/>'s
/// own class doc requires for any future widening: "capture real output, verify field names,
/// extend the fixture — not just editing the two constants."
///
/// <para><b>The capture:</b> a real, large production Unity project (not itself part of this
/// repo) running Unity 6000.0.68f1. Its <c>Packages/manifest.json</c> REQUESTS Addressables
/// 2.8.1, but <c>Packages/packages-lock.json</c> — the file Unity Package Manager actually
/// resolved to, and the one <see cref="AddressablesDetector"/> deliberately prefers over the
/// manifest — still shows 2.3.16 resolved (the manifest had been hand-edited to 2.8.1 without the
/// project being reopened in Unity since; the resolve never ran). The <c>.asset</c> files
/// currently on disk under <c>Assets/AddressableAssetsData/</c> were therefore actually written
/// by the 2.3.16 resolve, not 2.8.1 — this fixture's real-capture rows are attributed to 2.3.16
/// accordingly, and 2.8.x is confirmed on source-diff evidence alone (see
/// <see cref="AddressablesDetector"/>'s class doc for exactly what that means and why it's one
/// rung weaker than a real capture).</para>
///
/// <para>Captured verbatim (sanitized guids/names, same discipline as the 1.21.21 capture in
/// <see cref="AddressablesCaptureTests"/>): <c>AddressableAssetSettings.asset</c>'s <c>m_GroupAssets</c>
/// list (an ordinary guid-ref list — the SAME shape as 1.21.21's settings-&gt;group edge, just a
/// different field name than the single-group-reference shape the 1.21.21 fixture exercised) and
/// a group <c>.asset</c> using the IDENTICAL entry field set captured at 1.21.21:
/// <c>m_GUID</c>, <c>m_Address</c>, <c>m_ReadOnly</c>, <c>m_SerializedLabels</c>,
/// <c>FlaggedDuringContentUpdateRestriction</c>. Zero field renames found — this class's own
/// edge-resolution tests below prove the existing parser/walk handles this shape with zero code
/// changes, exactly like the 1.21.21 capture did.</para>
/// </summary>
public class AddressablesVersionWideningTests
{
    // ==== F4/F5: settings -> group -> target compose via the EXISTING generic guid-ref parsing,
    // ==== for the real 2.3.16-shaped capture, exactly as F1/F3 proved for 1.21.21 -- zero parser
    // ==== changes needed because the field names are unchanged. ================================

    [Fact]
    public void F4_SettingsToGroup_ViaMGroupAssetsField_IsAnOrdinaryResolvedGuidEdge()
    {
        using var project = BuildRealCaptureProject();
        using var engine = UnBrambleEngine.Open(project.Root);
        engine.RunIndex(full: false);

        var settings = engine.ResolveQueryTarget("Assets/AddressableAssetsData/AddressableAssetSettings.asset");
        Assert.NotNull(settings.Target);

        var direct = engine.Uses(settings.Target!, transitive: false, depthCap: 1);

        Assert.Contains(direct.Results, r =>
            r.Kind == "guid" &&
            r.TargetPath == "Assets/AddressableAssetsData/AssetGroups/SomeGroup.asset" &&
            r.Resolved &&
            r.ConfidenceLabel == "proven");
    }

    [Fact]
    public void F5_GroupToTarget_ViaMGuidField_IsAnOrdinaryResolvedGuidEdge()
    {
        using var project = BuildRealCaptureProject();
        using var engine = UnBrambleEngine.Open(project.Root);
        engine.RunIndex(full: false);

        var group = engine.ResolveQueryTarget("Assets/AddressableAssetsData/AssetGroups/SomeGroup.asset");
        Assert.NotNull(group.Target);

        var direct = engine.Uses(group.Target!, transitive: false, depthCap: 1);

        Assert.Contains(direct.Results, r =>
            r.Kind == "guid" &&
            r.TargetPath == "Assets/Data/AddressableTarget.asset" &&
            r.Resolved &&
            r.ConfidenceLabel == "proven");

        // Same structural exclusion F1/F3's fixture relies on (RegexPatterns.AddressablesGroupSelfGuidField):
        // the group's own top-level m_GUID (its Addressables-internal identity) must never surface
        // as an outgoing edge, resolved or not, in this newer shape either.
        Assert.DoesNotContain(direct.Results, r => r.TargetKey == "23160000000000000000000000000005");
    }

    // ==== The detector: real capture + source cross-check justify confirming both 2.3.x (the
    // ==== actually-resolved version on disk) and 2.8.x (source-identical to 2.3.x, see the class
    // ==== doc) without regressing the original 1.21.x confirmation. ============================

    [Fact]
    public void Detector_RealCaptureProject_ManifestRequests2_8_1_ButLockResolved2_3_16_UsesLockVersion_Confirmed()
    {
        // Mirrors the real captured project exactly: manifest.json requests 2.8.1 (a stale
        // request that was never actually resolved), packages-lock.json still shows the last
        // real resolve, 2.3.16. AddressablesDetector.ResolveVersion prefers the lock file on
        // purpose (it's what Unity actually resolved) -- this must classify as confirmed via the
        // 2.3.x range, using "2.3.16" as ResolvedVersion, NOT "2.8.1".
        using var temp = TempDir.Create();
        WriteManifest(temp.Root, """{ "dependencies": { "com.unity.addressables": "2.8.1" } }""");
        WritePackagesLock(temp.Root, "2.3.16");

        var result = AddressablesDetector.Detect(temp.Root);

        Assert.Equal(AddressablesStatus.DetectedConfirmedVersion, result.Status);
        Assert.Equal("2.3.16", result.ResolvedVersion);
        Assert.Null(result.Reason);
        Assert.False(result.IsGated);
    }

    [Fact]
    public void Detector_2_8_1_ManifestOnly_DetectsConfirmedVersion()
    {
        // The version the captured project's manifest.json actually requests, and what it will
        // resolve to once reopened in Unity (no lock file here -- this is what a genuinely fresh
        // 2.8.1 resolve looks like, manifest-only detection).
        using var temp = TempDir.Create();
        WriteManifest(temp.Root, """{ "dependencies": { "com.unity.addressables": "2.8.1" } }""");

        var result = AddressablesDetector.Detect(temp.Root);

        Assert.Equal(AddressablesStatus.DetectedConfirmedVersion, result.Status);
        Assert.Equal("2.8.1", result.ResolvedVersion);
        Assert.Null(result.Reason);
        Assert.False(result.IsGated);
    }

    [Fact]
    public void Detector_1_21_21_StillConfirmed_NoRegressionFromWidening()
    {
        using var temp = TempDir.Create();
        WriteManifest(temp.Root, """{ "dependencies": { "com.unity.addressables": "1.21.21" } }""");

        var result = AddressablesDetector.Detect(temp.Root);

        Assert.Equal(AddressablesStatus.DetectedConfirmedVersion, result.Status);
        Assert.Equal("1.21.21", result.ResolvedVersion);
        Assert.False(result.IsGated);
    }

    [Fact]
    public void Detector_2_5_1_BetweenTheTwoConfirmed2xRanges_StillUnconfirmed()
    {
        // The confirmed set is an explicit list of (major, minor) pairs, never "any 2.x" -- a
        // minor version sitting between 2.3.x and 2.8.x must still be gated.
        using var temp = TempDir.Create();
        WriteManifest(temp.Root, """{ "dependencies": { "com.unity.addressables": "2.5.1" } }""");

        var result = AddressablesDetector.Detect(temp.Root);

        Assert.Equal(AddressablesStatus.DetectedUnconfirmedVersion, result.Status);
        Assert.True(result.IsGated);
        Assert.Equal(
            "liveness unavailable: unconfirmed root coverage (Addressables detected, version 2.5.1 outside the confirmed range (Addressables 1.21.x, 2.3.x, 2.8.x))",
            result.Reason);
    }

    [Fact]
    public void Detector_ConfirmedVersionRangeLabel_ListsAllThreeRanges()
    {
        Assert.Equal("Addressables 1.21.x, 2.3.x, 2.8.x", AddressablesDetector.ConfirmedVersionRangeLabel);
    }

    // ---- fixture construction (TempDir pattern, per its own doc comment: "small manifest.json/
    // ---- packages-lock.json variants rather than a full Unity project" -- extended here with
    // ---- the minimal real-captured Addressables data needed for F4/F5's edge-resolution proof) -

    private static TempDir BuildRealCaptureProject()
    {
        var project = TempDir.Create();

        Directory.CreateDirectory(Path.Combine(project.Root, "ProjectSettings"));
        File.WriteAllText(
            Path.Combine(project.Root, "ProjectSettings", "ProjectVersion.txt"),
            "m_EditorVersion: 6000.0.68f1\nm_EditorVersionWithRevision: 6000.0.68f1 (e1e9baaf294b)\n");
        File.WriteAllText(
            Path.Combine(project.Root, "ProjectSettings", "EditorSettings.asset"),
            "%YAML 1.1\n--- !u!159 &1\nEditorSettings:\n  m_SerializationMode: 2\n");

        WriteManifest(project.Root, """{ "dependencies": { "com.unity.addressables": "2.8.1" } }""");
        WritePackagesLock(project.Root, "2.3.16");

        var addressablesDir = Path.Combine(project.Root, "Assets", "AddressableAssetsData");
        var groupsDir = Path.Combine(addressablesDir, "AssetGroups");
        var dataDir = Path.Combine(project.Root, "Assets", "Data");
        Directory.CreateDirectory(groupsDir);
        Directory.CreateDirectory(dataDir);

        // Captured shape: AddressableAssetSettings.asset -> group via m_GroupAssets (an ordinary
        // guid-ref list, confirmed real a second time, at a different version).
        File.WriteAllText(
            Path.Combine(addressablesDir, "AddressableAssetSettings.asset"),
            """
            %YAML 1.1
            %TAG !u! tag:unity3d.com,2011:
            --- !u!114 &11400000
            MonoBehaviour:
              m_ObjectHideFlags: 0
              m_CorrespondingSourceObject: {fileID: 0}
              m_PrefabInstance: {fileID: 0}
              m_PrefabAsset: {fileID: 0}
              m_GameObject: {fileID: 0}
              m_Enabled: 1
              m_EditorHideFlags: 0
              m_Script: {fileID: 11500000, guid: 23160000000000000000000000000001, type: 3}
              m_Name: AddressableAssetSettings
              m_EditorClassIdentifier:
              m_GroupAssets:
              - {fileID: 11400000, guid: 23160000000000000000000000000004, type: 2}
            """);
        File.WriteAllText(
            Path.Combine(addressablesDir, "AddressableAssetSettings.asset.meta"),
            """
            fileFormatVersion: 2
            guid: 23160000000000000000000000000003
            NativeFormatImporter:
              externalObjects: {}
              mainObjectFileID: 11400000
              userData:
              assetBundleName:
              assetBundleVariant:
            """);

        // Captured shape: the group's entries use m_GUID/m_Address/m_ReadOnly/m_SerializedLabels/
        // FlaggedDuringContentUpdateRestriction -- identical field names to 1.21.21's F1/F3
        // fixture. The group's own top-level m_GUID (...05) is its Addressables-internal identity
        // and must be excluded the same way F3's GroupSelfIdentity test proves for 1.21.21.
        File.WriteAllText(
            Path.Combine(groupsDir, "SomeGroup.asset"),
            """
            %YAML 1.1
            %TAG !u! tag:unity3d.com,2011:
            --- !u!114 &11400000
            MonoBehaviour:
              m_ObjectHideFlags: 0
              m_CorrespondingSourceObject: {fileID: 0}
              m_PrefabInstance: {fileID: 0}
              m_PrefabAsset: {fileID: 0}
              m_GameObject: {fileID: 0}
              m_Enabled: 1
              m_EditorHideFlags: 0
              m_Script: {fileID: 11500000, guid: 23160000000000000000000000000002, type: 3}
              m_Name: SomeGroup
              m_EditorClassIdentifier:
              m_GroupName: SomeGroup
              m_GUID: 23160000000000000000000000000005
              m_SerializeEntries:
              - m_GUID: 23160000000000000000000000000006
                m_Address: Assets/Data/AddressableTarget.asset
                m_ReadOnly: 0
                m_SerializedLabels: []
                FlaggedDuringContentUpdateRestriction: 0
              m_ReadOnly: 0
              m_Settings: {fileID: 11400000, guid: 23160000000000000000000000000003, type: 2}
            """);
        File.WriteAllText(
            Path.Combine(groupsDir, "SomeGroup.asset.meta"),
            """
            fileFormatVersion: 2
            guid: 23160000000000000000000000000004
            NativeFormatImporter:
              externalObjects: {}
              mainObjectFileID: 11400000
              userData:
              assetBundleName:
              assetBundleVariant:
            """);

        File.WriteAllText(
            Path.Combine(dataDir, "AddressableTarget.asset"),
            """
            %YAML 1.1
            %TAG !u! tag:unity3d.com,2011:
            --- !u!114 &11400000
            MonoBehaviour:
              m_ObjectHideFlags: 0
              m_Name: AddressableTarget
            """);
        File.WriteAllText(
            Path.Combine(dataDir, "AddressableTarget.asset.meta"),
            """
            fileFormatVersion: 2
            guid: 23160000000000000000000000000006
            NativeFormatImporter:
              externalObjects: {}
              mainObjectFileID: 11400000
              userData:
              assetBundleName:
              assetBundleVariant:
            """);

        return project;
    }

    private static void WriteManifest(string root, string content)
    {
        var dir = Path.Combine(root, "Packages");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), content);
    }

    private static void WritePackagesLock(string root, string addressablesVersion)
    {
        var dir = Path.Combine(root, "Packages");
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "packages-lock.json"),
            $$"""
            {
              "dependencies": {
                "com.unity.addressables": { "version": "{{addressablesVersion}}", "depth": 0, "source": "registry" }
              }
            }
            """);
    }
}
