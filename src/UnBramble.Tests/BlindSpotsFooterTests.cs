using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// Asset-only answers must not carry unrelated C# remediation. Symbol/script queries retain the
/// full caveat and diagnosis behavior because syntactic assemblies can affect those results.
/// </summary>
public class BlindSpotsFooterTests
{
    [Fact]
    public void WhoUses_ProvenAssetAnswer_SuppressesIrrelevantCsCaveats()
    {
        using var fixture = FixtureCopy.Create();
        var (exit, stdOut, _) = CliRunner.Run("who-uses", "Assets/Materials/Rock.mat", "-p", fixture.Root);

        Assert.Equal(0, exit);
        // Asset/string/reflection caveats remain, but C# assembly remediation is irrelevant.
        Assert.Contains("blind spots:", stdOut);
        Assert.DoesNotContain("syntactic assemblies:", stdOut);
        Assert.DoesNotContain("diagnosis + remediation:", stdOut);
        Assert.DoesNotContain("needs .csproj:", stdOut);
        Assert.DoesNotContain("open the project in the Unity Editor", stdOut);
    }

    [Fact]
    public void WhoUses_ScriptGuidWithOnlySerializedReferencers_SuppressesIrrelevantCsCaveats()
    {
        using var fixture = FixtureCopy.Create();
        var (exit, stdOut, _) = CliRunner.Run("who-uses", "14141414141414141414141414141404", "-p", fixture.Root);

        Assert.Equal(0, exit);
        Assert.Contains("m_Script", stdOut);
        Assert.DoesNotContain("syntactic-assemblies-present", stdOut);
        Assert.DoesNotContain("syntactic assemblies:", stdOut);
        Assert.DoesNotContain("needs .csproj:", stdOut);
    }

    [Fact]
    public void WhoUses_ProvenAssetAnswer_VerboseStillSuppressesIrrelevantCsDiagnoses()
    {
        using var fixture = FixtureCopy.Create();
        var (exit, stdOut, _) = CliRunner.Run("who-uses", "Assets/Materials/Rock.mat", "-p", fixture.Root, "--verbose");

        Assert.Equal(0, exit);
        Assert.DoesNotContain("needs .csproj:", stdOut);
        Assert.DoesNotContain("diagnosis + remediation:", stdOut);
    }

    [Fact]
    public void WhoUsesSymbol_PossibleFalseNegative_DiagnosesShownWithoutVerbose()
    {
        using var fixture = FixtureCopy.Create();
        File.WriteAllText(
            Path.Combine(fixture.Root, "Assets", "Scripts", "Lonely.cs"),
            "public class Lonely\n{\n    public void NobodyCallsThis()\n    {\n    }\n}\n");
        File.WriteAllText(
            Path.Combine(fixture.Root, "Assets", "Scripts", "Lonely.cs.meta"),
            "fileFormatVersion: 2\nguid: 10ade10ade10ade10ade10ade10ade10\nMonoImporter:\n  externalObjects: {}\n");

        var (exit, stdOut, _) = CliRunner.Run("who-uses", "Lonely.NobodyCallsThis", "-p", fixture.Root);

        Assert.Equal(0, exit);
        // The 0-proven-caller case is exactly what the warning + diagnoses exist to explain —
        // they must stay in the default (non-verbose) rendering here.
        Assert.Contains("may be a false negative", stdOut);
        Assert.Contains("needs .csproj:", stdOut);
        Assert.DoesNotContain("diagnosis + remediation:", stdOut);
    }
}
