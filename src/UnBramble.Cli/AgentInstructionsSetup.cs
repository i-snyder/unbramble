using System.Text;

namespace UnBramble.Cli;

/// <summary>
/// `init`-only, announced side effect (same "loud, not silent" philosophy as
/// <c>Program.SetUpIgnoreFiles</c>): writes static agent-discovery instructions at the project
/// root so a fresh coding-agent session -- which has no MCP server or prompt injection to learn
/// from -- can discover this tool exists just by reading the files it already reads on startup.
///
/// Two files, two different postures:
/// - <c>AGENTS.md</c>: owns a marker-delimited "managed block" that this method freely creates,
///   refreshes, and version-stamps on every `init` (see <see cref="UpsertManagedBlock"/>).
/// - <c>CLAUDE.md</c>: a user's prompt surface. Never edited if it already has substantive
///   content; a fresh one gets a one-line shim pointing at AGENTS.md instead of duplicating the
///   block (see <see cref="EnsureClaudeShim"/>).
/// </summary>
internal static class AgentInstructionsSetup
{
    private const string BeginMarkerPrefix = "<!-- unbramble:begin";

    private const string EndMarker = "<!-- unbramble:end -->";

    // Claude Code's @path import parser treats '.' as a valid path character (so imports like
    // @package.json work), which means punctuation glued directly onto "@AGENTS.md" -- e.g. a
    // trailing sentence period -- gets captured into the filename and silently fails to resolve.
    // @AGENTS.md must stand alone on its own line, matching Claude Code's own documented pattern,
    // or the import never actually lands in context.
    private const string ClaudeShimText = """
        @AGENTS.md

        Don't modify this file -- changes that would normally land in CLAUDE.md should go in AGENTS.md.
        """;

    private const string BodyTemplate = """
        ## unbramble

        `unbramble` is installed in this project and indexes it: GUID/asset references (prefabs, scenes, materials, shaders, Addressables, ...) plus real C# semantic analysis (Roslyn). Queries typically return in under a second once indexed.

        Never grep for GUIDs or text-search `.prefab`/`.unity`/`.asset`/`.asmdef`/`.meta` files -- text search silently misses references and gives false confidence in blast-radius answers. If you catch yourself extracting a GUID from a `.meta` file to search for it, stop: that is exactly what `unbramble who-uses <path-or-guid>` answers, completely, across the whole indexed project. Use `who-uses` / `uses` / `resolve` / `cs-refs` for "what references this" / "what breaks if I delete or rename this" / "who uses this GUID" -- before reaching for grep, not after. `stats` and `dead-candidates` cover project-wide overviews. Run `unbramble --help` or `unbramble <verb> --help` for usage; add `--json` for machine-readable output. The never-grep rule covers Unity-serialized files only: plain JSON config such as `Packages/manifest.json` or a package's `package.json` holds literal strings, not GUID references, and isn't indexed -- grep is the right tool there.

        For a C# symbol, `who-uses <Type.Member>` is the wider question and the one to prefer: it returns everything `cs-refs` does, plus the declaring file's own asset referencers and (where syntactic assemblies exist) speculative name-match leads. Both verbs report UnityEvent bindings -- a method wired to a Button.onClick in a prefab or scene is a real call site with no C# caller, so neither verb's zero means "nothing calls this".

        Answers name the owning serialized field next to each reference (`m_Settings.m_VolumeProfile`, `m_Materials[2]`), and `who-uses` tags every referencer `[build-reachable]` or `[not proven build-reachable]` -- the fast way to separate content the build actually reaches from test/dead content among referencers (proof only in the positive direction; "not proven" is not "unreachable"). On high-fan-out answers, scope with `--under <prefix>` (e.g. `--under Assets`); `uses` collapses `Library/PackageCache` internals to a counted line unless `--verbose` or `--under` expands them.

        For broken serialized links, use `uses <asset> --missing-only --summary`: groups repeated target GUIDs and includes owner GameObject, component, property path, prefab override/source context, `m_Script` classification, and build reachability. Use `--build-reachable-only` to cut dead/sample noise. Findings exit 0 because the query succeeded; add `--fail-if-found` only when intentionally using it as a CI gate.

        Audit several assets in one consistent snapshot with `audit-assets <paths-file> --missing --group-by-target --json` (the alias `uses --missing-only --paths <file>` is equivalent). Look up several GUIDs with `who-uses --guids <file>`. Add `--jsonl` to either batch command for one streaming object per target; progress goes to stderr.

        Freshness is self-managed -- no manual reindex step. Only the first-ever query on a freshly inited project can take minutes (a one-time cost); every query after that is fast.

        C# caveats are suppressed when the answer is strictly an asset-only missing-reference query. If a C#-dependent `who-uses`/`uses` answer names syntactic assemblies, or flags `possibleFalseNegative` (JSON: `syntacticAssemblies` / `possibleFalseNegative`), that assembly's callers aren't in the semantic index yet -- usually because Unity hasn't generated its `.csproj` yet (e.g. right after a fresh clone). Check any `speculative`-confidence leads printed alongside before concluding "nothing references this" -- real call sites found by identifier text, not proof, but not nothing either. Fix: open the project in the Unity Editor once (or open a .cs file in your IDE while Unity is running) to regenerate the missing `.csproj` files, then re-index.
        """;

    /// <summary>Entry point, mirrors <c>Program.SetUpIgnoreFiles</c>'s shape: called once from
    /// `RunInit` after the ignore-file setup step, with an <paramref name="announce"/> callback
    /// that the caller wires to a no-op under `--json` (this step must never pollute JSON
    /// output).</summary>
    public static void SetUp(string projectRoot, string version, Action<string> announce)
    {
        var agentsPath = Path.Combine(projectRoot, "AGENTS.md");
        var block = BuildBlock(version);
        UpsertManagedBlock(agentsPath, block, announce);
        EnsureClaudeShim(projectRoot, announce);
    }

    /// <summary>Removes the marker-delimited block (or the pre-marker unbramble section that
    /// older setup versions wrote) while preserving everything around it. Used only by project
    /// uninstall and rollback-receipt maintenance.</summary>
    public static byte[]? RemoveOwnedAgentsContent(byte[]? content, bool removeGeneratedContainerHeading = false)
    {
        if (content is null)
        {
            return null;
        }

        var originalText = Encoding.UTF8.GetString(content).TrimStart('\uFEFF');
        var lines = SplitLines(originalText).ToList();
        var start = lines.FindIndex(l => l.TrimStart().StartsWith(BeginMarkerPrefix, StringComparison.Ordinal));
        var end = -1;

        if (start >= 0)
        {
            end = lines.FindIndex(start + 1, l => l.Trim() == EndMarker);
            if (end < 0)
            {
                return content;
            }
        }
        else
        {
            start = lines.FindIndex(IsUnbrambleHeading);
            if (start < 0)
            {
                return content;
            }

            var headingLevel = CountHeadingLevel(lines[start]);
            end = lines.Count - 1;
            for (var i = start + 1; i < lines.Count; i++)
            {
                var level = CountHeadingLevel(lines[i]);
                if (level > 0 && level <= headingLevel)
                {
                    end = i - 1;
                    break;
                }
            }
        }

        var before = lines.Take(start).ToList();
        var after = lines.Skip(end + 1).ToList();
        while (before.Count > 0 && string.IsNullOrWhiteSpace(before[^1]))
        {
            before.RemoveAt(before.Count - 1);
        }

        while (after.Count > 0 && string.IsNullOrWhiteSpace(after[0]))
        {
            after.RemoveAt(0);
        }

        var result = new List<string>(before.Count + after.Count + 1);
        result.AddRange(before);
        if (before.Count > 0 && after.Count > 0)
        {
            result.Add(string.Empty);
        }

        result.AddRange(after);
        while (result.Count > 0 && string.IsNullOrWhiteSpace(result[^1]))
        {
            result.RemoveAt(result.Count - 1);
        }

        if (removeGeneratedContainerHeading
            && result.Count > 0
            && result[0].Trim() == "# AGENTS.md")
        {
            result.RemoveAt(0);
            while (result.Count > 0 && string.IsNullOrWhiteSpace(result[0]))
            {
                result.RemoveAt(0);
            }
        }

        if (result.Count == 0 || result.Count == 1 && result[0].Trim() == "# AGENTS.md")
        {
            return null;
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetBytes(string.Join(Environment.NewLine, result) + Environment.NewLine);
    }

    /// <summary>Removes only the exact shim generated by this class. A substantive user-owned
    /// CLAUDE.md that merely references AGENTS.md is never treated as ours.</summary>
    public static byte[]? RemoveGeneratedClaudeShim(byte[]? content)
    {
        if (content is null)
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(content).TrimStart('\uFEFF').Replace("\r\n", "\n");
        var shim = ClaudeShimText.Replace("\r\n", "\n");
        if (string.Equals(text.TrimEnd('\n'), shim.TrimEnd('\n'), StringComparison.Ordinal))
        {
            return null;
        }

        var prefix = shim.TrimEnd('\n') + "\n";
        if (!text.StartsWith(prefix, StringComparison.Ordinal))
        {
            return content;
        }

        var remainder = text[prefix.Length..].TrimStart('\n');
        return remainder.Length == 0
            ? null
            : new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                .GetBytes(remainder.TrimEnd('\n') + Environment.NewLine);
    }

    private static string BuildBlock(string version)
    {
        var lines = new List<string>
        {
            $"{BeginMarkerPrefix} v{version} -->",
            "<!-- Managed by `unbramble init`. Edits between these markers are overwritten when init reruns; put your own notes outside them. -->",
        };
        lines.AddRange(SplitLines(BodyTemplate));
        lines.Add(EndMarker);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Splices <paramref name="newBlock"/> into <paramref name="filePath"/> between
    /// `&lt;!-- unbramble:begin --&gt;` / `&lt;!-- unbramble:end --&gt;` markers, generic over any
    /// Markdown file so future convention files can reuse it. See the type doc comment for the
    /// full state table this implements.</summary>
    private static void UpsertManagedBlock(string filePath, string newBlock, Action<string> announce)
    {
        if (!File.Exists(filePath))
        {
            var created = "# AGENTS.md" + Environment.NewLine + Environment.NewLine + newBlock + Environment.NewLine;
            File.WriteAllText(filePath, created);
            announce($"Agent instructions: created {Path.GetFileName(filePath)}.");
            return;
        }

        var originalText = File.ReadAllText(filePath);
        var lines = SplitLines(originalText).ToList();

        var beginIndex = lines.FindIndex(l => l.TrimStart().StartsWith(BeginMarkerPrefix, StringComparison.Ordinal));
        if (beginIndex >= 0)
        {
            var endIndex = lines.FindIndex(beginIndex + 1, l => l.Trim() == EndMarker);
            if (endIndex < 0)
            {
                announce(
                    $"Warning: {Path.GetFileName(filePath)} has an unbramble:begin marker with no matching " +
                    "unbramble:end marker -- left untouched. Fix or delete the stray marker and re-run init.");
                return;
            }

            var newLines = new List<string>(lines.Count);
            newLines.AddRange(lines.Take(beginIndex));
            newLines.AddRange(SplitLines(newBlock));
            newLines.AddRange(lines.Skip(endIndex + 1));

            var newText = string.Join(Environment.NewLine, newLines);
            if (newText == originalText)
            {
                announce($"Agent instructions: {Path.GetFileName(filePath)} already up to date.");
                return;
            }

            File.WriteAllText(filePath, newText);
            announce($"Agent instructions: refreshed the managed block in {Path.GetFileName(filePath)}.");
            return;
        }

        var migratedText = TryMigrateUnmarkedSection(originalText, newBlock, out var migrated);
        if (migrated)
        {
            File.WriteAllText(filePath, migratedText);
            announce(
                $"Agent instructions: migrated existing 'unbramble' section in {Path.GetFileName(filePath)} " +
                "into a managed block (re-running init will now keep it current).");
            return;
        }

        var needsLeadingNewline = originalText.Length > 0 && !originalText.EndsWith('\n');
        using (var writer = new StreamWriter(filePath, append: true))
        {
            if (needsLeadingNewline)
            {
                writer.WriteLine();
            }

            writer.WriteLine();
            writer.WriteLine(newBlock);
        }

        announce($"Agent instructions: added the unbramble block to {Path.GetFileName(filePath)}.");
    }

    /// <summary>Migration for a pre-existing *unmarked* hand-written `## unbramble` (or any
    /// heading level) section: replaces the whole heading-to-next-heading span with the
    /// freshly generated marker-wrapped block. Only the first such heading is migrated if
    /// more than one somehow exists. Returns the original text unchanged (and
    /// <paramref name="migrated"/> false) if no such heading is found.</summary>
    private static string TryMigrateUnmarkedSection(string originalText, string newBlock, out bool migrated)
    {
        var lines = SplitLines(originalText).ToList();
        var headingIndex = lines.FindIndex(IsUnbrambleHeading);
        if (headingIndex < 0)
        {
            migrated = false;
            return originalText;
        }

        var headingLevel = CountHeadingLevel(lines[headingIndex]);
        var sectionEnd = lines.Count;
        for (var i = headingIndex + 1; i < lines.Count; i++)
        {
            var level = CountHeadingLevel(lines[i]);
            if (level > 0 && level <= headingLevel)
            {
                sectionEnd = i;
                break;
            }
        }

        var result = new List<string>(lines.Count);
        result.AddRange(lines.Take(headingIndex));
        result.AddRange(SplitLines(newBlock));
        result.AddRange(lines.Skip(sectionEnd));

        migrated = true;
        return string.Join(Environment.NewLine, result);
    }

    private static bool IsUnbrambleHeading(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed[0] != '#')
        {
            return false;
        }

        var hashCount = 0;
        while (hashCount < trimmed.Length && trimmed[hashCount] == '#')
        {
            hashCount++;
        }

        if (hashCount is 0 or > 6)
        {
            return false;
        }

        var rest = trimmed[hashCount..].Trim();
        return string.Equals(rest, "unbramble", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns the Markdown heading level (1-6) of <paramref name="line"/>, or 0 if it
    /// isn't a heading line.</summary>
    private static int CountHeadingLevel(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed[0] != '#')
        {
            return 0;
        }

        var hashCount = 0;
        while (hashCount < trimmed.Length && trimmed[hashCount] == '#')
        {
            hashCount++;
        }

        if (hashCount is 0 or > 6)
        {
            return 0;
        }

        // A bare "#word" with no space/EOL after the hashes isn't a heading per CommonMark,
        // but we're lenient here since this only needs to recognize our own generated headings
        // and reasonable hand-written ones.
        return hashCount;
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n").Split('\n');

    /// <summary>
    /// `CLAUDE.md` is a user's own prompt surface, not a file this tool owns -- so unlike
    /// AGENTS.md, it is either created fresh (with a one-line shim pointing at AGENTS.md) or
    /// left completely untouched.
    /// </summary>
    private static void EnsureClaudeShim(string projectRoot, Action<string> announce)
    {
        var claudePath = Path.Combine(projectRoot, "CLAUDE.md");
        if (!File.Exists(claudePath))
        {
            File.WriteAllText(claudePath, ClaudeShimText + Environment.NewLine);
            announce("Agent instructions: created CLAUDE.md pointing at AGENTS.md.");
            return;
        }

        var text = File.ReadAllText(claudePath);
        if (text.Contains("AGENTS.md", StringComparison.Ordinal))
        {
            return;
        }

        announce(
            "Note: CLAUDE.md exists but doesn't reference AGENTS.md -- Claude Code may not see the " +
            "unbramble instructions. Add a line like 'Read and follow @AGENTS.md.' to CLAUDE.md.");
    }
}
