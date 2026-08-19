using System.Globalization;
using UnBramble.Core.Freshness;
using UnBramble.Core.Monitoring;

namespace UnBramble.Cli;

/// <summary>
/// Renders `unbramble monitor`'s live, low-noise status view by polling
/// <see cref="WatchStatusFile"/> plus <see cref="HeartbeatFile"/> to tell whether the detached
/// worker still looks alive.
/// </summary>
internal static class WatchDashboard
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    // A `watch` process refreshes its heartbeat at least every WatcherOptions.Default.HeartbeatIdleCadence
    // (5s) even when fully idle. Well past 3x that with no update is a good "looks dead, not just
    // between ticks" signal for `monitor` -- generous on purpose (false "still alive" is harmless;
    // false "looks dead" while it's actually just slow is the more confusing failure mode to show
    // a user watching their own edit-save-check cycle).
    private static readonly TimeSpan HeartbeatStaleThreshold = TimeSpan.FromSeconds(20);

    /// <summary>`unbramble monitor [path]`: polls the detached worker's status file.</summary>
    public static void RunAttachedLoop(string projectRoot, ManualResetEventSlim stopSignal, TextWriter writer)
    {
        while (true)
        {
            var snapshot = WatchStatusFile.TryRead(projectRoot);
            var heartbeat = HeartbeatFile.TryRead(projectRoot);
            RenderFrame(writer, "unbramble monitor", projectRoot, snapshot, heartbeat, DateTime.UtcNow);

            if (stopSignal.Wait(PollInterval))
            {
                return;
            }
        }
    }

    private static void RenderFrame(TextWriter writer, string title, string projectRoot, WatchStatusSnapshot? snapshot, HeartbeatFile.Heartbeat? heartbeat, DateTime nowUtc)
    {
        try
        {
            Console.Clear();
        }
        catch (IOException)
        {
            // No real console (e.g. output redirected to a file/pipe) -- fall back to a plain
            // scrolling stream of frames rather than failing the whole command.
        }

        writer.Write(BuildFrame(title, projectRoot, snapshot, heartbeat, nowUtc));
        writer.Flush();
    }

    /// <summary>Pure string-building (no Console I/O) so it's directly unit-testable.</summary>
    internal static string BuildFrame(string title, string projectRoot, WatchStatusSnapshot? snapshot, HeartbeatFile.Heartbeat? heartbeat, DateTime nowUtc)
    {
        var w = new System.Text.StringBuilder();
        w.AppendLine($"{title} — {projectRoot}");

        if (snapshot is null)
        {
            var heartbeatNote = heartbeat is null
                ? "no watch status found yet."
                : $"no watch status file yet, but a heartbeat exists (pid {heartbeat.Value.Pid}, age {FormatAge(nowUtc - heartbeat.Value.UtcTimestamp)}) — it may be starting up.";
            w.AppendLine($"status: UNKNOWN — {heartbeatNote}");
            w.AppendLine();
            w.AppendLine("Starting the background watcher. Press Ctrl+C to close this monitor.");
            return w.ToString();
        }

        // "Looks alive": never flag stale while State is Processing -- work is demonstrably in
        // flight (the direct fix for the false "flagged stale while doing 114k edits" case).
        // Otherwise, base it on whichever of the freshness heartbeat and the status file's own
        // LastUpdatedUtc is fresher, rather than the heartbeat alone: during a cold start the
        // freshness heartbeat legitimately doesn't exist yet (WatcherHost.Promote writes it
        // last, deliberately -- see its own doc comment), but the status file now does (its own
        // initial-index state), and once idle, LastUpdatedUtc itself keeps advancing on its own
        // ~5s cadence (via the HeartbeatIdle event) instead of freezing. The in-process
        // The status file's own timestamp is useful during cold start, before the freshness
        // heartbeat exists; processing state itself is enough evidence that the worker is alive.
        var freshestSignal = heartbeat is { } hb && hb.UtcTimestamp > snapshot.LastUpdatedUtc
            ? hb.UtcTimestamp
            : snapshot.LastUpdatedUtc;
        var looksAlive = snapshot.State == WatchActivityState.Processing || (nowUtc - freshestSignal) < HeartbeatStaleThreshold;
        var aliveSuffix = heartbeat is not null && !looksAlive ? "  (heartbeat stale — this watcher may have exited)" : "";

        w.AppendLine($"pid {snapshot.Pid}   started {FormatAge(nowUtc - snapshot.StartedUtc)} ago   last update {FormatAge(nowUtc - snapshot.LastUpdatedUtc)} ago{aliveSuffix}");
        w.AppendLine();
        w.AppendLine(FormatStatusLine(snapshot, nowUtc));
        w.AppendLine();
        w.AppendLine("totals since start:");
        w.AppendLine($"  edits processed:     +{snapshot.Totals.FilesAdded} ~{snapshot.Totals.FilesChanged} -{snapshot.Totals.FilesRemoved}");
        w.AppendLine($"  compilation cache:   {snapshot.Totals.CacheIncrementalBuilds} incremental, {snapshot.Totals.CacheFullRebuilds} full-rebuild, {snapshot.Totals.CacheReusedNoOp} reused");
        w.AppendLine($"  batches applied:     {snapshot.Totals.BatchesApplied}");
        w.AppendLine($"  self-heal sweeps:    {snapshot.Totals.SelfHealSweeps} ({snapshot.Totals.SelfHealSweepsSkipped} skipped as no-op)");
        w.AppendLine($"  error resyncs:       {snapshot.Totals.ErrorResyncs}");
        w.AppendLine();

        if (snapshot.LastPass is { } lastPass)
        {
            w.AppendLine($"last completed pass ({lastPass.Kind}, {FormatAge(nowUtc - lastPass.CompletedUtc)} ago):");
            w.AppendLine($"  total {FormatMs(lastPass.TotalMs)}   " + string.Join(" ", lastPass.Steps.Where(s => s.Label != "total").Select(s => $"{s.Label}={FormatMs(s.Ms)}")));
            if (lastPass.Added is not null || lastPass.Changed is not null || lastPass.Removed is not null)
            {
                w.AppendLine($"  +{lastPass.Added ?? 0} ~{lastPass.Changed ?? 0} -{lastPass.Removed ?? 0} files");
            }
        }
        else
        {
            w.AppendLine("last completed pass: (none yet)");
        }

        w.AppendLine();
        w.AppendLine(AnsiStyle.InlineCommands(
            "Press Ctrl+C to close this monitor. Run `unbramble stop` to stop the watcher.",
            ConsoleCapabilities.SupportsAnsi));
        return w.ToString();
    }

    private static string FormatStatusLine(WatchStatusSnapshot snapshot, DateTime nowUtc)
    {
        switch (snapshot.State)
        {
            case WatchActivityState.Waiting:
                return "status: WAITING — another watcher process is already active for this project.";
            case WatchActivityState.Stopped:
                return "status: STOPPED.";
            case WatchActivityState.Processing when snapshot.Current is { } current:
                var elapsed = FormatMs((nowUtc - current.StartedUtc).TotalMilliseconds);
                var step = current.Unit is not null
                    ? $"  |  {current.Unit}: {current.Phase} ({current.Detail})"
                    : current.Phase is not null ? $"  |  {current.Phase}: {current.Detail}" : "";
                return $"status: PROCESSING {current.Kind} — {elapsed} elapsed{step}";
            case WatchActivityState.Processing:
                return "status: PROCESSING";
            default:
                return "status: WATCHING (idle)";
        }
    }

    private static string FormatMs(double ms) =>
        ms >= 1000 ? (ms / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + "s" : ms.ToString("0", CultureInfo.InvariantCulture) + "ms";

    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        return age.TotalMinutes >= 1
            ? $"{(int)age.TotalMinutes}m{age.Seconds:D2}s"
            : $"{age.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s";
    }
}
