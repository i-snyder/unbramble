using System.Reflection;
using UnBramble.Cli;

namespace UnBramble.Tests;

public class StartupMascotRendererTests
{
    private static readonly Type RendererType =
        typeof(Program).Assembly.GetType("UnBramble.Cli.StartupMascotRenderer")!;

    [Fact]
    public void Render_WideTerminal_PutsTitleAboveCroppedArtWithTwoBlankRowsAroundIt()
    {
        var output = Render(terminalWidth: 80, trueColor: true);
        var lines = output.Split(Environment.NewLine);

        Assert.Equal("  unbramble 0.1.0", StripAnsi(lines[0]));
        Assert.Equal("  Clearing a path through complex Unity projects", StripAnsi(lines[1]));
        Assert.Equal(string.Empty, lines[2]);
        Assert.Equal(string.Empty, lines[3]);
        Assert.Contains('\u2584', lines[4]);
        var lastArtLine = Array.FindLastIndex(lines, line => line.Contains('\u2584') || line.Contains('\u2580'));
        Assert.Equal(lines.Length - 4, lastArtLine);
        Assert.Contains('\u2584', output);
        Assert.Contains('\u2580', output);
        Assert.Contains("\x1b[49m", output, StringComparison.Ordinal);
        Assert.Contains("\x1b[48;2;", output, StringComparison.Ordinal);
        Assert.Contains("unbramble 0.1.0", output, StringComparison.Ordinal);
        Assert.Contains("Clearing a path through complex Unity projects", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Unity dependency graph", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Ready to grow!", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_NarrowTerminal_FallsBackWithoutClippingArt()
    {
        var output = Render(terminalWidth: 65, trueColor: true);

        Assert.DoesNotContain('\u2584', output);
        Assert.Contains($"unbramble 0.1.0{Environment.NewLine}Clearing a path through complex Unity projects", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Unity dependency graph", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Ready to grow!", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_Ansi256Fallback_DoesNotEmitTruecolor()
    {
        var output = Render(terminalWidth: 80, trueColor: false);

        Assert.Contains("\x1b[48;5;", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\x1b[48;2;", output, StringComparison.Ordinal);
    }

    private static string Render(int terminalWidth, bool trueColor)
    {
        var method = RendererType.GetMethod("Render", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)method.Invoke(null, ["0.1.0", trueColor, terminalWidth])!;
    }

    private static string StripAnsi(string value)
    {
        var sb = new System.Text.StringBuilder();
        var inEscape = false;
        foreach (var c in value)
        {
            if (c == '\x1b') inEscape = true;
            else if (inEscape && c == 'm') inEscape = false;
            else if (!inEscape) sb.Append(c);
        }

        return sb.ToString();
    }
}
