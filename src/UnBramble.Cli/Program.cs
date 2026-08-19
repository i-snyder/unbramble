using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using UnBramble.Cli.Defender;
using UnBramble.Cli.Json;
using UnBramble.Core;
using UnBramble.Core.Config;
using UnBramble.Core.CSharp;
using UnBramble.Core.Exceptions;
using UnBramble.Core.Freshness;
using UnBramble.Core.Model;
using UnBramble.Core.Monitoring;
using UnBramble.Core.ProjectDetection;
using UnBramble.Core.Query;
using UnBramble.Core.Scanning;

namespace UnBramble.Cli;

public static class Program
{
    private static readonly Dictionary<int, string> ClassNames = new()
    {
        [1] = "GameObject",
        [4] = "Transform",
        [21] = "Material",
        [23] = "MeshRenderer",
        [33] = "MeshFilter",
        [114] = "MonoBehaviour",
        [1001] = "PrefabInstance",
        [1045] = "EditorBuildSettings",
    };

    /// <summary>
    /// The one place every `error: `/`warning: ` line goes through -- always stderr (never mixed
    /// into stdout, so `--json` callers piping stdout are never at risk of a colorized line
    /// corrupting their parse), colorized when <see cref="ConsoleCapabilities.SupportsAnsi"/>
    /// (redirected/NO_COLOR/dumb-terminal output falls back to today's identical plain text).
    /// </summary>
    // Continuation lines hang under the message rather than under the label, so a wrapped
    // error/warning stays visibly one message instead of looking like a second, unlabelled one.
    private static void WriteError(string message) =>
        WriteParagraph(
            AnsiStyle.InlineCommands(message, ConsoleCapabilities.SupportsAnsi),
            firstPrefix: AnsiStyle.Alarm("error: ", ConsoleCapabilities.SupportsAnsi),
            contPrefix: "       ",
            writer: Console.Error);

    private static void WriteWarning(string message) =>
        WriteParagraph(
            AnsiStyle.InlineCommands(message, ConsoleCapabilities.SupportsAnsi),
            firstPrefix: AnsiStyle.Caution("warning: ", ConsoleCapabilities.SupportsAnsi),
            contPrefix: "         ",
            writer: Console.Error);

    /// <summary>
    /// Continuation indent for every wrapped paragraph below -- deep enough that a wrapped line
    /// is unmistakably a continuation of the statement above it and never reads as a new finding,
    /// which is exactly what the terminal's own column-0 wrapping got wrong.
    /// </summary>
    private const string ParagraphHangingIndent = "    ";

    /// <summary>
    /// The one place a multi-sentence prose line (a diagnosis, a remediation hint, the blind-spots
    /// footer) reaches the console. Wrapped to the terminal with a hanging indent when a human is
    /// looking (<see cref="ConsoleCapabilities.TerminalWidth"/>), emitted verbatim as a single
    /// line when it isn't -- see that property for why redirected output deliberately stays
    /// unwrapped.
    ///
    /// Wrapping one column short of the reported width: a line that exactly fills the last column
    /// triggers the terminal's own auto-wrap on top of ours, which on Windows conhost shows up as
    /// a stray blank line between paragraphs.
    /// </summary>
    internal static void WriteParagraph(string text, string firstPrefix = "", string? contPrefix = null, TextWriter? writer = null)
    {
        var output = writer ?? Console.Out;
        if (ConsoleCapabilities.TerminalWidth is not { } width)
        {
            output.WriteLine(firstPrefix + text);
            return;
        }

        foreach (var line in TextWrap.WrapLines(text, width - 1, firstPrefix, contPrefix ?? firstPrefix + ParagraphHangingIndent))
        {
            output.WriteLine(line);
        }
    }

    /// <summary>
    /// A key/value block (`stats`) rendered as an actual aligned table for a human: labels padded
    /// to a common value column, and a value too long for one line wrapping to hang at that same
    /// column rather than at some unrelated indent. Ragged `Label: value` lines force the eye to
    /// re-find where each value starts on every row; one column means one saccade down the values.
    ///
    /// Alignment is a human-only affordance, same as wrapping (see
    /// <see cref="ConsoleCapabilities.TerminalWidth"/>): a redirected stream keeps the exact
    /// single-space `Label: value` this has always emitted, so the padding never lands in a
    /// parse. The label text itself is identical either way -- only the run of spaces after the
    /// colon differs.
    /// </summary>
    private static void WriteLabeledRows(IReadOnlyList<(string Label, string Value)> rows, bool ansi)
    {
        // Padded off the raw label, never the styled one -- Label() wraps it in escape codes that
        // occupy zero columns, so padding the styled string would misalign every colorized row.
        var valueColumn = ConsoleCapabilities.TerminalWidth is null ? 0 : rows.Max(r => r.Label.Length) + 2;

        foreach (var (label, value) in rows)
        {
            var labelText = label + ":";
            var padding = valueColumn == 0 ? " " : new string(' ', Math.Max(1, valueColumn - labelText.Length));

            // Label+padding goes through firstPrefix, NOT prepended to the value: prefixes are
            // emitted verbatim, while the value is tokenized on spaces -- so a padded label folded
            // into the text would have its whole alignment run collapsed away by the tokenizer.
            WriteParagraph(
                value,
                firstPrefix: Label(labelText, ansi) + padding,
                contPrefix: new string(' ', valueColumn));
        }
    }

    /// <summary>
    /// The one styled writer every `init`/first-run setup step reports through -- the ignore-file
    /// rules, the agent-instruction files, and the whole Defender exchange, all of which already
    /// take an <c>Action&lt;string&gt;</c> writer, so they share this one and come out looking
    /// like one process instead of three subsystems each announcing itself differently.
    ///
    /// Styling is inferred rather than passed in, because these lines are written by code that
    /// shouldn't have to know about a palette. They already share a "<c>Subject: what happened</c>"
    /// grammar, so the subject is painted and the rest left alone -- meaning not one word of any
    /// existing announcement changed to get here.
    ///
    /// The inference is deliberately conservative, because a wrong guess mangles a real sentence:
    /// <list type="bullet">
    /// <item>An indented line is a pre-aligned detail row (Defender's exclusion table) -- passed
    /// through untouched, since wrapping it would break the very columns it aligned.</item>
    /// <item>A subject containing '.' isn't one: "Skipped. Manual steps: see README" would
    /// otherwise paint "Skipped. Manual steps" as a label.</item>
    /// <item>A long run before the colon isn't a subject either, it's a sentence that happens to
    /// contain one.</item>
    /// </list>
    /// </summary>
    internal static void WriteSetupLine(string line)
    {
        var ansi = ConsoleCapabilities.SupportsAnsi;
        if (line.Length == 0 || char.IsWhiteSpace(line[0]))
        {
            Console.WriteLine(line);
            return;
        }

        var separator = line.IndexOf(": ", StringComparison.Ordinal);
        var subject = separator > 0 ? line[..separator] : null;
        if (subject is null || subject.Length > MaxSetupSubjectLength || subject.Contains('.', StringComparison.Ordinal))
        {
            WriteParagraph(AnsiStyle.InlineCommands(line, ansi), contPrefix: ParagraphHangingIndent);
            return;
        }

        // "Warning:"/"Error:" carry meaning the generic subject tone would throw away -- a
        // stray-marker warning during setup must not read like a routine "here's what I did".
        var painted = subject switch
        {
            "Warning" => AnsiStyle.Caution(subject + ": ", ansi),
            "Error" => AnsiStyle.Alarm(subject + ": ", ansi),
            _ => AnsiStyle.Label(subject + ": ", ansi),
        };

        var body = AnsiStyle.InlineCommands(StyleSetupBody(subject, line[(separator + 2)..], ansi), ansi);
        WriteParagraph(body, firstPrefix: painted, contPrefix: ParagraphHangingIndent);
    }

    private static string StyleSetupBody(string subject, string body, bool ansi)
    {
        if (!ansi)
        {
            return body;
        }

        if (subject == "Ignore rules" && body.StartsWith("added ", StringComparison.Ordinal))
        {
            return AnsiStyle.Alive("added", ansi) + body[5..];
        }

        if (subject == "Agent instructions")
        {
            foreach (var verb in new[] { "created", "refreshed", "migrated", "added" })
            {
                if (body.StartsWith(verb + " ", StringComparison.Ordinal))
                {
                    return AnsiStyle.Alive(verb, ansi) + body[verb.Length..];
                }
            }

            const string current = " already up to date.";
            if (body.EndsWith(current, StringComparison.Ordinal))
            {
                return AnsiStyle.Notice(body[..^current.Length], ansi) + AnsiStyle.Alive(current, ansi);
            }
        }

        if (subject == "Windows Defender setup" && body.StartsWith("done.", StringComparison.Ordinal))
        {
            return AnsiStyle.Alive("done.", ansi) + body[5..];
        }

        return body;
    }

    /// <summary>Past this, a run before a colon is a sentence containing one, not a subject --
    /// the longest real subject today is "Windows Defender setup" (22).</summary>
    private const int MaxSetupSubjectLength = 28;

    /// <summary>A blank spacer line, but only when a human is reading: on a redirected stream the
    /// blank line is noise to whatever is parsing/grepping the output, and the visual grouping it
    /// buys doesn't exist there anyway.</summary>
    private static void WriteSpacer()
    {
        if (ConsoleCapabilities.TerminalWidth is not null)
        {
            Console.WriteLine();
        }
    }

    /// <summary>
    /// Wraps every command's output in one blank line above and below, so an answer reads as its
    /// own block instead of colliding with the shell prompt directly above and below it.
    ///
    /// Costs a redirected caller nothing: <see cref="WriteSpacer"/> is gated on
    /// <see cref="ConsoleCapabilities.TerminalWidth"/>, so a piped/captured stream (every agent,
    /// every script, the test suite) gets byte-for-byte what it got before. Even in the one case
    /// where a human-looking stream is parsed anyway -- an agent driving a PTY -- leading and
    /// trailing blank lines are ignorable whitespace for `--json` callers by definition, and the
    /// wrapping/alignment such a caller would already be seeing is the far bigger divergence.
    ///
    /// Wraps the whole dispatch (`--help`/`--version`/HomeCommand/error paths included) rather
    /// than each verb: one pair of spacers per invocation, guaranteed balanced, with no verb able
    /// to forget one.
    /// </summary>
    public static int Main(string[] args)
    {
        WriteSpacer();
        try
        {
            return RunCommand(args);
        }
        finally
        {
            WriteSpacer();
        }
    }

    /// <summary>Everything Main did before it grew the spacer wrapper above. Deliberately not
    /// named `Dispatch` -- that name is already taken below by the unknown-verb handler.</summary>
    private static int RunCommand(string[] args)
    {
        // Zero args: the friendly home command (first-time setup or a quick status glance --
        // see HomeCommand's own doc comment for the full case table), NOT the same thing as
        // `--help`. Deliberately outside the try/catch below, same as `--help`/`--version` --
        // HomeCommand.Run has its own internal error handling mirroring Main's, so this never
        // needs the shared wrapper.
        if (args.Length == 0)
        {
            return HomeCommand.Run([]);
        }

        if (args[0] is "--help" or "-h")
        {
            PrintUsage();
            return 0;
        }

        if (args[0] == "--version")
        {
            Console.WriteLine($"unbramble {Version}");
            return 0;
        }

        var verb = args[0];
        var rest = args[1..];

        // Help is a dispatch concern, not a verb option. Recognize it before ArgReader so every
        // verb supports the conventional `unbramble <verb> --help` shape (and help never opens a
        // project, refreshes an index, or fails for a missing required positional argument).
        if (rest.Any(arg => arg is "--help" or "-h"))
        {
            return PrintVerbUsage(verb);
        }

        try
        {
            return verb switch
            {
                "init" => RunInit(rest),
                "index" => RunIndex(rest),
                "resolve" => RunResolve(rest),
                "stats" => RunStats(rest),
                "who-uses" => RunWhoUses(rest),
                "uses" => RunUses(rest),
                "audit-assets" => RunAuditAssets(rest),
                "cs-refs" => RunCsRefs(rest),
                "dead-candidates" => RunDeadCandidates(rest),
                // Hidden process entry point. `monitor` and query auto-start use it; it is not a
                // user-facing verb and deliberately does not appear in help.
                "watch-worker" => RunWatchWorker(rest),
                "monitor" => RunMonitor(rest),
                "stop" => RunStop(rest),
                "defender" => RunDefender(rest),

                // A bare `unbramble <path>` (no verb) is the same home command as zero args,
                // just with an explicit starting directory -- e.g. `unbramble D:\proj`. Real
                // verbs above always match first, so a directory literally named e.g. "stats"
                // can never shadow that verb; any other unknown, non-directory token still hits
                // today's unknown-verb error via Dispatch. `args` (not `rest`) is passed through
                // so HomeCommand's own ArgReader.Parse sees the path as its positional, exactly
                // like every other verb's tail parsing does.
                _ => Directory.Exists(verb) ? HomeCommand.Run(args) : Dispatch(verb),
            };
        }
        catch (UnBrambleException ex)
        {
            WriteError(ex.Message);
            return 1;
        }
        catch (ArgReaderException ex)
        {
            WriteError(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            WriteError(ex.Message);
            return 1;
        }
    }

    private static int Dispatch(string verb)
    {
        PrintUsage();
        WriteError($"unknown verb '{verb}'");
        return 1;
    }

    private static int RunInit(string[] rest)
    {
        var reader = ArgReader.Parse(rest, "--json", "--verbose", "--no-agents", "--no-defender", "--defender");
        var json = reader.HasFlag("--json");
        var verbose = reader.HasFlag("--verbose");

        // --json/agent runs never prompt (design invariant, same as every other interactive step
        // in this codebase): ConsoleCapabilities.IsInteractive already requires both stdin and
        // stdout to be real terminals, but --json is checked explicitly too so a script piping
        // JSON to a file from a real terminal can't accidentally trip the prompt either.
        var interactive = !json && ConsoleCapabilities.IsInteractive;

        using var engine = UnBrambleEngine.Open(reader.ResolveStartPath());

        // Pre-scan first, renderer second -- see ExecuteInitPreScan's own doc comment for the
        // renderer/prompt interleaving bug this ordering prevents.
        ExecuteInitPreScan(
            engine, announce: !json, setUpAgents: !reader.HasFlag("--no-agents"),
            interactive, forceDefenderPrompt: reader.HasFlag("--defender"), skipDefender: reader.HasFlag("--no-defender"));
        var summary = RunIndexWithProgress(engine, full: false, json);

        PrintWarnings(summary.Warnings);
        MaybeWarnDefenderDrift(engine.ProjectRoot);

        if (json)
        {
            WriteIndexJson(summary);
            return 0;
        }

        if (summary.IsFirstRun)
        {
            var ansi = ConsoleCapabilities.SupportsAnsi;
            WriteParagraph($"{AnsiStyle.Label($"UnBramble {Version}", ansi)} — indexing {summary.ProjectRoot} (Unity {summary.UnityVersion})");
            WriteParagraph(
                AnsiStyle.Alive($"Indexed {FormatCount(summary.Stats.Files.Total)} files in {FormatSeconds(summary.Elapsed)}", ansi) +
                $" ({FormatCount(summary.Stats.Files.Assets)} assets, {FormatCount(summary.Stats.Files.Scripts)} scripts, " +
                $"{FormatCount(summary.Stats.Files.Folders)} folders, {FormatCount(summary.Stats.Files.Settings)} settings; " +
                $"{FormatCount(summary.Stats.IdentityOnlyCount)} identity-only from PackageCache)");
            WriteSetupLine($"Database: {summary.DbPath}");

            // One paragraph, wrapped to the actual terminal -- this used to be four WriteLines
            // hand-broken at ~78 columns, which is the same manual-wrapping problem the rest of
            // this pass removed: it wrapped raggedly on a narrow terminal and left a short,
            // arbitrary column on a wide one.
            WriteSpacer();
            WriteSetupLine(
                "Note: every query already self-refreshes (stat-sweep on each invocation), and queries auto-start " +
                "a background watcher as needed for near-instant freshness next time (config: watch.autoStart in " +
                "unbramble.json, default true). Run 'unbramble monitor' any time to start it immediately and see live progress.");
        }
        else
        {
            PrintIndexSummaryLine(summary);
        }

        if (verbose)
        {
            PrintPhaseTimings(summary.PhaseTimings);
        }

        return 0;
    }

    /// <summary>
    /// `init`'s working core, factored out so the zero-args home command's Case A (<see
    /// cref="HomeCommand"/>) can run the identical setup after its own interactive consent
    /// prompt, with its own renderer, without duplicating any of this logic. Order matches
    /// `RunInit`'s original inline sequence exactly: ignore files, then (optionally) agent
    /// instructions, then the actual sweep. <paramref name="announce"/> controls whether
    /// ignore-file/agent-setup lines print (mirrors `RunInit`'s own `!json` gate) -- callers that
    /// want silence (e.g. `--json`) pass false.
    /// </summary>
    /// <summary>
    /// One indexing pass with whichever progress rendering fits this terminal: ANSI gets the
    /// live bramble renderer (the same one the zero-args home command uses -- validation
    /// follow-up: `init`'s staggered plain lines on a minutes-long cold index read as "done or
    /// stalled?" next to the home command's live frame), everything else gets the plain printer
    /// (elapsed-stamped lines plus a keepalive during silent phases). Construct AFTER any
    /// prompt/announce output -- see <see cref="ExecuteInitPreScan"/>'s doc comment for the
    /// renderer/prompt interleaving bug that ordering prevents.
    /// </summary>
    private static IndexSummary RunIndexWithProgress(UnBrambleEngine engine, bool full, bool json)
    {
        if (!json && ConsoleCapabilities.SupportsAnsi)
        {
            using var renderer = new BrambleProgressRenderer(Version);
            Console.WriteLine();
            renderer.PrintMark();
            var summary = engine.RunIndex(full, renderer.OnScanProgress, renderer.OnPhase);
            renderer.Complete();
            return summary;
        }

        using var progress = new SweepProgressPrinter(header: null);
        return engine.RunIndex(full, progress.OnScanProgress, progress.OnPhase);
    }

    /// <summary>
    /// The pre-scan half of <see cref="ExecuteInitCore"/>: ignore files, (optionally) agent
    /// instructions, then the Defender consent prompt -- everything that must run
    /// and fully resolve BEFORE any progress renderer starts or any scanning begins. Factored out
    /// on its own (rather than left inline in <see cref="ExecuteInitCore"/>) so a caller that
    /// wants a progress renderer active ONLY while real scanning is happening -- see <see
    /// cref="HomeCommand"/>'s Case C, which hit a real bug when its ANSI renderer was
    /// constructed/started before this step ran: the renderer's timer ticked (and its redraws
    /// interleaved with the Defender prompt's own stdout, since a background timer callback and a
    /// foreground blocking `Console.ReadLine()` are not synchronized against each other) before
    /// and during the prompt -- can run this first, THEN construct/start its renderer, THEN call
    /// `engine.RunIndex` itself.
    /// </summary>
    internal static void ExecuteInitPreScan(
        UnBrambleEngine engine, bool announce, bool setUpAgents,
        bool interactive, bool forceDefenderPrompt = false, bool skipDefender = false)
    {
        SetUpIgnoreFiles(engine.ProjectRoot, json: !announce);
        if (setUpAgents)
        {
            AgentInstructionsSetup.SetUp(engine.ProjectRoot, Version, line => { if (announce) WriteSetupLine(line); });
        }

        // Defender setup: offered BEFORE the first sweep so the very first cold index is already
        // fast, not just subsequent ones -- gated on `announce` the same way the
        // ignore-file/agent-instruction steps above are (a `--json`/agent caller gets total
        // silence and never a stdin-blocking prompt either).
        //
        // Test-only escape hatch, same convention as MaybeAutoSpawnWatch's own
        // UNBRAMBLE_DISABLE_AUTO_SPAWN (see TestSupport's module initializer): nearly every CLI-level
        // test calls `init` via CliRunner, and the real Dependencies below shell out to real
        // powershell.exe/fsutil.exe and read the real registry to answer the eligibility check --
        // read-only and harmless, but it would otherwise run on every single one of those tests,
        // slowing the whole suite down for no reason. Unset (the normal case) in every real
        // invocation. DefenderExclusionSetupTests calls DefenderExclusionSetup's methods directly
        // (never through Program/CliRunner), so this guard never affects that suite's own coverage
        // of the full decision logic.
        if (announce && Environment.GetEnvironmentVariable("UNBRAMBLE_DISABLE_DEFENDER_SETUP") != "1")
        {
            DefenderExclusionSetup.MaybeOfferSetup(
                engine.ProjectRoot, engine.Config, DefenderExclusionSetup.Dependencies.CreateReal(),
                interactive, forceDefenderPrompt, skipDefender, WriteSetupLine, Console.ReadLine);
        }
    }

    /// <summary>
    /// `init`-only, announced side effect (never runs during query/watch -- no ambient side
    /// effects outside a deliberate setup verb, same "loud, not silent" philosophy the rest of
    /// this CLI follows): makes sure `.unbramble/` (<see cref="UnBramblePaths.StateDirName"/>)
    /// stays out of whatever VCS the project uses.
    ///
    /// Two independent layers, both idempotent (safe to run on every `init`, not just the
    /// first): (1) a self-ignoring `.unbramble/.gitignore` (containing just `*`) dropped inside
    /// the state directory itself -- the same trick git/npm use for their own cache dirs, belt
    /// and suspenders even if step (2) below is somehow skipped or fails; (2) an entry appended
    /// to the detected VCS's own root-level ignore file -- `.gitignore` for git, `ignore.conf`
    /// for Plastic SCM (detected via `.git`/`.plastic` markers at the project root) -- or, if
    /// neither is detected, a one-line manual-setup notice instead of guessing.
    /// </summary>
    private static void SetUpIgnoreFiles(string projectRoot, bool json)
    {
        var stateDir = UnBramblePaths.StateDirFor(projectRoot);
        Directory.CreateDirectory(stateDir);
        WriteSelfIgnoreFile(stateDir);
        TryHideStateDir(stateDir);

        void Announce(string line)
        {
            if (!json)
            {
                WriteSetupLine(line);
            }
        }

        var gitMarker = Path.Combine(projectRoot, ".git");
        var plasticMarker = Path.Combine(projectRoot, ".plastic");

        if (Directory.Exists(gitMarker) || File.Exists(gitMarker))
        {
            AppendLineIfMissing(Path.Combine(projectRoot, ".gitignore"), $"{UnBramblePaths.StateDirName}/");
            Announce($"Ignore rules: added '{UnBramblePaths.StateDirName}/' to .gitignore (git detected).");
        }
        else if (Directory.Exists(plasticMarker))
        {
            AppendLineIfMissing(Path.Combine(projectRoot, "ignore.conf"), UnBramblePaths.StateDirName);
            Announce($"Ignore rules: added '{UnBramblePaths.StateDirName}' to ignore.conf (Plastic SCM detected).");
        }
        else
        {
            Announce(
                $"Note: no .git or .plastic detected at the project root -- add '{UnBramblePaths.StateDirName}/' " +
                "to your VCS's ignore rules manually.");
        }
    }

    private static void WriteSelfIgnoreFile(string stateDir)
    {
        var selfIgnorePath = Path.Combine(stateDir, ".gitignore");
        if (!File.Exists(selfIgnorePath))
        {
            File.WriteAllText(selfIgnorePath, "*" + Environment.NewLine);
        }
    }

    /// <summary>Cosmetic parity with `.git` (which Windows/Explorer also hides) -- wrapped in a
    /// try/catch because this must never fail `init` itself: non-Windows platforms, permission
    /// issues, or any other surprise here just leave the folder visible, which is harmless.</summary>
    private static void TryHideStateDir(string stateDir)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var info = new DirectoryInfo(stateDir);
            if ((info.Attributes & FileAttributes.Hidden) == 0)
            {
                info.Attributes |= FileAttributes.Hidden;
            }
        }
        catch (Exception)
        {
            // Cosmetic only -- see this method's own doc comment.
        }
    }

    /// <summary>Idempotent single-line-entry append: creates <paramref name="filePath"/> if
    /// absent, appends <paramref name="entry"/> on its own line if the file exists but doesn't
    /// already contain it (comparing trimmed lines, so re-running `init` never duplicates the
    /// entry), and does nothing if it's already present.</summary>
    private static void AppendLineIfMissing(string filePath, string entry)
    {
        if (!File.Exists(filePath))
        {
            File.WriteAllText(filePath, entry + Environment.NewLine);
            return;
        }

        var existingLines = File.ReadAllLines(filePath);
        if (existingLines.Any(line => line.Trim() == entry))
        {
            return;
        }

        var text = File.ReadAllText(filePath);
        var needsLeadingNewline = text.Length > 0 && text[^1] != '\n';
        using var writer = new StreamWriter(filePath, append: true);
        if (needsLeadingNewline)
        {
            writer.WriteLine();
        }

        writer.WriteLine(entry);
    }

    private static int RunIndex(string[] rest)
    {
        var reader = ArgReader.Parse(rest, "--json", "--full", "--verbose");
        var json = reader.HasFlag("--json");
        var full = reader.HasFlag("--full");
        var verbose = reader.HasFlag("--verbose");

        using var engine = UnBrambleEngine.Open(reader.ResolveStartPath());
        var summary = RunIndexWithProgress(engine, full, json);

        PrintWarnings(summary.Warnings);
        MaybeWarnDefenderDrift(engine.ProjectRoot);

        if (json)
        {
            WriteIndexJson(summary);
            return 0;
        }

        PrintIndexSummaryLine(summary);
        if (verbose)
        {
            PrintPhaseTimings(summary.PhaseTimings);
        }

        return 0;
    }

    private static int RunResolve(string[] rest)
    {
        var reader = ArgReader.Parse(rest, "--json", "--verbose");
        var json = reader.HasFlag("--json");
        var verbose = reader.HasFlag("--verbose");

        var query = reader.Positional
            ?? throw new ArgReaderException("'resolve' requires a query (path, guid, or name fragment)");

        using var engine = UnBrambleEngine.Open(reader.Path ?? Directory.GetCurrentDirectory());
        PrintFreshness(engine, verbose, json);
        var matches = engine.Resolve(query);

        // A well-formed guid that resolves to nothing is an ANSWER ("not in this index"), not a
        // lookup failure — see UnBrambleEngine.IsBareGuid. Exit 0 so an agent probing a guid's
        // identity doesn't hit the error path on a perfectly good question; a non-guid query that
        // matches nothing still exits 2 exactly as before.
        var unresolvedGuid = matches.Count == 0 && UnBrambleEngine.IsBareGuid(query);

        if (json)
        {
            WriteResolveJson(query, matches, unresolvedGuid);
            return matches.Count == 0 && !unresolvedGuid ? 2 : 0;
        }

        if (unresolvedGuid)
        {
            Console.WriteLine($"{query}  unresolved -- no asset with this guid is in the index (a deleted asset, or one belonging to a package that isn't installed)");
            return 0;
        }

        if (matches.Count == 0)
        {
            WriteError($"no match for '{query}'");
            return 2;
        }

        foreach (var match in matches)
        {
            Console.WriteLine($"{match.Path}  guid={match.Guid ?? "(none)"}  kind={match.Kind.ToDbString()}");
        }

        return 0;
    }

    private static int RunStats(string[] rest)
    {
        var reader = ArgReader.Parse(rest, "--json", "--unresolved", "--collisions", "--verbose");
        var json = reader.HasFlag("--json");
        var unresolvedOnly = reader.HasFlag("--unresolved");
        var collisionsOnly = reader.HasFlag("--collisions");
        var verbose = reader.HasFlag("--verbose");

        using var engine = UnBrambleEngine.Open(reader.ResolveStartPath());
        // stats is a status command: it must report current state immediately rather than
        // stalling behind another process's (e.g. a just-started watcher's) first index.
        PrintFreshness(engine, verbose, json, waitForConcurrentSweep: false);

        if (collisionsOnly)
        {
            return PrintGuidCollisions(engine, json);
        }

        if (unresolvedOnly)
        {
            var items = engine.GetUnresolvedRefs();
            if (json)
            {
                WriteUnresolvedJson(items);
                return 0;
            }

            foreach (var group in items.GroupBy(i => i.SourcePath))
            {
                Console.WriteLine(group.Key + ":");
                foreach (var item in group)
                {
                    Console.WriteLine($"  {item.Kind}={item.TargetKey} (line {item.Line})");
                }
            }

            return 0;
        }

        var stats = engine.GetStats();

        if (json)
        {
            WriteStatsJson(engine, stats);
            return 0;
        }

        var ansi = ConsoleCapabilities.SupportsAnsi;
        var syntacticDetails = engine.GetSyntacticAssemblyDetails();

        var rows = new List<(string Label, string Value)>
        {
            ("Project", $"{engine.ProjectRoot} (Unity {engine.UnityVersion})"),
            ("Files", $"{FormatCount(stats.Files.Total)} ({FormatCount(stats.Files.Assets)} assets, " +
                $"{FormatCount(stats.Files.Scripts)} scripts, {FormatCount(stats.Files.Folders)} folders, " +
                $"{FormatCount(stats.Files.Settings)} settings)"),
            ("Identity-only (PackageCache)", FormatCount(stats.IdentityOnlyCount)),
            ("Guid-less nodes", FormatCount(stats.GuidLessCount)),
            ("Edges", FormatEdgeStatsLine(stats.Edges)),
            ("C#", FormatCsStatsLine(stats.Cs)),
        };

        // Only a row when there ARE collisions — a permanent "Guid collisions: 0" line would
        // spend attention on the healthy case.
        var collisionGroups = engine.GetGuidCollisionGroups();
        if (collisionGroups.Count > 0)
        {
            var collisionFiles = collisionGroups.Sum(g => g.Paths.Count);
            rows.Add(("Guid collisions", $"{FormatCount(collisionGroups.Count)} guid{(collisionGroups.Count == 1 ? "" : "s")} claimed by {FormatCount(collisionFiles)} files (list: stats --collisions)"));
        }

        if (syntacticDetails.Count > 0)
        {
            rows.Add((
                $"Syntactic assemblies ({syntacticDetails.Count}/{stats.Cs.TotalAssemblies})",
                FormatSyntacticAssemblyList(syntacticDetails.Count, syntacticDetails)));
        }

        rows.Add(("DB", $"{engine.DbPath} ({FormatBytes(stats.DbSizeBytes)}, schema v{stats.SchemaVersion})"));
        WriteLabeledRows(rows, ansi);

        // Findings last, after the table rather than wedged between "Syntactic assemblies" and
        // "DB": these are multi-line paragraphs, and splitting the aligned table around them cost
        // the table its one job (a single column of values to read straight down). Same shape as
        // the query footer -- facts first, prose diagnoses after.
        foreach (var note in BuildSyntacticDiagnosisNotes(syntacticDetails, ansi))
        {
            WriteSpacer();
            WriteParagraph(note);
        }

        return 0;
    }

    /// <summary>Section label for `stats`' text output -- e.g. "Files:". Delegates straight to
    /// <see cref="AnsiStyle.Label"/>; kept as a local alias only because `stats` reads better
    /// with a bare `Label(...)` at its call sites.</summary>
    private static string Label(string text, bool ansi) => AnsiStyle.Label(text, ansi);

    /// <summary>`stats --collisions`: every guid currently claimed by more than one indexed
    /// file, one group per guid — the on-demand detail behind the compacted sweep warning
    /// (setup used to print one warning per collision, flooding a
    /// duplicate-heavy project's first index). Derived live from the DB, never a persisted
    /// side artifact.</summary>
    private static int PrintGuidCollisions(UnBrambleEngine engine, bool json)
    {
        var groups = engine.GetGuidCollisionGroups();

        if (json)
        {
            var payload = new GuidCollisionsResultJson
            {
                Count = groups.Count,
                Groups = [.. groups.Select(g => new GuidCollisionGroupJson { Guid = g.Guid, Paths = [.. g.Paths] })],
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, CliJsonContext.Default.GuidCollisionsResultJson));
            return 0;
        }

        Console.WriteLine($"{groups.Count} guid collision{(groups.Count == 1 ? "" : "s")} (each guid below is claimed by every file listed under it; references to it resolve to one of them arbitrarily):");
        foreach (var group in groups)
        {
            Console.WriteLine($"guid {group.Guid}:");
            foreach (var path in group.Paths)
            {
                Console.WriteLine($"  {path}");
            }
        }

        return 0;
    }

    private sealed record AssetAuditTarget(string Query, QueryTarget? Target, string? Error, IReadOnlyList<UnresolvedRefEntry> Items);

    private static int RunAuditAssets(string[] rest)
    {
        var reader = ArgReader.Parse(rest,
            ["--json", "--jsonl", "--missing", "--summary", "--group-by-target", "--include-owner-fields", "--build-reachable-only", "--fail-if-found", "--verbose"],
            ["--paths", "--top"]);
        var json = reader.HasFlag("--json");
        var jsonl = reader.HasFlag("--jsonl");
        var grouped = reader.HasFlag("--summary") || reader.HasFlag("--group-by-target");
        var buildReachableOnly = reader.HasFlag("--build-reachable-only");
        var failIfFound = reader.HasFlag("--fail-if-found");
        var verbose = reader.HasFlag("--verbose");
        var top = ParsePositiveIntOption(reader, "--top");

        if (json && jsonl)
        {
            throw new ArgReaderException("--json and --jsonl are mutually exclusive");
        }

        var pathsOption = reader.GetValue("--paths");
        if (pathsOption is not null && reader.Positional is not null)
        {
            throw new ArgReaderException("pass the input file either positionally or with --paths, not both");
        }

        var input = pathsOption ?? reader.Positional
            ?? throw new ArgReaderException("'audit-assets' requires a text file containing one asset path per line");
        var inputFullPath = Path.GetFullPath(input);
        if (!File.Exists(inputFullPath))
        {
            throw new ArgReaderException($"input file not found: {input}");
        }

        var queries = File.ReadLines(inputFullPath)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();
        if (queries.Count == 0)
        {
            throw new ArgReaderException($"input file contains no asset paths: {input}");
        }

        using var engine = UnBrambleEngine.Open(reader.ResolveStartPathIgnoringPositional());
        PrintFreshness(engine, verbose, json || jsonl, assetOnly: true);

        var results = new List<AssetAuditTarget>(queries.Count);
        for (var i = 0; i < queries.Count; i++)
        {
            var query = queries[i];
            Console.Error.WriteLine($"Resolving {i + 1}/{queries.Count}: {query}");
            var resolution = engine.ResolveQueryTarget(query);
            if (resolution.Target?.FileId is not { } fileId)
            {
                var error = resolution.Candidates.Count > 0
                    ? $"ambiguous ({resolution.Candidates.Count} matches)"
                    : "no indexed asset match";
                Console.Error.WriteLine($"  {error}");
                var failedResult = new AssetAuditTarget(query, resolution.Target, error, []);
                results.Add(failedResult);
                if (jsonl)
                {
                    Console.WriteLine(JsonSerializer.Serialize(ToAssetAuditTargetJson(failedResult, grouped, top), CliJsonContext.Default.AssetAuditTargetJson));
                }
                continue;
            }

            IReadOnlyList<UnresolvedRefEntry> items = engine.GetUnresolvedRefs(fileId);

            Console.Error.WriteLine($"  {items.Count} unresolved link{(items.Count == 1 ? "" : "s")} found");
            if (jsonl && items.Count > 0)
            {
                Console.Error.WriteLine("  computing source build relevance...");
                items = AnnotateUnresolvedBuildReachability(items, engine);
            }

            if (jsonl && buildReachableOnly)
            {
                items = [.. items.Where(u => u.BuildReachable == true)];
            }

            var targetResult = new AssetAuditTarget(query, resolution.Target, null, items);
            results.Add(targetResult);
            if (jsonl)
            {
                Console.WriteLine(JsonSerializer.Serialize(ToAssetAuditTargetJson(targetResult, grouped, top), CliJsonContext.Default.AssetAuditTargetJson));
            }
        }

        var rawItems = results.SelectMany(r => r.Items).ToList();
        if (!jsonl && rawItems.Count > 0)
        {
            Console.Error.WriteLine($"Computing build relevance for {rawItems.Select(u => u.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Count()} source asset{(rawItems.Select(u => u.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 ? "" : "s")}...");
            var reachabilityStopwatch = Stopwatch.StartNew();
            var reachable = engine.ComputeBuildReachablePaths(rawItems.Select(u => u.SourcePath));
            results = [.. results.Select(result => result with
            {
                Items = [.. AnnotateUnresolvedBuildReachability(result.Items, reachable)
                    .Where(u => !buildReachableOnly || u.BuildReachable == true)],
            })];
            Console.Error.WriteLine($"Build relevance ready in {FormatSeconds(reachabilityStopwatch.Elapsed)}");
        }

        var allItems = results.SelectMany(r => r.Items).ToList();
        var errors = results.Count(r => r.Error is not null);
        if (json)
        {
            var groups = grouped ? GroupUnresolved(allItems, top) : [];
            var payload = new AssetAuditResultJson
            {
                Input = input,
                TargetCount = results.Count,
                ResolvedTargetCount = results.Count - errors,
                Count = allItems.Count,
                Grouped = grouped,
                Results = [.. results.Select(r => ToAssetAuditTargetJson(r, grouped, top))],
                Groups = [.. groups.Select(ToUnresolvedGroupJson)],
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, CliJsonContext.Default.AssetAuditResultJson));
        }
        else if (!jsonl && grouped)
        {
            PrintUnresolvedGroups(allItems, top);
        }
        else if (!jsonl)
        {
            foreach (var result in results)
            {
                Console.WriteLine(result.Error is null ? $"{result.Query}:" : $"{result.Query}: ERROR {result.Error}");
                foreach (var item in result.Items)
                {
                    Console.WriteLine("  " + FormatUnresolvedLine(item));
                }
            }
        }

        if (errors > 0)
        {
            return 2;
        }

        return failIfFound && allItems.Count > 0 ? 3 : 0;
    }

    private static AssetAuditTargetJson ToAssetAuditTargetJson(AssetAuditTarget result, bool grouped, int? top) => new()
    {
        Query = result.Query,
        Target = result.Target is null ? null : new QueryTargetJson { Path = result.Target.Path, Guid = result.Target.Guid },
        Error = result.Error,
        Count = result.Items.Count,
        Items = grouped ? [] : [.. result.Items.Select(ToUnresolvedJson)],
        Groups = grouped ? [.. GroupUnresolved(result.Items, top).Select(ToUnresolvedGroupJson)] : [],
    };

    /// <summary>
    /// `who-uses`: accepts a path/guid target OR a C# symbol (`cs-refs` is a provisional alias
    /// of the symbol form). Disambiguation: try path/guid resolution first, then symbol
    /// resolution; if BOTH resolve, `--symbol` or a doc-id-kind prefix (`T:`/`M:`/`F:`/`P:`/`E:`)
    /// is required and both interpretations are listed — never guess which one the caller meant.
    /// </summary>
    private static int RunWhoUses(string[] rest)
    {
        var reader = ArgReader.Parse(rest, ["--json", "--jsonl", "--transitive", "--verbose", "--symbol"], ["--depth", "--kind", "--under", "--guids"]);
        var json = reader.HasFlag("--json");
        var jsonl = reader.HasFlag("--jsonl");
        var transitive = reader.HasFlag("--transitive");
        var verbose = reader.HasFlag("--verbose");
        var forceSymbol = reader.HasFlag("--symbol");
        var kindFilter = ParseKindFilter(reader);
        var underFilter = reader.GetValue("--under");
        var depthCap = ParseDepth(reader);
        var guidFile = reader.GetValue("--guids");
        if (guidFile is not null && reader.Positional is not null)
        {
            throw new ArgReaderException("pass either one target or --guids <file>, not both");
        }

        if (json && jsonl)
        {
            throw new ArgReaderException("--json and --jsonl are mutually exclusive");
        }

        var targetArg = reader.Positional;
        if (targetArg is null && guidFile is null)
        {
            throw new ArgReaderException("'who-uses' requires a target or --guids <file>");
        }

        var targetLooksAssetOnly = guidFile is not null ||
            (targetArg is not null && !forceSymbol &&
                (UnBrambleEngine.IsBareGuid(targetArg) ||
                 targetArg.Contains('/') || targetArg.Contains('\\') || Path.IsPathFullyQualified(targetArg)) &&
                !targetArg.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));

        using var engine = UnBrambleEngine.Open(reader.ResolveStartPathIgnoringPositional());
        PrintFreshness(engine, verbose, json || jsonl, assetOnly: targetLooksAssetOnly);

        if (guidFile is not null)
        {
            if (forceSymbol)
            {
                throw new ArgReaderException("--symbol cannot be combined with --guids");
            }

            return RunWhoUsesGuidBatch(engine, guidFile, transitive, depthCap, kindFilter, underFilter, json, jsonl, verbose);
        }

        if (forceSymbol || HasDocIdPrefix(targetArg!))
        {
            return RunWhoUsesSymbol(engine, targetArg!, transitive, depthCap, kindFilter, underFilter, json || jsonl, verbose);
        }

        var pathResolution = engine.ResolveQueryTarget(targetArg!);
        // A bare GUID or an explicit path cannot also be a C# symbol. Avoid the project-wide
        // fuzzy symbol lookup for these overwhelmingly common asset-query shapes; on a large project that
        // unrelated lookup dominated an otherwise indexed unresolved-GUID reverse query.
        var assetOnlyShape = UnBrambleEngine.IsBareGuid(targetArg!) ||
            targetArg!.Contains('/') || targetArg.Contains('\\') || Path.IsPathFullyQualified(targetArg);
        var symbolResolution = assetOnlyShape
            ? new CsSymbolResolution(null, [])
            : engine.ResolveCsSymbol(targetArg!);
        var pathFound = pathResolution.Target is not null;
        var symbolFound = symbolResolution.DocId is not null;

        if (pathFound && symbolFound)
        {
            return ReportAmbiguousPathOrSymbol(targetArg!, pathResolution.Target!, symbolResolution.DocId!, json || jsonl);
        }

        if (symbolFound)
        {
            return RunWhoUsesSymbolFromResolution(symbolResolution.DocId!, engine, transitive, depthCap, kindFilter, underFilter, json || jsonl, verbose);
        }

        if (!pathFound)
        {
            // Neither a path/guid nor an unambiguous symbol resolved. If the symbol side is the
            // one with real ambiguity (candidates) and the path side found nothing at all,
            // surfacing the symbol candidates is more useful than a flat "no match".
            if (pathResolution.Candidates.Count == 0 && symbolResolution.Candidates.Count > 0)
            {
                return ReportCsSymbolResolutionFailure(targetArg!, symbolResolution, json || jsonl);
            }

            return ReportTargetNotFound(targetArg!, pathResolution.Candidates, json || jsonl);
        }

        var answer = TagBuildReachability(
            ApplyUnderFilter(ApplyKindFilter(engine.WhoUses(pathResolution.Target!, transitive, depthCap), kindFilter), underFilter, forward: false),
            engine);
        answer = SuppressIrrelevantCsCaveats(answer);

        if (json || jsonl)
        {
            WriteQueryJson("who-uses", answer);
            return 0;
        }

        Console.WriteLine(FormatTargetHeader(pathResolution.Target!));
        if (answer.TransitiveUnavailable)
        {
            WriteWarning("target guid does not resolve to an indexed file; no transitive walk is possible.");
        }

        if (!transitive)
        {
            PrintDirectReferencers(answer.Results);
        }
        else
        {
            PrintTransitiveWhoUses(answer);
        }

        PrintBlindSpotsFooter(answer, verbose);
        return 0;
    }

    private static int RunWhoUsesGuidBatch(
        UnBrambleEngine engine,
        string input,
        bool transitive,
        int depthCap,
        string? kindFilter,
        string? underFilter,
        bool json,
        bool jsonl,
        bool verbose)
    {
        var fullPath = Path.GetFullPath(input);
        if (!File.Exists(fullPath))
        {
            throw new ArgReaderException($"guid input file not found: {input}");
        }

        var guids = File.ReadLines(fullPath)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();
        if (guids.Count == 0)
        {
            throw new ArgReaderException($"guid input file contains no guids: {input}");
        }

        var invalid = guids.FirstOrDefault(guid => !UnBrambleEngine.IsBareGuid(guid));
        if (invalid is not null)
        {
            throw new ArgReaderException($"invalid guid in {input}: {invalid}");
        }

        var answers = new List<QueryAnswer>(guids.Count);
        for (var i = 0; i < guids.Count; i++)
        {
            var guid = guids[i];
            Console.Error.WriteLine($"Resolving {i + 1}/{guids.Count}: {guid}");
            var target = engine.ResolveQueryTarget(guid).Target!;
            var answer = SuppressIrrelevantCsCaveats(
                ApplyUnderFilter(ApplyKindFilter(engine.WhoUses(target, transitive, depthCap), kindFilter), underFilter, forward: false));
            Console.Error.WriteLine($"  {answer.Results.Count} referencer{(answer.Results.Count == 1 ? "" : "s")} found");
            if (jsonl && answer.Results.Count > 0)
            {
                Console.Error.WriteLine("  computing build reachability...");
                answer = TagBuildReachability(answer, engine.ComputeBuildReachablePaths(answer.Results.Select(result => result.SourcePath)));
            }

            answers.Add(answer);
            if (jsonl)
            {
                Console.WriteLine(JsonSerializer.Serialize(ToQueryResultJson("who-uses", answer), CliJsonContext.Default.QueryResultJson));
            }
        }

        if (!jsonl && answers.Any(answer => answer.Results.Count > 0))
        {
            Console.Error.WriteLine("Computing build reachability once for the batch...");
            var reachable = engine.ComputeBuildReachablePaths(answers.SelectMany(answer => answer.Results).Select(result => result.SourcePath));
            answers = [.. answers.Select(answer => TagBuildReachability(answer, reachable))];
        }

        if (json)
        {
            var payload = new QueryBatchResultJson
            {
                Query = "who-uses",
                Count = answers.Count,
                Results = [.. answers.Select(answer => ToQueryResultJson("who-uses", answer))],
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, CliJsonContext.Default.QueryBatchResultJson));
        }
        else if (!jsonl)
        {
            foreach (var answer in answers)
            {
                Console.WriteLine(FormatTargetHeader(answer.Target));
                if (!transitive)
                {
                    PrintDirectReferencers(answer.Results);
                }
                else
                {
                    PrintTransitiveWhoUses(answer);
                }

                PrintBlindSpotsFooter(answer, verbose);
            }
        }

        return 0;
    }

    private static QueryAnswer SuppressIrrelevantCsCaveats(QueryAnswer answer)
    {
        if (answer.PossibleFalseNegative || answer.Results.Any(result => result.Kind is "cs" or "event"))
        {
            return answer;
        }

        return answer with
        {
            BlindSpots = [.. answer.BlindSpots.Where(spot => spot is not BlindSpots.SyntacticAssembliesPresent and not BlindSpots.CsprojStale and not BlindSpots.DisabledRegionRefsPossible)],
            SyntacticAssemblies = null,
            PossibleFalseNegative = false,
        };
    }

    private static int RunWhoUsesSymbol(UnBrambleEngine engine, string targetArg, bool transitive, int depthCap, string? kindFilter, string? underFilter, bool json, bool verbose)
    {
        // targetArg is passed through as-is: stored doc_ids already carry their kind-letter
        // prefix ("T:Foo", "M:Foo.Jump"), so a "T:"/"M:"/... -prefixed argument hits
        // ResolveCsSymbol's exact-doc_id branch directly, same as an unprefixed name would hit
        // its Type.Member / fuzzy branches.
        var resolution = engine.ResolveCsSymbol(targetArg);
        if (resolution.DocId is null)
        {
            return ReportCsSymbolResolutionFailure(targetArg, resolution, json);
        }

        return RunWhoUsesSymbolFromResolution(resolution.DocId, engine, transitive, depthCap, kindFilter, underFilter, json, verbose);
    }

    private static int RunWhoUsesSymbolFromResolution(string docId, UnBrambleEngine engine, bool transitive, int depthCap, string? kindFilter, string? underFilter, bool json, bool verbose)
    {
        var answer = TagBuildReachability(
            ApplyUnderFilter(ApplyKindFilter(engine.WhoUsesSymbol(docId, transitive, depthCap), kindFilter), underFilter, forward: false),
            engine);

        if (json)
        {
            WriteQueryJson("who-uses", answer, symbol: docId);
            return 0;
        }

        Console.WriteLine($"symbol {docId}");
        PrintSymbolQueryResults(answer);
        PrintBlindSpotsFooter(answer, verbose);
        return 0;
    }

    /// <summary>A leading kind-letter doc-id prefix (`T:`/`M:`/`F:`/`P:`/`E:`) is one of the two
    /// explicit ways (with `--symbol`) to force symbol resolution over path/guid resolution
    /// when both could apply.</summary>
    private static bool HasDocIdPrefix(string arg) =>
        arg.Length > 2 && arg[1] == ':' && arg[0] is 'T' or 'M' or 'F' or 'P' or 'E';

    private static int ReportAmbiguousPathOrSymbol(string targetArg, QueryTarget pathTarget, string docId, bool json)
    {
        const string hint = "Disambiguate with --symbol (forces the C# symbol reading) or a doc-id prefix (e.g. 'T:Foo', 'M:Foo.Jump').";

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new AmbiguousPathOrSymbolJson
                {
                    Query = targetArg,
                    PathInterpretation = new QueryTargetJson { Path = pathTarget.Path, Guid = pathTarget.Guid },
                    SymbolInterpretation = docId,
                    Hint = hint,
                },
                CliJsonContext.Default.AmbiguousPathOrSymbolJson));
        }
        else
        {
            WriteError($"'{targetArg}' is ambiguous — it resolves as both a path/guid and a C# symbol:");
            Console.Error.WriteLine($"  path:   {pathTarget.Path ?? pathTarget.Guid}");
            Console.Error.WriteLine($"  symbol: {docId}");
            Console.Error.WriteLine(hint);
        }

        return 2;
    }

    private static string? ParseKindFilter(ArgReader reader)
    {
        var raw = reader.GetValue("--kind");
        if (raw is null)
        {
            return null;
        }

        if (raw is not ("guid" or "path" or "cs" or "event" or "dll"))
        {
            throw new ArgReaderException("'--kind' must be one of: guid, path, cs, event, dll");
        }

        return raw;
    }

    private static QueryAnswer ApplyKindFilter(QueryAnswer answer, string? kind)
    {
        if (kind is null)
        {
            return answer;
        }

        var filtered = answer.Results.Where(r => r.Kind == kind).ToList();
        var hasNonSpeculative = filtered.Any(r => r.ConfidenceLabel is not null && r.ConfidenceLabel != UnBramble.Core.Query.EdgeConfidence.Speculative);
        var possibleFalseNegative = !hasNonSpeculative && answer.SyntacticAssemblies is not null
            && (answer.Target.Path?.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ?? false);
        return answer with
        {
            Results = filtered,
            Confidence = UnBramble.Core.Query.EdgeConfidence.AnswerLevel(filtered),
            PossibleFalseNegative = possibleFalseNegative,
        };
    }

    /// <summary>
    /// `--under <prefix>`: scopes an answer to one location — a
    /// `uses` on HDRP's global settings returned 398 dependencies, mostly registry-package
    /// internals drowning the dozen project-side ones the question was about. Filters on the
    /// side of the edge the verb varies over: the dependency (TargetPath) for `uses`
    /// (<paramref name="forward"/> true), the referencer (SourcePath) for `who-uses`. Rows with
    /// no path on the filtered side (unresolved, builtin) are excluded — a location filter can
    /// only keep what provably HAS that location. Prefix match is per path segment,
    /// case-insensitive, either slash direction.
    /// </summary>
    private static QueryAnswer ApplyUnderFilter(QueryAnswer answer, string? under, bool forward)
    {
        if (under is null)
        {
            return answer;
        }

        var normalized = under.Replace('\\', '/').TrimEnd('/');
        if (normalized.Length == 0)
        {
            throw new ArgReaderException("'--under' requires a non-empty path prefix (e.g. --under Assets)");
        }

        bool Matches(string? path) =>
            path is not null &&
            path.StartsWith(normalized, StringComparison.OrdinalIgnoreCase) &&
            (path.Length == normalized.Length || path[normalized.Length] == '/');

        var filtered = answer.Results.Where(r => Matches(forward ? r.TargetPath : r.SourcePath)).ToList();
        return answer with
        {
            Results = filtered,
            Confidence = UnBramble.Core.Query.EdgeConfidence.AnswerLevel(filtered),
        };
    }

    /// <summary>
    /// Annotates every result of a who-uses answer with whether its SOURCE file is forward-
    /// reachable from the liveness roots (<see cref="UnBrambleEngine.ComputeBuildReachablePaths"/>)
    /// — proven referencers can still be irrelevant
    /// test/dead content, and the tool couldn't say which was which. who-uses only: for `uses`,
    /// every dependency inherits the target's own reachability, so the tag would be one fact
    /// repeated per line.
    /// </summary>
    private static QueryAnswer TagBuildReachability(QueryAnswer answer, UnBrambleEngine engine)
    {
        if (answer.Results.Count == 0)
        {
            return answer;
        }

        return TagBuildReachability(answer, engine.ComputeBuildReachablePaths(answer.Results.Select(result => result.SourcePath)));
    }

    private static QueryAnswer TagBuildReachability(QueryAnswer answer, HashSet<string> reachable)
    {
        return answer with
        {
            Results = answer.Results.Select(r => r with { BuildReachable = reachable.Contains(r.SourcePath) }).ToList(),
        };
    }

    private static int RunUses(string[] rest)
    {
        if (rest.Contains("--paths", StringComparer.Ordinal))
        {
            if (!rest.Contains("--missing-only", StringComparer.Ordinal))
            {
                throw new ArgReaderException("uses --paths currently requires --missing-only; use audit-assets for batch asset auditing");
            }

            return RunAuditAssets([.. rest.Select(arg => arg == "--missing-only" ? "--missing" : arg)]);
        }

        var reader = ArgReader.Parse(rest,
            ["--json", "--transitive", "--missing-only", "--verbose", "--fail-if-found", "--summary", "--group-by-target", "--build-reachable-only"],
            ["--depth", "--kind", "--under", "--top"]);
        var json = reader.HasFlag("--json");
        var transitive = reader.HasFlag("--transitive");
        var missingOnly = reader.HasFlag("--missing-only");
        var failIfFound = reader.HasFlag("--fail-if-found");
        var grouped = reader.HasFlag("--summary") || reader.HasFlag("--group-by-target");
        var buildReachableOnly = reader.HasFlag("--build-reachable-only");
        var top = ParsePositiveIntOption(reader, "--top");
        var verbose = reader.HasFlag("--verbose");
        var kindFilter = ParseKindFilter(reader);
        var underFilter = reader.GetValue("--under");
        var depthCap = ParseDepth(reader);
        var targetArg = reader.Positional ?? throw new ArgReaderException("'uses' requires a target (path or guid)");

        using var engine = UnBrambleEngine.Open(reader.ResolveStartPathIgnoringPositional());
        PrintFreshness(engine, verbose, json, assetOnly: missingOnly);

        var resolution = engine.ResolveQueryTarget(targetArg);
        if (resolution.Target is null)
        {
            return ReportTargetNotFound(targetArg, resolution.Candidates, json);
        }

        if (missingOnly)
        {
            Console.Error.WriteLine($"Resolving missing references: {targetArg}");
            var missingStopwatch = Stopwatch.StartNew();
            var unresolved = resolution.Target.FileId is { } fileId ? engine.GetUnresolvedRefs(fileId) : [];
            Console.Error.WriteLine($"  {unresolved.Count} unresolved link{(unresolved.Count == 1 ? "" : "s")} found in {FormatSeconds(missingStopwatch.Elapsed)}");
            if (unresolved.Count > 0)
            {
                Console.Error.WriteLine("Computing source build relevance...");
                var reachabilityStopwatch = Stopwatch.StartNew();
                unresolved = AnnotateUnresolvedBuildReachability(unresolved, engine);
                Console.Error.WriteLine($"  build relevance ready in {FormatSeconds(reachabilityStopwatch.Elapsed)}");
            }

            if (buildReachableOnly)
            {
                unresolved = [.. unresolved.Where(u => u.BuildReachable == true)];
            }

            if (json)
            {
                WriteUnresolvedJson(unresolved, grouped, top);
            }
            else if (grouped)
            {
                PrintUnresolvedGroups(unresolved, top);
            }
            else
            {
                foreach (var u in unresolved)
                {
                    Console.WriteLine(FormatUnresolvedLine(u));
                }
            }

            return failIfFound && unresolved.Count > 0 ? 3 : 0;
        }

        if (grouped || buildReachableOnly || top is not null || failIfFound)
        {
            throw new ArgReaderException("--summary, --group-by-target, --top, --build-reachable-only, and --fail-if-found require --missing-only");
        }

        var answer = ApplyUnderFilter(ApplyKindFilter(engine.Uses(resolution.Target, transitive, depthCap), kindFilter), underFilter, forward: true);

        if (json)
        {
            WriteQueryJson("uses", answer);
            return 0;
        }

        Console.WriteLine(FormatTargetHeader(resolution.Target));
        if (answer.TransitiveUnavailable)
        {
            WriteWarning("target guid does not resolve to an indexed file; nothing to enumerate.");
        }

        // An explicit --under scope IS the "show me these" request — never collapse inside it.
        var expandAll = verbose || underFilter is not null;
        if (!transitive)
        {
            PrintDirectDependencies(answer.Results, expandAll);
        }
        else
        {
            PrintTransitiveUses(answer, expandAll);
        }

        PrintBlindSpotsFooter(answer, verbose);
        return 0;
    }

    /// <summary>
    /// `cs-refs`: symbol-level reverse lookup, provisional verb name and shape. Matches against
    /// doc_id/name (exact, then Type.Member, then fuzzy with candidate listing on ambiguity —
    /// never runs a query on a guess, same discipline as who-uses/uses' fuzzy path resolution).
    /// </summary>
    private static int RunCsRefs(string[] rest)
    {
        var reader = ArgReader.Parse(rest, ["--json", "--verbose"], []);
        var json = reader.HasFlag("--json");
        var verbose = reader.HasFlag("--verbose");
        var query = reader.Positional ?? throw new ArgReaderException("'cs-refs' requires a symbol name or doc-id");

        using var engine = UnBrambleEngine.Open(reader.Path ?? Directory.GetCurrentDirectory());
        PrintFreshness(engine, verbose, json);

        var resolution = engine.ResolveCsSymbol(query);
        if (resolution.DocId is null)
        {
            return ReportCsSymbolResolutionFailure(query, resolution, json);
        }

        var answer = engine.GetCsRefsAnswer(resolution.DocId);
        var refs = answer.Refs;

        if (json)
        {
            var payload = new CsRefsResultJson
            {
                Query = query,
                DocId = resolution.DocId,
                Count = refs.Count,
                Results = [.. refs.Select(r => new CsRefEntryJson { Source = r.SourcePath, Line = r.Line, ContainingSymbol = r.ContainingSymbol, RefKind = r.RefKind, Confidence = r.Confidence })],
                EventResults = [.. answer.EventRefs.Select(ToEdgeJson)],
                BlindSpots = [.. answer.BlindSpots],
                SyntacticAssemblies = ToSyntacticAssembliesJson(answer.SyntacticAssemblies),
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, CliJsonContext.Default.CsRefsResultJson));
            return 0;
        }

        Console.WriteLine(resolution.DocId);
        Console.WriteLine($"{refs.Count} referencer{(refs.Count == 1 ? "" : "s")}:");
        foreach (var r in refs)
        {
            var containing = r.ContainingSymbol is not null ? $"{r.ContainingSymbol} " : "";
            Console.WriteLine($"  {r.SourcePath}:{r.Line}   {containing}{r.RefKind}  [{r.Confidence}]");
        }

        // A UnityEvent binding is a real call site of this method, just wired in serialized data
        // instead of in code — printed in its own section rather than merged into the count above,
        // because "referencers" there means symbol_refs call sites and quietly changing what that
        // number counts would break every consumer of this verb's shape.
        if (answer.EventRefs.Count > 0)
        {
            Console.WriteLine($"{answer.EventRefs.Count} UnityEvent binding{(answer.EventRefs.Count == 1 ? "" : "s")} (serialized, not called from code):");
            foreach (var r in answer.EventRefs)
            {
                Console.WriteLine($"  {FormatEventEdge(r)}");
            }
        }

        PrintCsRefsFooter(answer, refs.Count, resolution.DocId, verbose);
        return 0;
    }

    /// <summary>
    /// `cs-refs`' caveat footer. Reuses <see cref="PrintBlindSpotsFooter"/> so the wording can
    /// never drift from the who-uses/uses footer, wrapping the answer in a QueryAnswer purely as
    /// the transport for the shared fields (no target/results are rendered by that method).
    /// PossibleFalseNegative is deliberately left false: its warning text tells the reader to
    /// "check any speculative matches above", and `cs-refs` has no speculative section — the
    /// empty-answer pointer below does that job honestly instead, naming the verb that DOES run
    /// the name-match fallback.
    /// </summary>
    private static void PrintCsRefsFooter(CsRefsAnswer answer, int refCount, string docId, bool verbose)
    {
        PrintBlindSpotsFooter(
            new QueryAnswer(
                new QueryTarget(null, null, null), [], Truncated: false, TransitiveUnavailable: false,
                Confidence: null, BlindSpots: answer.BlindSpots, SyntacticAssemblies: answer.SyntacticAssemblies),
            verbose);

        if (refCount == 0 && answer.EventRefs.Count == 0)
        {
            var ansi = ConsoleCapabilities.SupportsAnsi;
            WriteParagraph(
                AnsiStyle.Muted("(no referencer of any kind found — ", ansi) +
                "`" + AnsiStyle.Command($"unbramble who-uses {docId}", ansi) + "`" +
                AnsiStyle.Muted(
                    " asks the wider question: it adds the declaring file's own asset referencers and, where " +
                    "syntactic assemblies exist, speculative name-match leads)", ansi));
        }
    }

    /// <summary>
    /// `unbramble dead-candidates`: thin CLI over <see cref="UnBrambleEngine.RunDeadCandidates"/>
    /// — all root/fixed-point/screen logic lives in Core. Exit codes: 0 = ran (candidates or
    /// not), 1 = liveness unavailable (gate failure) — 2/3 are never used by this verb.
    /// </summary>
    private static int RunDeadCandidates(string[] rest)
    {
        var reader = ArgReader.Parse(rest, ["--json", "--include-advisory"], ["--kind"]);
        var json = reader.HasFlag("--json");
        var includeAdvisory = reader.HasFlag("--include-advisory");
        var kindFilter = reader.GetValue("--kind");
        if (kindFilter is not (null or "assets" or "cs" or "all"))
        {
            throw new ArgReaderException("'--kind' must be one of: assets, cs, all");
        }

        using var engine = UnBrambleEngine.Open(reader.ResolveStartPath());
        using var progress = new SweepProgressPrinter(
            "freshness: no live watcher heartbeat -- sweeping the index inline before answering (a cold sweep of a large project can take minutes; progress follows)");
        var result = engine.RunDeadCandidates(progress.OnScanProgress, progress.OnPhase);
        progress.Dispose();

        bool PassesKindFilter(string path)
        {
            var isCs = path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
            return kindFilter switch
            {
                "cs" => isCs,
                "assets" => !isCs,
                _ => true,
            };
        }

        var provenDead = result.ProvenDead.Where(e => PassesKindFilter(e.Path)).ToList();
        var advisoryDead = result.AdvisoryDead.Where(e => PassesKindFilter(e.Path)).ToList();

        if (json)
        {
            WriteDeadCandidatesJson(result, provenDead, advisoryDead);
            return result.Available ? 0 : 1;
        }

        if (!result.Available)
        {
            Console.Error.WriteLine("liveness unavailable:");
            foreach (var reason in result.UnavailableReasons)
            {
                WriteParagraph(reason, firstPrefix: "  ", writer: Console.Error);
            }

            return 1;
        }

        var roots = result.Roots!;
        WriteParagraph(
            $"liveness roots: {FormatCount(roots.ProjectSettingsFileCount)} ProjectSettings files, " +
            $"{FormatCount(roots.ResourcesFileCount)} Resources/ files, {FormatCount(roots.StreamingAssetsFileCount)} StreamingAssets files, " +
            $"{FormatCount(roots.EntryPointFileCount)} entry-point files; Addressables: {roots.AddressablesStatusText}");
        WriteParagraph($"analysis: {FormatCount(result.TotalAssemblies)} assemblies, all semantic; csprojs fresh");
        WriteParagraph($"excluded by convention (never candidates): {FormatCount(result.ConventionExcludedCount)} files (*.asmdef, *.asmref, link.xml, csc.rsp, package.json)");
        if (roots.AllowlistCount > 0)
        {
            WriteParagraph($"allowlist: {FormatCount(roots.AllowlistCount)} files treated as live (unbramble.json liveness.allowlist)");
        }

        WriteSpacer();
        Console.WriteLine($"provably unreachable ({FormatCount(provenDead.Count)} files):");
        foreach (var entry in provenDead)
        {
            Console.WriteLine($"  {entry.Path}  [proven]  ({entry.Reason})");
        }

        // Same grammar as the query footer: a break, then the caveats that qualify everything
        // above rather than adding to it.
        WriteSpacer();
        WriteParagraph($"(blind spots — apply to every claim above: {string.Join(", ", result.BlindSpots)})");
        WriteParagraph(
            "residual risk beyond the above is absorbed by the workflow, not by this tool's confidence: " +
            "propose a batch -> delete -> run the project's own smoke tests -> merge if green -> repeat.");

        if (includeAdvisory)
        {
            Console.WriteLine($"advisory (screened -> treated as live for propagation, {FormatCount(advisoryDead.Count)} files):");
            foreach (var entry in advisoryDead)
            {
                Console.WriteLine($"  {entry.Path}  [advisory: {entry.Reason}]");
            }
        }
        else if (advisoryDead.Count > 0)
        {
            Console.WriteLine($"advisory (screened -> treated as live for propagation; shown with --include-advisory): {FormatCount(advisoryDead.Count)} files");
        }

        return 0;
    }

    private static void WriteDeadCandidatesJson(
        UnBramble.Core.Liveness.DeadCandidatesResult result,
        List<UnBramble.Core.Liveness.DeadCandidateEntry> provenDead,
        List<UnBramble.Core.Liveness.AdvisoryDeadEntry> advisoryDead)
    {
        var payload = new DeadCandidatesResultJson
        {
            Available = result.Available,
            UnavailableReasons = [.. result.UnavailableReasons],
            Roots = result.Roots is { } r
                ? new LivenessRootSummaryJson
                {
                    ProjectSettingsFileCount = r.ProjectSettingsFileCount,
                    ResourcesFileCount = r.ResourcesFileCount,
                    StreamingAssetsFileCount = r.StreamingAssetsFileCount,
                    EntryPointFileCount = r.EntryPointFileCount,
                    Addressables = r.AddressablesStatusText,
                    AllowlistCount = r.AllowlistCount,
                }
                : null,
            TotalAssemblies = result.TotalAssemblies,
            SyntacticAssemblies = result.SyntacticAssemblies,
            ConventionExcludedCount = result.ConventionExcludedCount,
            ProvenDead = [.. provenDead.Select(e => new DeadCandidateEntryJson { Path = e.Path, Reasons = [e.Reason] })],
            AdvisoryDead = [.. advisoryDead.Select(e => new AdvisoryDeadEntryJson { Path = e.Path, Reason = e.Reason })],
            BlindSpots = [.. result.BlindSpots],
        };
        Console.WriteLine(JsonSerializer.Serialize(payload, CliJsonContext.Default.DeadCandidatesResultJson));
    }

    private static int ParseDepth(ArgReader reader)
    {
        var raw = reader.GetValue("--depth");
        if (raw is null)
        {
            return UnBrambleEngine.DefaultDepthCap;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 1)
        {
            throw new ArgReaderException("'--depth' requires a positive integer");
        }

        return value;
    }

    private static int? ParsePositiveIntOption(ArgReader reader, string option)
    {
        var raw = reader.GetValue(option);
        if (raw is null)
        {
            return null;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 1)
        {
            throw new ArgReaderException($"'{option}' requires a positive integer");
        }

        return value;
    }

    private static int ReportTargetNotFound(string targetArg, IReadOnlyList<ResolveMatch> candidates, bool json)
    {
        if (candidates.Count == 0)
        {
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    new TargetNotFoundJson { Query = targetArg, Candidates = [] },
                    CliJsonContext.Default.TargetNotFoundJson));
            }
            else
            {
                WriteError($"no match for '{targetArg}'");
            }

            return 2;
        }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new TargetNotFoundJson
                {
                    Query = targetArg,
                    Candidates = [.. candidates.Select(c => new ResolveMatchJson { Path = c.Path, Guid = c.Guid, Kind = c.Kind.ToDbString(), IdentityOnly = c.IdentityOnly })],
                },
                CliJsonContext.Default.TargetNotFoundJson));
        }
        else
        {
            WriteError($"ambiguous target '{targetArg}' ({candidates.Count} matches):");
            foreach (var c in candidates)
            {
                Console.Error.WriteLine($"  {c.Path}");
            }
        }

        return 2;
    }

    /// <summary>Shared not-found/ambiguous reporting for a `ResolveCsSymbol` result — used by
    /// both `cs-refs` and `who-uses`' symbol-argument path so the two surfaces can never drift
    /// in how they report the same resolution outcome.</summary>
    private static int ReportCsSymbolResolutionFailure(string query, CsSymbolResolution resolution, bool json)
    {
        if (resolution.Candidates.Count == 0)
        {
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(
                    new CsSymbolAmbiguousJson { Query = query, Candidates = [] },
                    CliJsonContext.Default.CsSymbolAmbiguousJson));
            }
            else
            {
                WriteError($"no C# symbol match for '{query}'");
            }

            return 2;
        }

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                new CsSymbolAmbiguousJson
                {
                    Query = query,
                    Candidates = [.. resolution.Candidates.Select(c => new CsSymbolCandidateJson { DocId = c.DocId, Kind = c.Kind, Name = c.Name, Source = c.SourcePath })],
                },
                CliJsonContext.Default.CsSymbolAmbiguousJson));
        }
        else
        {
            WriteError($"ambiguous C# symbol '{query}' ({resolution.Candidates.Count} matches):");
            foreach (var c in resolution.Candidates)
            {
                Console.Error.WriteLine($"  {c.DocId}  ({c.SourcePath})");
            }
        }

        return 2;
    }

    private static void PrintDirectReferencers(IReadOnlyList<EdgeResult> results)
    {
        var guidCount = results.Count(r => r.Kind == "guid");
        var pathCount = results.Count(r => r.Kind == "path");
        var csCount = results.Count(r => r.Kind == "cs");
        var eventCount = results.Count(r => r.Kind == "event");
        var dllCount = results.Count(r => r.Kind == "dll");
        // The two newer kinds are appended only when present, so the common answer's summary keeps
        // the shape it has always had rather than growing two permanent "0 x" terms.
        var extraKinds = string.Concat(
            eventCount > 0 ? $", {eventCount} event" : "",
            dllCount > 0 ? $", {dllCount} dll" : "");
        var kindSummary = $"({guidCount} guid, {pathCount} path, {csCount} cs{extraKinds})";
        Console.WriteLine($"{results.Count} direct referencer{(results.Count == 1 ? "" : "s")} {kindSummary}:");
        foreach (var r in results)
        {
            Console.WriteLine(r.Kind switch
            {
                "cs" => $"  {FormatCsEdge(r)}{FormatBuildReachability(r)}",
                "event" => $"  {FormatEventEdge(r)}{FormatBuildReachability(r)}",
                "dll" => $"  {FormatDllEdge(r)}{FormatBuildReachability(r)}",
                _ => $"  {r.SourcePath}:{r.Line}{FormatAnnotation(r)}{FormatBuildReachability(r)}",
            });
        }
    }

    /// <summary>
    /// Text rendering for a `kind='dll'` edge — an `.asmdef` naming a precompiled plugin assembly
    /// in `precompiledReferences`, e.g. "Assets/Scripts/Game.asmdef:11   precompiledReferences →
    /// VendorPlugin.dll  [proven]". The raw serialized name is shown rather than the resolved
    /// path because the name IS the reference; the resolved path is what the row's target already
    /// says.
    /// </summary>
    private static string FormatDllEdge(EdgeResult r)
    {
        var label = r.ConfidenceLabel ?? r.Confidence;
        var labelPart = label is null ? "  [unresolved]" : $"  [{label}]";
        return $"{r.SourcePath}:{r.Line}   precompiledReferences → {r.TargetKey}{labelPart}";
    }

    /// <summary>The build-reachability tag suffix. The negative wording is deliberately "not
    /// proven", never "unreachable" — see <see cref="UnBramble.Core.UnBrambleEngine.ComputeBuildReachablePaths"/>:
    /// missing cs edges (syntactic assemblies) and blind spots can only under-report
    /// reachability, so its absence is absence of proof, not proof of absence.</summary>
    private static string FormatBuildReachability(EdgeResult r) => r.BuildReachable switch
    {
        true => "  [build-reachable]",
        false => "  [not proven build-reachable]",
        null => "",
    };

    /// <summary>
    /// Text rendering for a `kind='event'` edge, e.g. "Assets/UI/MainMenu.prefab:88 event
    /// m_OnClick.m_PersistentCalls.m_Calls[0].m_Target → Foo.Jump [proven] (GameObject
    /// "PlayButton")". The property path (when captured) is what names the owning UnityEvent
    /// FIELD — the leading segment ("m_OnClick") is the part a human wants, kept as the full
    /// stored path rather than a trimmed guess; the GameObject-suffix shape is copied verbatim
    /// from <see cref="FormatAnnotation"/>, applied at the string-shape level since
    /// FormatAnnotation itself is guid/path-edge-shaped (MethodName + GameObject together) and
    /// an event edge's method name is already the primary "→ target" of this line.
    /// </summary>
    private static string FormatEventEdge(EdgeResult r)
    {
        var label = r.ConfidenceLabel ?? r.Confidence;
        var implicitTag = r.Implicit ? " [implicit]" : "";
        var gameObjectSuffix = r.GameObject is not null ? $"  (GameObject \"{r.GameObject}\")" : "";
        var pathPart = r.PropertyPath is not null ? $"{r.PropertyPath} " : "";
        return $"{r.SourcePath}:{r.Line}   event {pathPart}→ {r.TargetSymbol}  [{label}]{implicitTag}{gameObjectSuffix}";
    }

    /// <summary>Text output for a symbol-argument who-uses answer: depth 0 is the symbol-level
    /// referencer section; depth 1+ is the declaring file's asset/cs context, grouped the same
    /// way a normal transitive who-uses answer is.</summary>
    private static void PrintSymbolQueryResults(QueryAnswer answer)
    {
        var symbolEdges = answer.Results.Where(r => r.Depth == 0 && r.ConfidenceLabel != EdgeConfidence.Speculative).ToList();
        var speculativeEdges = answer.Results.Where(r => r.Depth == 0 && r.ConfidenceLabel == EdgeConfidence.Speculative).ToList();
        var fileContext = answer.Results.Where(r => r.Depth >= 1).ToList();

        Console.WriteLine($"{symbolEdges.Count} symbol-level referencer{(symbolEdges.Count == 1 ? "" : "s")}:");
        foreach (var r in symbolEdges)
        {
            Console.WriteLine(r.Kind == "event" ? $"  {FormatEventEdge(r)}" : $"  {FormatCsEdge(r)}");
        }

        if (speculativeEdges.Count > 0)
        {
            Console.WriteLine("speculative (name-match in syntactic assemblies):");
            foreach (var r in speculativeEdges)
            {
                Console.WriteLine($"  {FormatCsEdge(r)}");
            }
        }

        if (fileContext.Count == 0)
        {
            return;
        }

        Console.WriteLine("file-level context (declaring file's own referencers):");
        foreach (var group in fileContext.GroupBy(r => r.Depth).OrderBy(g => g.Key))
        {
            Console.WriteLine($"  depth {group.Key}:");
            foreach (var r in group)
            {
                Console.WriteLine(r.Kind == "cs" ? $"    {FormatCsEdge(r)}" : $"    {r.SourcePath}:{r.Line}{FormatAnnotation(r)}  [{r.ConfidenceLabel}]");
            }
        }
    }

    /// <summary>Remediation hint for a plain project script (Assets/-sourced or predefined): opening
    /// the project once is sufficient because Unity always generates that assembly's csproj.</summary>
    private const string SyntacticRemediationHint = "open the project in the Unity Editor once (or open a .cs file in your IDE while Unity is running) to regenerate .csproj files, then re-index";

    /// <summary>
    /// Remediation hint for a package-sourced assembly (asmdef under Packages/ or LocalPackages/,
    /// see <see cref="SyntacticAssemblyDetail.IsPackageSourced"/>): "open Unity once" alone does
    /// NOT regenerate these — Unity's Preferences > External Tools > "Generate .csproj files for:"
    /// has separate, unchecked-by-default checkboxes per package category (Local/Embedded/
    /// Registry/Git), so a package asmdef can stay syntactic forever no matter how many times the
    /// project or a .cs file is reopened unless the matching box is checked.
    /// </summary>
    private const string PackageSyntacticRemediationHint = "open the project in the Unity Editor once, AND in Preferences > External Tools check \"Generate .csproj files for:\" includes this package's category (Local/Embedded/Registry/Git packages -- unchecked by default), then Assets > Open C# Project to regenerate, then re-index";

    /// <summary>Picks the package-aware hint when any assembly in the (possibly capped) sample is
    /// package-sourced, otherwise the plain one -- see the two hints' own doc comments. Callers
    /// pass only the subset NOT already explained by <see cref="BuildSyntacticDiagnosisNotes"/>'s
    /// stronger orphaned/broken findings, so this never repeats "just reopen Unity" advice for an
    /// assembly Unity was never going to compile in the first place.</summary>
    private static string RemediationHintFor(IReadOnlyList<SyntacticAssemblyDetail> sample) =>
        sample.Any(d => d.IsPackageSourced) ? PackageSyntacticRemediationHint : SyntacticRemediationHint;

    /// <summary>
    /// One line per distinct diagnosis found across the sample (usually zero or one -- multiple
    /// only when a mixed sample spans several failure modes at once):
    ///
    /// - <b>looks orphaned</b> (<see cref="SyntacticAssemblyDetail.NeverCompiledByUnity"/> true AND
    ///   <see cref="SyntacticAssemblyDetail.ExternalReferencerCount"/> zero): Unity has never
    ///   compiled it (no <c>Library/ScriptAssemblies/&lt;name&gt;.dll</c> -- checking every csproj
    ///   preference checkbox and reopening the project changes nothing, since there's nothing for
    ///   Unity to generate a project file FOR) and nothing else in the project references its
    ///   files either. Found live: a package folder dropped under <c>LocalPackages/</c> but never
    ///   added to <c>Packages/manifest.json</c> -- invisible to Unity's Package Manager, and (in
    ///   that specific case) also unreferenced by any prefab/scene/other assembly, i.e. genuinely
    ///   dead weight, not just missing IDE metadata.
    /// - <b>looks broken</b> (never compiled, but referenced): the opposite conclusion from the
    ///   same never-compiled signal -- something else in the project (a prefab's missing-script
    ///   component, typically) still points at it, so this is a live but currently-broken
    ///   dependency, not dead code. Wiring it into the package manager fixes the break; the
    ///   "orphaned" remediation (delete it) would be actively wrong here.
    /// - <b>needs .csproj</b> (neither of the above): the ordinary case -- Unity compiles it fine,
    ///   there's just no usable project file to read defines/references out of. Falls through to
    ///   <see cref="RemediationHintFor"/>'s package/generic regeneration hint.
    ///
    /// Each finding names every assembly it applies to, not the whole sample -- a mixed sample
    /// (rare, but possible once several unrelated syntactic assemblies exist at once) must not
    /// blur "this one looks safe to delete" and "this one is just missing a csproj" together.
    /// </summary>
    private static IReadOnlyList<string> BuildSyntacticDiagnosisNotes(IReadOnlyList<SyntacticAssemblyDetail> sample, bool ansi)
    {
        var notes = new List<string>();

        var orphaned = sample.Where(d => d.NeverCompiledByUnity == true && d.ExternalReferencerCount == 0).Select(d => d.Name).ToList();
        if (orphaned.Count > 0)
        {
            notes.Add(
                AnsiStyle.Finding("looks orphaned: ", ansi) +
                $"{string.Join(", ", orphaned)} — never compiled by Unity (no Library/ScriptAssemblies/*.dll; check it's registered in Packages/manifest.json, not just present on disk) and 0 references found anywhere in the project (guid/path/cs). " +
                AnsiStyle.Muted("Verify manually (reflection, string-path loading, and disabled #if regions are blind spots) before removing.", ansi));
        }

        var broken = sample.Where(d => d.NeverCompiledByUnity == true && d.ExternalReferencerCount > 0).ToList();
        if (broken.Count > 0)
        {
            var who = string.Join(", ", broken.Select(d => $"{d.Name} ({d.ExternalReferencerCount} referencer{(d.ExternalReferencerCount == 1 ? "" : "s")})"));
            notes.Add(
                AnsiStyle.Caution("looks broken: ", ansi) +
                $"{who} — never compiled by Unity, but still referenced elsewhere in the project (e.g. a prefab with a missing script). " +
                "Register it in Packages/manifest.json to fix, or clean up the references if it should be removed.");
        }

        var rest = sample.Where(d => d.NeverCompiledByUnity != true).ToList();
        if (rest.Count > 0)
        {
            // Labelled and subject-named like the two findings above, rather than the bare
            // imperative sentence this used to be: unlabelled, it read as advice about the whole
            // answer, when it's actually about these specific assemblies -- and it sat directly
            // under two notes that DID name their subjects, so the natural reading was that it
            // applied to those. "needs .csproj" covers every reason in this bucket (missing,
            // unusable, and parse-failed all want the same regenerate-it fix).
            notes.Add(
                AnsiStyle.Label("needs .csproj: ", ansi) +
                $"{string.Join(", ", rest.Select(d => d.Name))} — " +
                RemediationHintFor(rest));
        }

        return notes;
    }

    /// <summary>The unconditional blind-spots footer: an answer that could be wrong for a
    /// reason the tool knows about says so on every answer, not just in docs — printed for
    /// every non-JSON who-uses/uses answer. The one-line caveat (and the possible-false-negative
    /// warning, when it applies) is unconditional; the multi-paragraph per-assembly DIAGNOSES are
    /// not — on an asset-graph question with proven results they
    /// repeated in full on every query, drowning the answer they qualify. They print only when
    /// they can actually bear on this answer (a possible false negative — the case they exist to
    /// explain) or on --verbose; otherwise the syntactic-assemblies line carries a pointer to
    /// them instead.</summary>
    private static void PrintBlindSpotsFooter(QueryAnswer answer, bool verbose)
    {
        if (answer.BlindSpots.Count == 0)
        {
            return;
        }

        var ansi = ConsoleCapabilities.SupportsAnsi;
        var showDiagnoses = verbose || answer.PossibleFalseNegative;

        // Blank line first: everything below qualifies the answer rather than being part of it,
        // and without the break the caveats read as more results (the answer itself is often two
        // lines against a dozen lines of footer).
        WriteSpacer();

        var confidencePart = answer.Confidence is not null ? $"answer confidence: {answer.Confidence}; " : "";
        WriteParagraph(AnsiStyle.Muted($"({confidencePart}blind spots: {string.Join(", ", answer.BlindSpots)})", ansi));

        if (answer.SyntacticAssemblies is { } summary)
        {
            var detailPointer = showDiagnoses
                ? ""
                : AnsiStyle.Muted(" — diagnosis + remediation: --verbose or `", ansi) +
                  AnsiStyle.Command("unbramble stats", ansi) + AnsiStyle.Muted("`", ansi);
            WriteParagraph(
                AnsiStyle.Muted($"(syntactic assemblies: {FormatSyntacticAssemblyList(summary.Total, summary.Sample)}", ansi) +
                detailPointer + AnsiStyle.Muted(")", ansi));
        }

        // Ahead of the per-assembly diagnoses below, not after them: this is the one line that
        // says the ANSWER may be wrong, so it belongs with the confidence/blind-spots material
        // that also qualifies the answer. Trailing them, it landed several paragraphs downstream
        // of the "0 direct referencers" it exists to contradict, behind findings about assemblies
        // the query never mentioned -- which is precisely the reading ("nothing references this")
        // that it's supposed to prevent.
        if (answer.PossibleFalseNegative)
        {
            var count = answer.SyntacticAssemblies?.Total ?? 0;
            WriteSpacer();
            WriteParagraph(
                AnsiStyle.Caution("warning: ", ansi) +
                $"no proven caller was found — this may be a false negative, not \"nothing references this\": " +
                $"{count} assembl{(count == 1 ? "y was" : "ies were")} indexed syntactic-only (text-derived references that " +
                "cannot be joined to this target); check any speculative matches above, or regenerate .csproj files and re-index.");
        }

        if (showDiagnoses && answer.SyntacticAssemblies is { } diagnosed)
        {
            foreach (var note in BuildSyntacticDiagnosisNotes(diagnosed.Sample, ansi))
            {
                // Each diagnosis is its own multi-line paragraph about its own assembly; run
                // together they turn back into the wall of text this spacing exists to break up.
                WriteSpacer();
                WriteParagraph(note);
            }
        }
    }

    /// <summary>"Game (no generated .csproj), Core (.csproj parse failed), and 3 more" — shared
    /// by the query footer and `stats`' text listing.</summary>
    private static string FormatSyntacticAssemblyList(int total, IReadOnlyList<SyntacticAssemblyDetail> sample)
    {
        var named = string.Join(", ", sample.Select(d => $"{d.Name} ({CsModeReasons.Describe(d.Reason)})"));
        var remaining = total - sample.Count;
        return remaining > 0 ? $"{named}, and {remaining} more" : named;
    }

    private static string FormatCsEdge(EdgeResult r)
    {
        var sourceSymbol = r.SourceSymbol is not null ? $"{r.SourceSymbol} " : "";
        var label = r.ConfidenceLabel ?? r.Confidence;
        return $"{r.SourcePath}:{r.Line}   cs {r.RefKind} {sourceSymbol}→ {r.TargetSymbol}  [{label}]";
    }

    /// <summary>Dependencies under this prefix are registry-package internals — accurate but
    /// overwhelming on high-fan-out targets (a settings asset returned
    /// 398 dependencies, ~all package resources drowning the dozen project-side ones the
    /// question was about), so the text rendering collapses them past a threshold. Never a
    /// silent cap: the collapse line states the count and both expansion routes.</summary>
    private const string PackageCachePrefix = "Library/PackageCache/";

    private const int PackageCacheCollapseThreshold = 6;

    private static bool IsPackageCacheDependency(EdgeResult r) =>
        r.TargetPath?.StartsWith(PackageCachePrefix, StringComparison.OrdinalIgnoreCase) ?? false;

    private static void PrintDirectDependencies(IReadOnlyList<EdgeResult> results, bool verbose)
    {
        var guidCount = results.Count(r => r.Kind == "guid");
        var pathCount = results.Count(r => r.Kind == "path");
        var csCount = results.Count(r => r.Kind == "cs");
        var dllCount = results.Count(r => r.Kind == "dll");
        var kindSummary = string.Concat(
            $"({guidCount} guid, {pathCount} path",
            csCount > 0 ? $", {csCount} cs" : "",
            dllCount > 0 ? $", {dllCount} dll" : "",
            ")");
        Console.WriteLine($"{results.Count} direct dependenc{(results.Count == 1 ? "y" : "ies")} {kindSummary}:");
        PrintDependencyLines(results, verbose, indent: "  ");
    }

    /// <summary>
    /// Shared dependency-line rendering for the direct and transitive `uses` shapes: project-side
    /// entries print in full; Library/PackageCache entries (registry-package internals) collapse
    /// to one counted line once they outnumber <see cref="PackageCacheCollapseThreshold"/>,
    /// unless --verbose. JSON output never collapses — this is a text-rendering concession to
    /// high-fan-out targets, not a change to what the answer contains.
    /// </summary>
    private static void PrintDependencyLines(IReadOnlyList<EdgeResult> results, bool verbose, string indent)
    {
        var packageCacheCount = results.Count(IsPackageCacheDependency);
        var collapse = !verbose && packageCacheCount > PackageCacheCollapseThreshold;

        foreach (var r in results)
        {
            if (collapse && IsPackageCacheDependency(r))
            {
                continue;
            }

            Console.WriteLine(r.Kind == "cs" ? $"{indent}{FormatCsEdge(r)}" : indent + FormatDependency(r));
        }

        if (collapse)
        {
            Console.WriteLine($"{indent}({packageCacheCount} under Library/PackageCache — registry-package internals; list them with --verbose, or scope with --under Library/PackageCache)");
        }
    }

    private static void PrintTransitiveWhoUses(QueryAnswer answer)
    {
        foreach (var group in answer.Results.GroupBy(r => r.Depth).OrderBy(g => g.Key))
        {
            // At depth 1 "via" is trivially the query target itself — omit as redundant.
            var entries = group.Select(r =>
            {
                var label = r.ConfidenceLabel is not null ? $" [{r.ConfidenceLabel}]" : "";
                return (group.Key == 1 || r.Via is null ? r.SourcePath : $"{r.SourcePath} (via {Path.GetFileName(r.Via)})") + label;
            });
            Console.WriteLine($"depth {group.Key}:  " + string.Join(", ", entries));
        }

        if (answer.Truncated)
        {
            Console.WriteLine("(truncated at the depth cap — there may be more beyond this depth)");
        }
    }

    private static void PrintTransitiveUses(QueryAnswer answer, bool verbose)
    {
        foreach (var group in answer.Results.GroupBy(r => r.Depth).OrderBy(g => g.Key))
        {
            Console.WriteLine($"depth {group.Key}:");
            PrintDependencyLines(group.ToList(), verbose, indent: "  ");
        }

        if (answer.Truncated)
        {
            Console.WriteLine("(truncated at the depth cap — there may be more beyond this depth)");
        }
    }

    private static string FormatDependency(EdgeResult r)
    {
        if (!r.Resolved)
        {
            return r.Builtin
                ? $"(Unity builtin) ({r.SourcePath}:{r.Line})  [{r.ConfidenceLabel}]"
                : $"UNRESOLVED {r.Kind}={r.TargetKey} ({r.SourcePath}:{r.Line})";
        }

        return $"{r.TargetPath}{FormatAnnotation(r)}  ({r.SourcePath}:{r.Line})  [{r.ConfidenceLabel}]";
    }

    private static string FormatAnnotation(EdgeResult r)
    {
        var parts = new List<string>();
        if (r.ClassId is { } classId && ClassNames.TryGetValue(classId, out var className))
        {
            parts.Add(className);
        }

        // The owning serialized field ("m_Settings.m_VolumeProfile") right after the class name
        // — the class+line alone still forces a Unity round-trip to
        // learn WHICH field held the reference.
        if (r.PropertyPath is not null)
        {
            parts.Add(r.PropertyPath);
        }

        if (r.MethodName is not null)
        {
            parts.Add($"→ {r.MethodName}");
        }

        var annotation = parts.Count > 0 ? " " + string.Join(" ", parts) : "";
        var gameObjectSuffix = r.GameObject is not null ? $"  (GameObject \"{r.GameObject}\")" : "";
        return annotation + gameObjectSuffix;
    }

    private static string FormatTargetHeader(QueryTarget target) =>
        target.Path is not null
            ? $"{target.Path}  guid={target.Guid ?? "(none)"}"
            : $"(external, unresolved) guid={target.Guid}";

    private static string FormatUnresolvedLine(UnresolvedRefEntry u)
    {
        var owner = new List<string>();
        var component = DescribeUnresolvedComponent(u);
        if (component is not null)
        {
            owner.Add($"component={component}");
        }

        if (u.GameObject is not null)
        {
            owner.Add($"GameObject=\"{u.GameObject}\"");
        }

        if (u.PropertyPath is not null)
        {
            owner.Add($"field={u.PropertyPath}");
        }

        if (u.IsScriptReference)
        {
            owner.Add("m_Script");
        }

        if (u.IsPrefabOverride)
        {
            owner.Add(u.PrefabSource is null ? "prefab override" : $"prefab override of {u.PrefabSource}");
        }

        var ownerSuffix = owner.Count == 0 ? "" : "  " + string.Join("  ", owner);
        var buildSuffix = u.BuildReachable switch
        {
            true => "  [build-reachable]",
            false => "  [not proven build-reachable]",
            _ => "",
        };
        return $"UNRESOLVED {u.Kind}={u.TargetKey} ({u.SourcePath}:{u.Line}){ownerSuffix}{buildSuffix}";
    }

    private static string? DescribeUnresolvedComponent(UnresolvedRefEntry u) =>
        u.Component ?? (u.ClassId is { } classId && ClassNames.TryGetValue(classId, out var name) ? name : null);

    private sealed record UnresolvedGroup(
        string Kind,
        string TargetKey,
        IReadOnlyList<UnresolvedRefEntry> Items,
        IReadOnlyList<string> Sources,
        IReadOnlyList<string> Fields,
        IReadOnlyList<string> Components,
        IReadOnlyList<string> GameObjects,
        IReadOnlyList<string> PrefabSources);

    private static IReadOnlyList<UnresolvedGroup> GroupUnresolved(IReadOnlyList<UnresolvedRefEntry> items, int? top)
    {
        IEnumerable<UnresolvedGroup> groups = items
            .GroupBy(u => $"{u.Kind}\0{u.TargetKey}", StringComparer.OrdinalIgnoreCase)
            .Select(g => new UnresolvedGroup(
                g.First().Kind,
                g.First().TargetKey,
                [.. g],
                [.. g.Select(u => u.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)],
                [.. g.Select(u => u.PropertyPath).Where(x => x is not null).Select(x => x!).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal)],
                [.. g.Select(DescribeUnresolvedComponent).Where(x => x is not null).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)],
                [.. g.Select(u => u.GameObject).Where(x => x is not null).Select(x => x!).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal)],
                [.. g.Select(u => u.PrefabSource).Where(x => x is not null).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)]))
            .OrderByDescending(g => g.Items.Count)
            .ThenBy(g => g.TargetKey, StringComparer.OrdinalIgnoreCase);

        if (top is { } limit)
        {
            groups = groups.Take(limit);
        }

        return [.. groups];
    }

    private static void PrintUnresolvedGroups(IReadOnlyList<UnresolvedRefEntry> items, int? top)
    {
        foreach (var group in GroupUnresolved(items, top))
        {
            var details = new List<string>();
            if (group.Fields.Count > 0)
            {
                details.Add("fields: " + string.Join(", ", group.Fields));
            }

            if (group.Components.Count > 0)
            {
                details.Add("components: " + string.Join(", ", group.Components));
            }

            if (group.PrefabSources.Count > 0)
            {
                details.Add("prefab sources: " + string.Join(", ", group.PrefabSources));
            }

            var buildReachableSources = group.Items.Where(u => u.BuildReachable == true).Select(u => u.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            details.Add($"sources: {group.Sources.Count}");
            details.Add($"build-reachable sources: {buildReachableSources}");
            Console.WriteLine($"{group.TargetKey}  {group.Items.Count} reference{(group.Items.Count == 1 ? "" : "s")}  {string.Join("  ", details)}");
        }
    }

    private static IReadOnlyList<UnresolvedRefEntry> AnnotateUnresolvedBuildReachability(
        IReadOnlyList<UnresolvedRefEntry> items,
        UnBrambleEngine engine)
    {
        if (items.Count == 0)
        {
            return items;
        }

        return AnnotateUnresolvedBuildReachability(items, engine.ComputeBuildReachablePaths(items.Select(item => item.SourcePath)));
    }

    private static IReadOnlyList<UnresolvedRefEntry> AnnotateUnresolvedBuildReachability(
        IReadOnlyList<UnresolvedRefEntry> items,
        HashSet<string> reachable) =>
        [.. items.Select(u => u with { BuildReachable = reachable.Contains(u.SourcePath) })];

    private static string FormatEdgeStatsLine(EdgeStats e) =>
        $"{FormatCount(e.GuidTotal)} guid ({FormatCount(e.GuidUnresolved)} unresolved, {FormatCount(e.GuidBuiltin)} builtin), " +
        $"{FormatCount(e.PathTotal)} path ({FormatCount(e.PathUnresolved)} unresolved)";

    private static string FormatCsStatsLine(CsStats cs)
    {
        var modeSummary = cs.TotalAssemblies == 0
            ? "no assemblies analyzed"
            : cs.SyntacticAssemblies == 0
                ? "semantic"
                : cs.SyntacticAssemblies == cs.TotalAssemblies
                    ? "syntactic"
                    : $"semantic; {FormatCount(cs.SyntacticAssemblies)} assemblies syntactic";

        return $"{FormatCount(cs.Types)} types, {FormatCount(cs.Members)} members, {FormatCount(cs.Refs)} refs, {FormatCount(cs.NameHints)} name hints (mode: {modeSummary})";
    }

    private static void PrintIndexSummaryLine(IndexSummary summary) =>
        Console.WriteLine(
            $"Index refreshed in {FormatSeconds(summary.Elapsed)}: +{summary.Added} files, ~{summary.Changed} changed, " +
            $"-{summary.Removed} removed; {summary.Stats.Edges.TotalUnresolved} unresolved refs.");

    /// <summary>
    /// The strings here are a mix: some already carry their own "warning: "/"error: " prefix
    /// (guid collisions, the false-negative callout), others are plain informational sweep notes
    /// (the syntactic-mode notice) -- colorizing by sniffing whichever prefix is already there
    /// keeps every caller's existing wording untouched instead of forcing a second prefix on top.
    /// </summary>
    private static void PrintWarnings(IReadOnlyList<string> warnings, TextWriter? writer = null)
    {
        var output = writer ?? Console.Error;
        var ansi = ConsoleCapabilities.SupportsAnsi;
        foreach (var warning in warnings)
        {
            if (warning.StartsWith("warning: ", StringComparison.Ordinal))
            {
                WriteParagraph(warning["warning: ".Length..], AnsiStyle.Caution("warning: ", ansi), "         ", output);
            }
            else if (warning.StartsWith("error: ", StringComparison.Ordinal))
            {
                WriteParagraph(warning["error: ".Length..], AnsiStyle.Alarm("error: ", ansi), "       ", output);
            }
            else
            {
                // Notice, not Label: this branch colors a whole informational sentence (the
                // syntactic-mode sweep notice), and Label's bold would shout it.
                WriteParagraph(AnsiStyle.Notice(warning, ansi), contPrefix: ParagraphHangingIndent, writer: output);
            }
        }
    }

    /// <summary>Passive Defender drift check (see <see cref="DefenderDriftDetector"/>): runs after
    /// every completed `index`/`init` pass, reading only files already on disk (the just-appended
    /// <c>index-history.log</c> line plus the recorded exclusion state) -- never elevates, never
    /// prompts, just a one-line stderr nudge toward `unbramble defender setup` when a new junction
    /// target looks like it's still being cold-scanned by Defender.</summary>
    private static void MaybeWarnDefenderDrift(string projectRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var history = IndexHistoryLog.TryReadAll(projectRoot);
        if (history.Count == 0)
        {
            return;
        }

        var state = DefenderStateFile.TryRead(projectRoot);
        var warning = DefenderDriftDetector.CheckForWarning(history[^1], state);
        if (warning is not null)
        {
            WriteWarning(warning);
        }
    }

    /// <summary>`unbramble defender &lt;status|setup|remove&gt;` -- see
    /// <see cref="DefenderExclusionSetup"/>'s own doc comment for what each subcommand does.
    /// Deliberately does NOT open a full <see cref="UnBrambleEngine"/> (avoids requiring the
    /// project to already be inited): project detection + unbramble.json loading only, the same
    /// lightweight path <c>HomeCommand.IsInited</c> uses.</summary>
    private static int RunDefender(string[] rest)
    {
        if (rest.Length == 0)
        {
            WriteError("'defender' requires a subcommand: status, setup, remove");
            return 64;
        }

        var sub = rest[0];
        var subRest = rest[1..];
        return sub switch
        {
            "status" => RunDefenderStatus(subRest),
            "setup" => RunDefenderSetup(subRest),
            "remove" => RunDefenderRemove(subRest),
            _ => UnknownDefenderSubcommand(sub),
        };
    }

    private static int UnknownDefenderSubcommand(string sub)
    {
        WriteError($"unknown 'defender' subcommand '{sub}' (expected: status, setup, remove)");
        return 64;
    }

    private static (string ProjectRoot, UnBrambleConfig Config)? ResolveDefenderProject(string startPath)
    {
        var projectRoot = ProjectDetector.FindProjectRoot(startPath);
        if (projectRoot is null)
        {
            WriteError($"no Unity project found starting from '{startPath}' (no ProjectSettings/ProjectVersion.txt)");
            return null;
        }

        var config = UnBrambleConfigLoader.Load(projectRoot, out var warnings);
        PrintWarnings(warnings);
        return (projectRoot, config);
    }

    private static int RunDefenderStatus(string[] rest)
    {
        var reader = ArgReader.Parse(rest);
        var resolved = ResolveDefenderProject(reader.ResolveStartPath());
        if (resolved is null)
        {
            return 1;
        }

        var (projectRoot, config) = resolved.Value;
        DefenderExclusionSetup.RunStatus(projectRoot, config, DefenderExclusionSetup.Dependencies.CreateReal(), Console.WriteLine);
        return 0;
    }

    private static int RunDefenderSetup(string[] rest)
    {
        var reader = ArgReader.Parse(rest);
        var resolved = ResolveDefenderProject(reader.ResolveStartPath());
        if (resolved is null)
        {
            return 1;
        }

        var (projectRoot, config) = resolved.Value;
        DefenderExclusionSetup.RunSetup(
            projectRoot, config, DefenderExclusionSetup.Dependencies.CreateReal(), ConsoleCapabilities.IsInteractive,
            Console.WriteLine, Console.ReadLine);
        return 0;
    }

    private static int RunDefenderRemove(string[] rest)
    {
        var reader = ArgReader.Parse(rest);
        var resolved = ResolveDefenderProject(reader.ResolveStartPath());
        if (resolved is null)
        {
            return 1;
        }

        var (projectRoot, _) = resolved.Value;
        DefenderExclusionSetup.RunRemove(projectRoot, DefenderExclusionSetup.Dependencies.CreateReal(), Console.WriteLine);
        return 0;
    }

    /// <summary>
    /// The pull path: resolve/stats/who-uses/uses all call this right after opening the engine.
    /// Prints whichever warnings apply (the sweep's, if one ran; else the plain open warnings)
    /// and, with --verbose, one line distinguishing "watcher heartbeat" freshness from a sweep
    /// that just ran (verify-all's watch-smoke step asserts on this exact wording).
    ///
    /// Stream routing: in TEXT mode everything here goes to
    /// STDOUT, ahead of the results — an agent harness merges the stdout/stderr pipes with no
    /// cross-stream ordering guarantee, so freshness/progress written to stderr before the
    /// results could display after them and read as trailing garbage. One stream = intrinsic
    /// order. `--json` keeps the stderr routing: stdout's JSON contract stays pure.
    /// </summary>
    /// <param name="waitForConcurrentSweep">Forwarded to <see
    /// cref="UnBrambleEngine.EnsureFresh"/> -- see that param's own doc comment. False only for
    /// `stats` (a status command that must report immediately, not stall behind someone else's
    /// first index).</param>
    private static void PrintFreshness(UnBrambleEngine engine, bool verbose, bool json, bool waitForConcurrentSweep = true, bool assetOnly = false)
    {
        var diag = json ? Console.Error : Console.Out;
        using var progress = new SweepProgressPrinter(
            "freshness: no live watcher heartbeat -- sweeping the index inline before answering (a cold sweep of a large project can take minutes; progress follows)",
            diag);
        var outcome = engine.EnsureFresh(progress.OnScanProgress, progress.OnPhase, waitForConcurrentSweep);
        progress.Dispose();

        if (outcome.ConcurrentSweepInProgress)
        {
            // Always printed, not verbose-gated: this materially changes how to read whatever
            // numbers follow (a possibly-partial snapshot, not the final one), so it can't be
            // hidden behind --verbose the way the routine sweep-vs-heartbeat detail below is.
            WriteParagraph(
                AnsiStyle.InlineCommands(
                    "another unbramble process is currently updating this project's index -- the state below is the last committed snapshot, not the final one. Re-run once it finishes; use 'unbramble monitor' to inspect a watcher, or 'unbramble stop' if the owner is stuck.",
                    ConsoleCapabilities.SupportsAnsi),
                firstPrefix: AnsiStyle.Label("note: ", ConsoleCapabilities.SupportsAnsi),
                contPrefix: "      ",
                writer: diag);
        }

        var warnings = outcome.SweepPerformed ? outcome.Summary!.Warnings : engine.OpenWarnings;
        if (assetOnly && !verbose)
        {
            warnings = [.. warnings.Where(w => !w.StartsWith("C# analysis:", StringComparison.Ordinal))];
        }

        PrintWarnings(warnings, diag);
        MaybeAutoSpawnWatch(engine, outcome);

        if (!verbose)
        {
            return;
        }

        if (outcome.SweepPerformed)
        {
            var filesSwept = outcome.Summary!.Stats.Files.Total + outcome.Summary.Stats.IdentityOnlyCount;
            diag.WriteLine($"freshness: swept {FormatCount(filesSwept)} files");
        }
        else if (outcome.ConcurrentSweepInProgress)
        {
            diag.WriteLine("freshness: concurrent sweep detected elsewhere, not waited on");
        }
        else
        {
            var ageSeconds = outcome.HeartbeatAge!.Value.TotalSeconds;
            diag.WriteLine($"freshness: watcher heartbeat (age {ageSeconds.ToString("0", CultureInfo.InvariantCulture)}s)");
        }
    }

    /// <summary>
    /// Background watcher startup (docs/architecture.md): when <see
    /// cref="UnBrambleEngine.EnsureFresh"/> just had to run the inline sweep (no fresh heartbeat),
    /// fire off a detached watcher worker so the NEXT query can skip the sweep instead.
    /// Fire-and-forget in every sense that matters: never awaited, never blocks the CLI's exit,
    /// and every failure path here (disabled by config, crash-loop cooldown, spawn throws) is
    /// swallowed silently -- THIS query already got a correct answer from EnsureFresh's own
    /// sweep, so nothing here is allowed to change its answer or exit code. See
    /// <see cref="AutoSpawnPolicy"/> for the actual (unit-tested) decision logic; this method is
    /// just the I/O shell around it.
    /// </summary>
    private static void MaybeAutoSpawnWatch(UnBrambleEngine engine, FreshnessOutcome outcome)
    {
        var now = DateTime.UtcNow;
        var lastAttempt = AutoWatchMarkers.TryReadLastSpawnAttempt(engine.ProjectRoot);
        if (!AutoSpawnPolicy.ShouldSpawn(outcome.SweepPerformed, engine.Config.Watch.AutoStart, lastAttempt, now, AutoSpawnPolicy.DefaultCooldown))
        {
            return;
        }

        // Recorded regardless of whether the actual process spawn below succeeds, is skipped by
        // the test-only opt-out, or throws -- the crash-loop guard's whole point is bounding the
        // ATTEMPT rate, not the success rate (see AutoSpawnPolicy's own doc comment).
        AutoWatchMarkers.RecordSpawnAttempt(engine.ProjectRoot, now);

        SpawnDetachedWatcher(engine.ProjectRoot);
    }

    /// <summary>
    /// Fires off a detached watcher worker for <paramref name="projectRoot"/> and returns
    /// immediately -- the actual I/O shell behind <see cref="MaybeAutoSpawnWatch"/>'s decision,
    /// and reused as-is by the zero-args home command (<see cref="HomeCommand"/>) for its own
    /// "ensure a watcher is running" step (Cases D/E), so the two callers can never diverge on
    /// how a watcher gets spawned.
    /// </summary>
    internal static void SpawnDetachedWatcher(string projectRoot, bool explicitRequest = false)
    {
        // Test-only escape hatch: UnBramble.Tests sets this once for the whole test process (see
        // TestSupport's module initializer) so that exercising a query verb against a fixture
        // with no live heartbeat -- the common case for nearly every CLI-level test -- never
        // actually spawns a real OS process. Unset (the normal case) in every real invocation.
        if (!explicitRequest && Environment.GetEnvironmentVariable("UNBRAMBLE_DISABLE_AUTO_SPAWN") == "1")
        {
            return;
        }

        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                return;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,

                // Load-bearing (found live against a large real project, verified with a
                // pipe-captured repro): UseShellExecute=false makes .NET call CreateProcess
                // with bInheritHandles=TRUE, so the detached child inherits EVERY inheritable
                // handle in this query process -- most damagingly the caller's stdout/stderr
                // pipe write-ends when a script/agent captures this query's output. The caller
                // then never sees EOF when the query exits (the long-lived watcher still holds
                // a duplicated write handle) and hangs indefinitely on a query that actually
                // finished. Redirecting the child's OWN std streams does not help: the leak is
                // the wholesale handle duplication, not the child's std handle assignment.
                // UseShellExecute=true launches via ShellExecuteEx, which does not inherit this
                // process's handles at all -- the child starts with no tie of any kind to the
                // caller's pipes. Costs: no env-var/redirection control (needed: none) and a
                // shell association lookup (a direct .exe path, always fine).
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            startInfo.ArgumentList.Add("watch-worker");
            startInfo.ArgumentList.Add(projectRoot);

            using var child = Process.Start(startInfo);
        }
        catch (Exception)
        {
            // Best-effort only -- see this method's own doc comment. A failed spawn just means
            // the next query sweeps again too, exactly like this one did.
        }
    }

    private static int RunWatchWorker(string[] rest)
    {
        var reader = ArgReader.Parse(rest, [], []);

        // A worker is always detached and observed through watch.status.json. Null streams are
        // load-bearing: nothing can consume them, and inherited pipe handles must never keep an
        // agent invocation alive after the spawning command exits.
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);

        using var engine = UnBrambleEngine.Open(reader.ResolveStartPath());
        var tracker = new WatchStatusTracker(engine.ProjectRoot, Environment.ProcessId);
        engine.EnableWatchCompilationCache(tracker.OnDiagnosticLine);

        using var stopSignal = new ManualResetEventSlim(false);
        using var host = new WatcherHost(
            engine,
            onEvent: e =>
            {
                if (e == WatcherEvent.HeartbeatIdle)
                {
                    tracker.OnIdleTick();
                }
                else
                {
                    tracker.OnWatcherEvent(e);
                }

                if (e == WatcherEvent.AutoIdleTimeout)
                {
                    stopSignal.Set();
                }
            },
            onDiagnostic: tracker.OnDiagnosticLine,
            autoMode: true);

        try
        {
            // Background workers never queue as passive standbys. If another worker already owns
            // the project, this redundant spawn exits successfully and immediately.
            if (!host.TryStartOnceForAuto())
            {
                return 0;
            }

            stopSignal.Wait();
        }
        finally
        {
            host.Stop();
        }

        return 0;
    }

    /// <summary>
    /// `unbramble monitor [path]`: ensures the project's detached watcher worker exists, then
    /// polls the status file it writes. Ctrl+C stops only this presentation process; the worker
    /// keeps the index fresh until its ordinary idle timeout or `unbramble stop`.
    /// </summary>
    private static int RunMonitor(string[] rest)
    {
        var reader = ArgReader.Parse(rest, [], []);
        var startPath = reader.ResolveStartPath();
        var projectRoot = ProjectDetector.FindProjectRoot(startPath);
        if (projectRoot is null)
        {
            WriteError($"no Unity project found starting from '{startPath}' (no ProjectSettings/ProjectVersion.txt)");
            return 1;
        }

        // Idempotent by construction: every worker attempts WatcherLock once and exits 0 when
        // another already owns it. Starting unconditionally avoids duplicating liveness/race
        // checks here and makes `monitor` the one obvious public command for both "start" and
        // "show me progress".
        EnsureWatcherForMonitor(projectRoot);

        using var stopSignal = new ManualResetEventSlim(false);
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            stopSignal.Set();
        };

        Console.CancelKeyPress += onCancel;
        try
        {
            WatchDashboard.RunAttachedLoop(projectRoot, stopSignal, Console.Out);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }

        return 0;
    }

    /// <summary>
    /// `unbramble stop`: kills any other live `unbramble.exe` process (a watcher worker, most
    /// commonly) so the user never has to reach for Task Manager. Deliberately simple: no
    /// project scoping, no config mutation, no graceful multi-step shutdown -- SQLite's WAL +
    /// `synchronous=NORMAL` (see <see cref="UnBramble.Core.Store.UnBrambleStore"/>) already makes
    /// a hard kill safe, worst case triggering the same self-heal a watcher already does after
    /// any unclean exit. Always exits 0 -- "found and stopped some" and "found none" are both
    /// success outcomes for this command.
    /// </summary>
    private static int RunStop(string[] rest)
    {
        var reader = ArgReader.Parse(rest, [], []);
        var startPath = reader.ResolveStartPath();

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            WriteError("could not determine this process's own executable path.");
            return 1;
        }

        var expectedFileName = Path.GetFileName(exePath);
        var imageName = Path.GetFileNameWithoutExtension(exePath);
        var currentPid = Environment.ProcessId;

        // Best-effort PID -> project attribution: only ever looks at the
        // ONE project reachable from the current directory/positional path, via the exact same
        // resolution `monitor` uses -- never a filesystem-wide search for `.unbramble` folders.
        // A miss here just means the report below falls back to bare PID + image path.
        var pidToProject = new Dictionary<int, string>();
        var projectRoot = ProjectDetector.FindProjectRoot(startPath);
        if (projectRoot is not null)
        {
            if (WatchStatusFile.TryRead(projectRoot) is { } status)
            {
                pidToProject[status.Pid] = status.ProjectRoot;
            }

            if (HeartbeatFile.TryRead(projectRoot) is { } heartbeat)
            {
                pidToProject.TryAdd(heartbeat.Pid, projectRoot);
            }
        }

        var stopped = 0;
        foreach (var process in Process.GetProcessesByName(imageName))
        {
            using (process)
            {
                if (process.Id == currentPid)
                {
                    continue;
                }

                string? mainModulePath;
                try
                {
                    mainModulePath = process.MainModule?.FileName;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Can't inspect this process (different user, protected process, already
                    // exited, etc.) -- skip it rather than guessing it's really us.
                    continue;
                }

                if (mainModulePath is null || !mainModulePath.EndsWith(expectedFileName, StringComparison.OrdinalIgnoreCase))
                {
                    // Coincidentally-named but not actually this build's exe -- leave it alone.
                    continue;
                }

                var pid = process.Id;
                try
                {
                    process.Kill();
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Already exited between enumeration and Kill() -- nothing left to report.
                    continue;
                }

                stopped++;
                var attribution = pidToProject.TryGetValue(pid, out var watchedProject)
                    ? $", was watching {watchedProject}"
                    : "";
                Console.WriteLine($"Stopped {expectedFileName} (PID {pid}){attribution}");
            }
        }

        if (stopped == 0)
        {
            Console.WriteLine("No unbramble processes running.");
        }

        return 0;
    }

    private static void LogWatchEvent(WatcherEvent watcherEvent)
    {
        var message = watcherEvent switch
        {
            WatcherEvent.LockWaiting => "waiting for lock (another watcher process is active)...",
            WatcherEvent.Promoted => "promoted: now the active watcher for this project.",
            WatcherEvent.BatchApplied => "applied a change batch.",
            WatcherEvent.SelfHealSweep => "self-heal: running the periodic full sweep.",
            WatcherEvent.ErrorResync => "watcher buffer overflow — running an immediate full resync.",
            _ => null,
        };

        if (message is not null)
        {
            Console.Error.WriteLine($"watch: {message}");
        }
    }

    private static void WriteResolveJson(string query, IReadOnlyList<ResolveMatch> matches, bool unresolvedGuid)
    {
        var payload = new ResolveResultJson
        {
            Query = query,
            Matches = matches
                .Select(m => new ResolveMatchJson { Path = m.Path, Guid = m.Guid, Kind = m.Kind.ToDbString(), IdentityOnly = m.IdentityOnly })
                .ToList(),
            UnresolvedGuid = unresolvedGuid,
        };
        Console.WriteLine(JsonSerializer.Serialize(payload, CliJsonContext.Default.ResolveResultJson));
    }

    private static void WriteStatsJson(UnBrambleEngine engine, StatsResult stats)
    {
        var syntactic = engine.GetSyntacticAssemblyDetails();
        var payload = new StatsResultJson
        {
            Project = engine.ProjectRoot,
            UnityVersion = engine.UnityVersion,
            Files = ToFileCountsJson(stats.Files),
            IdentityOnly = stats.IdentityOnlyCount,
            GuidLess = stats.GuidLessCount,
            Edges = ToEdgeStatsJson(stats.Edges),
            Db = new DbInfoJson { Path = engine.DbPath, SizeBytes = stats.DbSizeBytes, SchemaVersion = stats.SchemaVersion },
            RefSourceExtensions = [.. stats.RefSourceExtensions],
            Cs = ToCsStatsJson(stats.Cs),
            SyntacticAssemblies = syntactic.Count == 0
                ? null
                : [.. syntactic.Select(d => new SyntacticAssemblyJson { Name = d.Name, Reason = d.Reason })],
        };
        Console.WriteLine(JsonSerializer.Serialize(payload, CliJsonContext.Default.StatsResultJson));
    }

    private static void WriteIndexJson(IndexSummary summary)
    {
        var payload = new IndexResultJson
        {
            Project = summary.ProjectRoot,
            UnityVersion = summary.UnityVersion,
            ElapsedSeconds = summary.Elapsed.TotalSeconds,
            PhaseTimings = new PhaseTimingsJson
            {
                ScanSeconds = summary.PhaseTimings.Scan.TotalSeconds,
                SweepDiffSeconds = summary.PhaseTimings.SweepDiff.TotalSeconds,
                DirtyReparseSeconds = summary.PhaseTimings.DirtyReparse.TotalSeconds,
                CsAnalysisSeconds = summary.PhaseTimings.CsAnalysis.TotalSeconds,
            },
            Added = summary.Added,
            Changed = summary.Changed,
            Removed = summary.Removed,
            Files = ToFileCountsJson(summary.Stats.Files),
            IdentityOnly = summary.Stats.IdentityOnlyCount,
            Edges = ToEdgeStatsJson(summary.Stats.Edges),
            Db = new DbInfoJson { Path = summary.DbPath, SizeBytes = summary.Stats.DbSizeBytes, SchemaVersion = summary.Stats.SchemaVersion },
            Cs = ToCsStatsJson(summary.Stats.Cs),
            Warnings = [.. summary.Warnings],
        };
        Console.WriteLine(JsonSerializer.Serialize(payload, CliJsonContext.Default.IndexResultJson));
    }

    /// <summary>The human-readable phase breakdown, printed only under --verbose so the default
    /// index/init output stays uncluttered — the numbers are always present in --json
    /// regardless.</summary>
    private static void PrintPhaseTimings(IndexPhaseTimings t) =>
        Console.Error.WriteLine(
            $"phases: scan {FormatSeconds(t.Scan)}, sweep-diff {FormatSeconds(t.SweepDiff)}, " +
            $"dirty-reparse {FormatSeconds(t.DirtyReparse)}, cs-analysis {FormatSeconds(t.CsAnalysis)}");

    private static void WriteQueryJson(string query, QueryAnswer answer, string? symbol = null)
    {
        var payload = ToQueryResultJson(query, answer, symbol);
        Console.WriteLine(JsonSerializer.Serialize(payload, CliJsonContext.Default.QueryResultJson));
    }

    private static QueryResultJson ToQueryResultJson(string query, QueryAnswer answer, string? symbol = null) =>
        new()
        {
            Query = query,
            Target = new QueryTargetJson { Path = answer.Target.Path, Guid = answer.Target.Guid },
            Symbol = symbol,
            Results = [.. answer.Results.Select(ToEdgeJson)],
            Truncated = answer.Truncated,
            Confidence = answer.Confidence,
            BlindSpots = [.. answer.BlindSpots],
            SyntacticAssemblies = ToSyntacticAssembliesJson(answer.SyntacticAssemblies),
            PossibleFalseNegative = answer.PossibleFalseNegative,
        };

    private static SyntacticAssembliesJson? ToSyntacticAssembliesJson(SyntacticAssemblySummary? summary) =>
        summary is null
            ? null
            : new SyntacticAssembliesJson
            {
                Total = summary.Total,
                Assemblies = [.. summary.Sample.Select(d => new SyntacticAssemblyJson { Name = d.Name, Reason = d.Reason })],
                Remediation = SyntacticRemediationHint,
            };

    private static void WriteUnresolvedJson(IReadOnlyList<UnresolvedRefEntry> items, bool grouped = false, int? top = null)
    {
        var groups = grouped ? GroupUnresolved(items, top) : [];
        var payload = new UnresolvedResultJson
        {
            Count = items.Count,
            Grouped = grouped,
            Items = grouped ? [] : [.. items.Select(ToUnresolvedJson)],
            Groups = [.. groups.Select(ToUnresolvedGroupJson)],
        };
        Console.WriteLine(JsonSerializer.Serialize(payload, CliJsonContext.Default.UnresolvedResultJson));
    }

    private static UnresolvedGroupJson ToUnresolvedGroupJson(UnresolvedGroup g) => new()
    {
        Kind = g.Kind,
        TargetKey = g.TargetKey,
        Count = g.Items.Count,
        Sources = [.. g.Sources],
        Fields = [.. g.Fields],
        Components = [.. g.Components],
        GameObjects = [.. g.GameObjects],
        PrefabSources = [.. g.PrefabSources],
        ScriptReferences = g.Items.Count(u => u.IsScriptReference),
        PrefabOverrides = g.Items.Count(u => u.IsPrefabOverride),
        BuildReachableSources = g.Items.Where(u => u.BuildReachable == true).Select(u => u.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
    };

    private static UnresolvedRefJson ToUnresolvedJson(UnresolvedRefEntry u) => new()
    {
        Source = u.SourcePath,
        Kind = u.Kind,
        TargetKey = u.TargetKey,
        Line = u.Line,
        ClassId = u.ClassId,
        GameObject = u.GameObject,
        Component = DescribeUnresolvedComponent(u),
        ComponentScriptGuid = u.ComponentScriptGuid,
        PropertyPath = u.PropertyPath,
        IsScriptReference = u.IsScriptReference,
        IsPrefabOverride = u.IsPrefabOverride,
        PrefabSource = u.PrefabSource,
        BuildReachable = u.BuildReachable,
    };

    private static EdgeResultJson ToEdgeJson(EdgeResult e) => new()
    {
        Source = e.SourcePath,
        Target = e.TargetPath,
        TargetKey = e.TargetKey,
        Line = e.Line,
        Kind = e.Kind,
        Depth = e.Depth,
        Resolved = e.Resolved,
        Builtin = e.Builtin,
        ClassId = e.ClassId,
        GameObject = e.GameObject,
        MethodName = e.MethodName,
        PropertyPath = e.PropertyPath,
        Via = e.Via,
        Confidence = e.ConfidenceLabel,
        TargetSymbol = e.TargetSymbol,
        SourceSymbol = e.SourceSymbol,
        RefKind = e.RefKind,
        Implicit = e.Implicit,
        BuildReachable = e.BuildReachable,
    };

    private static EdgeStatsJson ToEdgeStatsJson(EdgeStats e) => new()
    {
        GuidTotal = e.GuidTotal,
        GuidUnresolved = e.GuidUnresolved,
        GuidBuiltin = e.GuidBuiltin,
        PathTotal = e.PathTotal,
        PathUnresolved = e.PathUnresolved,
    };

    private static CsStatsJson ToCsStatsJson(CsStats cs) => new()
    {
        Types = cs.Types,
        Members = cs.Members,
        Refs = cs.Refs,
        TotalAssemblies = cs.TotalAssemblies,
        SyntacticAssemblies = cs.SyntacticAssemblies,
        NameHints = cs.NameHints,
    };

    private static FileCountsJson ToFileCountsJson(KindCounts counts) => new()
    {
        Total = counts.Total,
        Assets = counts.Assets,
        Scripts = counts.Scripts,
        Folders = counts.Folders,
        Settings = counts.Settings,
    };

    private static string FormatCount(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string FormatSeconds(TimeSpan elapsed) => elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";

    private static string FormatBytes(long bytes) => (bytes / 1024.0 / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    internal static string Version =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? "0.0.0";

    private static void PrintUsage()
    {
        Console.WriteLine(AnsiStyle.CommandBlock("""
            unbramble - Unity dependency graph CLI

            Usage:
              unbramble --version
              unbramble --help
              unbramble [path]                (no verb: first-time setup, or a quick status glance)
              unbramble init [path] [--json] [--no-agents] [--no-defender] [--defender]
              unbramble index [path] [--full] [--json]
              unbramble monitor [path]
              unbramble stop
              unbramble defender status [path]
              unbramble defender setup [path]
              unbramble defender remove [path]
              unbramble who-uses <path|guid|symbol> [--guids file] [--symbol] [--transitive] [--depth N] [--kind guid|path|cs|event] [--under prefix] [--json|--jsonl] [--verbose]
              unbramble uses <path|guid> [--missing-only] [--summary] [--top N] [--build-reachable-only] [--fail-if-found] [--json] [--verbose]
              unbramble audit-assets <paths-file> [--missing] [--group-by-target] [--include-owner-fields] [--build-reachable-only] [--top N] [--json|--jsonl] [--fail-if-found]
              unbramble cs-refs <name|doc-id> [-p path] [--json] [--verbose]   (alias of `who-uses <symbol>`)
              unbramble resolve <path|guid|name-fragment> [-p path] [--json] [--verbose]
              unbramble stats [path] [--unresolved] [--collisions] [--json] [--verbose]
              unbramble dead-candidates [path] [--json] [--include-advisory] [--kind assets|cs|all]

            who-uses also accepts a C# symbol (a name, "Type.Member", or a "T:"/"M:"/"F:"/"P:"/
            "E:"-prefixed doc-id) — resolved as a path/guid first, then as a symbol; if an
            argument resolves as both, pass --symbol or a doc-id prefix to disambiguate.

            --under scopes an answer to one location (e.g. --under Assets, --under
            Library/PackageCache): who-uses keeps referencers under the prefix, uses keeps
            dependencies under it. On high-fan-out targets, uses' text output collapses
            Library/PackageCache dependencies to a counted line — --verbose lists them.

            who-uses tags each referencer [build-reachable] (proven forward-reachable from the
            build roots: Build Settings scenes, Resources/, StreamingAssets/, entry points,
            Addressables) or [not proven build-reachable] — absence of proof, not "unreachable".

            `monitor` starts the background watcher when needed and shows its live progress.
            Ctrl+C closes the monitor only; `unbramble stop` stops the watcher.
            """, ConsoleCapabilities.SupportsAnsi));
    }

    private static int PrintVerbUsage(string verb)
    {
        var usage = verb switch
        {
            "init" => "unbramble init [path] [--json] [--verbose] [--no-agents] [--no-defender] [--defender]",
            "index" => "unbramble index [path] [--full] [--json] [--verbose]",
            "monitor" => "unbramble monitor [path]",
            "stop" => "unbramble stop",
            "defender" => "unbramble defender <status|setup|remove> [path]",
            "who-uses" => "unbramble who-uses <path|guid|symbol> [--guids file] [--symbol] [--transitive] [--depth N] [--kind guid|path|cs|event|dll] [--under prefix] [--json|--jsonl] [--verbose]",
            "uses" => "unbramble uses <path|guid> [--missing-only] [--paths file] [--summary|--group-by-target] [--top N] [--build-reachable-only] [--fail-if-found] [--json] [--verbose]",
            "audit-assets" => "unbramble audit-assets <paths-file> [--missing] [--group-by-target] [--include-owner-fields] [--build-reachable-only] [--top N] [--json|--jsonl] [--fail-if-found] [-p project]",
            "cs-refs" => "unbramble cs-refs <name|doc-id> [-p path] [--json] [--verbose]",
            "resolve" => "unbramble resolve <path|guid|name-fragment> [-p path] [--json] [--verbose]",
            "stats" => "unbramble stats [path] [--unresolved] [--collisions] [--json] [--verbose]",
            "dead-candidates" => "unbramble dead-candidates [path] [--json] [--include-advisory] [--kind assets|cs|all]",
            _ => null,
        };

        if (usage is null)
        {
            return Dispatch(verb);
        }

        Console.WriteLine("Usage:");
        Console.WriteLine("  " + AnsiStyle.Command(usage, ConsoleCapabilities.SupportsAnsi));
        if (verb == "uses")
        {
            Console.WriteLine();
            Console.WriteLine("--missing-only lists unresolved references and exits 0 when the query succeeds. Add --fail-if-found for a CI-style exit code 3 when findings exist.");
        }

        return 0;
    }

    /// <summary>The non-blocking startup half of <see cref="RunMonitor"/>, separated so the
    /// spawn-marker contract is directly testable without entering the monitor's UI loop.</summary>
    internal static void EnsureWatcherForMonitor(string projectRoot, bool startProcess = true)
    {
        var now = DateTime.UtcNow;
        AutoWatchMarkers.RecordSpawnAttempt(projectRoot, now);
        AutoWatchMarkers.TouchLastQuery(projectRoot, now);
        if (startProcess)
        {
            SpawnDetachedWatcher(projectRoot, explicitRequest: true);
        }
    }
}
