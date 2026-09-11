using UnBramble.Core.Config;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// `init`'s new ignore-file setup step (docs/architecture.md "State directory: `.unbramble/`,
/// not `Library/`"; <c>Program.SetUpIgnoreFiles</c>): idempotently wires `.unbramble/` into
/// whatever VCS the project uses (git's `.gitignore`, Plastic SCM's `ignore.conf`), prints a
/// notice when neither is detected, and always drops a self-ignoring `.unbramble/.gitignore`
/// regardless of which VCS (if any) is present.
/// </summary>
public class InitIgnoreFilesTests
{
    [Fact]
    public void Init_GitMarkerPresent_CreatesGitignoreWithStateDirEntry()
    {
        using var fixture = FixtureCopy.Create();
        CreateGitMarker(fixture.Root);

        var (exitCode, stdOut, _) = CliRunner.Run("init", "-p", fixture.Root);

        Assert.Equal(0, exitCode);
        var gitignorePath = Path.Combine(fixture.Root, ".gitignore");
        Assert.True(File.Exists(gitignorePath));
        Assert.Contains($"{UnBramblePaths.StateDirName}/", File.ReadAllLines(gitignorePath));
        Assert.Contains(".gitignore", stdOut);
    }

    [Fact]
    public void Init_GitMarkerPresent_AppendsToExistingGitignoreWithoutDuplicating()
    {
        using var fixture = FixtureCopy.Create();
        CreateGitMarker(fixture.Root);
        var gitignorePath = Path.Combine(fixture.Root, ".gitignore");
        File.WriteAllText(gitignorePath, "bin/" + Environment.NewLine + "obj/" + Environment.NewLine);

        _ = CliRunner.Run("init", "-p", fixture.Root);
        _ = CliRunner.Run("init", "-p", fixture.Root);

        var lines = File.ReadAllLines(gitignorePath);
        Assert.Contains("bin/", lines);
        Assert.Contains("obj/", lines);
        Assert.Equal(1, lines.Count(l => l.Trim() == $"{UnBramblePaths.StateDirName}/"));
    }

    [Fact]
    public void Init_PlasticMarkerPresent_CreatesIgnoreConfWithStateDirEntry()
    {
        using var fixture = FixtureCopy.Create();
        CreatePlasticMarker(fixture.Root);

        var (exitCode, stdOut, _) = CliRunner.Run("init", "-p", fixture.Root);

        Assert.Equal(0, exitCode);
        var ignoreConfPath = Path.Combine(fixture.Root, "ignore.conf");
        Assert.True(File.Exists(ignoreConfPath));
        Assert.Contains(UnBramblePaths.StateDirName, File.ReadAllLines(ignoreConfPath));
        Assert.Contains("ignore.conf", stdOut);
    }

    [Fact]
    public void Init_PlasticMarkerPresent_IdempotentOnSecondRun()
    {
        using var fixture = FixtureCopy.Create();
        CreatePlasticMarker(fixture.Root);

        _ = CliRunner.Run("init", "-p", fixture.Root);
        _ = CliRunner.Run("init", "-p", fixture.Root);

        var ignoreConfPath = Path.Combine(fixture.Root, "ignore.conf");
        var lines = File.ReadAllLines(ignoreConfPath);
        Assert.Equal(1, lines.Count(l => l.Trim() == UnBramblePaths.StateDirName));
    }

    [Fact]
    public void Init_EmptyGitDirectoryAndValidPlasticMarker_UsesPlasticOnly()
    {
        using var fixture = FixtureCopy.Create();
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".git"));
        CreatePlasticMarker(fixture.Root);

        var (exitCode, stdOut, _) = CliRunner.Run("init", "-p", fixture.Root);

        Assert.Equal(0, exitCode);
        Assert.Contains("Plastic SCM detected", stdOut, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(fixture.Root, "ignore.conf")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, ".gitignore")));
    }

    [Fact]
    public void Init_BothValidMarkersNonInteractive_SkipsRootIgnoreFilesAndExplainsChoice()
    {
        using var fixture = FixtureCopy.Create();
        CreateGitMarker(fixture.Root);
        CreatePlasticMarker(fixture.Root);

        var (exitCode, stdOut, _) = CliRunner.Run("init", "-p", fixture.Root);

        Assert.Equal(0, exitCode);
        Assert.Contains("both Git and Plastic SCM", stdOut, StringComparison.Ordinal);
        Assert.Contains("--vcs plastic", stdOut, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(fixture.Root, ".gitignore")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "ignore.conf")));
    }

    [Fact]
    public void Init_BothValidMarkersWithPlasticOverride_UsesPlasticOnly()
    {
        using var fixture = FixtureCopy.Create();
        CreateGitMarker(fixture.Root);
        CreatePlasticMarker(fixture.Root);

        var (exitCode, stdOut, _) = CliRunner.Run("init", "-p", fixture.Root, "--vcs", "plastic");

        Assert.Equal(0, exitCode);
        Assert.Contains("Plastic SCM selected", stdOut, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(fixture.Root, "ignore.conf")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, ".gitignore")));
    }

    [Fact]
    public void Init_GitdirFileMarker_UsesGitIgnore()
    {
        using var fixture = FixtureCopy.Create();
        File.WriteAllText(Path.Combine(fixture.Root, ".git"), "gitdir: ../metadata/worktree" + Environment.NewLine);

        var (exitCode, stdOut, _) = CliRunner.Run("init", "-p", fixture.Root);

        Assert.Equal(0, exitCode);
        Assert.Contains("Git detected", stdOut, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(fixture.Root, ".gitignore")));
    }

    [Fact]
    public void Init_InvalidVcsOverride_FailsBeforeCreatingProjectState()
    {
        using var fixture = FixtureCopy.Create();

        var (exitCode, _, stdErr) = CliRunner.Run("init", "-p", fixture.Root, "--vcs", "svn");

        Assert.Equal(1, exitCode);
        Assert.Contains("must be git, plastic, both, or none", stdErr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(UnBramblePaths.StateDirFor(fixture.Root)));
    }

    [Fact]
    public void Init_NeitherMarkerPresent_PrintsManualNoticeAndWritesNoIgnoreFile()
    {
        using var fixture = FixtureCopy.Create();

        var (exitCode, stdOut, _) = CliRunner.Run("init", "-p", fixture.Root);

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(Path.Combine(fixture.Root, ".gitignore")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "ignore.conf")));
        Assert.Contains("your VCS's ignore rules manually", stdOut);
    }

    [Fact]
    public void Init_AlwaysWritesSelfIgnoringGitignoreInsideStateDir()
    {
        using var fixture = FixtureCopy.Create();

        _ = CliRunner.Run("init", "-p", fixture.Root);

        var selfIgnorePath = Path.Combine(fixture.Root, UnBramblePaths.StateDirName, ".gitignore");
        Assert.True(File.Exists(selfIgnorePath));
        Assert.Equal("*", File.ReadAllText(selfIgnorePath).Trim());
    }

    [Fact]
    public void Init_StateDirLandsAtProjectRoot_NotInsideLibrary()
    {
        using var fixture = FixtureCopy.Create();

        var (exitCode, _, _) = CliRunner.Run("init", "-p", fixture.Root);

        Assert.Equal(0, exitCode);
        Assert.True(Directory.Exists(Path.Combine(fixture.Root, UnBramblePaths.StateDirName)));
        Assert.True(File.Exists(UnBramblePaths.RelativeTo(fixture.Root, UnBramblePaths.DbRelativePath)));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "Library", "UnBramble")));
    }

    private static void CreateGitMarker(string root)
    {
        var marker = Path.Combine(root, ".git");
        Directory.CreateDirectory(marker);
        File.WriteAllText(Path.Combine(marker, "HEAD"), "ref: refs/heads/main" + Environment.NewLine);
    }

    private static void CreatePlasticMarker(string root)
    {
        var marker = Path.Combine(root, ".plastic");
        Directory.CreateDirectory(marker);
        File.WriteAllText(Path.Combine(marker, "plastic.workspace"), "fixture" + Environment.NewLine);
    }
}
