using UnBramble.Core;
using UnBramble.Core.Model;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// `uses --missing-only` (over all fixture sources), `stats --unresolved`, and the index
/// summary must all report the same unresolved-ref counts; builtins excluded everywhere. All
/// three surfaces are exercised through the actual CLI (Program.Main) so this test also proves
/// they consume the same canonical unresolved_refs view rather than parallel ad-hoc counts.
///
/// Menu.uxml carries a second broken path ref (`orphan.png`, unresolvable from Menu.uxml's own
/// directory) so the path-ref-name-collision liveness screen has a collision case sourced from
/// a genuinely dead file to catch -- Menu.uxml contributes 2 path-kind unresolved items instead
/// of 1.
///
/// Two of the three guid-kind unresolved items come from the Addressables settings/group
/// fixtures' own `m_Script` guids -- boilerplate MonoBehaviour identity fields, deliberately
/// left unresolved in this tiny fixture since it doesn't vendor the real Addressables package;
/// the group's script guid is the real confirmed `AddressableAssetGroup` guid, hardcoded
/// verbatim. Their own top-level `m_GUID:` self-identity field is not a third unresolved item --
/// it is excluded from ref extraction entirely (RegexPatterns.AddressablesGroupSelfGuidField),
/// so it never reaches `refs` at all, resolved or not.
/// </summary>
public class UnresolvedAccountingTests
{
    [Fact]
    public void UnresolvedAccounting_AgreesAcrossUsesMissingOnly_StatsUnresolved_AndIndexSummary()
    {
        using var fixture = FixtureCopy.Create();
        var init = CliRunner.Run("init", "-p", fixture.Root);
        Assert.Equal(0, init.ExitCode);

        // uses --missing-only, run over every non-folder, non-identity-only fixture source:
        // the union across all of them must be exactly the 2 known-broken refs.
        using (var engine = UnBrambleEngine.Open(fixture.Root))
        {
            var sources = engine.GetAllFiles().Where(f => !f.IdentityOnly && f.Kind is not FileKind.Folder).ToList();
            var foundGuidUnresolved = false;
            var foundPathUnresolved = false;
            var totalUnresolvedAcrossSources = 0;

            foreach (var file in sources)
            {
                var result = CliRunner.Run("uses", file.Path, "-p", fixture.Root, "--missing-only");
                if (file.Path.Equals("Assets/Scenes/Level.unity", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.Equal(0, result.ExitCode);
                    Assert.Contains("deadbeefdeadbeefdeadbeefdeadbeef", result.StdOut, StringComparison.OrdinalIgnoreCase);
                    foundGuidUnresolved = true;
                    totalUnresolvedAcrossSources++;
                }
                else if (file.Path.Equals("Assets/UI/Menu.uxml", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.Equal(0, result.ExitCode);
                    Assert.Contains("Ghost.uxml", result.StdOut, StringComparison.OrdinalIgnoreCase);
                    foundPathUnresolved = true;
                    totalUnresolvedAcrossSources++;
                }
                else if (file.Path.Equals("Assets/AddressableAssetsData/AddressableAssetSettings.asset", StringComparison.OrdinalIgnoreCase))
                {
                    // Boilerplate m_Script guid, deliberately unresolved in this fixture (see class doc).
                    Assert.Equal(0, result.ExitCode);
                    Assert.Contains("a55e7700000000000000000000000005", result.StdOut, StringComparison.OrdinalIgnoreCase);
                    totalUnresolvedAcrossSources++;
                }
                else if (file.Path.Equals("Assets/AddressableAssetsData/AssetGroups/SomeGroup.asset", StringComparison.OrdinalIgnoreCase))
                {
                    // The real confirmed AddressableAssetGroup script guid, hardcoded verbatim,
                    // unresolved since the fixture doesn't vendor the real Addressables package.
                    Assert.Equal(0, result.ExitCode);
                    Assert.Contains("bbb281ee3bf0b054c82ac2347e9e782c", result.StdOut, StringComparison.OrdinalIgnoreCase);
                    totalUnresolvedAcrossSources++;
                }
                else
                {
                    Assert.Equal(0, result.ExitCode);
                    Assert.Equal("", result.StdOut);
                }
            }

            Assert.True(foundGuidUnresolved);
            Assert.True(foundPathUnresolved);
            Assert.Equal(4, totalUnresolvedAcrossSources);
        }

        // stats --unresolved: same 2 items, grouped by source.
        var stats = CliRunner.Run("stats", "-p", fixture.Root, "--unresolved");
        Assert.Equal(0, stats.ExitCode);
        Assert.Contains("Assets/Scenes/Level.unity", stats.StdOut);
        Assert.Contains("deadbeefdeadbeefdeadbeefdeadbeef", stats.StdOut, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Assets/UI/Menu.uxml", stats.StdOut);
        Assert.Contains("Ghost.uxml", stats.StdOut);
        Assert.Contains("Assets/AddressableAssetsData/AddressableAssetSettings.asset", stats.StdOut);
        Assert.Contains("Assets/AddressableAssetsData/AssetGroups/SomeGroup.asset", stats.StdOut);

        // Builtins never counted as unresolved anywhere.
        Assert.DoesNotContain("0000000000000000e000000000000000", stats.StdOut);
        Assert.DoesNotContain("0000000000000000f000000000000000", stats.StdOut);

        // The group's own top-level self-identity guid must NEVER surface as unresolved -- it's
        // excluded from ref extraction entirely, not merely hidden from this particular view.
        Assert.DoesNotContain("a55e7700000000000000000000000003", stats.StdOut, StringComparison.OrdinalIgnoreCase);

        // index's summary line: same canonical count (5 -- Menu.uxml now contributes 2 path-kind
        // unresolved rows instead of 1, see this class's doc comment).
        var index = CliRunner.Run("index", "-p", fixture.Root);
        Assert.Equal(0, index.ExitCode);
        Assert.Contains("5 unresolved refs", index.StdOut);
    }

    [Fact]
    public void StatsJson_EdgeCounts_ExcludeBuiltinsFromUnresolved()
    {
        using var fixture = FixtureCopy.Create();
        _ = CliRunner.Run("init", "-p", fixture.Root);

        using var engine = UnBrambleEngine.Open(fixture.Root);
        var stats = engine.GetStats();

        Assert.Equal(3, stats.Edges.GuidUnresolved);
        // Menu.uxml's second broken path ref (`orphan.png`) is a second path-kind unresolved
        // row (see this class's doc comment).
        Assert.Equal(2, stats.Edges.PathUnresolved);
        // Assets/Dead/Orphan.mat adds a fourth builtin-shader guid usage on top of Level.unity's
        // two and Rock.mat's one.
        Assert.Equal(4, stats.Edges.GuidBuiltin);
        Assert.Equal(5, stats.Edges.TotalUnresolved);
    }
}
