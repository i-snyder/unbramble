using System.Reflection;
using UnBramble.Cli;

namespace UnBramble.Tests;

/// <summary>
/// The hedgerow palette's contract. Reflection-driven for the same reason as
/// <see cref="BrambleProgressRendererTests"/>: `AnsiStyle` is `internal` and this test project
/// has no InternalsVisibleTo grant on UnBramble.Cli.
///
/// The point of these is the two invariants a palette can silently lose: every role stays
/// visually distinct from every other (a warning that renders as a label is worse than no color),
/// and ANSI-off stays a byte-for-byte passthrough (the whole redirected/NO_COLOR contract).
/// </summary>
public class AnsiStyleTests
{
    private static readonly Type StyleType = typeof(Program).Assembly.GetType("UnBramble.Cli.AnsiStyle")!;
    private static readonly Type PaletteType = typeof(Program).Assembly.GetType("UnBramble.Cli.AnsiStyle+Palette")!;

    private static readonly string[] RoleNames = ["Alarm", "Caution", "Finding", "Command", "Label", "Notice", "Alive", "Muted"];
    private static readonly string[] PigmentNames = ["Leaf", "Bark", "Hazel", "Amber", "Rosehip", "Blackberry", "Bluebell", "Bracken"];

    private static string Role(string name, string text, bool ansiEnabled) =>
        (string)StyleType.GetMethod(name, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [text, ansiEnabled])!;

    private static object Pigment(string name) =>
        PaletteType.GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static string Sgr(object rgb, bool bold, bool trueColor) =>
        (string)StyleType.GetMethod("Sgr", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [rgb, bold, trueColor])!;

    private static int Xterm256(object rgb) =>
        (int)rgb.GetType().GetProperty("Xterm256")!.GetValue(rgb)!;

    [Theory]
    [InlineData("Alarm")]
    [InlineData("Caution")]
    [InlineData("Finding")]
    [InlineData("Command")]
    [InlineData("Label")]
    [InlineData("Notice")]
    [InlineData("Alive")]
    [InlineData("Muted")]
    public void EveryRole_AnsiDisabled_IsAPlainPassthrough(string role)
    {
        // The redirected/NO_COLOR/dumb-terminal contract: not "mostly plain", exactly the input.
        Assert.Equal("looks orphaned: ", Role(role, "looks orphaned: ", false));
    }

    [Theory]
    [InlineData("Alarm")]
    [InlineData("Caution")]
    [InlineData("Finding")]
    [InlineData("Command")]
    [InlineData("Label")]
    [InlineData("Notice")]
    [InlineData("Alive")]
    [InlineData("Muted")]
    public void EveryRole_AnsiEnabled_WrapsTheTextAndResets(string role)
    {
        var painted = Role(role, "text", true);

        Assert.StartsWith("\x1b[", painted, StringComparison.Ordinal);
        Assert.EndsWith("\x1b[0m", painted, StringComparison.Ordinal);
        Assert.Contains("text", painted, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRole_RendersDistinctlyFromEveryOtherRole()
    {
        // A palette whose roles collide is worse than no palette: the reader learns to trust a
        // color that isn't carrying the meaning they think it is.
        var rendered = RoleNames.ToDictionary(r => r, r => Role(r, "x", true));

        Assert.Equal(RoleNames.Length, rendered.Values.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryPigment_HasADistinctXterm256Fallback()
    {
        // The 256 fallback is where roles are most likely to collapse into each other -- the cube
        // is coarse, and nearest-RGB genuinely does land Amber one green step from Hazel.
        var indices = PigmentNames.Select(p => Xterm256(Pigment(p))).ToList();

        Assert.Equal(indices.Count, indices.Distinct().Count());
    }

    [Fact]
    public void EveryPigment_FallsBackInsideTheThemeableFreeRegionOfXterm256()
    {
        // 0-15 are remapped by the user's terminal theme -- a pigment landing there could render
        // as any color at all, which is the exact dependency this palette exists to escape.
        // 232-255 is the grayscale ramp: no hedgerow pigment should quantize to grey.
        foreach (var name in PigmentNames)
        {
            var index = Xterm256(Pigment(name));
            Assert.True(index is >= 16 and <= 231, $"{name} falls back to xterm {index}, outside the 6x6x6 color cube");
        }
    }

    [Fact]
    public void Sgr_TrueColor_EmitsTheExactRgbPigment()
    {
        // Leaf is the shared canopy anchor -- BrambleProgressRendererTests pins the same numbers.
        Assert.Equal("\x1b[38;2;84;190;110m", Sgr(Pigment("Leaf"), bold: false, trueColor: true));
        Assert.Equal("\x1b[1;38;2;84;190;110m", Sgr(Pigment("Leaf"), bold: true, trueColor: true));
    }

    [Fact]
    public void Sgr_WithoutTrueColor_EmitsTheHandPickedFallbackInOneSequence()
    {
        Assert.Equal("\x1b[38;5;71m", Sgr(Pigment("Leaf"), bold: false, trueColor: false));
        Assert.Equal("\x1b[1;38;5;71m", Sgr(Pigment("Leaf"), bold: true, trueColor: false));
    }

    [Fact]
    public void YesNoPrompt_AnsiDisabled_IsThePlainBracketConvention()
    {
        // Byte-identical to the literal every prompt used before the helper existed -- the
        // Defender consent test asserts on that line, and a script reading it must see no escapes.
        Assert.Equal("[Y/n]", YesNoPrompt(false));
    }

    [Fact]
    public void YesNoPrompt_AnsiEnabled_ColorsOnlyTheDefaultAnswer()
    {
        var prompt = YesNoPrompt(true);

        // The capital Y is the payload: it must render as Alive, distinctly from the brackets
        // around it, or the prompt is just decorated rather than informative.
        Assert.Contains(Role("Alive", "Y", true), prompt, StringComparison.Ordinal);
        Assert.Contains(Role("Muted", "[", true), prompt, StringComparison.Ordinal);
        Assert.NotEqual(Role("Muted", "Y", true), Role("Alive", "Y", true));
    }

    [Fact]
    public void NoYesPrompt_AnsiDisabled_IsThePlainBracketConvention()
    {
        Assert.Equal("[y/N]", NoYesPrompt(false));
    }

    [Fact]
    public void NoYesPrompt_AnsiEnabled_ColorsOnlyTheDefaultAnswer()
    {
        var prompt = NoYesPrompt(true);

        Assert.Contains(Role("Alive", "N", true), prompt, StringComparison.Ordinal);
        Assert.Contains(Role("Muted", "[y/", true), prompt, StringComparison.Ordinal);
        Assert.NotEqual(Role("Muted", "N", true), Role("Alive", "N", true));
    }

    [Fact]
    public void InlineCommands_AnsiEnabled_ColorsCommandButNotItsQuotes()
    {
        var method = StyleType.GetMethod("InlineCommands", BindingFlags.Public | BindingFlags.Static)!;
        var output = (string)method.Invoke(null, ["The project's ready. Undo with 'unbramble defender remove'.", true])!;

        Assert.Contains("'" + Role("Command", "unbramble defender remove", true) + "'", output, StringComparison.Ordinal);
        Assert.StartsWith("The project's ready. Undo with '", output, StringComparison.Ordinal);
    }

    [Fact]
    public void InlineCommands_AnsiDisabled_IsAPlainPassthrough()
    {
        var method = StyleType.GetMethod("InlineCommands", BindingFlags.Public | BindingFlags.Static)!;
        var input = "Run `unbramble stats` next.";

        Assert.Equal(input, (string)method.Invoke(null, [input, false])!);
    }

    [Fact]
    public void CommandBlock_ColorsIndentedUsageButNotTheProductHeading()
    {
        var method = StyleType.GetMethod("CommandBlock", BindingFlags.Public | BindingFlags.Static)!;
        var output = (string)method.Invoke(null, ["unbramble - heading\n  unbramble stats", true])!;

        Assert.StartsWith("unbramble - heading", output, StringComparison.Ordinal);
        Assert.EndsWith("  " + Role("Command", "unbramble stats", true), output, StringComparison.Ordinal);
    }

    private static string YesNoPrompt(bool ansiEnabled) =>
        (string)StyleType.GetMethod("YesNoPrompt", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [ansiEnabled])!;

    private static string NoYesPrompt(bool ansiEnabled) =>
        (string)StyleType.GetMethod("NoYesPrompt", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [ansiEnabled])!;

    /// <summary>
    /// Bark's fallback is the one that must never be "fixed" back to a computed nearest match:
    /// nearest-RGB picks xterm 95 = (135,95,95), a dusty rose, because blue 49 is arithmetically
    /// closer to 95 than to 0 and lifting it desaturates the brown out of existence.
    /// </summary>
    [Fact]
    public void Bark_FallsBackToARealBrown_NotTheNearestRgbMatch()
    {
        Assert.Equal(130, Xterm256(Pigment("Bark")));
        Assert.NotEqual(95, Xterm256(Pigment("Bark")));
    }
}
