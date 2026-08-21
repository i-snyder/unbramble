using System.Runtime.CompilerServices;

namespace UnBramble.Tests.TestSupport;

/// <summary>
/// <c>unbramble uninstall</c> deliberately stops every live process with its executable name.
/// CliRunner hosts the command inside the shared testhost process, so exercising project cleanup
/// there must skip only that real process-enumeration step. StopCommandTests cover the real
/// system-wide behavior through uniquely named executable copies.
/// </summary>
internal static class ProcessStopTestGuard
{
    [ModuleInitializer]
    internal static void DisableRealProcessStop() =>
        Environment.SetEnvironmentVariable("UNBRAMBLE_DISABLE_PROCESS_STOP", "1");
}
