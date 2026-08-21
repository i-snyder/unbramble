using UnBramble.Core.Config;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

public class UninstallCommandTests
{
    [Fact]
    public void Uninstall_UnchangedSetup_RestoresOriginalBytesAndRemovesOwnedState()
    {
        using var fixture = FixtureCopy.Create();
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".git"));
        var agentsPath = Path.Combine(fixture.Root, "AGENTS.md");
        var gitignorePath = Path.Combine(fixture.Root, ".gitignore");
        var claudePath = Path.Combine(fixture.Root, "CLAUDE.md");
        var originalAgents = "# Project agents\r\n\r\nKeep this exact.\r\n"u8.ToArray();
        var originalGitignore = "Library/\r\nTemp/\r\n"u8.ToArray();
        var originalClaude = "# Claude\r\n\r\nUser-owned instructions.\r\n"u8.ToArray();
        File.WriteAllBytes(agentsPath, originalAgents);
        File.WriteAllBytes(gitignorePath, originalGitignore);
        File.WriteAllBytes(claudePath, originalClaude);

        AssertSuccess(CliRunner.Run("init", "-p", fixture.Root));
        var uninstall = CliRunner.Run("uninstall", "-p", fixture.Root, "--yes");

        AssertSuccess(uninstall);
        Assert.Empty(uninstall.StdErr);
        Assert.Equal(originalAgents, File.ReadAllBytes(agentsPath));
        Assert.Equal(originalGitignore, File.ReadAllBytes(gitignorePath));
        Assert.Equal(originalClaude, File.ReadAllBytes(claudePath));
        Assert.False(Directory.Exists(UnBramblePaths.StateDirFor(fixture.Root)));
        Assert.Contains("Uninstall: complete", uninstall.StdOut);
        Assert.Contains("CLI: remains installed at", uninstall.StdOut);
        Assert.DoesNotContain("unbramble uninstall --machine", uninstall.StdOut);
    }

    [Fact]
    public void Uninstall_LaterEdits_RemovesOwnedContentAndPreservesUserChanges()
    {
        using var fixture = FixtureCopy.Create();
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".git"));
        AssertSuccess(CliRunner.Run("init", "-p", fixture.Root));

        var agentsPath = Path.Combine(fixture.Root, "AGENTS.md");
        var gitignorePath = Path.Combine(fixture.Root, ".gitignore");
        var claudePath = Path.Combine(fixture.Root, "CLAUDE.md");
        File.AppendAllText(agentsPath, Environment.NewLine + "## Added later" + Environment.NewLine + "Keep me." + Environment.NewLine);
        File.AppendAllText(gitignorePath, "Build/" + Environment.NewLine);
        File.AppendAllText(claudePath, Environment.NewLine + "A later Claude note." + Environment.NewLine);

        var uninstall = CliRunner.Run("uninstall", "-p", fixture.Root, "--yes");

        AssertSuccess(uninstall);
        var agents = File.ReadAllText(agentsPath);
        Assert.DoesNotContain("unbramble:begin", agents);
        Assert.DoesNotContain("# AGENTS.md", agents);
        Assert.Contains("## Added later", agents);
        Assert.Contains("Keep me.", agents);
        Assert.Equal(["Build/"], File.ReadAllLines(gitignorePath));
        Assert.DoesNotContain("@AGENTS.md", File.ReadAllText(claudePath));
        Assert.Contains("A later Claude note.", File.ReadAllText(claudePath));
        Assert.Contains("later edits", uninstall.StdOut);
        Assert.False(Directory.Exists(UnBramblePaths.StateDirFor(fixture.Root)));
    }

    [Fact]
    public void Uninstall_PreReceiptProject_UsesOwnershipMarkersWithoutDeletingOtherContent()
    {
        using var fixture = FixtureCopy.Create();
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".git"));
        AssertSuccess(CliRunner.Run("init", "-p", fixture.Root));
        File.Delete(Path.Combine(
            UnBramblePaths.StateDirFor(fixture.Root),
            UnBramblePaths.ProjectInstallStateFileName));
        File.AppendAllText(
            Path.Combine(fixture.Root, "AGENTS.md"),
            Environment.NewLine + "## User section" + Environment.NewLine + "Preserve this." + Environment.NewLine);

        var uninstall = CliRunner.Run("uninstall", "-p", fixture.Root, "--yes");

        AssertSuccess(uninstall);
        var agents = File.ReadAllText(Path.Combine(fixture.Root, "AGENTS.md"));
        Assert.DoesNotContain("unbramble:begin", agents);
        Assert.Contains("## User section", agents);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "CLAUDE.md")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, ".gitignore")));
        Assert.False(Directory.Exists(UnBramblePaths.StateDirFor(fixture.Root)));
    }

    [Fact]
    public void Uninstall_OutsideUnityProject_FailsWithoutDeletingAnything()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"unbramble-uninstall-not-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var sentinel = Path.Combine(directory, ".unbramble", "keep.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(sentinel)!);
        File.WriteAllText(sentinel, "keep");
        try
        {
            var (exitCode, _, stdErr) = CliRunner.Run("uninstall", "-p", directory);

            Assert.Equal(1, exitCode);
            Assert.Contains("no Unity project found", stdErr);
            Assert.Contains(
                $"{Environment.NewLine}If you want to uninstall UnBramble from this machine, run 'unbramble uninstall --machine'.{Environment.NewLine}",
                stdErr);
            Assert.True(File.Exists(sentinel));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Uninstall_WithoutYesInNonInteractiveCaller_PrintsPlanAndChangesNothing()
    {
        using var fixture = FixtureCopy.Create();
        AssertSuccess(CliRunner.Run("init", "-p", fixture.Root));
        var stateDir = UnBramblePaths.StateDirFor(fixture.Root);
        var agentsPath = Path.Combine(fixture.Root, "AGENTS.md");

        var (exitCode, stdOut, stdErr) = CliRunner.Run("uninstall", "-p", fixture.Root);

        Assert.Equal(1, exitCode);
        Assert.Contains("Uninstall project", stdOut);
        Assert.Contains("with -y or --yes", stdErr);
        Assert.DoesNotContain("error: error:", stdErr);
        Assert.True(Directory.Exists(stateDir));
        Assert.Contains("unbramble:begin", File.ReadAllText(agentsPath));
    }

    [Fact]
    public void Uninstall_ShortYesFlag_BypassesConfirmation()
    {
        using var fixture = FixtureCopy.Create();
        AssertSuccess(CliRunner.Run("init", "-p", fixture.Root));

        var uninstall = CliRunner.Run("uninstall", "-p", fixture.Root, "-y");

        AssertSuccess(uninstall);
        Assert.Contains("Confirmation: accepted (-y/--yes).", uninstall.StdOut);
        Assert.False(Directory.Exists(UnBramblePaths.StateDirFor(fixture.Root)));
    }

    private static void AssertSuccess((int ExitCode, string StdOut, string StdErr) result) =>
        Assert.True(
            result.ExitCode == 0,
            $"expected exit 0, got {result.ExitCode}\n-- stdout --\n{result.StdOut}\n-- stderr --\n{result.StdErr}");
}
