using System.Text.Json;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// `init`'s agent-discovery instruction step (docs/architecture.md; `AgentInstructionsSetup`):
/// idempotently writes an unbramble-owned managed block into `AGENTS.md` and, when safe, a
/// `CLAUDE.md` shim pointing at it -- so a fresh coding-agent session with no MCP server or
/// prompt injection can still discover this tool by reading files it already reads on startup.
/// </summary>
public class AgentInstructionsTests
{
    [Fact]
    public void Init_FreshProject_CreatesAgentsMdWithMarkersAndClaudeShim()
    {
        using var fixture = FixtureCopy.Create();

        var (exitCode, stdOut, _) = CliRunner.Run("init", "-p", fixture.Root);

        Assert.Equal(0, exitCode);
        var agentsPath = Path.Combine(fixture.Root, "AGENTS.md");
        Assert.True(File.Exists(agentsPath));
        var agentsText = File.ReadAllText(agentsPath);
        Assert.Contains("<!-- unbramble:begin", agentsText);
        Assert.Contains("<!-- unbramble:end -->", agentsText);
        Assert.Contains("## unbramble", agentsText);
        Assert.Contains("audit-assets", agentsText);
        Assert.Contains("--fail-if-found", agentsText);
        Assert.Contains("--build-reachable-only", agentsText);

        var claudePath = Path.Combine(fixture.Root, "CLAUDE.md");
        Assert.True(File.Exists(claudePath));
        Assert.Contains("AGENTS.md", File.ReadAllText(claudePath));

        Assert.Contains("AGENTS.md", stdOut);
        Assert.Contains("CLAUDE.md", stdOut);
    }

    [Fact]
    public void Init_RunTwice_FilesAreByteIdenticalAndBlockAppearsOnce()
    {
        using var fixture = FixtureCopy.Create();

        _ = CliRunner.Run("init", "-p", fixture.Root);
        var agentsPath = Path.Combine(fixture.Root, "AGENTS.md");
        var claudePath = Path.Combine(fixture.Root, "CLAUDE.md");
        var agentsAfterFirstRun = File.ReadAllText(agentsPath);
        var claudeAfterFirstRun = File.ReadAllText(claudePath);

        _ = CliRunner.Run("init", "-p", fixture.Root);

        Assert.Equal(agentsAfterFirstRun, File.ReadAllText(agentsPath));
        Assert.Equal(claudeAfterFirstRun, File.ReadAllText(claudePath));

        var beginCount = CountOccurrences(agentsAfterFirstRun, "<!-- unbramble:begin");
        Assert.Equal(1, beginCount);
    }

    [Fact]
    public void Init_PreExistingAgentsMdWithUnrelatedContent_PreservesContentAndAppendsBlockOnce()
    {
        using var fixture = FixtureCopy.Create();
        var agentsPath = Path.Combine(fixture.Root, "AGENTS.md");
        var original = "# AGENTS.md" + Environment.NewLine + Environment.NewLine +
            "## Build" + Environment.NewLine + "Run `dotnet build`." + Environment.NewLine;
        File.WriteAllText(agentsPath, original);

        var (exitCode, _, _) = CliRunner.Run("init", "-p", fixture.Root);

        Assert.Equal(0, exitCode);
        var result = File.ReadAllText(agentsPath);
        Assert.Contains(original.TrimEnd(), result);
        Assert.Equal(1, CountOccurrences(result, "<!-- unbramble:begin"));
    }

    [Fact]
    public void Init_StaleVersionMarkerBlock_RefreshesToCurrentVersionAndBody()
    {
        using var fixture = FixtureCopy.Create();
        var agentsPath = Path.Combine(fixture.Root, "AGENTS.md");
        var outsideContentBefore = "Some notes before.";
        var outsideContentAfter = "Some notes after.";
        var stale =
            "# AGENTS.md" + Environment.NewLine + Environment.NewLine +
            outsideContentBefore + Environment.NewLine + Environment.NewLine +
            "<!-- unbramble:begin v0.0.1 -->" + Environment.NewLine +
            "old stale body" + Environment.NewLine +
            "<!-- unbramble:end -->" + Environment.NewLine + Environment.NewLine +
            outsideContentAfter + Environment.NewLine;
        File.WriteAllText(agentsPath, stale);

        var (exitCode, _, _) = CliRunner.Run("init", "-p", fixture.Root);

        Assert.Equal(0, exitCode);
        var result = File.ReadAllText(agentsPath);
        Assert.DoesNotContain("v0.0.1", result);
        Assert.DoesNotContain("old stale body", result);
        Assert.Contains("## unbramble", result);
        Assert.Contains(outsideContentBefore, result);
        Assert.Contains(outsideContentAfter, result);
    }

    [Fact]
    public void Init_UnmarkedUnbrambleHeading_MigratesIntoManagedBlock()
    {
        using var fixture = FixtureCopy.Create();
        var agentsPath = Path.Combine(fixture.Root, "AGENTS.md");
        var original =
            "# AGENTS.md" + Environment.NewLine + Environment.NewLine +
            "## Build" + Environment.NewLine + "Run `dotnet build`." + Environment.NewLine + Environment.NewLine +
            "## unbramble" + Environment.NewLine + "Some hand-written notes about unbramble." + Environment.NewLine + Environment.NewLine +
            "## Testing" + Environment.NewLine + "Run `dotnet test`." + Environment.NewLine;
        File.WriteAllText(agentsPath, original);

        var (exitCode, stdOut, _) = CliRunner.Run("init", "-p", fixture.Root);

        Assert.Equal(0, exitCode);
        var result = File.ReadAllText(agentsPath);
        Assert.Contains("## Build", result);
        Assert.Contains("Run `dotnet build`.", result);
        Assert.Contains("## Testing", result);
        Assert.Contains("Run `dotnet test`.", result);
        Assert.DoesNotContain("Some hand-written notes about unbramble.", result);
        Assert.Equal(1, CountOccurrences(result, "## unbramble"));
        Assert.Contains("<!-- unbramble:begin", result);
        Assert.Contains("migrated existing 'unbramble' section", stdOut);
    }

    [Fact]
    public void Init_ClaudeMdAlreadyShimmed_IsByteIdenticalAfterInit()
    {
        using var fixture = FixtureCopy.Create();
        var claudePath = Path.Combine(fixture.Root, "CLAUDE.md");
        var shimText = "Read and follow @AGENTS.md." + Environment.NewLine;
        File.WriteAllText(claudePath, shimText);

        var (exitCode, _, _) = CliRunner.Run("init", "-p", fixture.Root);

        Assert.Equal(0, exitCode);
        Assert.Equal(shimText, File.ReadAllText(claudePath));
    }

    [Fact]
    public void Init_ClaudeMdWithSubstantiveContentNoAgentsReference_IsUntouchedWithNotice()
    {
        using var fixture = FixtureCopy.Create();
        var claudePath = Path.Combine(fixture.Root, "CLAUDE.md");
        var substantive = "# My project" + Environment.NewLine + "Custom instructions that have nothing to do with agents.md." + Environment.NewLine;
        File.WriteAllText(claudePath, substantive);

        var (exitCode, stdOut, _) = CliRunner.Run("init", "-p", fixture.Root);

        Assert.Equal(0, exitCode);
        Assert.Equal(substantive, File.ReadAllText(claudePath));
        Assert.Contains("doesn't reference AGENTS.md", stdOut);
    }

    [Fact]
    public void Init_CorruptMarkers_LeavesAgentsMdUntouchedWithWarningAndExitZero()
    {
        using var fixture = FixtureCopy.Create();
        var agentsPath = Path.Combine(fixture.Root, "AGENTS.md");
        var corrupt =
            "# AGENTS.md" + Environment.NewLine + Environment.NewLine +
            "<!-- unbramble:begin v0.1.0 -->" + Environment.NewLine +
            "no matching end marker here" + Environment.NewLine;
        File.WriteAllText(agentsPath, corrupt);

        var (exitCode, stdOut, _) = CliRunner.Run("init", "-p", fixture.Root);

        Assert.Equal(0, exitCode);
        Assert.Equal(corrupt, File.ReadAllText(agentsPath));
        Assert.Contains("Warning", stdOut);
    }

    [Fact]
    public void Init_NoAgentsFlag_CreatesNeitherFile()
    {
        using var fixture = FixtureCopy.Create();

        var (exitCode, _, _) = CliRunner.Run("init", "-p", fixture.Root, "--no-agents");

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "AGENTS.md")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "CLAUDE.md")));
    }

    [Fact]
    public void Init_JsonFlag_StdoutIsPureJsonWithNoAnnouncementLines()
    {
        using var fixture = FixtureCopy.Create();

        var (exitCode, stdOut, _) = CliRunner.Run("init", "-p", fixture.Root, "--json");

        Assert.Equal(0, exitCode);
        using var doc = JsonDocument.Parse(stdOut);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.True(File.Exists(Path.Combine(fixture.Root, "AGENTS.md")));
        Assert.True(File.Exists(Path.Combine(fixture.Root, "CLAUDE.md")));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
