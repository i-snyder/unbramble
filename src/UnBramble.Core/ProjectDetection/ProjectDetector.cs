namespace UnBramble.Core.ProjectDetection;

/// <summary>
/// Finds the root of a Unity project by walking up parent directories looking for
/// ProjectSettings/ProjectVersion.txt — a marker every Unity project already has, the same
/// way git walks up looking for .git/.
/// </summary>
public static class ProjectDetector
{
    /// <summary>
    /// Walks up from <paramref name="startPath"/> (a file or directory) looking for a
    /// directory containing ProjectSettings/ProjectVersion.txt. Returns the project root
    /// (the directory containing ProjectSettings/), or null if none is found before hitting
    /// the filesystem root.
    /// </summary>
    public static string? FindProjectRoot(string startPath)
    {
        var current = Directory.Exists(startPath)
            ? new DirectoryInfo(startPath)
            : new FileInfo(startPath).Directory;

        while (current is not null)
        {
            var marker = Path.Combine(current.FullName, "ProjectSettings", "ProjectVersion.txt");
            if (File.Exists(marker))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    /// <summary>
    /// Reads the Unity editor version out of an already-detected project's
    /// ProjectSettings/ProjectVersion.txt. Returns "unknown" if the file is missing,
    /// unreadable, or doesn't contain the expected line.
    /// </summary>
    public static string ReadUnityVersion(string projectRoot)
    {
        var path = Path.Combine(projectRoot, "ProjectSettings", "ProjectVersion.txt");
        try
        {
            var content = File.ReadAllText(path);
            var match = RegexPatterns.EditorVersion().Match(content);
            return match.Success ? match.Groups[1].Value : "unknown";
        }
        catch (IOException)
        {
            return "unknown";
        }
        catch (UnauthorizedAccessException)
        {
            return "unknown";
        }
    }
}
