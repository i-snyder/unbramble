using UnBramble.Core.CSharp;
using UnBramble.Core.Model;

namespace UnBramble.Tests;

/// <summary>Asmdef mapping against the real fixture, plus the loose-script predefined-assembly rules on synthetic trees.</summary>
public class CsAssemblyDiscoveryTests
{
    private static readonly string FixtureRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "MiniProject");

    [Fact]
    public void Discover_Fixture_YieldsCoreAndGameAssemblies_GameReferencesCore()
    {
        var files = new List<CsFileEntry>
        {
            new(1, "Assets/Scripts/Core/Core.asmdef", "66666666666666666666666666666612", FileKind.Asset, IdentityOnly: false),
            new(2, "Assets/Scripts/Core/CoreUtil.cs", null, FileKind.Script, IdentityOnly: false),
            new(3, "Assets/Scripts/Core/UnityStubs.cs", null, FileKind.Script, IdentityOnly: false),
            new(4, "Assets/Scripts/Game.asmdef", "77777777777777777777777777777713", FileKind.Asset, IdentityOnly: false),
            new(5, "Assets/Scripts/Foo.cs", null, FileKind.Script, IdentityOnly: false),
        };

        var (units, warnings) = AssemblyUnitDiscovery.Discover(files, p => Path.Combine(FixtureRoot, p.Replace('/', Path.DirectorySeparatorChar)));
        Assert.Empty(warnings);

        var core = Assert.Single(units, u => u.Name == "Core");
        Assert.Equal(2, core.Scripts.Count);
        Assert.Contains(core.Scripts, s => s.Path == "Assets/Scripts/Core/CoreUtil.cs");
        Assert.Contains(core.Scripts, s => s.Path == "Assets/Scripts/Core/UnityStubs.cs");
        Assert.Empty(core.References);

        var game = Assert.Single(units, u => u.Name == "Game");
        Assert.Single(game.Scripts);
        Assert.Equal("Assets/Scripts/Foo.cs", game.Scripts[0].Path);
        Assert.Contains("Core", game.References);

        var (ordered, orderWarnings) = AssemblyUnitDiscovery.TopologicalOrder(units);
        Assert.Empty(orderWarnings);
        var coreIndex = ordered.ToList().FindIndex(u => u.Name == "Core");
        var gameIndex = ordered.ToList().FindIndex(u => u.Name == "Game");
        Assert.True(coreIndex < gameIndex, "Core must compile before Game (Game references Core)");
    }

    [Fact]
    public void Discover_LooseScripts_AssignedToPredefinedAssembliesByPathRule()
    {
        var files = new List<CsFileEntry>
        {
            new(1, "Assets/Plugins/Vendor/Lib.cs", null, FileKind.Script, IdentityOnly: false),
            new(2, "Assets/Editor/BuildStep.cs", null, FileKind.Script, IdentityOnly: false),
            new(3, "Assets/Scripts/Loose/Bare.cs", null, FileKind.Script, IdentityOnly: false),
        };

        var (units, warnings) = AssemblyUnitDiscovery.Discover(files, _ => throw new InvalidOperationException("no asmdef/asmref content should be read for this synthetic set"));
        Assert.Empty(warnings);

        var byName = units.ToDictionary(u => u.Name);
        Assert.True(byName.TryGetValue(AssemblyUnitDiscovery.PredefinedFirstPass, out var firstPass));
        Assert.Contains(firstPass!.Scripts, s => s.Path == "Assets/Plugins/Vendor/Lib.cs");

        Assert.True(byName.TryGetValue(AssemblyUnitDiscovery.PredefinedEditor, out var editor));
        Assert.Contains(editor!.Scripts, s => s.Path == "Assets/Editor/BuildStep.cs");

        Assert.True(byName.TryGetValue(AssemblyUnitDiscovery.PredefinedAssemblyCSharp, out var main));
        Assert.Contains(main!.Scripts, s => s.Path == "Assets/Scripts/Loose/Bare.cs");
    }

    [Fact]
    public void TopologicalOrder_Cycle_FallsBackToDeclarationOrder_WithWarning_NeverThrows()
    {
        var a = new AssemblyUnit("A", 1, "A.asmdef", ["B"], [], false);
        var b = new AssemblyUnit("B", 2, "B.asmdef", ["A"], [], false);

        var (ordered, warnings) = AssemblyUnitDiscovery.TopologicalOrder([a, b]);

        Assert.Equal(2, ordered.Count);
        Assert.NotEmpty(warnings);
    }
}
