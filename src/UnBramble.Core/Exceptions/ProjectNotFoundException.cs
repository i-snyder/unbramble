namespace UnBramble.Core.Exceptions;

/// <summary>
/// Thrown when no Unity project (ProjectSettings/ProjectVersion.txt) could be found by
/// walking up from the given start directory.
/// </summary>
public sealed class ProjectNotFoundException : UnBrambleException
{
    public ProjectNotFoundException(string startPath)
        : base($"not inside a Unity project (no ProjectSettings/ProjectVersion.txt found walking up from {startPath})")
    {
    }
}
