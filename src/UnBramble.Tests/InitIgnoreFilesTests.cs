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
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".git"));

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
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".git"));
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
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".plastic"));

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
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".plastic"));

        _ = CliRunner.Run("init", "-p", fixture.Root);
        _ = CliRunner.Run("init", "-p", fixture.Root);

        var ignoreConfPath = Path.Combine(fixture.Root, "ignore.conf");
        var lines = File.ReadAllLines(ignoreConfPath);
        Assert.Equal(1, lines.Count(l => l.Trim() == UnBramblePaths.StateDirName));
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
}
