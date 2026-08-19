using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using UnBramble.Core;
using UnBramble.Core.Liveness;
using UnBramble.Core.Query;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// Covers `unbramble dead-candidates` (liveness / dead-code detection): root materialization,
/// the file-granular fixed-point reachability walk, screens that seed liveness rather than
/// merely suppressing a false positive, referenced-by-convention exclusions, the allowlist,
/// preflight availability gates (syntactic-mode assemblies, unconfirmed Addressables version,
/// stale generated csproj), the output contract (--json, --kind, --include-advisory), and the
/// global liveness invariant (no provenDead file is ever the target of a resolved edge from a
/// live file).
///
/// Uses the same throwaway-semantic-csproj pattern as <see cref="Milestone08aTests"/>/
/// <see cref="Milestone08cTests"/> (a local copy of the helper, not a shared one) so the
/// fixture's two assemblies land in Semantic mode — required because any syntactic-mode assembly
/// anywhere makes liveness unavailable, full stop, so most of this class needs semantic mode just
/// to get past the gate. The committed base fixture deliberately has no generated csproj, so a
/// separate test (<see cref="DeadCandidates_BaseFixture_SyntacticMode_IsUnavailable"/> below)
/// exercises that exact gate-failure case using the unmodified fixture.
/// </summary>
public class Milestone08eTests
{
    private static readonly string ExpectedLivenessPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "expected-liveness.json");

    // ==== base (unmodified) fixture has no generated csproj -> syntactic mode -> gate fails ====

    [Fact]
    public void DeadCandidates_BaseFixture_SyntacticMode_IsUnavailable()
    {
        using var fixture = FixtureCopy.Create();
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var result = engine.RunDeadCandidates();

        Assert.False(result.Available);
        Assert.Contains(result.UnavailableReasons, r => r.Contains("syntactic-mode assembly present", StringComparison.Ordinal));
    }

    [Fact]
    public void Cli_DeadCandidates_BaseFixture_ExitCode1_PrintsLivenessUnavailable()
    {
        using var fixture = FixtureCopy.Create();
        _ = CliRunner.Run("init", "-p", fixture.Root);

        var (exitCode, _, stdErr) = CliRunner.Run("dead-candidates", "-p", fixture.Root);

        Assert.Equal(1, exitCode);
        Assert.Contains("liveness unavailable", stdErr, StringComparison.Ordinal);
    }

    // ==== The full semantic-mode run: exact provenDead/advisoryDead sets against the ledger ====

    [Fact]
    public void DeadCandidates_SemanticMode_ExactlyMatchesLedger_ProvenDeadSet()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var result = engine.RunDeadCandidates();
        Assert.True(result.Available, string.Join("; ", result.UnavailableReasons));

        var ledger = LoadLedger();
        var expected = ledger.ProvenDead.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = result.ProvenDead.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = expected.Except(actual).ToList();
        var extra = actual.Except(expected).ToList();
        Assert.True(missing.Count == 0 && extra.Count == 0,
            $"missing: [{string.Join(", ", missing)}]; extra: [{string.Join(", ", extra)}]");
    }

    [Fact]
    public void DeadCandidates_SemanticMode_ExactlyMatchesLedger_AdvisoryDeadSetWithReasons()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var result = engine.RunDeadCandidates();
        Assert.True(result.Available, string.Join("; ", result.UnavailableReasons));

        var ledger = LoadLedger();
        var expected = ledger.AdvisoryDead.Select(e => (e.Path, e.Reason)).ToHashSet();
        var actual = result.AdvisoryDead.Select(e => (e.Path, e.Reason)).ToHashSet();

        var missing = expected.Except(actual).ToList();
        var extra = actual.Except(expected).ToList();
        Assert.True(missing.Count == 0 && extra.Count == 0,
            $"missing: [{string.Join(", ", missing)}]; extra: [{string.Join(", ", extra)}]");
    }

    [Fact]
    public void DeadCandidates_SemanticMode_MustBeLiveFiles_NeverInEitherDeadBucket()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var result = engine.RunDeadCandidates();
        Assert.True(result.Available, string.Join("; ", result.UnavailableReasons));

        var ledger = LoadLedger();
        var provenPaths = result.ProvenDead.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var advisoryPaths = result.AdvisoryDead.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var path in ledger.MustBeLive)
        {
            Assert.False(provenPaths.Contains(path), $"'{path}' must never be provenDead");
            Assert.False(advisoryPaths.Contains(path), $"'{path}' must never be advisoryDead");
        }
    }

    [Fact]
    public void DeadCandidates_SemanticMode_RootSummaryAndConventionExclusions_MatchLedger()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var result = engine.RunDeadCandidates();
        Assert.True(result.Available, string.Join("; ", result.UnavailableReasons));

        var ledger = LoadLedger();
        Assert.Equal(ledger.Roots.ProjectSettingsFileCount, result.Roots!.ProjectSettingsFileCount);
        Assert.Equal(ledger.Roots.ResourcesFileCount, result.Roots.ResourcesFileCount);
        Assert.Equal(ledger.Roots.StreamingAssetsFileCount, result.Roots.StreamingAssetsFileCount);
        Assert.Equal(ledger.Roots.EntryPointFileCount, result.Roots.EntryPointFileCount);
        Assert.Equal(ledger.Roots.AddressablesDetected, result.Roots.AddressablesStatusText.StartsWith("detected", StringComparison.Ordinal));

        Assert.Equal(ledger.ConventionExcludedCount, result.ConventionExcludedCount);
    }

    // ==== Asymmetric-risk counterexamples: method-group reference, field-type-only reference,
    // ==== and a screened-file dependency chain -- cases that could otherwise cause the fixed
    // ==== point to falsely mark a live file's dependency as dead ===============================

    [Fact]
    public void AsymmetricRisk_F20_FieldTypeOnlyChain_BarNeverProvenDead()
    {
        // Foo.cs (live) declares `public Bar config;` -- a plain declaration-site field-type
        // reference, captured as an ordinary semantic type-ref row. Because the fixed point
        // propagates along unified_walk_edges from every live file's outgoing edges with no
        // symbol-level activation gate, Bar.cs becomes live the moment Foo.cs does -- there is
        // no separate "did anything activate the field symbol" step that could fail to notice a
        // field-type-only reference.
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var result = engine.RunDeadCandidates();
        Assert.True(result.Available, string.Join("; ", result.UnavailableReasons));

        AssertNeverDead(result, "Assets/Scripts/Bar.cs");
    }

    [Fact]
    public void AsymmetricRisk_F19_MethodGroupChain_HelperNeverProvenDead()
    {
        // Foo.Start contains `System.Action h = Helper.Handle;` -- a method-group/delegate-
        // conversion reference. If that weren't captured as a ref row at any granularity,
        // Helper.cs would have zero inbound edges and be indistinguishable from a genuinely dead
        // file. Capturing non-invocation IMethodSymbol occurrences as ordinary semantic
        // call-kind refs (see Milestone08aTests) means Helper.cs is live via ordinary
        // propagation, same as any other resolved edge.
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var result = engine.RunDeadCandidates();
        Assert.True(result.Available, string.Join("; ", result.UnavailableReasons));

        AssertNeverDead(result, "Assets/Scripts/Helper.cs");
    }

    [Fact]
    public void AsymmetricRisk_F22_ScreenedFileDependencyChain_SwordVfxLiveNotDead_SwordAdvisoryNotProven()
    {
        // Sword.cs implements the live IWeapon interface but has zero direct inbound refs
        // (Roslyn resolves calls through an IWeapon-typed receiver to IWeapon's own doc_id,
        // never Sword's) -- the interface/virtual-dispatch guard screens it instead. Screens must
        // also seed liveness (not just suppress a false positive on the screened file itself),
        // or a screened interface implementer's own private dependency (here, SwordVfx.cs,
        // field-referenced only from Sword.cs) would surface as proven dead -- a false positive
        // by construction. Screens-seed-liveness means: Sword.cs is screened -> seeded into
        // LiveFiles -> its own outgoing edges propagate next pass -> SwordVfx.cs is live.
        // Neither ends up "provably dead".
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var result = engine.RunDeadCandidates();
        Assert.True(result.Available, string.Join("; ", result.UnavailableReasons));

        var swordAdvisory = Assert.Single(result.AdvisoryDead, e => e.Path == "Assets/Scripts/Sword.cs");
        Assert.Equal(ScreenReasons.InterfaceDispatchGuard, swordAdvisory.Reason);
        Assert.DoesNotContain(result.ProvenDead, e => e.Path == "Assets/Scripts/Sword.cs");

        AssertNeverDead(result, "Assets/Scripts/SwordVfx.cs");
    }

    // ==== enum-only/delegate-only files must not be proven dead while a live file references
    // ==== them (SemanticCsExtractor previously had no VisitEnumDeclaration/
    // ==== VisitDelegateDeclaration, so these files produced no symbol rows and no inbound edge
    // ==== could ever attach) ====================================================================

    [Fact]
    public void Finding1_EnumOnlyFile_ReferencedFromLiveField_NeverProvenDead()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var result = engine.RunDeadCandidates();
        Assert.True(result.Available, string.Join("; ", result.UnavailableReasons));

        AssertNeverDead(result, "Assets/Scripts/GameState.cs");
    }

    [Fact]
    public void Finding1_DelegateOnlyFile_ReferencedFromLiveField_NeverProvenDead()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var result = engine.RunDeadCandidates();
        Assert.True(result.Available, string.Join("; ", result.UnavailableReasons));

        AssertNeverDead(result, "Assets/Scripts/ClickHandler.cs");
    }

    // ==== bare (unqualified) method-group references (e.g. via `using static`) must produce an
    // ==== inbound edge, not silently vanish. ====================================================

    [Fact]
    public void Finding2_BareMethodGroupReference_ViaUsingStatic_NeverProvenDead()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var result = engine.RunDeadCandidates();
        Assert.True(result.Available, string.Join("; ", result.UnavailableReasons));

        AssertNeverDead(result, "Assets/Scripts/BareHandler.cs");
    }

    // ==== package.json under a non-Packages/ root must still be convention-excluded, never a
    // ==== dead-candidates target at all. ========================================================

    [Fact]
    public void Finding3_PackageJsonUnderLocalPackagesRoot_IsConventionExcluded_NeverACandidate()
    {
        Assert.True(UnBramble.Core.Liveness.ConventionExclusions.IsExcluded("LocalPackages/com.studio.shared/package.json"));
        Assert.True(UnBramble.Core.Liveness.ConventionExclusions.IsExcluded("Packages/com.vendor.thing/package.json"));
        Assert.True(UnBramble.Core.Liveness.ConventionExclusions.IsExcluded("package.json"));
        Assert.False(UnBramble.Core.Liveness.ConventionExclusions.IsExcluded("Assets/Data/package.json.txt"));
    }

    // ==== Addressables confirmed but the expected settings asset is missing from the index --
    // ==== must report unavailable, never proceed with zero roots. ==============================

    [Fact]
    public void Finding4_AddressablesConfirmed_ButSettingsAssetMissingFromIndex_LivenessUnavailable()
    {
        using var temp = TempDir.Create();
        Directory.CreateDirectory(Path.Combine(temp.Root, "ProjectSettings"));
        File.WriteAllText(
            Path.Combine(temp.Root, "ProjectSettings", "ProjectVersion.txt"),
            "m_EditorVersion: 2022.3.30f1\nm_EditorVersionWithRevision: 2022.3.30f1 (70558241b701)\n");
        File.WriteAllText(
            Path.Combine(temp.Root, "ProjectSettings", "EditorSettings.asset"),
            "%YAML 1.1\n--- !u!159 &1\nEditorSettings:\n  m_SerializationMode: 2\n");
        Directory.CreateDirectory(Path.Combine(temp.Root, "Packages"));
        File.WriteAllText(
            Path.Combine(temp.Root, "Packages", "manifest.json"),
            """{ "dependencies": { "com.unity.addressables": "1.21.21" } }""");

        // Deliberately NO Assets/AddressableAssetsData/AddressableAssetSettings.asset anywhere
        // in the fixture -- confirmed version, but the expected root file just isn't there.

        using var engine = UnBrambleEngine.Open(temp.Root);
        engine.RunIndex(full: false);

        var result = engine.RunDeadCandidates();

        Assert.False(result.Available);
        Assert.Contains(result.UnavailableReasons, r =>
            r.Contains("Addressables detected", StringComparison.Ordinal) &&
            r.Contains("settings asset was not found in the index", StringComparison.Ordinal));

        // Must be told apart from the unconfirmed-version reason family (distinct message).
        Assert.DoesNotContain(result.UnavailableReasons, r => r.Contains("outside the confirmed range", StringComparison.Ordinal));
        Assert.DoesNotContain(result.UnavailableReasons, r => r.Contains("version could not be determined", StringComparison.Ordinal));
    }

    // ==== path-ref-name-collision and syntactic-text-collision screens must NOT require their
    // ==== source to be live (unlike name-hint-collision/disabled-region-screen, which do). ======

    [Fact]
    public void Finding5_PathRefNameCollision_FromADeadSourceFile_StillScreensTheCandidate()
    {
        // Assets/UI/Menu.uxml is itself unreachable/dead (nothing references it), and carries a
        // second broken path ref naming "orphan.png" -- a filename that matches the genuinely
        // dead Assets/Dead/orphan.png. The screen rule (any path-kind unresolved_refs row whose
        // target text's final path segment matches the candidate's filename) has no "sourced
        // from a live file" qualifier, so the fact that Menu.uxml itself is dead must not matter:
        // orphan.png must be demoted from provenDead to advisoryDead. Requiring the source file
        // to be live here would miss this collision entirely, since Menu.uxml is never live.
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var result = engine.RunDeadCandidates();
        Assert.True(result.Available, string.Join("; ", result.UnavailableReasons));

        // Confirm the premise: Menu.uxml (the ref's SOURCE) is itself unreachable/dead, not
        // screened by anything of its own -- the regression is specifically about whether an
        // unresolved ref sourced from a DEAD file still screens its target.
        Assert.Contains(result.ProvenDead, e => e.Path == "Assets/UI/Menu.uxml");

        Assert.DoesNotContain(result.ProvenDead, e => e.Path == "Assets/Dead/orphan.png");
        var advisory = Assert.Single(result.AdvisoryDead, e => e.Path == "Assets/Dead/orphan.png");
        Assert.Equal(ScreenReasons.PathRefNameCollision, advisory.Reason);
    }

    // ==== zero-symbols screen (ScreenReasons.NoExtractedSymbols) ================================
    // A whole-file platform-gated .cs file (its entire content, not just a call site, wrapped in
    // an #if for a platform never active under the fixture's empty define set) produces zero
    // rows in `symbols` -- Roslyn correctly never compiles a token of it. None of the other
    // screens can fire for it (they all match against the candidate's own declared
    // symbols/attrs/base list, and it has none), so without a dedicated screen it would fall
    // through to provenDead despite being referenced from Foo.Start's own disabled
    // #if UNITY_ANDROID region (`new AndroidWorker()`) -- a false positive of the same
    // asymmetric-risk shape the other screens above guard against. See AndroidWorker.cs's own
    // doc comment for the full story.

    [Fact]
    public void ZeroSymbolsScreen_WholeFilePlatformGatedFile_HasZeroSymbolsRows_ConfirmsFixturePrecondition()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        using var conn = OpenDb(engine.DbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM symbols s
            JOIN files f ON f.id = s.file_id
            WHERE f.path = 'Assets/Scripts/AndroidWorker.cs';
            """;
        var symbolCount = Convert.ToInt64(cmd.ExecuteScalar());

        Assert.Equal(0, symbolCount);
    }

    [Fact]
    public void ZeroSymbolsScreen_WholeFilePlatformGatedFile_NotProvenDead_IsAdvisoryWithNoExtractedSymbols()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var result = engine.RunDeadCandidates();
        Assert.True(result.Available, string.Join("; ", result.UnavailableReasons));

        Assert.DoesNotContain(result.ProvenDead, e => e.Path == "Assets/Scripts/AndroidWorker.cs");
        var advisory = Assert.Single(result.AdvisoryDead, e => e.Path == "Assets/Scripts/AndroidWorker.cs");
        Assert.Equal(ScreenReasons.NoExtractedSymbols, advisory.Reason);
    }

    [Fact]
    public void ZeroSymbolsScreen_ExistingFileWithSymbols_StillScreenedByDisabledRegionScreen_NotByZeroSymbols()
    {
        // Regression guard for the mechanism above: AndroidOnly.cs has a normal, fully-compiled
        // class body -- only its call site in Foo.Start is #if-disabled -- so it has real
        // symbols and must still be screened by the disabled-region-screen, never by the
        // zero-symbols screen.
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        using (var conn = OpenDb(engine.DbPath))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT COUNT(*) FROM symbols s
                JOIN files f ON f.id = s.file_id
                WHERE f.path = 'Assets/Scripts/AndroidOnly.cs';
                """;
            var symbolCount = Convert.ToInt64(cmd.ExecuteScalar());
            Assert.True(symbolCount > 0, "AndroidOnly.cs must still produce real symbol rows (class + method) -- only its caller's call site is disabled, not its own body.");
        }

        var result = engine.RunDeadCandidates();
        Assert.True(result.Available, string.Join("; ", result.UnavailableReasons));

        Assert.DoesNotContain(result.ProvenDead, e => e.Path == "Assets/Scripts/AndroidOnly.cs");
        var advisory = Assert.Single(result.AdvisoryDead, e => e.Path == "Assets/Scripts/AndroidOnly.cs");
        Assert.Equal(ScreenReasons.DisabledRegionScreen, advisory.Reason);
    }

    // ==== who-uses/uses' disabled-region-refs-possible blind spot has the identical zero-symbols
    // ==== gap (HasDisabledRegionNameHintForFile joined against `symbols`, which is empty for a
    // ==== whole-file platform-gated file) -- closed with a filename-stem fallback restricted to
    // ==== files with zero symbol rows. ==========================================================

    [Fact]
    public void WhoUsesAndUses_ZeroSymbolFile_DisabledRegionNameCollision_StillSetsBlindSpotViaFilenameFallback()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var resolution = engine.ResolveQueryTarget("Assets/Scripts/AndroidWorker.cs");
        Assert.NotNull(resolution.Target);

        var whoUses = engine.WhoUses(resolution.Target!, transitive: false, depthCap: 1);
        var uses = engine.Uses(resolution.Target!, transitive: false, depthCap: 1);

        Assert.Contains(BlindSpots.DisabledRegionRefsPossible, whoUses.BlindSpots);
        Assert.Contains(BlindSpots.DisabledRegionRefsPossible, uses.BlindSpots);
    }

    // ==== a semantic-mode assembly whose generated csproj was deleted after indexing must fail
    // ==== the gate, not silently skip the check. ================================================

    [Fact]
    public void Finding7_SemanticModeAssembly_GeneratedCsprojDeletedAfterIndexing_LivenessUnavailable()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);

        using (var engineBefore = UnBrambleEngine.Open(fixture.Root))
        {
            engineBefore.RunIndex(full: false);
            var before = engineBefore.RunDeadCandidates();
            Assert.True(before.Available, string.Join("; ", before.UnavailableReasons));
        }

        // Delete the recorded-semantic assembly's generated csproj -- nothing else changes (no
        // ProjectSettings/Assets file touched), so the generic freshness sweep has nothing to
        // notice; only the dedicated csproj-freshness gate check can catch this.
        File.Delete(Path.Combine(fixture.Root, "Game.csproj"));

        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);
        var result = engine.RunDeadCandidates();

        Assert.False(result.Available);
        Assert.Contains(
            "liveness unavailable: generated csproj missing for a semantic-mode assembly — reopen the project in Unity (or your IDE while Unity is running) to resync it",
            result.UnavailableReasons);
    }

    private static void AssertNeverDead(DeadCandidatesResult result, string path)
    {
        Assert.DoesNotContain(result.ProvenDead, e => e.Path == path);
        Assert.DoesNotContain(result.AdvisoryDead, e => e.Path == path);
    }

    // ==== global liveness invariant: no provenDead file is ever the target of a resolved edge
    // ==== from a live file, and no root is ever provenDead -- must hold by construction,
    // ==== asserted here so it stays held. =======================================================

    [Fact]
    public void GlobalInvariant_NoProvenDeadFile_IsTargetOfAResolvedEdgeFromALiveFile()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var result = engine.RunDeadCandidates();
        Assert.True(result.Available, string.Join("; ", result.UnavailableReasons));

        var provenPaths = result.ProvenDead.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // "Live" here means every file NOT in provenDead and NOT in advisoryDead (screened files
        // are, by construction, seeded into LiveFiles too -- but checking against the STRICTLY
        // live set, i.e. excluding advisory, is the stronger and more interesting form of the
        // invariant: even a merely-screened file's resolved outgoing edges must never land on a
        // provenDead target).
        var advisoryPaths = result.AdvisoryDead.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        using var conn = OpenDb(engine.DbPath);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT sf.path, tf.path
            FROM unified_walk_edges uw
            JOIN files sf ON sf.id = uw.source_file_id
            JOIN files tf ON tf.id = uw.target_file_id;
            """;
        using var reader = cmd.ExecuteReader();
        var violations = new List<string>();
        while (reader.Read())
        {
            var sourcePath = reader.GetString(0);
            var targetPath = reader.GetString(1);
            if (provenPaths.Contains(targetPath) && !provenPaths.Contains(sourcePath) && !advisoryPaths.Contains(sourcePath))
            {
                violations.Add($"{sourcePath} -> {targetPath}");
            }
        }

        Assert.True(violations.Count == 0, $"coherence violation(s): {string.Join(", ", violations)}");

        // (b) roots ∩ provenDead = ∅.
        var rootPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ProjectSettings/EditorBuildSettings.asset", "ProjectSettings/EditorSettings.asset", "ProjectSettings/ProjectSettings.asset",
            "Assets/Resources/LooseConfig.asset", "Assets/Resources/loose.png",
            "Assets/StreamingAssets/payload.bin",
            "Assets/AddressableAssetsData/AddressableAssetSettings.asset",
            "Assets/Scripts/Bootstrap.cs",
        };
        Assert.Empty(rootPaths.Intersect(provenPaths, StringComparer.OrdinalIgnoreCase));
    }

    // ==== Addressables-unavailable variant: unconfirmed version leaves liveness unavailable ====

    [Fact]
    public void DeadCandidates_AddressablesUnconfirmedVersion_LivenessUnavailable_ExactReason()
    {
        using var temp = TempDir.Create();
        Directory.CreateDirectory(Path.Combine(temp.Root, "ProjectSettings"));
        File.WriteAllText(
            Path.Combine(temp.Root, "ProjectSettings", "ProjectVersion.txt"),
            "m_EditorVersion: 2022.3.30f1\nm_EditorVersionWithRevision: 2022.3.30f1 (70558241b701)\n");
        File.WriteAllText(
            Path.Combine(temp.Root, "ProjectSettings", "EditorSettings.asset"),
            "%YAML 1.1\n--- !u!159 &1\nEditorSettings:\n  m_SerializationMode: 2\n");
        Directory.CreateDirectory(Path.Combine(temp.Root, "Packages"));
        File.WriteAllText(
            Path.Combine(temp.Root, "Packages", "manifest.json"),
            """{ "dependencies": { "com.unity.addressables": "2.5.1" } }""");

        using var engine = UnBrambleEngine.Open(temp.Root);
        engine.RunIndex(full: false);

        var result = engine.RunDeadCandidates();

        Assert.False(result.Available);
        Assert.Contains(
            "liveness unavailable: unconfirmed root coverage (Addressables detected, version 2.5.1 outside the confirmed range (Addressables 1.21.x, 2.3.x, 2.8.x))",
            result.UnavailableReasons);
    }

    // ==== stale-csproj gate variant ==============================================================

    [Fact]
    public void DeadCandidates_StaleCsproj_LivenessUnavailable_ExactReason()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);

        // Confirm it's available BEFORE staling anything (sanity baseline).
        using (var engineBefore = UnBrambleEngine.Open(fixture.Root))
        {
            engineBefore.RunIndex(full: false);
            var before = engineBefore.RunDeadCandidates();
            Assert.True(before.Available, string.Join("; ", before.UnavailableReasons));
        }

        // Touch ProjectSettings/ProjectSettings.asset to be newer than the Game.csproj.
        var csprojPath = Path.Combine(fixture.Root, "Game.csproj");
        var configPath = Path.Combine(fixture.Root, "ProjectSettings", "ProjectSettings.asset");
        File.SetLastWriteTimeUtc(csprojPath, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(configPath, DateTime.UtcNow);

        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);
        var result = engine.RunDeadCandidates();

        Assert.False(result.Available);
        Assert.Contains(
            "liveness unavailable: generated csproj older than project configuration — reopen the project in Unity (or your IDE while Unity is running) to resync it",
            result.UnavailableReasons);
    }

    // ==== who-uses/uses csproj-stale blind spot ==================================================
    // BlindSpots.CsprojStale existed in the enum but nothing ever set it -- who-uses/uses answers
    // never surfaced staleness even though the exact same extended-mtime check
    // (CheckCsprojFreshness, reused via UnBrambleEngine.IsAnyCsprojStale) already disqualifies
    // dead-candidates for it (the stale-csproj gate above). who-uses/uses should only add the
    // flag, not go unavailable, so these queries must still succeed with results.

    [Fact]
    public void WhoUsesAndUses_FreshCsproj_SemanticMode_DoesNotSetCsprojStale()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var resolution = engine.ResolveQueryTarget("Assets/Scripts/Foo.cs");
        var whoUses = engine.WhoUses(resolution.Target!, transitive: false, depthCap: 1);
        var uses = engine.Uses(resolution.Target!, transitive: false, depthCap: 1);

        Assert.DoesNotContain("csproj-stale", whoUses.BlindSpots);
        Assert.DoesNotContain("csproj-stale", uses.BlindSpots);
        // Sanity: semantic mode (both csprojs present and fresh) also means no false positive
        // on the unrelated syntactic-mode flag.
        Assert.DoesNotContain("syntactic-assemblies-present", whoUses.BlindSpots);
    }

    [Fact]
    public void WhoUsesAndUses_StaleCsproj_SemanticMode_SetsCsprojStale()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);

        // Same staling recipe as DeadCandidates_StaleCsproj_LivenessUnavailable_ExactReason
        // above: Game.csproj older than ProjectSettings.asset.
        var csprojPath = Path.Combine(fixture.Root, "Game.csproj");
        var configPath = Path.Combine(fixture.Root, "ProjectSettings", "ProjectSettings.asset");
        File.SetLastWriteTimeUtc(csprojPath, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(configPath, DateTime.UtcNow);

        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var resolution = engine.ResolveQueryTarget("Assets/Scripts/Foo.cs");
        var whoUses = engine.WhoUses(resolution.Target!, transitive: false, depthCap: 1);
        var uses = engine.Uses(resolution.Target!, transitive: false, depthCap: 1);

        // The query itself must still succeed -- staleness is advisory here, not disqualifying.
        Assert.NotEmpty(whoUses.Results);
        Assert.Contains("csproj-stale", whoUses.BlindSpots);
        Assert.Contains("csproj-stale", uses.BlindSpots);
    }

    [Fact]
    public void Cli_WhoUsesAndUses_StaleCsproj_Json_IncludesCsprojStaleBlindSpot()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        _ = CliRunner.Run("init", "-p", fixture.Root);

        var csprojPath = Path.Combine(fixture.Root, "Game.csproj");
        var configPath = Path.Combine(fixture.Root, "ProjectSettings", "ProjectSettings.asset");
        File.SetLastWriteTimeUtc(csprojPath, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(configPath, DateTime.UtcNow);

        var (whoUsesExit, whoUsesJson, _) = CliRunner.Run("who-uses", "Assets/Scripts/Foo.cs", "-p", fixture.Root, "--json");
        Assert.Equal(0, whoUsesExit);
        Assert.Contains("\"csproj-stale\"", whoUsesJson);

        var (usesExit, usesJson, _) = CliRunner.Run("uses", "Assets/Scripts/Foo.cs", "-p", fixture.Root, "--json");
        Assert.Equal(0, usesExit);
        Assert.Contains("\"csproj-stale\"", usesJson);
    }

    [Fact]
    public void Cli_WhoUses_StaleCsproj_HumanFooter_MentionsCsprojStale()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        _ = CliRunner.Run("init", "-p", fixture.Root);

        var csprojPath = Path.Combine(fixture.Root, "Game.csproj");
        var configPath = Path.Combine(fixture.Root, "ProjectSettings", "ProjectSettings.asset");
        File.SetLastWriteTimeUtc(csprojPath, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(configPath, DateTime.UtcNow);

        var (exitCode, stdOut, _) = CliRunner.Run("who-uses", "Assets/Scripts/Foo.cs", "-p", fixture.Root);

        Assert.Equal(0, exitCode);
        Assert.Contains("csproj-stale", stdOut);
    }

    // ==== `disabled-region-refs-possible` blind spot ============================================
    // A caller wrapped in `#if UNITY_ANDROID || UNITY_IPHONE` (or similar) is invisible to Roslyn
    // under the desktop/editor defines this analysis compiles with, so who-uses/uses would
    // otherwise give zero signal that a real caller was missed. The fixture mirrors this:
    // Foo.Jump is referenced for real via the onLocalJump/onLevelPoke UnityEvent bindings AND has
    // a second, #if UNITY_ANDROID-gated self-call in Foo.Start (compiled out under the fixture's
    // empty define set) -- see Foo.cs's own comment at that call site.

    [Fact]
    public void WhoUsesSymbol_DisabledRegionNameCollision_SetsDisabledRegionRefsPossible()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var resolution = engine.ResolveCsSymbol("M:Foo.Jump");
        Assert.Equal("M:Foo.Jump", resolution.DocId);

        var answer = engine.WhoUsesSymbol(resolution.DocId!, transitive: false, depthCap: 1);

        // The real reference (the declared UnityEvent binding) must still be shown -- the flag
        // is additive signal, never a substitute for the real edges.
        Assert.Contains(answer.Results, r => r.Kind == "event" && r.RefKind == "declared");
        Assert.Contains(BlindSpots.DisabledRegionRefsPossible, answer.BlindSpots);
    }

    [Fact]
    public void WhoUsesSymbol_NoDisabledRegionNameCollision_DoesNotSetDisabledRegionRefsPossible()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        // CoreUtil.Ping is called once, in live code (Foo.Start), and never appears inside any
        // #if-disabled region anywhere in the fixture -- no false positive.
        var resolution = engine.ResolveCsSymbol("CoreUtil.Ping");
        Assert.NotNull(resolution.DocId);

        var answer = engine.WhoUsesSymbol(resolution.DocId!, transitive: false, depthCap: 1);

        Assert.NotEmpty(answer.Results);
        Assert.DoesNotContain(BlindSpots.DisabledRegionRefsPossible, answer.BlindSpots);
    }

    [Fact]
    public void WhoUsesAndUses_FileShapedQuery_DisabledRegionNameCollisionFallback_UsesDeclaredSymbols()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        // A plain file-shaped query (no single resolved symbol) has no one symbol to check, so
        // Finalize falls back to "any symbol declared in the queried file" (Foo.cs declares
        // Jump, which collides). Exercise both who-uses (direct) and uses (direct) on Foo.cs
        // itself.
        var resolution = engine.ResolveQueryTarget("Assets/Scripts/Foo.cs");
        var whoUses = engine.WhoUses(resolution.Target!, transitive: false, depthCap: 1);
        var uses = engine.Uses(resolution.Target!, transitive: false, depthCap: 1);

        Assert.Contains(BlindSpots.DisabledRegionRefsPossible, whoUses.BlindSpots);
        Assert.Contains(BlindSpots.DisabledRegionRefsPossible, uses.BlindSpots);

        // Negative control in the SAME test: a file with no disabled-region name collision at
        // all (CoreUtil.cs only declares Ping, which never appears in a disabled region) must
        // not set the flag -- confirms the fallback is genuinely per-file, not project-wide.
        var coreUtilResolution = engine.ResolveQueryTarget("Assets/Scripts/Core/CoreUtil.cs");
        var coreUtilWhoUses = engine.WhoUses(coreUtilResolution.Target!, transitive: false, depthCap: 1);
        Assert.DoesNotContain(BlindSpots.DisabledRegionRefsPossible, coreUtilWhoUses.BlindSpots);
    }

    [Fact]
    public void Cli_WhoUses_DisabledRegionNameCollision_Json_IncludesBlindSpot()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        _ = CliRunner.Run("init", "-p", fixture.Root);

        var (exitCode, stdOut, _) = CliRunner.Run("who-uses", "M:Foo.Jump", "-p", fixture.Root, "--json");

        Assert.Equal(0, exitCode);
        Assert.Contains("\"disabled-region-refs-possible\"", stdOut);
    }

    [Fact]
    public void Cli_WhoUses_DisabledRegionNameCollision_HumanFooter_MentionsBlindSpot()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        _ = CliRunner.Run("init", "-p", fixture.Root);

        var (exitCode, stdOut, _) = CliRunner.Run("who-uses", "M:Foo.Jump", "-p", fixture.Root);

        Assert.Equal(0, exitCode);
        Assert.Contains("disabled-region-refs-possible", stdOut);
    }

    [Fact]
    public void Cli_WhoUses_NoDisabledRegionNameCollision_Json_OmitsBlindSpot()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        _ = CliRunner.Run("init", "-p", fixture.Root);

        var (exitCode, stdOut, _) = CliRunner.Run("who-uses", "CoreUtil.Ping", "-p", fixture.Root, "--json");

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("\"disabled-region-refs-possible\"", stdOut);
    }

    // ==== allowlist: seeds both the allowlisted file and its dependency into liveness ============

    [Fact]
    public void DeadCandidates_Allowlist_SeedsBothTheFileAndItsDependency()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);

        var result = engine.RunDeadCandidates();
        Assert.True(result.Available, string.Join("; ", result.UnavailableReasons));

        Assert.Equal(1, result.Roots!.AllowlistCount);
        AssertNeverDead(result, "Assets/Scripts/KeptByReflection.cs");
        AssertNeverDead(result, "Assets/Scripts/KeptDep.cs");
    }

    // ==== CLI surface smoke: --json, --kind, --include-advisory ================================

    [Fact]
    public void Cli_DeadCandidates_Json_ReportsAvailableTrue_AndExpectedBuckets()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        _ = CliRunner.Run("init", "-p", fixture.Root);

        var (exitCode, stdOut, _) = CliRunner.Run("dead-candidates", "-p", fixture.Root, "--json");

        Assert.Equal(0, exitCode);
        Assert.Contains("\"available\":true", stdOut);
        Assert.Contains("\"unbrambleSchema\":1", stdOut);
        Assert.Contains("Assets/Prefabs/Enemy.prefab", stdOut);
        Assert.Contains("Assets/Scripts/Sword.cs", stdOut);
        Assert.Contains("interface-dispatch-guard", stdOut);
    }

    [Fact]
    public void Cli_DeadCandidates_KindCsFilter_OnlyReturnsCsFiles()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        _ = CliRunner.Run("init", "-p", fixture.Root);

        var (exitCode, stdOut, _) = CliRunner.Run("dead-candidates", "-p", fixture.Root, "--json", "--kind", "cs", "--include-advisory");

        Assert.Equal(0, exitCode);
        Assert.Contains("Assets/Scripts/Sword.cs", stdOut);
        Assert.DoesNotContain("Assets/Prefabs/Enemy.prefab", stdOut);
    }

    [Fact]
    public void Cli_DeadCandidates_HumanOutput_ShowsAdvisoryOnlyWithFlag()
    {
        using var fixture = FixtureCopy.Create();
        WriteSemanticModeCsprojs(fixture.Root);
        _ = CliRunner.Run("init", "-p", fixture.Root);

        var (_, withoutFlag, _) = CliRunner.Run("dead-candidates", "-p", fixture.Root);
        Assert.DoesNotContain("Assets/Scripts/Sword.cs", withoutFlag);

        var (_, withFlag, _) = CliRunner.Run("dead-candidates", "-p", fixture.Root, "--include-advisory");
        Assert.Contains("Assets/Scripts/Sword.cs", withFlag);
    }

    // ---- unity-callback-guard screen: closes a false-provenDead result for types Unity/a
    // ---- package invokes through a metadata-only interface or a known callback base class, with
    // ---- no ordinary inbound C# call edge. See
    // ---- LivenessModels.ScreenReasons.UnityCallbackGuard's doc comment. -----------------------

    [Fact]
    public void DeadCandidates_UnityCallbackInterface_IsAdvisoryAndSeedsItsDependency()
    {
        using var fixture = FixtureCopy.Create();
        var stubsPath = Path.Combine(fixture.Root, "Assets", "Scripts", "Core", "UnityStubs.cs");
        File.AppendAllText(stubsPath, "\nnamespace UnityEngine { public interface ISerializationCallbackReceiver { void OnBeforeSerialize(); void OnAfterDeserialize(); } }\n");

        WriteFixtureScript(
            fixture.Root,
            "CallbackDependency.cs",
            "public sealed class CallbackDependency { public static void Touch() { } }",
            "ca220000000000000000000000000002");
        WriteFixtureScript(
            fixture.Root,
            "CallbackOnly.cs",
            "public sealed class CallbackOnly : UnityEngine.ISerializationCallbackReceiver { public void OnBeforeSerialize() { CallbackDependency.Touch(); } public void OnAfterDeserialize() { } }",
            "ca220000000000000000000000000003");
        WriteSemanticModeCsprojs(fixture.Root);

        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);
        var result = engine.RunDeadCandidates();

        Assert.True(result.Available, string.Join("; ", result.UnavailableReasons));
        Assert.Contains(
            result.AdvisoryDead,
            entry => entry.Path == "Assets/Scripts/CallbackOnly.cs"
                && entry.Reason == ScreenReasons.UnityCallbackGuard);
        Assert.DoesNotContain(result.ProvenDead, entry => entry.Path == "Assets/Scripts/CallbackDependency.cs");
    }

    [Fact]
    public void DeadCandidates_UnityCallbackBase_IsAdvisoryAndSeedsItsDependency()
    {
        using var fixture = FixtureCopy.Create();
        var stubsPath = Path.Combine(fixture.Root, "Assets", "Scripts", "Core", "UnityStubs.cs");
        File.AppendAllText(stubsPath, "\nnamespace UnityEditor { public class AssetPostprocessor { } }\n");

        WriteFixtureScript(
            fixture.Root,
            "PostprocessorDependency.cs",
            "public static class PostprocessorDependency { public static void Touch() { } }",
            "ca220000000000000000000000000004");
        WriteFixtureScript(
            fixture.Root,
            "CallbackPostprocessor.cs",
            "public sealed class CallbackPostprocessor : UnityEditor.AssetPostprocessor { public void OnPostprocessAllAssets() { PostprocessorDependency.Touch(); } }",
            "ca220000000000000000000000000005");
        WriteSemanticModeCsprojs(fixture.Root);

        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);
        var result = engine.RunDeadCandidates();

        Assert.True(result.Available, string.Join("; ", result.UnavailableReasons));
        Assert.Contains(
            result.AdvisoryDead,
            entry => entry.Path == "Assets/Scripts/CallbackPostprocessor.cs"
                && entry.Reason == ScreenReasons.UnityCallbackGuard);
        Assert.DoesNotContain(result.ProvenDead, entry => entry.Path == "Assets/Scripts/PostprocessorDependency.cs");
    }

    // ---- helpers (same throwaway-csproj pattern as Milestone08aTests/Milestone08cTests,
    // ---- duplicated locally rather than shared via a common test helper) ----------------------

    private static void WriteFixtureScript(string fixtureRoot, string fileName, string source, string guid)
    {
        var path = Path.Combine(fixtureRoot, "Assets", "Scripts", fileName);
        File.WriteAllText(path, source);
        File.WriteAllText(path + ".meta", $$"""
            fileFormatVersion: 2
            guid: {{guid}}
            MonoImporter:
              externalObjects: {}
              serializedVersion: 2
              defaultReferences: []
              executionOrder: 0
              icon: {instanceID: 0}
              userData:
              assetBundleName:
              assetBundleVariant:
            """);
    }

    private static void WriteSemanticModeCsprojs(string fixtureRoot)
    {
        WriteSemanticModeCsproj(fixtureRoot, "Core");
        WriteSemanticModeCsproj(fixtureRoot, "Game");
    }

    private static void WriteSemanticModeCsproj(string fixtureRoot, string assemblyName)
    {
        var ns = XNamespace.Get("http://schemas.microsoft.com/developer/msbuild/2003");
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var itemGroup = new XElement(
            ns + "ItemGroup",
            paths.Select(p => new XElement(
                ns + "Reference",
                new XAttribute("Include", Path.GetFileNameWithoutExtension(p)),
                new XElement(ns + "HintPath", p))));
        var project = new XElement(ns + "Project", new XAttribute("ToolsVersion", "Current"), itemGroup);
        new XDocument(project).Save(Path.Combine(fixtureRoot, assemblyName + ".csproj"));
    }

    private static Microsoft.Data.Sqlite.SqliteConnection OpenDb(string dbPath)
    {
        var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        return conn;
    }

    private static LivenessLedger LoadLedger()
    {
        var json = File.ReadAllText(ExpectedLivenessPath);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var ledger = JsonSerializer.Deserialize<LivenessLedger>(json, options);
        Assert.NotNull(ledger);
        return ledger!;
    }

    private sealed class LivenessLedger
    {
        [JsonPropertyName("roots")]
        public RootsLedger Roots { get; set; } = new();

        [JsonPropertyName("conventionExcludedCount")]
        public int ConventionExcludedCount { get; set; }

        [JsonPropertyName("conventionExcludedPaths")]
        public List<string> ConventionExcludedPaths { get; set; } = [];

        [JsonPropertyName("provenDead")]
        public List<string> ProvenDead { get; set; } = [];

        [JsonPropertyName("advisoryDead")]
        public List<AdvisoryLedgerEntry> AdvisoryDead { get; set; } = [];

        [JsonPropertyName("mustBeLive")]
        public List<string> MustBeLive { get; set; } = [];
    }

    private sealed class RootsLedger
    {
        [JsonPropertyName("projectSettingsFileCount")]
        public int ProjectSettingsFileCount { get; set; }

        [JsonPropertyName("resourcesFileCount")]
        public int ResourcesFileCount { get; set; }

        [JsonPropertyName("streamingAssetsFileCount")]
        public int StreamingAssetsFileCount { get; set; }

        [JsonPropertyName("entryPointFileCount")]
        public int EntryPointFileCount { get; set; }

        [JsonPropertyName("addressablesDetected")]
        public bool AddressablesDetected { get; set; }
    }

    private sealed class AdvisoryLedgerEntry
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = "";

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = "";
    }
}
