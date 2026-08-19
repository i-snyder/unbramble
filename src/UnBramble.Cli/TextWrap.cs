using System.Text;

namespace UnBramble.Cli;

/// <summary>
/// Word wrapping that measures what the *terminal* shows, not what the string contains: every
/// prose line this CLI prints has already been through <see cref="AnsiStyle"/>, so a naive
/// wrapper would count each invisible `\x1b[35m` toward the column budget and wrap a 60-column
/// paragraph at 45. Two rules follow from that, and they're the whole reason this file exists
/// rather than a one-line <c>string.Split</c> at the call site:
///
/// 1. <see cref="VisibleLength"/> skips CSI escape sequences entirely.
/// 2. Wrapping only ever happens at a space *between* words, so a line can never be cut inside
///    an escape sequence (which would print the sequence's tail as literal garbage like "35m").
///
/// SGR state deliberately survives a wrap: a paragraph opened with a color code and closed with
/// a reset several lines later stays colored throughout, because the continuation lines inherit
/// the still-unreset attribute. That only works because every continuation prefix here is plain
/// whitespace -- colored spaces are indistinguishable from uncolored ones.
/// </summary>
internal static class TextWrap
{
    /// <summary>
    /// Columns the text occupies once the terminal has swallowed its escape sequences. Counts
    /// chars, not grapheme clusters -- every string this wraps is ASCII prose plus project-file
    /// paths, and the em-dash/arrow glyphs already in use are single-width.
    /// </summary>
    public static int VisibleLength(string text)
    {
        var length = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (IsCsiIntroducer(text, i))
            {
                // Skip past "\x1b[", then over the parameter/intermediate bytes to the final
                // byte; the loop's own i++ steps off the final byte.
                i += 2;
                while (i < text.Length && !IsCsiFinalByte(text[i]))
                {
                    i++;
                }

                continue;
            }

            length++;
        }

        return length;
    }

    /// <summary>
    /// Wraps <paramref name="text"/> to <paramref name="width"/> columns, prefixing the first
    /// line with <paramref name="firstPrefix"/> and every continuation line with
    /// <paramref name="contPrefix"/> -- a hanging indent, so a wrapped line is visibly a
    /// continuation rather than a new statement. This is the single readability fix that matters
    /// most for this CLI's long diagnosis prose: the terminal's own wrapping restarts at column 0
    /// and happily breaks mid-word.
    ///
    /// A word longer than the remaining width (a long asset path) is placed rather than
    /// hard-split -- the terminal's own wrap is the better failure mode there than a path cut in
    /// half at an arbitrary column, which no longer round-trips a copy/paste.
    /// </summary>
    public static IReadOnlyList<string> WrapLines(string text, int width, string firstPrefix = "", string contPrefix = "")
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return [];
        }

        var contWidth = VisibleLength(contPrefix);
        var lines = new List<string>();
        var line = new StringBuilder(firstPrefix);
        var column = VisibleLength(firstPrefix);
        var lineHasWord = false;

        foreach (var word in words)
        {
            var wordWidth = VisibleLength(word);
            if (lineHasWord && column + 1 + wordWidth > width)
            {
                lines.Add(line.ToString());
                line.Clear().Append(contPrefix);
                column = contWidth;
                lineHasWord = false;
            }

            if (lineHasWord)
            {
                line.Append(' ');
                column++;
            }

            line.Append(word);
            column += wordWidth;
            lineHasWord = true;
        }

        lines.Add(line.ToString());
        return lines;
    }

    private static bool IsCsiIntroducer(string text, int i) =>
        text[i] == '\x1b' && i + 1 < text.Length && text[i + 1] == '[';

    /// <summary>A CSI sequence ends at the first byte in 0x40-0x7E ("@" through "~") -- e.g. the
    /// "m" of "\x1b[35m".</summary>
    private static bool IsCsiFinalByte(char c) => c is >= '\x40' and <= '\x7e';
}
