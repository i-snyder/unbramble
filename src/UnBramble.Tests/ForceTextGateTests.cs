using UnBramble.Core.Exceptions;
using UnBramble.Core.ProjectDetection;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

public class ForceTextGateTests
{
    [Fact]
    public void Assert_NonForceTextSerializationMode_Throws()
    {
        using var fixture = FixtureCopy.Create();
        var editorSettingsPath = fixture.Combine("ProjectSettings", "EditorSettings.asset");
        var content = File.ReadAllText(editorSettingsPath).Replace("m_SerializationMode: 2", "m_SerializationMode: 0");
        File.WriteAllText(editorSettingsPath, content);

        var ex = Assert.Throws<ForceTextNotEnabledException>(() => ForceTextGate.Assert(fixture.Root));
        Assert.Contains("Force Text", ex.Message, StringComparison.Ordinal);
        Assert.Contains("UnBramble's YAML parsing requires text-serialized assets", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Assert_MissingEditorSettings_ThrowsCouldNotVerify()
    {
        using var fixture = FixtureCopy.Create();
        File.Delete(fixture.Combine("ProjectSettings", "EditorSettings.asset"));

        var ex = Assert.Throws<ForceTextNotEnabledException>(() => ForceTextGate.Assert(fixture.Root));
        Assert.Contains("could not verify", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_NonForceTextSerializationMode_ExitsOneWithDocumentedMessage()
    {
        using var fixture = FixtureCopy.Create();
        var editorSettingsPath = fixture.Combine("ProjectSettings", "EditorSettings.asset");
        var content = File.ReadAllText(editorSettingsPath).Replace("m_SerializationMode: 2", "m_SerializationMode: 0");
        File.WriteAllText(editorSettingsPath, content);

        var (exitCode, _, stdErr) = CliRunner.Run("index", "-p", fixture.Root);

        Assert.Equal(1, exitCode);
        Assert.Contains("Force Text", stdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Cli_MissingEditorSettings_ExitsOneWithDocumentedMessage()
    {
        using var fixture = FixtureCopy.Create();
        File.Delete(fixture.Combine("ProjectSettings", "EditorSettings.asset"));

        var (exitCode, _, stdErr) = CliRunner.Run("index", "-p", fixture.Root);

        Assert.Equal(1, exitCode);
        Assert.Contains("could not verify", stdErr, StringComparison.Ordinal);
    }
}
