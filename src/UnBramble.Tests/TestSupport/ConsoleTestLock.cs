namespace UnBramble.Tests.TestSupport;

/// <summary>
/// <see cref="Console.Out"/>/<see cref="Console.Error"/> are process-global, and xUnit runs
/// different test classes (different collections) in parallel by default -- so any two tests
/// that redirect them (whether through <see cref="CliRunner"/> or a reflection-based harness
/// like HomeCommandTests') must serialize on the SAME lock object, or one test's captured output
/// can bleed into another's. Shared here rather than each call site keeping its own private lock.
///
/// The same rule applies to WRITING, not just redirecting: a test that never swaps the console
/// must still never write to it (no <c>Console.WriteLine</c> as a callback sink -- use a
/// recorder lambda), because whatever it writes lands in whichever OTHER test's captured writer
/// happens to be installed at that instant. Found live: a Defender test passing
/// <c>Console.WriteLine</c> as its announce callback intermittently deposited "Windows Defender
/// setup..." lines at the head of unrelated `--json` tests' captured stdout under full-suite
/// parallel load, breaking their JSON parse maybe twice per dozen runs.
/// </summary>
internal static class ConsoleTestLock
{
    public static readonly object Gate = new();
}
