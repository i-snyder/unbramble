using System.Globalization;
using UnBramble.Core;
using UnBramble.Core.Config;
using UnBramble.Core.Exceptions;
using UnBramble.Core.Freshness;
using UnBramble.Core.Model;
using UnBramble.Core.ProjectDetection;

namespace UnBramble.Cli;

/// <summary>
/// `unbramble` with no verb at all: the friendly entry point a first-time (or just-forgot-the-
/// verb) user reaches for. Two jobs depending on project state, neither of which any explicit
/// subcommand performs today:
///
/// <list type="bullet">
/// <item>Project not yet indexed: offer to run `init` interactively, with a visible progress
/// experience while it works (Cases A/B/C/D below).</item>
/// <item>Project already indexed: a glanceable status summary, plus making sure a background
/// watcher is running (Case E below) -- the same "ensure freshness" job every query verb's
/// <c>PrintFreshness</c> preamble does, just without forcing an inline sweep (must return in
/// well under a second regardless of project size).</item>
/// </list>
///
/// The full case table:
///
/// <code>
/// | Project inited? | Interactive (stdin+stdout TTY)? | Behavior                              | Exit |
/// |------------------|----------------------------------|----------------------------------------|------|
/// | No               | Yes                               | Case A: prompt -> Case C -> Case D     | 0    |
/// | No               | No                                | Case B: one stderr message, no waiting | 1    |
/// | Yes              | Yes                               | Case E, ANSI-decorated                 | 0    |
/// | Yes              | No                                | Case E, plain text                     | 0    |
/// | No Unity project found (any)                          | one stderr message              | 1    |
/// </code>
///
/// No explicit subcommand's behavior changes because of this file existing (design invariant):
/// the interactive prompt lives ONLY here, `RunInit`/every query verb stays headless.
/// </summary>
public static class HomeCommand
{
    /// <summary>
    /// The real environment this command reads from, injected so the interactive-prompt logic
    /// (Cases A/B) is unit-testable: <see cref="Console.IsInputRedirected"/> reflects OS handles
    /// and can't be faked by swapping <see cref="Console.SetOut"/>/<see cref="Console.SetIn"/>
    /// the way <c>CliRunner</c> does for every other verb's stdout/stderr capture.
    /// </summary>
    internal readonly record struct HomeEnvironment(bool IsInteractive, bool SupportsAnsi, Func<string?> ReadLine);

    public static int Run(string[] rest) =>
        Run(rest, new HomeEnvironment(ConsoleCapabilities.IsInteractive, ConsoleCapabilities.SupportsAnsi, Console.ReadLine));

    internal static int Run(string[] rest, HomeEnvironment env)
    {
        // Mirrors Program.Main's own try/catch: this method runs OUTSIDE that wrapper (same as
        // --help/--version), so it needs the identical safety net itself -- e.g. UnBrambleEngine.Open
        // in Case A/E can throw ForceTextNotEnabledException, and a bare `unbramble` should report
        // that as a normal "error: ..." line, not an unhandled-exception crash.
        try
        {
            return RunCore(rest, env);
        }
        catch (UnBrambleException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        catch (ArgReaderException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int RunCore(string[] rest, HomeEnvironment env)
    {
        var reader = ArgReader.Parse(rest);
        var startPath = reader.ResolveStartPath();

        var projectRoot = ProjectDetector.FindProjectRoot(startPath);
        if (projectRoot is null)
        {
            Console.Error.WriteLine("No Unity project found here or in any parent folder. cd into a Unity project and try again.");
            return 1;
        }

        if (IsInited(projectRoot))
        {
            return RunCaseE(projectRoot, env);
        }

        return env.IsInteractive ? RunCaseA(startPath, env) : RunCaseB();
    }

    /// <summary>
    /// The exact same check <see cref="UnBrambleEngine.Open"/> performs implicitly when it calls
    /// <c>UnBrambleStore.OpenOrCreate</c> -- reproduced here (rather than calling Open itself) so
    /// this can run BEFORE consent: Open's OpenOrCreate call would create the DB file (and thus
    /// make every not-yet-inited project look inited) the moment it runs, and nothing may touch
    /// disk before a not-inited project's user says yes.
    /// </summary>
    private static bool IsInited(string projectRoot)
    {
        var config = UnBrambleConfigLoader.Load(projectRoot, out _);
        var dbPath = Path.Combine(projectRoot, config.DbPath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(dbPath);
    }

    private static int RunCaseB()
    {
        Console.Error.WriteLine("error: this project isn't indexed yet, and there's no interactive terminal to confirm setup.");
        Console.Error.WriteLine(AnsiStyle.InlineCommands(
            "Run `unbramble init` to set it up explicitly (one-time; can take a while on large projects).",
            ConsoleCapabilities.SupportsAnsi));
        return 1;
    }

    private static int RunCaseA(string startPath, HomeEnvironment env)
    {
        // This is the one moment a first-time user meets the tool. Keep the large mascot here,
        // before either consent prompt; the indexing screen below reuses the same visual language
        // without stamping the mascot a second time.
        if (env.SupportsAnsi)
        {
            Console.Write(StartupMascotRenderer.Render(
                Program.Version,
                ConsoleCapabilities.SupportsTrueColor,
                ConsoleCapabilities.TerminalWidth ?? 80));
        }
        else
        {
            Console.WriteLine($"unbramble {Program.Version}");
            Console.WriteLine("Clearing a path through complex Unity projects");
            Console.WriteLine();
        }

        Console.WriteLine("This project isn't set up yet. Set it up now? " + AnsiStyle.YesNoPrompt(env.SupportsAnsi));
        if (!IsAccept(env.ReadLine()))
        {
            return DeclineWarmly();
        }

        Console.WriteLine();
        Program.WriteParagraph(
            "Setup indexes the whole project once — a large project can take a few minutes. " +
            "After that, a background watcher keeps it fresh automatically.");
        Console.WriteLine("Start? " + AnsiStyle.YesNoPrompt(env.SupportsAnsi));
        if (!IsAccept(env.ReadLine()))
        {
            return DeclineWarmly();
        }

        // Only now -- after two explicit yeses -- does anything touch disk (Open creates the DB).
        using var engine = UnBrambleEngine.Open(startPath);
        return RunCaseCAndD(engine, env);
    }

    private static int DeclineWarmly()
    {
        Console.WriteLine("No problem — run `unbramble` again whenever you're ready.");
        return 0;
    }

    /// <summary>Accept = empty (default yes), "y", or "yes" (case-insensitive, trimmed).
    /// Everything else -- including a null (EOF: stdin closed mid-prompt) -- declines. Never
    /// re-prompts on an unrecognized answer; one question, one answer.</summary>
    private static bool IsAccept(string? answer)
    {
        if (answer is null)
        {
            return false;
        }

        var trimmed = answer.Trim();
        return trimmed.Length == 0
            || trimmed.Equals("y", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Case C (visualized first-time indexing) immediately followed by Case D
    /// (completion + watcher handoff) -- kept as one method since C's renderer needs to be
    /// `Complete()`d before D's summary prints, and D needs the `IndexSummary` C just produced.</summary>
    private static int RunCaseCAndD(UnBrambleEngine engine, HomeEnvironment env)
    {
        Console.WriteLine();

        // Ignore files, agent instructions, and the Defender consent prompt must
        // all fully resolve BEFORE any progress renderer is constructed/started or any scanning
        // begins -- see Program.ExecuteInitPreScan's own doc comment for the real bug this
        // ordering fixes: a renderer constructed before this step ticked (and its redraws
        // interleaved with the Defender prompt's own stdout) before and during the prompt.
        Program.ExecuteInitPreScan(engine, announce: true, setUpAgents: true, interactive: true);

        IndexSummary summary;
        if (env.SupportsAnsi)
        {
            using var renderer = new BrambleProgressRenderer(Program.Version);

            // showMark: false -- Case A already showed the mascot a few lines up. Both are gated
            // on the identical env.SupportsAnsi, so whenever this renderer runs at all, the
            // startup art has provably already been drawn.
            Console.WriteLine();
            renderer.PrintMark(showMark: false);
            summary = engine.RunIndex(full: false, renderer.OnScanProgress, renderer.OnPhase);
            renderer.Complete();
        }
        else
        {
            // The exact plain-line fallback `init` itself uses -- one shared progress data
            // pipeline (RunIndex's onScanProgress/onPhase callbacks) feeding whichever renderer
            // fits this terminal.
            using var plain = new SweepProgressPrinter(header: "Unbrambling...");
            summary = engine.RunIndex(full: false, plain.OnScanProgress, plain.OnPhase);
        }

        return PrintCaseDAndSpawnWatcher(engine, summary, env.SupportsAnsi);
    }

    private static int PrintCaseDAndSpawnWatcher(UnBrambleEngine engine, IndexSummary summary, bool ansi)
    {
        var totalFiles = summary.Stats.Files.Total + summary.Stats.IdentityOnlyCount;
        Console.WriteLine();
        Console.WriteLine("  " +
            AnsiStyle.Alive("Unbrambled! You're ready to grow.  ---<-", ansi) +
            AnsiStyle.Alarm("@", ansi));
        Console.WriteLine("  " + AnsiStyle.Muted($"{FormatExactCount(totalFiles)} files indexed in {FormatElapsed(summary.Elapsed)}.", ansi));
        Console.WriteLine("  " + AnsiStyle.Muted("A background watcher will keep the index fresh from here on.", ansi));
        Console.WriteLine();
        Console.WriteLine("  " + TryLine(ansi, "unbramble who-uses <file-or-type>", "unbramble --help"));

        // Same watcher this project would auto-spawn from its first real query anyway (see
        // Program.MaybeAutoSpawnWatch) -- doing it right here instead just means the FIRST query
        // doesn't have to pay for it. No cooldown check: this is necessarily the very first spawn
        // attempt for this project (a not-inited project has no .unbramble/ marker history yet).
        if (engine.Config.Watch.AutoStart)
        {
            var now = DateTime.UtcNow;
            AutoWatchMarkers.RecordSpawnAttempt(engine.ProjectRoot, now);
            AutoWatchMarkers.TouchLastQuery(engine.ProjectRoot, now);
            Program.SpawnDetachedWatcher(engine.ProjectRoot);
        }

        return 0;
    }

    /// <summary>
    /// Already-inited project: a fast, honest status glance. Deliberately does NOT call
    /// <see cref="UnBrambleEngine.EnsureFresh"/> -- that can run a full inline sweep on a stale
    /// heartbeat, and this command must return in well under a second regardless of project
    /// size (it reports freshness, it doesn't manufacture it). Ensuring a watcher is running
    /// reuses the exact same decision <c>Program.MaybeAutoSpawnWatch</c> makes for a query that
    /// just had to sweep.
    /// </summary>
    private static int RunCaseE(string projectRoot, HomeEnvironment env)
    {
        using var engine = UnBrambleEngine.Open(projectRoot);

        var now = DateTime.UtcNow;
        var heartbeat = HeartbeatFile.TryRead(projectRoot);
        var isFresh = heartbeat is { } hb && HeartbeatFreshness.IsFresh(hb.UtcTimestamp, now, HeartbeatFreshness.DefaultStaleThreshold);

        string freshnessLine;
        if (isFresh)
        {
            var ageSeconds = Math.Max(0, (int)(now - heartbeat!.Value.UtcTimestamp).TotalSeconds);
            freshnessLine = $"Index is fresh — watcher active (updated {ageSeconds.ToString(CultureInfo.InvariantCulture)}s ago).";
        }
        else if (!engine.Config.Watch.AutoStart)
        {
            freshnessLine = "No watcher running (watch.autoStart is off) — queries refresh on demand.";
        }
        else
        {
            var lastAttempt = AutoWatchMarkers.TryReadLastSpawnAttempt(projectRoot);
            if (AutoSpawnPolicy.ShouldSpawn(sweepPerformed: true, autoStartEnabled: true, lastAttempt, now, AutoSpawnPolicy.DefaultCooldown))
            {
                AutoWatchMarkers.RecordSpawnAttempt(projectRoot, now);
                AutoWatchMarkers.TouchLastQuery(projectRoot, now);
                Program.SpawnDetachedWatcher(projectRoot);
                freshnessLine = "Starting a background watcher — your first query may pause briefly to catch up.";
            }
            else
            {
                freshnessLine = "No watcher running — queries refresh on demand.";
            }
        }

        var stats = engine.GetStats();
        var totalFiles = stats.Files.Total + stats.IdentityOnlyCount;
        var totalLinks = stats.Edges.GuidTotal + stats.Edges.PathTotal;

        Console.WriteLine("  " + AnsiStyle.Muted($"unbramble — {engine.ProjectRoot}", env.SupportsAnsi));
        Console.WriteLine();
        // Notice, not Caution, for the not-fresh branches: "no watcher running -- queries refresh
        // on demand" is a normal, working state, and painting it like a warning would train the
        // reader to ignore the color that does mean something.
        Console.WriteLine("  " + (isFresh ? AnsiStyle.Alive(freshnessLine, env.SupportsAnsi) : AnsiStyle.Notice(freshnessLine, env.SupportsAnsi)));
        Console.WriteLine($"  ~{FormatCompactCount(totalFiles)} files, ~{FormatCompactCount(totalLinks)} links tracked (Unity {engine.UnityVersion}).");
        Console.WriteLine();
        Console.WriteLine("  " + TryLine(env.SupportsAnsi, "unbramble who-uses <file-or-type>", "unbramble stats", "unbramble --help"));

        return 0;
    }

    /// <summary>The "here's what to type next" line, shared by Case D (just finished setup) and
    /// Case E (already set up) so the two never drift apart. The commands are the only part worth
    /// looking at, so the label around them is the part that recedes.</summary>
    private static string TryLine(bool ansi, params string[] commands) =>
        AnsiStyle.Muted("Try:  ", ansi) +
        string.Join("    ", commands.Select(c => AnsiStyle.Command(c, ansi)));

    private static string FormatExactCount(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds < 60)
        {
            return elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";
        }

        var minutes = (int)elapsed.TotalMinutes;
        var seconds = elapsed.Seconds;
        return $"{minutes}m {seconds.ToString("D2", CultureInfo.InvariantCulture)}s";
    }

    /// <summary>`&lt; 1000` exact; `&gt;= 1000` rounds to the nearest thousand ("47k"); `&gt;= 1,000,000`
    /// to one decimal of a million ("1.2M"). Used only for Case E's glanceable summary -- Case D's
    /// completion line and `unbramble stats` both use exact comma-grouped counts.</summary>
    internal static string FormatCompactCount(long value)
    {
        if (value < 1000)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        if (value < 1_000_000)
        {
            return Math.Round(value / 1000.0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "k";
        }

        return (value / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M";
    }
}
