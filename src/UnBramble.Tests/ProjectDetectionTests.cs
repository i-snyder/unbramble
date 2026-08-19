using UnBramble.Core.ProjectDetection;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

public class ProjectDetectionTests
{
    [Fact]
    public void FindProjectRoot_FromNestedDirectory_WalksUpToProjectRoot()
    {
        using var fixture = FixtureCopy.Create();
        var nested = fixture.Combine("Assets", "Scripts", "Core");

        var found = ProjectDetector.FindProjectRoot(nested);

        Assert.NotNull(found);
        Assert.Equal(Path.GetFullPath(fixture.Root), Path.GetFullPath(found), ignoreCase: true);
    }

    [Fact]
    public void FindProjectRoot_OutsideAnyProject_ReturnsNull()
    {
        var isolated = Path.Combine(Path.GetTempPath(), "unbramble-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolated);
        try
        {
            Assert.Null(ProjectDetector.FindProjectRoot(isolated));
        }
        finally
        {
            Directory.Delete(isolated, recursive: true);
        }
    }

    [Fact]
    public void Cli_OutsideAnyProject_ExitsOneWithDocumentedMessage()
    {
        var isolated = Path.Combine(Path.GetTempPath(), "unbramble-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolated);
        try
        {
            var (exitCode, _, stdErr) = CliRunner.Run("stats", "-p", isolated);

            Assert.Equal(1, exitCode);
            Assert.Contains("not inside a Unity project", stdErr, StringComparison.Ordinal);
            Assert.Contains("ProjectSettings/ProjectVersion.txt", stdErr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(isolated, recursive: true);
        }
    }
}
