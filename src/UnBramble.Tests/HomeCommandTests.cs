using System.Reflection;
using UnBramble.Cli;
using UnBramble.Core.Config;
using UnBramble.Core.Freshness;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// Zero-args `unbramble` (<c>HomeCommand</c>). Reflection-driven the same way
/// <c>WatchDashboardFrameTests</c> reaches <c>WatchDashboard.BuildFrame</c>: `HomeCommand`'s
/// testable `Run(rest, HomeEnvironment)` overload and the `HomeEnvironment` seam itself are both
/// `internal`, and this test project has no InternalsVisibleTo grant on UnBramble.Cli.
///
/// Truth table under test (mirrors HomeCommand's own doc comment):
///   not-inited + non-interactive -> Case B: exit 1, no disk writes.
///   not-inited + interactive     -> Case A prompts -> (decline: exit 0, no disk writes) or
///                                    (accept-accept: Case C indexing -> Case D, exit 0).
///   inited     + any             -> Case E: exit 0, fast glanceable summary.
/// </summary>
public class HomeCommandTests
{
    /// <summary><paramref name="beforeRun"/> runs inside the console lock, immediately before
    /// HomeCommand — same reasoning as <see cref="CliRunner.Run(Action, string[])"/>: state that
    /// decays on wall-clock time (a fresh heartbeat) must not be arranged while this call is
    /// still queued behind another test class's CLI run.</summary>
    private static (int ExitCode, string StdOut, string StdErr) RunHome(string[] rest, bool isInteractive, bool supportsAnsi, Func<string?> readLine, Action? beforeRun = null)
    {
        var homeCommandType = typeof(Program).Assembly.GetType("UnBramble.Cli.HomeCommand")!;
        var envType = homeCommandType.GetNestedType("HomeEnvironment", BindingFlags.NonPublic)!;
        var ctor = envType.GetConstructor([typeof(bool), typeof(bool), typeof(Func<string?>)])!;
        var env = ctor.Invoke([isInteractive, supportsAnsi, readLine]);
        var runMethod = homeCommandType.GetMethod(
            "Run",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(string[]), envType],
            modifiers: null)!;

        lock (ConsoleTestLock.Gate)
        {
            beforeRun?.Invoke();

            var originalOut = Console.Out;
            var originalErr = Console.Error;
            using var stdOut = new StringWriter();
            using var stdErr = new StringWriter();
            Console.SetOut(stdOut);
            Console.SetError(stdErr);

            int exitCode;
            try
            {
                exitCode = (int)runMethod.Invoke(null, [rest, env])!;
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalErr);
            }

            return (exitCode, stdOut.ToString(), stdErr.ToString());
        }
    }

    private static Func<string?> Answers(params string?[] answers)
    {
        var queue = new Queue<string?>(answers);
        return () => queue.Count > 0 ? queue.Dequeue() : null;
    }

    private static string DbPath(string root) =>
        UnBramblePaths.RelativeTo(root, UnBramblePaths.DbRelativePath);

    [Fact]
    public void NotInited_NonInteractive_ExitsOneAndTouchesNothing()
    {
        using var fixture = FixtureCopy.Create();

        var (exitCode, _, stdErr) = RunHome(["-p", fixture.Root], isInteractive: false, supportsAnsi: false, Answers());

        Assert.Equal(1, exitCode);
        Assert.Contains("unbramble init", stdErr, StringComparison.Ordinal);
        Assert.False(File.Exists(DbPath(fixture.Root)), "non-interactive Case B must never touch disk");
    }

    [Fact]
    public void NotInited_Interactive_DeclineFirstPrompt_WarmCancelNoDbCreated()
    {
        using var fixture = FixtureCopy.Create();

        var (exitCode, stdOut, _) = RunHome(["-p", fixture.Root], isInteractive: true, supportsAnsi: false, Answers("n"));

        Assert.Equal(0, exitCode);
        Assert.Contains("run `unbramble` again", stdOut, StringComparison.Ordinal);
        Assert.False(File.Exists(DbPath(fixture.Root)));
    }

    [Fact]
    public void NotInited_Interactive_DeclineSecondPrompt_WarmCancelNoDbCreated()
    {
        using var fixture = FixtureCopy.Create();

        var (exitCode, stdOut, _) = RunHome(["-p", fixture.Root], isInteractive: true, supportsAnsi: false, Answers("y", "n"));

        Assert.Equal(0, exitCode);
        Assert.Contains("run `unbramble` again", stdOut, StringComparison.Ordinal);
        Assert.False(File.Exists(DbPath(fixture.Root)));
    }

    [Fact]
    public void NotInited_Interactive_EofAtFirstPrompt_TreatedAsDecline()
    {
        using var fixture = FixtureCopy.Create();

        var (exitCode, stdOut, _) = RunHome(["-p", fixture.Root], isInteractive: true, supportsAnsi: false, Answers((string?)null));

        Assert.Equal(0, exitCode);
        Assert.Contains("run `unbramble` again", stdOut, StringComparison.Ordinal);
        Assert.False(File.Exists(DbPath(fixture.Root)));
    }

    [Fact]
    public void NotInited_Interactive_AcceptAccept_PlainRenderer_IndexesAndReportsDone()
    {
        using var fixture = FixtureCopy.Create();

        var (exitCode, stdOut, _) = RunHome(["-p", fixture.Root], isInteractive: true, supportsAnsi: false, Answers("", ""));

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(DbPath(fixture.Root)), "accept-accept must create the DB");
        Assert.Contains("Unbrambled! You're ready to grow.  ---<-@", stdOut, StringComparison.Ordinal);
        Assert.Contains("files indexed in", stdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("\x1b[", stdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void NotInited_Interactive_AcceptAccept_AnsiRenderer_ReportsDoneWithColor()
    {
        using var fixture = FixtureCopy.Create();

        var (exitCode, stdOut, _) = RunHome(["-p", fixture.Root], isInteractive: true, supportsAnsi: true, Answers("y", "yes"));

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(DbPath(fixture.Root)));
        Assert.Contains("Unbrambled! You're ready to grow.  ---<-", stdOut, StringComparison.Ordinal);
        Assert.Contains("files indexed in", stdOut, StringComparison.Ordinal);
        Assert.True(
            stdOut.Contains("\x1b[1;38;5;167m@", StringComparison.Ordinal) ||
            stdOut.Contains("\x1b[1;38;2;224;82;74m@", StringComparison.Ordinal),
            "the rose @ should use the rosehip color in either supported ANSI mode");
        Assert.True(
            stdOut.Contains("\x1b[38;5;101m", StringComparison.Ordinal) ||
            stdOut.Contains("\x1b[38;2;150;132;108m", StringComparison.Ordinal),
            "the file-count line should use the muted bracken color in either supported ANSI mode");
        Assert.Contains('\u2584', stdOut);
        Assert.Contains("\x1b[", stdOut, StringComparison.Ordinal);
    }

    /// <summary>
    /// Regression for a real bug: Case C's <c>BrambleProgressRenderer</c> used to be
    /// constructed/started BEFORE `ExecuteInitCore`'s pre-scan step (ignore files, agent
    /// instructions, and -- the actual trigger -- the Defender consent prompt) ran, so the
    /// renderer's initial frame (and, on a slow pre-scan step, its 1s-cadence timer ticks)
    /// could print and even interleave with the pre-scan step's own announcements and prompt.
    /// `HomeCommand.RunCaseCAndD` now runs `Program.ExecuteInitPreScan` to completion first and
    /// only constructs the renderer afterward -- asserted here via stdout ordering: the pre-scan
    /// step's own announcement text (from `SetUpIgnoreFiles`, unconditionally printed either way
    /// since this fixture has no `.git`/`.plastic` marker) must appear before the renderer's first
    /// output, never after.
    ///
    /// Anchored on the renderer's cursor-hide specifically, NOT on "the first ANSI escape
    /// anywhere in stdout" as it originally was. That was only ever a proxy for "the renderer
    /// started", and it silently stopped being one once Case A began opening on a colored mascot
    /// and prompt of its own -- both of which legitimately precede the pre-scan step. The cursor-hide
    /// is emitted by nothing but <c>BrambleProgressRenderer.PrintMark</c>, so it names the thing
    /// the regression is actually about.
    /// </summary>
    [Fact]
    public void NotInited_Interactive_AnsiRenderer_PreScanAnnouncementsPrecedeRendererOutput()
    {
        using var fixture = FixtureCopy.Create();

        var (exitCode, stdOut, _) = RunHome(["-p", fixture.Root], isInteractive: true, supportsAnsi: true, Answers("y", "yes"));

        Assert.Equal(0, exitCode);
        var ignoreFilesIndex = stdOut.IndexOf("Note: no .git or .plastic detected", StringComparison.Ordinal);
        var rendererStartIndex = stdOut.IndexOf(RendererCursorHide, StringComparison.Ordinal);
        Assert.True(ignoreFilesIndex >= 0, "expected SetUpIgnoreFiles' announcement in stdout");
        Assert.True(rendererStartIndex >= 0, "expected the ANSI renderer to have produced output");
        Assert.True(
            ignoreFilesIndex < rendererStartIndex,
            $"pre-scan announcement (index {ignoreFilesIndex}) must precede the renderer's first output (index {rendererStartIndex}) -- " +
            "the renderer must never start before the pre-scan step (which includes the Defender prompt) has fully resolved");
    }

    /// <summary>The first byte <c>BrambleProgressRenderer</c> ever writes, and written by nothing
    /// else on this code path.</summary>
    private const string RendererCursorHide = "\x1b[?25l";

    [Fact]
    public void Inited_BareRun_ExitsZeroWithCompactSummaryAndNoAnsi()
    {
        using var fixture = FixtureCopy.Create();
        _ = CliRunner.Run("init", "-p", fixture.Root);

        var (exitCode, stdOut, _) = RunHome(["-p", fixture.Root], isInteractive: true, supportsAnsi: false, Answers());

        Assert.Equal(0, exitCode);
        Assert.Contains("files,", stdOut, StringComparison.Ordinal);
        Assert.Contains("links tracked", stdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("\x1b[", stdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Inited_NonInteractive_StillExitsZeroPlainText()
    {
        using var fixture = FixtureCopy.Create();
        _ = CliRunner.Run("init", "-p", fixture.Root);

        var (exitCode, stdOut, _) = RunHome(["-p", fixture.Root], isInteractive: false, supportsAnsi: false, Answers());

        Assert.Equal(0, exitCode);
        Assert.Contains("links tracked", stdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void NoUnityProject_ReportsFriendlyErrorRegardlessOfInteractivity()
    {
        using var temp = TempDir.Create();

        var (exitCode, _, stdErr) = RunHome(["-p", temp.Root], isInteractive: true, supportsAnsi: true, Answers());

        Assert.Equal(1, exitCode);
        Assert.Contains("No Unity project found", stdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Inited_StaleHeartbeat_RecordsSpawnAttempt()
    {
        using var fixture = FixtureCopy.Create();
        _ = CliRunner.Run("init", "-p", fixture.Root);

        var (exitCode, _, _) = RunHome(["-p", fixture.Root], isInteractive: true, supportsAnsi: false, Answers());

        Assert.Equal(0, exitCode);
        Assert.NotNull(AutoWatchMarkers.TryReadLastSpawnAttempt(fixture.Root));
    }

    [Fact]
    public void Inited_FreshHeartbeat_NeverAttemptsSpawn()
    {
        using var fixture = FixtureCopy.Create();
        _ = CliRunner.Run("init", "-p", fixture.Root);

        var (exitCode, stdOut, _) = RunHome(
            ["-p", fixture.Root],
            isInteractive: true,
            supportsAnsi: false,
            Answers(),
            beforeRun: () => HeartbeatFile.Write(fixture.Root, pid: 4242, DateTime.UtcNow));

        Assert.Equal(0, exitCode);
        Assert.Contains("Index is fresh", stdOut, StringComparison.Ordinal);
        Assert.Null(AutoWatchMarkers.TryReadLastSpawnAttempt(fixture.Root));
    }

    [Fact]
    public void NotInited_AcceptAccept_RecordsSpawnAttemptAfterCaseD()
    {
        using var fixture = FixtureCopy.Create();

        var (exitCode, _, _) = RunHome(["-p", fixture.Root], isInteractive: true, supportsAnsi: false, Answers("", ""));

        Assert.Equal(0, exitCode);
        Assert.NotNull(AutoWatchMarkers.TryReadLastSpawnAttempt(fixture.Root));
    }

    [Fact]
    public void WatchAutoStartDisabled_Inited_StaleHeartbeat_NeverAttemptsSpawn()
    {
        using var fixture = FixtureCopy.Create();
        File.WriteAllText(Path.Combine(fixture.Root, "unbramble.json"), """{"watch":{"autoStart":false}}""");
        _ = CliRunner.Run("init", "-p", fixture.Root);

        var (exitCode, stdOut, _) = RunHome(["-p", fixture.Root], isInteractive: true, supportsAnsi: false, Answers());

        Assert.Equal(0, exitCode);
        Assert.Contains("watch.autoStart is off", stdOut, StringComparison.Ordinal);
        Assert.Null(AutoWatchMarkers.TryReadLastSpawnAttempt(fixture.Root));
    }

    // --- Regression: existing entry points untouched by this feature -----------------------

    [Fact]
    public void Help_StillPrintsUsageAndExitsZero()
    {
        var (exitCode, stdOut, _) = CliRunner.Run("--help");

        Assert.Equal(0, exitCode);
        Assert.Contains("Usage:", stdOut, StringComparison.Ordinal);
    }

    // --- Pure function: FormatCompactCount ---------------------------------------------------

    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(1000, "1k")]
    [InlineData(47331, "47k")]
    [InlineData(999_499, "999k")]
    [InlineData(1_000_000, "1.0M")]
    [InlineData(1_200_000, "1.2M")]
    public void FormatCompactCount_MatchesSpecifiedBuckets(long value, string expected)
    {
        var homeCommandType = typeof(Program).Assembly.GetType("UnBramble.Cli.HomeCommand")!;
        var method = homeCommandType.GetMethod("FormatCompactCount", BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.Equal(expected, (string)method.Invoke(null, [value])!);
    }

    [Fact]
    public void ZeroArgs_UnderTestRunnerRedirection_NeverHangs_LandsInCaseBOrNoProjectError()
    {
        // No fake environment here -- this goes through the REAL Main([]) -> HomeCommand.Run([])
        // -> ConsoleCapabilities path, under whatever stdin/stdout redirection the test runner
        // itself has in place. The assertion isn't about which message prints (that depends on
        // whether the test process's cwd happens to sit under a Unity project) -- it's that this
        // call returns promptly with a failure exit code instead of blocking on Console.ReadLine.
        var (exitCode, _, _) = CliRunner.Run();

        Assert.Equal(1, exitCode);
    }
}
