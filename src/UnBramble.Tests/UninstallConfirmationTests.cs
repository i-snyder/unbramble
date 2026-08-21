using UnBramble.Cli;

namespace UnBramble.Tests;

public class UninstallConfirmationTests
{
    [Theory]
    [InlineData("y")]
    [InlineData("YES")]
    public void InteractivePrompt_AcceptsOnlyExplicitYesForms(string answer)
    {
        var lines = new List<string>();
        var result = UninstallConfirmation.Ask(
            assumeYes: false,
            new UninstallConfirmation.Environment(true, false, () => answer, lines.Add, lines.Add));

        Assert.Equal(ConfirmationResult.Accepted, result);
        Assert.Contains(lines, line => line.Contains("[y/N]", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("n")]
    [InlineData("maybe")]
    public void InteractivePrompt_DefaultsToNo(string answer)
    {
        var lines = new List<string>();
        var result = UninstallConfirmation.Ask(
            assumeYes: false,
            new UninstallConfirmation.Environment(true, false, () => answer, lines.Add, lines.Add));

        Assert.Equal(ConfirmationResult.Declined, result);
        Assert.Contains("Cancelled. Nothing changed.", lines);
    }

    [Fact]
    public void YesFlag_BypassesInputButStillAnnouncesConfirmation()
    {
        var lines = new List<string>();
        var read = false;
        var result = UninstallConfirmation.Ask(
            assumeYes: true,
            new UninstallConfirmation.Environment(true, false, () => { read = true; return "n"; }, lines.Add, lines.Add));

        Assert.Equal(ConfirmationResult.Accepted, result);
        Assert.False(read);
        Assert.Contains("Confirmation accepted (-y/--yes).", lines);
    }

    [Fact]
    public void NonInteractivePrompt_ReturnsUnavailableWithYesRemediation()
    {
        var errors = new List<string>();
        var result = UninstallConfirmation.Ask(
            assumeYes: false,
            new UninstallConfirmation.Environment(false, false, () => throw new InvalidOperationException(), _ => { }, errors.Add));

        Assert.Equal(ConfirmationResult.Unavailable, result);
        Assert.Single(errors);
        Assert.Contains("rerun with --yes", errors[0]);
    }
}
