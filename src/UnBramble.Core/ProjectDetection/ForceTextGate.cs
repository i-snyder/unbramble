using UnBramble.Core.Exceptions;

namespace UnBramble.Core.ProjectDetection;

/// <summary>
/// Startup assertion, required before every command that touches the index: the target
/// project must have ProjectSettings/EditorSettings.asset showing Force Text serialization
/// (m_SerializationMode: 2). UnBramble's YAML/GUID parsing only works on text-serialized
/// assets. Fails strictly even on a missing field/file rather than assuming compliance.
/// </summary>
public static class ForceTextGate
{
    /// <exception cref="ForceTextNotEnabledException">
    /// The project is not using Force Text serialization, or that could not be verified.
    /// </exception>
    public static void Assert(string projectRoot)
    {
        var path = Path.Combine(projectRoot, "ProjectSettings", "EditorSettings.asset");

        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            throw ForceTextNotEnabledException.CouldNotVerify(
                "ProjectSettings/EditorSettings.asset is missing or unreadable");
        }
        catch (DirectoryNotFoundException)
        {
            throw ForceTextNotEnabledException.CouldNotVerify(
                "ProjectSettings/EditorSettings.asset is missing or unreadable");
        }
        catch (IOException)
        {
            throw ForceTextNotEnabledException.CouldNotVerify(
                "ProjectSettings/EditorSettings.asset is missing or unreadable");
        }
        catch (UnauthorizedAccessException)
        {
            throw ForceTextNotEnabledException.CouldNotVerify(
                "ProjectSettings/EditorSettings.asset is missing or unreadable");
        }

        if (!RegexPatterns.ForceTextMode().IsMatch(content))
        {
            throw new ForceTextNotEnabledException();
        }
    }
}
