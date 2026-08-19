using System.Diagnostics;
using System.Globalization;
using UnBramble.Core.Scanning;

namespace UnBramble.Cli;

/// <summary>
/// Live reporting for a sweep that turns out to be slow. Found live against a large real
/// project: a COLD sweep there spends minutes in the scan
/// phase at near-zero CPU (I/O-bound serial walk -- measured a ~469s baseline), followed by
/// more minutes of reparse/C#-analysis, all previously with zero output of any kind on a query
/// verb. That is observably indistinguishable from a deadlock, and the process was in fact
/// killed as one.
///
/// Output stream is caller-chosen (defaults to stderr): query verbs in TEXT mode pass stdout so
/// the whole invocation is ONE ordered stream -- an agent harness merges the two pipes with no
/// cross-stream ordering guarantee, so progress written to stderr before the results could
/// display after them. `--json` runs keep the stderr
/// default: stdout's JSON contract stays pure.
///
/// Stays completely silent for fast sweeps: nothing prints until the sweep has been running
/// longer than <see cref="QuietWindow"/> (fixture-sized projects and warm no-change sweeps
/// never cross it), so existing expectations for quick runs are unchanged in
/// practice. Once past the window, prints one context header (when the caller supplied
/// one), then scan progress at most every <see cref="ProgressCadence"/>, plus each
/// post-scan phase-boundary label -- every line stamped with elapsed time. A keepalive timer
/// additionally prints "still &lt;doing the current phase&gt;" whenever nothing else has printed
/// for <see cref="KeepaliveSilence"/>: the post-scan phases (diff apply, reparse, C# analysis)
/// emit no per-item progress and can run for minutes, which on the first real cold index read
/// as "done or stalled?" -- exactly the ambiguity this class exists to prevent. Dispose after
/// the engine call returns to stop the keepalive.
///
/// Thread-safety: scan progress arrives on a timer thread (see <c>ScanHeartbeat</c>), phase
/// lines on the sweeping thread, keepalives on this class's own timer -- one gate serializes
/// all three.
///
/// Also the plain-text fallback for `init`/`index`/the zero-args home command whenever the
/// terminal doesn't support ANSI rendering -- see <see cref="BrambleProgressRenderer"/> for the
/// decorated alternative. Both are fed by the exact same <c>RunIndex(onScanProgress, onPhase)</c>
/// callbacks, so there is one progress data pipeline regardless of which renders it.
/// </summary>
internal sealed class SweepProgressPrinter : IDisposable
{
    private static readonly TimeSpan QuietWindow = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan ProgressCadence = TimeSpan.FromSeconds(10);

    /// <summary>Longer than <see cref="ProgressCadence"/> on purpose: while scan lines are
    /// flowing every ~10s the keepalive never fires; it only speaks for the phases that print
    /// nothing on their own.</summary>
    private static readonly TimeSpan KeepaliveSilence = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private readonly Stopwatch _sinceConstruction = Stopwatch.StartNew();
    private readonly TextWriter _writer;
    private readonly Timer _keepaliveTimer;
    private string? _pendingHeader;
    private string _currentActivity = "scanning";
    private long _lastLineTicks;
    private bool _disposed;

    public SweepProgressPrinter(string? header, TextWriter? writer = null)
    {
        _pendingHeader = header;
        _writer = writer ?? Console.Error;
        _keepaliveTimer = new Timer(_ => OnKeepaliveTick(), null, KeepaliveSilence, KeepaliveSilence);
    }

    public void OnScanProgress(ScanProgress progress)
    {
        lock (_gate)
        {
            if (_disposed || _sinceConstruction.Elapsed < QuietWindow)
            {
                return;
            }

            var elapsed = _sinceConstruction.Elapsed;
            if (_lastLineTicks != 0 && elapsed.Ticks - _lastLineTicks < ProgressCadence.Ticks)
            {
                return;
            }

            PrintLine($"sweep: scanning -- {progress.FilesSeen:N0} files / {progress.DirsVisited:N0} dirs so far ({FormatElapsed(elapsed)} elapsed)");
        }
    }

    public void OnPhase(string label)
    {
        lock (_gate)
        {
            // The activity is remembered even inside the quiet window: a fast scan followed by a
            // slow silent phase must have the RIGHT label ready when the keepalive first speaks.
            _currentActivity = ExtractActivity(label);

            if (_disposed || _sinceConstruction.Elapsed < QuietWindow)
            {
                return;
            }

            PrintLine($"sweep: {label} ({FormatElapsed(_sinceConstruction.Elapsed)} elapsed)");
        }
    }

    /// <summary>Speaks only when every other source has been silent for
    /// <see cref="KeepaliveSilence"/> -- the "is it stalled?" reassurance for phases that emit
    /// no per-item progress of their own.</summary>
    private void OnKeepaliveTick()
    {
        lock (_gate)
        {
            if (_disposed || _sinceConstruction.Elapsed < QuietWindow)
            {
                return;
            }

            var elapsed = _sinceConstruction.Elapsed;
            if (_lastLineTicks != 0 && elapsed.Ticks - _lastLineTicks < KeepaliveSilence.Ticks)
            {
                return;
            }

            PrintLine($"sweep: still {_currentActivity} -- not stalled ({FormatElapsed(elapsed)} elapsed)");
        }
    }

    /// <summary>Stops the keepalive. Call as soon as the engine call being reported on has
    /// returned -- a keepalive line after the real output would recreate the trailing-garbage
    /// problem the stream routing fixed.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        _keepaliveTimer.Dispose();
    }

    /// <summary>The "what are we doing right now" clause of a phase label, for keepalive lines:
    /// a compound label ("scan complete (…); applying inventory diff") contributes its last
    /// clause ("applying inventory diff"), so "still applying inventory diff" reads naturally.</summary>
    private static string ExtractActivity(string label)
    {
        var idx = label.LastIndexOf("; ", StringComparison.Ordinal);
        var activity = idx >= 0 ? label[(idx + 2)..] : label;
        return activity.Length > 0 ? activity : "working";
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : elapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    private void PrintLine(string text)
    {
        if (_pendingHeader is { } header)
        {
            _pendingHeader = null;
            _writer.WriteLine(header);
        }

        _writer.WriteLine(text);
        _lastLineTicks = _sinceConstruction.Elapsed.Ticks;
    }
}
