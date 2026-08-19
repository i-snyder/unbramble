namespace UnBramble.Core.Exceptions;

/// <summary>
/// Base type for expected, user-facing failures (environment/usage problems) that a CLI
/// front-end should report cleanly (exit code 1) rather than treat as a bug. Core owns the
/// message text; UnBramble.Cli owns presentation (e.g. prefixing "error: ").
/// </summary>
public abstract class UnBrambleException : Exception
{
    protected UnBrambleException(string message) : base(message)
    {
    }
}
