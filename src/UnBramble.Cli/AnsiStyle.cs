using System.Globalization;

namespace UnBramble.Cli;

/// <summary>
/// Shared ANSI styling for every CLI surface (Program.cs's errors/warnings/stats/query output,
/// HomeCommand's dashboard). Every method takes the caller's own `ansiEnabled` (almost always
/// <see cref="ConsoleCapabilities.SupportsAnsi"/>) and is a plain-text passthrough when it's
/// false, so callers never need a separate code path for a redirected/NO_COLOR/dumb terminal.
///
/// ============================================================================================
/// VISUAL DESIGN -- the hedgerow palette. Tune the pigments freely; the roles are the contract.
/// ============================================================================================
///
/// Deliberately NOT the stock terminal palette. Stock red/yellow/green/cyan is what every other
/// CLI on the machine looks like, and this tool has its own story already: the bramble greening
/// as it grows (<see cref="BrambleProgressRenderer"/>'s brown-rooted, green-canopied sprig).
/// This palette is that same hedge seen up close -- every pigment is a thing you'd actually find
/// in one, and each maps onto the one job it's the obvious color for:
///
/// - <see cref="Alive"/> (leaf green) -- thriving, verified, fresh.
/// - <see cref="Caution"/> (turning-leaf amber) -- going over, but not gone: warnings, and the
///   "looks broken" finding (still referenced, just not compiling).
/// - <see cref="Alarm"/> (rosehip red) -- the alarm berry. Errors only.
/// - <see cref="Finding"/> (blackberry) -- the ripe thing you spot in the hedge and stop for:
///   the strongest diagnosis this tool makes ("looks orphaned").
/// - <see cref="Command"/> (bluebell) -- something the user can type; deliberately outside the
///   brown/green status axis so commands stay distinct inside any surrounding sentence.
/// - <see cref="Label"/>/<see cref="Notice"/> (hazel bark) -- the woody structure the rest hangs
///   off: `stats` keys, `note:` prefixes, routine advice.
/// - <see cref="Muted"/> (dry bracken) -- present but spent: chrome, footers, trailing hints.
///   A warm low-contrast brown rather than the stock `\x1b[2m` dim, which renders as a barely
///   legible grey on a dark terminal -- de-emphasized should still be *readable*.
///
/// Leaf and hazel are anchored to <see cref="BrambleProgressRenderer"/>'s canopy/root colors --
/// literally the same constants (see <see cref="Palette"/>), so the progress bar and the answer
/// it prints belong to one tool rather than coincidentally owning similar browns.
///
/// Rendered as 24-bit color where the terminal advertises it, else each pigment's hand-picked
/// xterm-256 stand-in. Picking those by nearest-RGB-distance was tried and rejected: it turns
/// Bark (146,93,49) into xterm 95 = (135,95,95), a dusty rose, because blue 49 is arithmetically
/// nearer 95 than 0 and lifting it desaturates the brown away entirely. Euclidean RGB distance
/// is not perceptual distance, and on a 6-level-per-channel cube the error is big enough to lose
/// the hue -- so each fallback below is chosen for looking like the pigment, not for scoring well.
/// </summary>
internal static class AnsiStyle
{
    /// <summary>The hedgerow's pigments. Shared with <see cref="BrambleProgressRenderer"/>'s
    /// gradient anchors -- change these and both surfaces move together, which is the point.</summary>
    internal static class Palette
    {
        /// <summary>Leaf green: the canopy, the growing end of the color story.</summary>
        internal static readonly Rgb Leaf = new(84, 190, 110, Xterm256: 71);

        /// <summary>Bramble brown: the root/soil end of the color story. Its xterm-256 stand-in
        /// is the same 130 <see cref="BrambleProgressRenderer"/>'s ramp already starts from.</summary>
        internal static readonly Rgb Bark = new(146, 93, 49, Xterm256: 130);

        /// <summary>Hazel: bark lifted to stay legible as *text* on a dark terminal, where raw
        /// <see cref="Bark"/> reads as mud. The gradient can use the deep tone because it's a
        /// filled block; a label can't.</summary>
        internal static readonly Rgb Hazel = new(198, 140, 78, Xterm256: 173);

        /// <summary>Turning leaf: amber, going over. Its fallback is deliberately the saturated
        /// 214 rather than the arithmetically-closer 179, which is one green step from
        /// <see cref="Hazel"/>'s 173 and would make a warning indistinguishable from a label.</summary>
        internal static readonly Rgb Amber = new(222, 158, 58, Xterm256: 214);

        /// <summary>Rosehip: the alarm berry.</summary>
        internal static readonly Rgb Rosehip = new(224, 82, 74, Xterm256: 167);

        /// <summary>Blackberry, lifted off true black-purple for the same legibility reason as
        /// <see cref="Hazel"/>.</summary>
        internal static readonly Rgb Blackberry = new(176, 112, 214, Xterm256: 140);

        /// <summary>Bluebell: the interactive accent used only for commands a user can type.</summary>
        internal static readonly Rgb Bluebell = new(105, 151, 230, Xterm256: 75);

        /// <summary>Dry bracken: spent, but still there.</summary>
        internal static readonly Rgb Bracken = new(150, 132, 108, Xterm256: 101);
    }

    /// <param name="Xterm256">The pigment's stand-in on a terminal without 24-bit color -- see
    /// this class's own remarks for why these are hand-picked and not computed.</param>
    internal readonly record struct Rgb(byte R, byte G, byte B, int Xterm256);

    /// <summary>Errors: `error:` lines, failure exit paths.</summary>
    public static string Alarm(string text, bool ansiEnabled) => Paint(text, Palette.Rosehip, bold: true, ansiEnabled);

    /// <summary>Warnings, false-negative callouts, staleness notes, and the "looks broken"
    /// finding -- all the same idea: going over, not yet gone.</summary>
    public static string Caution(string text, bool ansiEnabled) => Paint(text, Palette.Amber, bold: true, ansiEnabled);

    /// <summary>The strongest diagnosis this tool makes ("looks orphaned") -- distinct from a
    /// routine <see cref="Caution"/> so it can't get lost among ordinary warnings in a long
    /// stats/query listing.</summary>
    public static string Finding(string text, bool ansiEnabled) => Paint(text, Palette.Blackberry, bold: true, ansiEnabled);

    /// <summary>A command the user can type, whether shown alone or inline in guidance.</summary>
    public static string Command(string text, bool ansiEnabled) => Paint(text, Palette.Bluebell, bold: false, ansiEnabled);

    /// <summary>Structural key/prefix -- `stats`' "Files:", a `note: ` prefix, routine advice.
    /// Structural, not alarming, so it gets its own tone away from the findings.</summary>
    public static string Label(string text, bool ansiEnabled) => Paint(text, Palette.Hazel, bold: true, ansiEnabled);

    /// <summary>A whole informational line (not a `Key:` prefix) -- same wood as
    /// <see cref="Label"/>, unbolded, because bolding a full sentence shouts.</summary>
    public static string Notice(string text, bool ansiEnabled) => Paint(text, Palette.Hazel, bold: false, ansiEnabled);

    /// <summary>Success/fresh/confirmed states.</summary>
    public static string Alive(string text, bool ansiEnabled) => Paint(text, Palette.Leaf, bold: false, ansiEnabled);

    /// <summary>De-emphasized secondary detail: paths, counts, remediation hints trailing a
    /// headline finding, the blind-spots footer.</summary>
    public static string Muted(string text, bool ansiEnabled) => Paint(text, Palette.Bracken, bold: false, ansiEnabled);

    /// <summary>
    /// The `[Y/n]` affordance for prompts that accept Enter as yes.
    ///
    /// The capital letter is the default, and that's the one thing here worth a color: it tells
    /// the reader what Enter does without them having to know the bracket convention. Everything
    /// around it is punctuation, so it recedes.
    /// </summary>
    public static string YesNoPrompt(bool ansiEnabled) =>
        Muted("[", ansiEnabled) + Alive("Y", ansiEnabled) + Muted("/n]", ansiEnabled);

    /// <summary>The `[y/N]` affordance for prompts that require an explicit yes.</summary>
    public static string NoYesPrompt(bool ansiEnabled) =>
        Muted("[y/", ansiEnabled) + Alive("N", ansiEnabled) + Muted("]", ansiEnabled);

    /// <summary>Highlights complete quoted/backticked <c>unbramble ...</c> commands without
    /// coloring their delimiters or surrounding prose.</summary>
    public static string InlineCommands(string text, bool ansiEnabled)
    {
        if (!ansiEnabled)
        {
            return text;
        }

        var sb = new System.Text.StringBuilder(text.Length + 32);
        var cursor = 0;
        while (cursor < text.Length)
        {
            var singleQuote = text.IndexOf("'unbramble ", cursor, StringComparison.Ordinal);
            var backtick = text.IndexOf("`unbramble ", cursor, StringComparison.Ordinal);
            var quote = singleQuote < 0
                ? backtick
                : backtick < 0 ? singleQuote : Math.Min(singleQuote, backtick);
            if (quote < 0)
            {
                sb.Append(text, cursor, text.Length - cursor);
                break;
            }

            var end = text.IndexOf(text[quote], quote + 1);
            if (end < 0)
            {
                sb.Append(text, cursor, text.Length - cursor);
                break;
            }

            sb.Append(text, cursor, quote - cursor + 1);
            var content = text[(quote + 1)..end];
            sb.Append(Command(content, ansiEnabled));
            sb.Append(text[end]);
            cursor = end + 1;
        }

        return sb.ToString();
    }

    /// <summary>Styles complete usage lines plus inline commands inside explanatory lines.</summary>
    public static string CommandBlock(string text, bool ansiEnabled)
    {
        if (!ansiEnabled)
        {
            return text;
        }

        var lines = text.ReplaceLineEndings("\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            var indentLength = lines[i].Length - trimmed.Length;
            lines[i] = indentLength > 0 && trimmed.StartsWith("unbramble ", StringComparison.Ordinal)
                ? lines[i][..(lines[i].Length - trimmed.Length)] + Command(trimmed, ansiEnabled)
                : InlineCommands(lines[i], ansiEnabled);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private const string Reset = "\x1b[0m";

    private static string Paint(string text, Rgb color, bool bold, bool ansiEnabled) =>
        ansiEnabled ? Sgr(color, bold, ConsoleCapabilities.SupportsTrueColor) + text + Reset : text;

    /// <summary>
    /// One combined SGR sequence rather than a bold code followed by a color code -- fewer bytes
    /// on the wire, and one less thing for <see cref="TextWrap"/> to measure past.
    ///
    /// Takes <paramref name="trueColor"/> rather than reading the capability, mirroring
    /// <c>BrambleProgressRenderer.AnsiForCell</c>: it makes both rendering branches directly
    /// assertable, instead of leaving the one under test to depend on whether the machine running
    /// the suite happens to export COLORTERM.
    /// </summary>
    internal static string Sgr(Rgb color, bool bold, bool trueColor)
    {
        var weight = bold ? "1;" : "";
        return trueColor
            ? $"\x1b[{weight}38;2;{Num(color.R)};{Num(color.G)};{Num(color.B)}m"
            : $"\x1b[{weight}38;5;{Num(color.Xterm256)}m";
    }

    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);
}
