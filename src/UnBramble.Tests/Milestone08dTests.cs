using UnBramble.Core;
using UnBramble.Core.Addressables;
using UnBramble.Core.Parsing;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// Addressables asset-reference capture and detection. AddressableAssetSettings.asset ->
/// group.asset -> target asset(s) are ordinary resolvable guid refs, captured by the existing
/// generic guid-ref parser with zero new parsing/walk logic:
///   - settings -> group is a plain guid ref.
///   - the group's addressable-target field is named `m_GUID` (not `m_AssetGUID`) and is matched
///     by the existing case-insensitive, unanchored GuidRef regex via its "GUID:" tail.
///   - the group's OWN top-level `m_GUID:` field is Addressables' internal group identity,
///     distinct from the group's own .meta guid. Left unhandled it would be captured as
///     permanent unresolved noise, so it is structurally excluded by a small, targeted parser
///     rule (see RegexPatterns.AddressablesGroupSelfGuidField).
///   - there is no separate sub-object addressing field: a sub-object addressable entry is
///     textually indistinguishable from a normal (whole-asset) entry in `m_SerializeEntries` --
///     same `m_GUID` field, whose value is the *containing* asset's AssetDatabase guid, not a
///     separate sub-object identity. So no extra parsing is needed for sub-object entries either.
///
/// This class does not build root/liveness logic (see Milestone08eTests for that). Its job is:
/// confirm the capture above against the fixture, and ship the reusable
/// Addressables-presence-and-version detector that Milestone08eTests' liveness preflight gate
/// consumes.
/// </summary>
public class Milestone08dTests
{
    // ==== settings -> group -> {target, sub-object-address-target} compose via the existing
    // ==== generic guid-ref parsing -- no new parsing/walk logic needed since settings->group->
    // ==== target is just ordinary resolvable guid edges. EdgeExtractionTests' whole-ledger
    // ==== equality test is the primary regression net for the exact edge shapes
    // ==== (expected-edges.json); these tests exercise the same data through the actual query
    // ==== surface instead of raw table reads.

    [Fact]
    public void F1_SettingsToGroup_IsAnOrdinaryResolvedGuidEdge_ZeroParserChanges()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
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
    public void F1_GroupToTarget_ViaMGuidField_IsAnOrdinaryResolvedGuidEdge()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var group = engine.ResolveQueryTarget("Assets/AddressableAssetsData/AssetGroups/SomeGroup.asset");
        Assert.NotNull(group.Target);

        var direct = engine.Uses(group.Target!, transitive: false, depthCap: 1);

        // The field is m_GUID (not m_AssetGUID), and the existing case-insensitive, unanchored
        // GuidRef regex already matches it via its "GUID:" tail -- no parser change was needed
        // to capture this edge.
        Assert.Contains(direct.Results, r =>
            r.Kind == "guid" &&
            r.TargetPath == "Assets/Data/AddressableTarget.asset" &&
            r.Resolved &&
            r.ConfidenceLabel == "proven");
    }

    [Fact]
    public void F3_SubObjectAddressEntry_ResolvesToTheOwningAsset_ViaTheSameMGuidMechanism()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var group = engine.ResolveQueryTarget("Assets/AddressableAssetsData/AssetGroups/SomeGroup.asset");
        var direct = engine.Uses(group.Target!, transitive: false, depthCap: 1);

        // The sub-object-address m_SerializeEntries item carries the same field, m_GUID, whose
        // value is AddressableAtlas.asset's own AssetDatabase guid (the owning/parent asset) --
        // no separate sub-object identity field exists to parse.
        Assert.Contains(direct.Results, r =>
            r.Kind == "guid" &&
            r.TargetPath == "Assets/Data/AddressableAtlas.asset" &&
            r.Resolved &&
            r.ConfidenceLabel == "proven");
    }

    [Fact]
    public void GroupSelfIdentity_MGuidField_IsNeverCapturedAsAnOutgoingRef()
    {
        // The group's own top-level m_GUID (Addressables-internal identity, distinct from its
        // .meta guid) is structurally excluded rather than captured-then-permanently-unresolved.
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var group = engine.ResolveQueryTarget("Assets/AddressableAssetsData/AssetGroups/SomeGroup.asset");
        var direct = engine.Uses(group.Target!, transitive: false, depthCap: 1);

        // The group's m_Script guid (bbb281ee...) IS a legitimate unresolved edge (boilerplate
        // MonoBehaviour identity, not vendored in this fixture -- see UnresolvedAccountingTests)
        // -- what must never appear, resolved or not, is the self-identity value specifically.
        Assert.DoesNotContain(direct.Results, r => r.TargetKey == "a55e7700000000000000000000000003");

        // Directly against the raw refs (EdgeExtractionTests' ledger-driven test is the primary
        // net for this; ResolveQueryTarget on the self-identity value itself must simply find
        // nothing indexed under it, since it was never anyone's .meta guid to begin with AND was
        // never captured as a target_guid either).
        var resolved = engine.Resolve("a55e7700000000000000000000000003");
        Assert.Empty(resolved);
    }

    // ---- the exclusion's own structural distinction (unit-level, ReferenceParser's public API
    // ---- directly on synthetic fragments, same discipline as ParserUnitTests.cs) -------------

    [Fact]
    public void SelfGuidExclusion_BareTopLevelField_NeverExtracted_ButListItemStill_Is()
    {
        var content = """
            %YAML 1.1
            --- !u!114 &11400000
            MonoBehaviour:
              m_GroupName: SomeGroup
              m_GUID: a55e7700000000000000000000000003
              m_SerializeEntries:
              - m_GUID: a55e7700000000000000000000000004
                m_Address: Whatever
            """;
        var parsed = ParseFragment(content, "Assets/AddressableAssetsData/AssetGroups/X.asset");

        Assert.DoesNotContain(parsed.GuidRefs, r => r.TargetGuid == "a55e7700000000000000000000000003");
        Assert.Contains(parsed.GuidRefs, r => r.TargetGuid == "a55e7700000000000000000000000004");
    }

    [Fact]
    public void SelfGuidExclusion_DoesNotAccidentallyMatchADifferentFieldName()
    {
        // A different, similarly-shaped field name ending the same way ("...Guid:") must still
        // be captured normally -- the exclusion is scoped to the exact "m_GUID:" bare field, not
        // "anything guid-shaped".
        var content = "  m_OtherGuid: a55e7700000000000000000000000003";
        var parsed = ParseFragment(content, "Assets/AddressableAssetsData/AssetGroups/X.asset");

        Assert.Contains(parsed.GuidRefs, r => r.TargetGuid == "a55e7700000000000000000000000003");
    }

    private static ParsedFileRefs ParseFragment(string content, string sourceProjectPath, string? ownGuid = null)
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, content);
            return new ReferenceParser().ParseContentSource(tmp, sourceProjectPath, ownGuid);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    // ==== Prove the fixture edges above compose correctly end-to-end via the existing transitive
    // ==== walk -- not new root/liveness logic (see Milestone08eTests for that). The
    // ==== AddressableAssetSettings.asset file node acting as a reachability root requires zero
    // ==== new code because settings->group->target is already an ordinary multi-hop guid chain
    // ==== the existing transitive CTE walk already handles.

    [Fact]
    public void RootRule_SettingsAsset_TransitivelyReachesBothAddressableTargets_ViaExistingWalk()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var settings = engine.ResolveQueryTarget("Assets/AddressableAssetsData/AddressableAssetSettings.asset");
        var answer = engine.Uses(settings.Target!, transitive: true, depthCap: UnBrambleEngine.DefaultDepthCap);

        // settings --(depth 1)--> group --(depth 2)--> {target, atlas}. No Addressables-specific
        // root/reachability code exists yet (see Milestone08eTests for that) -- this is the same
        // recursive CTE every other `uses --transitive` query already walks.
        Assert.Contains(answer.Results, r => r.TargetPath == "Assets/AddressableAssetsData/AssetGroups/SomeGroup.asset" && r.Depth == 1);
        Assert.Contains(answer.Results, r => r.TargetPath == "Assets/Data/AddressableTarget.asset" && r.Depth == 2);
        Assert.Contains(answer.Results, r => r.TargetPath == "Assets/Data/AddressableAtlas.asset" && r.Depth == 2);

        // The whole chain is proven (plain guid refs, exact identity resolution) -- so the
        // answer-level confidence is proven too, not dragged down by anything Addressables-specific.
        Assert.Equal("proven", answer.Confidence);
    }

    [Fact]
    public void RootRule_AddressableTarget_TransitivelyReachedBackToSettingsAsset_ReverseWalk()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var target = engine.ResolveQueryTarget("Assets/Data/AddressableTarget.asset");
        var answer = engine.WhoUses(target.Target!, transitive: true, depthCap: UnBrambleEngine.DefaultDepthCap);

        // The reverse direction of the exact same closure -- who-uses and uses share one edge
        // relation, so this composes for free once the forward direction does. This is the
        // mechanical form of "the settings asset is the root" that the liveness fixed point
        // (Milestone08eTests) relies on: a target reached this way already shows up as reachable
        // from the settings file today, without any liveness code existing yet.
        Assert.Contains(answer.Results, r => r.SourcePath == "Assets/AddressableAssetsData/AssetGroups/SomeGroup.asset" && r.Depth == 1);
        Assert.Contains(answer.Results, r => r.SourcePath == "Assets/AddressableAssetsData/AddressableAssetSettings.asset" && r.Depth == 2);
    }

    // ==== The detector: reusable Core infrastructure the liveness preflight gate (see
    // ==== Milestone08eTests) consumes. Tested at the Core API level throughout
    // ==== (AddressablesDetector.Detect takes a plain project-root path -- there is no CLI verb
    // ==== to invoke it directly).

    [Fact]
    public void Detector_MiniProjectFixture_DetectsConfirmedVersion_ViaBothSignals()
    {
        // The committed fixture declares "com.unity.addressables": "1.21.21" in
        // Packages/manifest.json AND has real files under Assets/AddressableAssetsData/ -- both
        // detection signals fire, and the version is exactly the confirmed one.
        using var fixture = FixtureCopy.Create();

        var result = AddressablesDetector.Detect(fixture.Root);

        Assert.Equal(AddressablesStatus.DetectedConfirmedVersion, result.Status);
        Assert.Equal("1.21.21", result.ResolvedVersion);
        Assert.Null(result.Reason);
        Assert.False(result.IsGated);
    }

    [Fact]
    public void Detector_NoManifestEntryNoDataFolder_NotDetected()
    {
        using var temp = TempDir.Create();
        WriteManifest(temp.Root, """{ "dependencies": { "com.fake.other": "1.0.0" } }""");

        var result = AddressablesDetector.Detect(temp.Root);

        Assert.Equal(AddressablesStatus.NotDetected, result.Status);
        Assert.Null(result.ResolvedVersion);
        Assert.Null(result.Reason);
        Assert.False(result.IsGated);
    }

    [Fact]
    public void Detector_NoProjectAtAll_NotDetected_NeverThrows()
    {
        // A bare, empty directory -- no Packages/, no Assets/. The detector must degrade
        // silently, not throw, since "no Addressables" is the overwhelmingly common case.
        using var temp = TempDir.Create();

        var result = AddressablesDetector.Detect(temp.Root);

        Assert.Equal(AddressablesStatus.NotDetected, result.Status);
    }

    // ---- Addressables-detection variant: a test project copy containing only
    // ---- Packages/manifest.json with com.unity.addressables -- the detection gate is exercised
    // ---- at every one of the three possible outcomes below, not just one.

    [Fact]
    public void F2_ManifestOnly_ConfirmedVersion_DetectedAndGateLifts()
    {
        using var temp = TempDir.Create();
        WriteManifest(temp.Root, """{ "dependencies": { "com.unity.addressables": "1.21.21" } }""");

        var result = AddressablesDetector.Detect(temp.Root);

        Assert.Equal(AddressablesStatus.DetectedConfirmedVersion, result.Status);
        Assert.Equal("1.21.21", result.ResolvedVersion);
        Assert.Null(result.Reason);
        Assert.False(result.IsGated);
    }

    [Fact]
    public void F2_ManifestOnly_OutOfRangeVersion_DetectedAndGated_WithDistinguishingReason()
    {
        // 2.5.x is deliberately picked here (not 2.3.x or 2.8.x): the confirmed set is an
        // explicit list of (major, minor) pairs, not "any 2.x" -- this proves a minor version
        // sitting BETWEEN two confirmed 2.x ranges is still correctly gated.
        using var temp = TempDir.Create();
        WriteManifest(temp.Root, """{ "dependencies": { "com.unity.addressables": "2.5.1" } }""");

        var result = AddressablesDetector.Detect(temp.Root);

        Assert.Equal(AddressablesStatus.DetectedUnconfirmedVersion, result.Status);
        Assert.Equal("2.5.1", result.ResolvedVersion);
        Assert.True(result.IsGated);
        Assert.Equal(
            "liveness unavailable: unconfirmed root coverage (Addressables detected, version 2.5.1 outside the confirmed range (Addressables 1.21.x, 2.3.x, 2.8.x))",
            result.Reason);
    }

    [Fact]
    public void F2_ManifestOnly_UnparseableVersion_TreatedAsOutsideConfirmedRange_SafeDefault()
    {
        // A git/local package reference: if a project's Addressables version can't be determined
        // at all, treat it as outside the confirmed range -- the safe default, not an error.
        using var temp = TempDir.Create();
        WriteManifest(temp.Root, """{ "dependencies": { "com.unity.addressables": "file:../local-addressables" } }""");

        var result = AddressablesDetector.Detect(temp.Root);

        Assert.Equal(AddressablesStatus.DetectedUnconfirmedVersion, result.Status);
        Assert.True(result.IsGated);
        Assert.Equal("liveness unavailable: unconfirmed root coverage (Addressables detected, version could not be determined)", result.Reason);
    }

    [Fact]
    public void F2_DataFolderOnly_NoManifestEntry_DetectedButVersionUndetermined()
    {
        // Addressables data present on disk with no corresponding manifest dependency at all
        // (catches vendored/unusual manifests).
        using var temp = TempDir.Create();
        var dataDir = Path.Combine(temp.Root, "Assets", "AddressableAssetsData", "AssetGroups");
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, "SomeGroup.asset"), "MonoBehaviour:\n  m_GroupName: SomeGroup\n");

        var result = AddressablesDetector.Detect(temp.Root);

        Assert.Equal(AddressablesStatus.DetectedUnconfirmedVersion, result.Status);
        Assert.Null(result.ResolvedVersion);
        Assert.True(result.IsGated);
        Assert.Equal("liveness unavailable: unconfirmed root coverage (Addressables detected, version could not be determined)", result.Reason);
    }

    [Fact]
    public void Detector_PackagesLockJson_PreferredOverManifestJson_WhenBothPresent()
    {
        // The lock file records what Package Manager ACTUALLY resolved; a stale/loose manifest
        // range should never win over it.
        using var temp = TempDir.Create();
        WriteManifest(temp.Root, """{ "dependencies": { "com.unity.addressables": "1.21.21" } }""");
        var lockPath = Path.Combine(temp.Root, "Packages", "packages-lock.json");
        File.WriteAllText(lockPath, """
            {
              "dependencies": {
                "com.unity.addressables": { "version": "1.22.0", "depth": 0, "source": "registry" }
              }
            }
            """);

        var result = AddressablesDetector.Detect(temp.Root);

        Assert.Equal(AddressablesStatus.DetectedUnconfirmedVersion, result.Status);
        Assert.Equal("1.22.0", result.ResolvedVersion);
    }

    [Fact]
    public void Detector_MalformedManifestJson_DegradesSilently_NeverThrows()
    {
        using var temp = TempDir.Create();
        Directory.CreateDirectory(Path.Combine(temp.Root, "Packages"));
        File.WriteAllText(Path.Combine(temp.Root, "Packages", "manifest.json"), "{ not valid json");

        var result = AddressablesDetector.Detect(temp.Root);

        Assert.Equal(AddressablesStatus.NotDetected, result.Status);
    }

    private static void WriteManifest(string root, string content)
    {
        var dir = Path.Combine(root, "Packages");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), content);
    }
}
