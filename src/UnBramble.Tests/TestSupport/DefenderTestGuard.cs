using System.Runtime.CompilerServices;

namespace UnBramble.Tests.TestSupport;

/// <summary>
/// Mirrors <see cref="AutoSpawnTestGuard"/> exactly, for the same reason: nearly every CLI-level
/// test calls `init` via <see cref="CliRunner"/>, which -- after the Windows-Defender-exclusion
/// setup feature -- runs an eligibility check using REAL seams
/// (<c>DefenderExclusionSetup.Dependencies.CreateReal()</c>) that shell out to real
/// `powershell.exe`/`fsutil.exe` and read the real registry. Read-only and harmless, but it would
/// otherwise fire on every single one of those tests, slowing the whole suite down. This module
/// initializer sets the one environment variable <c>Program.ExecuteInitCore</c> checks to skip
/// that call entirely, for the whole lifetime of this test process. The decision logic itself
/// (<see cref="UnBramble.Cli.Defender.DefenderExclusionSetup"/>) is still exercised directly, with
/// fake seams, by <c>DefenderExclusionSetupTests</c> -- that suite calls it straight, never
/// through <see cref="CliRunner"/>/<c>Program</c>, so this guard never affects its coverage.
/// </summary>
internal static class DefenderTestGuard
{
    [ModuleInitializer]
    internal static void DisableRealDefenderCheck() =>
        Environment.SetEnvironmentVariable("UNBRAMBLE_DISABLE_DEFENDER_SETUP", "1");
}
