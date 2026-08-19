using UnBramble.Cli;

namespace UnBramble.Tests.TestSupport;

/// <summary>
/// Invokes UnBramble.Cli's Program.Main in-process (rather than shelling out to the published
/// exe), capturing stdout/stderr and the exit code. Console.Out/Error are process-global, so
/// calls are serialized with a lock to stay correct if xUnit runs test classes in parallel.
/// </summary>
public static class CliRunner
{
    public static (int ExitCode, string StdOut, string StdErr) Run(params string[] args) =>
        Run(beforeRun: null, args);

    /// <summary>
    /// Runs <paramref name="beforeRun"/> inside the console lock, immediately before Main —
    /// for arranging state whose validity decays with wall-clock time, the clearest case being
    /// a *fresh* watcher heartbeat (<c>HeartbeatFile.Write(..., DateTime.UtcNow)</c>, fresh for
    /// only 15s per <c>HeartbeatFreshness.DefaultStaleThreshold</c>).
    ///
    /// Arranging that before the call instead is a real, recurring flake, not a theoretical one:
    /// the gate is contended by every CLI test in the suite and .NET's <c>lock</c> is not FIFO,
    /// so an arbitrary number of other test classes' CLI runs can go first. The heartbeat then
    /// ages past its threshold while this call is still queued, and the CLI reads a stale one —
    /// the test fails having never exercised the branch it names. Timing-dependent, so it only
    /// bites under parallel load and passes on a re-run or in isolation.
    ///
    /// Inside the lock the gap shrinks to Main's own startup (milliseconds against a 15s
    /// threshold), and — the part that actually matters — nothing else can be scheduled into it,
    /// because holding the gate is exactly what stops other CLI runs from interleaving.
    /// </summary>
    public static (int ExitCode, string StdOut, string StdErr) Run(Action? beforeRun, params string[] args)
    {
        lock (ConsoleTestLock.Gate)
        {
            beforeRun?.Invoke();

            var originalOut = Console.Out;
            var originalErr = Console.Error;
            using var stdOut = new StringWriter();
            using var stdErr = new StringWriter();
            Console.SetOut(stdOut);
            Console.SetError(stdErr);

            int exitCode;
            try
            {
                exitCode = Program.Main(args);
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalErr);
            }

            return (exitCode, stdOut.ToString(), stdErr.ToString());
        }
    }
}
