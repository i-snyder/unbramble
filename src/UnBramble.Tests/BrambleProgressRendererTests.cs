using System.Reflection;
using UnBramble.Cli;

namespace UnBramble.Tests;

/// <summary>
/// Pure-function coverage of `BrambleProgressRenderer`'s visual-design helpers (gradient,
/// thorn-to-vine animation, middle-truncation, frame assembly) -- reflection-driven, same reasoning as
/// `WatchDashboardFrameTests`: these are `internal static` and this test project has no
/// InternalsVisibleTo grant on UnBramble.Cli. The stateful machinery (OnScanProgress/OnPhase/
/// Complete) is exercised end-to-end instead, via `HomeCommandTests`' accept-accept-with-ANSI
/// case -- the useful assertion there is "it ran to completion and produced colored output", not
/// frame-by-frame text, which is exactly what the manual verification checklist covers instead.
/// </summary>
public class BrambleProgressRendererTests
{
    private static readonly Type RendererType =
        typeof(Program).Assembly.GetType("UnBramble.Cli.BrambleProgressRenderer")!;

    private static T Invoke<T>(string methodName, params object?[] args)
    {
        var method = RendererType.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!;
        return (T)method.Invoke(null, args)!;
    }

    [Fact]
    public void InterpolateColor_AtZero_IsBrambleBrown()
    {
        var (r, g, b) = Invoke<ValueTuple<byte, byte, byte>>("InterpolateColor", 0.0);

        Assert.Equal((146, 93, 49), (r, g, b));
    }

    [Fact]
    public void InterpolateColor_AtOne_IsLeafGreen()
    {
        var (r, g, b) = Invoke<ValueTuple<byte, byte, byte>>("InterpolateColor", 1.0);

        Assert.Equal((84, 190, 110), (r, g, b));
    }

    [Fact]
    public void InterpolateColor_ChannelsAreMonotoneAcrossTheGradient()
    {
        var samples = Enumerable.Range(0, 11)
            .Select(i => Invoke<ValueTuple<byte, byte, byte>>("InterpolateColor", i / 10.0))
            .ToList();

        // Red trends down (brown -> green has less red), green and blue trend up.
        for (var i = 1; i < samples.Count; i++)
        {
            Assert.True(samples[i].Item1 <= samples[i - 1].Item1, "red channel should be non-increasing");
            Assert.True(samples[i].Item2 >= samples[i - 1].Item2, "green channel should be non-decreasing");
            Assert.True(samples[i].Item3 >= samples[i - 1].Item3, "blue channel should be non-decreasing");
        }
    }

    [Fact]
    public void InterpolateColor_ClampsOutOfRangeInput()
    {
        var low = Invoke<ValueTuple<byte, byte, byte>>("InterpolateColor", -5.0);
        var high = Invoke<ValueTuple<byte, byte, byte>>("InterpolateColor", 5.0);

        Assert.Equal((146, 93, 49), (low.Item1, low.Item2, low.Item3));
        Assert.Equal((84, 190, 110), (high.Item1, high.Item2, high.Item3));
    }

    [Theory]
    [InlineData("a very long path that needs truncating/somewhere/in/the/middle.cs", 20)]
    [InlineData("short.cs", 20)]
    public void TruncateMiddle_NeverExceedsRequestedWidth(string text, int maxWidth)
    {
        var result = Invoke<string>("TruncateMiddle", text, maxWidth);

        Assert.True(result.Length <= Math.Max(maxWidth, text.Length <= maxWidth ? text.Length : maxWidth));
    }

    [Fact]
    public void TruncateMiddle_ShortTextPassesThroughUnchanged()
    {
        Assert.Equal("short.cs", Invoke<string>("TruncateMiddle", "short.cs", 20));
    }

    [Fact]
    public void TruncateMiddle_LongTextKeepsHeadAndTailWithEllipsisBetween()
    {
        var result = Invoke<string>("TruncateMiddle", "Assets/Very/Deep/Nested/Path/To/A/File.cs", 20);

        Assert.Equal(20, result.Length);
        Assert.Contains("...", result, StringComparison.Ordinal);
        Assert.StartsWith("Assets", result, StringComparison.Ordinal);
        Assert.EndsWith(".cs", result, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFrame_WideTerminal_IncludesStatusTextAndCurrentPathLine()
    {
        var frame = Invoke<object>("BuildFrame", 100, TimeSpan.FromSeconds(253), "scanning — 100 files / 20 dirs", "Assets/Foo.cs", 0, false);
        var frameType = frame.GetType();
        var line1 = (string)frameType.GetProperty("Line1")!.GetValue(frame)!;
        var line2 = (string?)frameType.GetProperty("Line2")!.GetValue(frame);

        // 253 seconds = 4 minutes 13 seconds.
        Assert.Contains("00:04:13", line1, StringComparison.Ordinal);
        Assert.Contains("Unbrambling...", line1, StringComparison.Ordinal);
        Assert.Contains(new string('^', 12), line1, StringComparison.Ordinal);

        // One color for the thorn run plus reset; no escape sequence per cell.
        var escapeCount = line1.Split("\x1b[", StringSplitOptions.None).Length - 1;
        Assert.Equal(2, escapeCount);

        Assert.NotNull(line2);
        Assert.Contains("scanning — 100 files / 20 dirs", line2, StringComparison.Ordinal);
        Assert.Contains("Foo.cs", line2, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFrame_NarrowTerminal_DropsLine2Entirely()
    {
        var frame = Invoke<object>("BuildFrame", 50, TimeSpan.FromSeconds(65), "scanning — 1 files / 1 dirs", "Assets/Foo.cs", 0, false);
        var frameType = frame.GetType();
        var line2 = (string?)frameType.GetProperty("Line2")!.GetValue(frame);

        Assert.Null(line2);
    }

    [Fact]
    public void BuildFrame_ElapsedPastAnHour_RendersHoursMinutesSecondsCorrectly()
    {
        var frame = Invoke<object>("BuildFrame", 100, TimeSpan.FromHours(1), "analyzing", null, 0, false);
        var frameType = frame.GetType();
        var line1 = (string)frameType.GetProperty("Line1")!.GetValue(frame)!;

        Assert.Contains("01:00:00", line1, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "^^^")]
    [InlineData(1, "~^^")]
    [InlineData(2, "~~^")]
    [InlineData(3, "~~~")]
    [InlineData(4, "^~~")]
    [InlineData(5, "^^~")]
    [InlineData(6, "^^^")]
    public void RenderUnbrambleIndicator_LoopsWithoutImplyingPercentComplete(int tick, string expected)
    {
        var rendered = Invoke<string>("RenderUnbrambleIndicator", 3, tick, false);

        Assert.Equal(expected, StripAnsi(rendered));
    }

    [Fact]
    public void AnsiForCell_TrueColor_EmitsTruecolorEscapeAtEndpoints()
    {
        var brown = Invoke<string>("AnsiForCell", 0.0, true);
        var green = Invoke<string>("AnsiForCell", 1.0, true);

        Assert.Equal("\x1b[38;2;146;93;49m", brown);
        Assert.Equal("\x1b[38;2;84;190;110m", green);
    }

    [Fact]
    public void AnsiForCell_Fallback_Emits256ColorEscape()
    {
        var code = Invoke<string>("AnsiForCell", 0.0, false);

        Assert.StartsWith("\x1b[38;5;", code, StringComparison.Ordinal);
    }

    private static string StripAnsi(string value)
    {
        var sb = new System.Text.StringBuilder();
        var inEscape = false;
        foreach (var c in value)
        {
            if (c == '\x1b')
            {
                inEscape = true;
            }
            else if (inEscape && c == 'm')
            {
                inEscape = false;
            }
            else if (!inEscape)
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
