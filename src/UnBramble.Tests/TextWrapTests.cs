using System.Reflection;
using UnBramble.Cli;

namespace UnBramble.Tests;

/// <summary>
/// Pure-function coverage of `TextWrap`, the ANSI-aware wrapper behind every wrapped paragraph
/// and aligned `stats` row. Reflection-driven for the same reason as
/// <see cref="BrambleProgressRendererTests"/>: it's `internal static` and this test project has
/// no InternalsVisibleTo grant on UnBramble.Cli.
///
/// The cases that matter are the ones a plain <c>Split</c>-based wrapper gets wrong: styled text
/// must be measured by what the terminal *shows* (escape codes occupy no columns), and no line
/// may ever end inside an escape sequence -- a split there prints the sequence's tail as literal
/// garbage.
/// </summary>
public class TextWrapTests
{
    private static readonly Type WrapType = typeof(Program).Assembly.GetType("UnBramble.Cli.TextWrap")!;

    private static int VisibleLength(string text) =>
        (int)WrapType.GetMethod("VisibleLength", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [text])!;

    private static IReadOnlyList<string> WrapLines(string text, int width, string firstPrefix = "", string contPrefix = "") =>
        (IReadOnlyList<string>)WrapType.GetMethod("WrapLines", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [text, width, firstPrefix, contPrefix])!;

    [Fact]
    public void VisibleLength_PlainText_CountsEveryCharacter()
    {
        Assert.Equal(5, VisibleLength("hello"));
    }

    [Fact]
    public void VisibleLength_IgnoresSgrEscapeSequences()
    {
        // The shape AnsiStyle.Finding("looks orphaned", ansi: true) produces.
        Assert.Equal("looks orphaned".Length, VisibleLength("\x1b[35mlooks orphaned\x1b[0m"));
    }

    [Fact]
    public void VisibleLength_IgnoresNestedAndCompoundCodes()
    {
        // Label() double-wraps (Bold(Cyan(x))), and BoldRed uses a compound "1;31" parameter --
        // both must still measure as their bare text.
        Assert.Equal(6, VisibleLength("\x1b[1m\x1b[36mFiles:\x1b[0m\x1b[0m"));
        Assert.Equal(6, VisibleLength("\x1b[1;31merror:\x1b[0m"));
    }

    [Fact]
    public void VisibleLength_UnterminatedEscape_DoesNotOverrunTheString()
    {
        // Defensive: a truncated sequence must not throw or count past the end.
        Assert.Equal(0, VisibleLength("\x1b[35"));
    }

    [Fact]
    public void WrapLines_ShortText_StaysOnOneLine()
    {
        Assert.Equal(["hello world"], WrapLines("hello world", 40));
    }

    [Fact]
    public void WrapLines_EmptyText_ProducesNothing()
    {
        // Not a single blank/prefix-only line -- callers would emit a stray indent.
        Assert.Empty(WrapLines("", 40));
    }

    [Fact]
    public void WrapLines_BreaksAtWidthAndAppliesTheHangingIndent()
    {
        var lines = WrapLines("aaa bbb ccc ddd", 11, contPrefix: "    ");

        Assert.Equal(["aaa bbb ccc", "    ddd"], lines);
    }

    [Fact]
    public void WrapLines_NoLineExceedsTheRequestedWidth()
    {
        const string text = "never compiled by Unity and 0 references found anywhere in the project";
        var lines = WrapLines(text, 30, contPrefix: "    ");

        Assert.All(lines, line => Assert.True(VisibleLength(line) <= 30, $"line overflowed: '{line}'"));
    }

    /// <summary>
    /// The whole reason this class exists rather than a <c>string.Split</c> at the call site: the
    /// escape codes must not consume any of the column budget, so styled and unstyled text of the
    /// same visible length have to wrap identically.
    /// </summary>
    [Fact]
    public void WrapLines_StyledText_WrapsOnVisibleWidthNotRawLength()
    {
        var plain = WrapLines("looks orphaned: some.package never compiled", 24);
        var styled = WrapLines("\x1b[35mlooks orphaned:\x1b[0m some.package never compiled", 24);

        Assert.Equal(plain.Count, styled.Count);
        Assert.Equal(plain[0], styled[0].Replace("\x1b[35m", "").Replace("\x1b[0m", ""));
    }

    [Fact]
    public void WrapLines_NeverBreaksInsideAnEscapeSequence()
    {
        var lines = WrapLines("\x1b[1;33mlooks broken:\x1b[0m runtime pkg was never compiled by Unity", 20, contPrefix: "  ");

        // A line ending mid-sequence would leave a dangling "\x1b[" with no final byte, and the
        // terminal would print the remainder ("33m") as literal text on the next line.
        Assert.True(lines.Count > 1, "the fixture must actually wrap for this to prove anything");
        Assert.All(lines, line => Assert.False(EndsInsideEscape(line), $"line ends mid-escape: '{line}'"));
    }

    [Fact]
    public void WrapLines_WordLongerThanWidth_IsPlacedRatherThanSplit()
    {
        // A path cut at an arbitrary column no longer round-trips a copy/paste, so overflow is
        // the deliberate failure mode -- the terminal's own wrap handles it from there.
        var lines = WrapLines("see Assets/_Game/Bootstrap/LoadingScreen/Scripts/Controller.cs now", 20);

        Assert.Contains(lines, line => line.Contains("Assets/_Game/Bootstrap/LoadingScreen/Scripts/Controller.cs"));
    }

    [Fact]
    public void WrapLines_FirstPrefixCountsTowardTheFirstLinesWidth()
    {
        // How WriteLabeledRows aligns: the padded label is a prefix, and the value must wrap as
        // though it started at that column, not at column 0.
        var lines = WrapLines("aaa bbb", 12, firstPrefix: "Label:  ", contPrefix: "        ");

        Assert.Equal(["Label:  aaa", "        bbb"], lines);
    }

    private static bool EndsInsideEscape(string line)
    {
        var lastEsc = line.LastIndexOf('\x1b');
        if (lastEsc < 0)
        {
            return false;
        }

        // Inside iff no CSI final byte (0x40-0x7E) appears after the last ESC's "[".
        for (var i = lastEsc + 2; i < line.Length; i++)
        {
            if (line[i] is >= '\x40' and <= '\x7e')
            {
                return false;
            }
        }

        return true;
    }
}
