using System.Runtime.CompilerServices;

namespace UnBramble.Tests.TestSupport;

/// <summary>
/// Nearly every CLI-level test in this project runs a query verb (who-uses/uses/resolve/stats/
/// cs-refs) via <see cref="CliRunner"/> against a fixture copy with no live watcher heartbeat --
/// which, after the auto-spawn feature (docs/architecture.md "Auto-spawn watcher"), is exactly
/// the condition that makes <c>Program.MaybeAutoSpawnWatch</c> attempt to spawn a real detached
/// detached watcher OS process. Left unguarded, running this test suite would spawn one
/// (possibly many, since each fixture copy has its own cooldown-marker-free `.unbramble/`)
/// per test. This module initializer sets the one environment variable
/// <c>Program.MaybeAutoSpawnWatch</c> checks to skip the actual <c>Process.Start</c> call, for
/// the whole lifetime of this test process -- it runs once, before any test, with no per-test
/// setup required. The DECISION logic upstream of that check (<see
/// cref="UnBramble.Core.Freshness.AutoSpawnPolicy"/>) is still exercised normally by tests that
/// target it directly.
/// </summary>
internal static class AutoSpawnTestGuard
{
    [ModuleInitializer]
    internal static void DisableRealProcessSpawn() =>
        Environment.SetEnvironmentVariable("UNBRAMBLE_DISABLE_AUTO_SPAWN", "1");
}
