namespace UnBramble.Cli;

public enum ConfirmationResult
{
    Accepted,
    Declined,
    Unavailable,
}

/// <summary>One explicit, default-no confirmation convention for destructive uninstall paths.
/// The environment is injectable because redirected test output is deliberately non-interactive.</summary>
public static class UninstallConfirmation
{
    public readonly record struct Environment(
        bool IsInteractive,
        bool SupportsAnsi,
        Func<string?> ReadLine,
        Action<string> WriteLine,
        Action<string> WriteError);

    public static ConfirmationResult Ask(bool assumeYes, Environment env)
    {
        if (assumeYes)
        {
            env.WriteLine("Confirmation accepted (-y/--yes).");
            return ConfirmationResult.Accepted;
        }

        if (!env.IsInteractive)
        {
            env.WriteError("uninstall requires confirmation, but no interactive terminal is available; review the plan above and rerun with --yes.");
            return ConfirmationResult.Unavailable;
        }

        env.WriteLine("Continue? " + AnsiStyle.NoYesPrompt(env.SupportsAnsi));
        var answer = env.ReadLine()?.Trim();
        if (answer is not null
            && (answer.Equals("y", StringComparison.OrdinalIgnoreCase)
                || answer.Equals("yes", StringComparison.OrdinalIgnoreCase)))
        {
            return ConfirmationResult.Accepted;
        }

        env.WriteLine("Cancelled. Nothing changed.");
        return ConfirmationResult.Declined;
    }
}
