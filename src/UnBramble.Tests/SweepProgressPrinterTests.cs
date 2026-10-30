using System.Reflection;
using UnBramble.Cli;
using UnBramble.Core.Scanning;

namespace UnBramble.Tests;

/// <summary>
/// The plain progress printer's contract (a minutes-long cold init
/// read as "done or stalled?"): silent inside the quiet window, and every line printed past it
/// carries an elapsed stamp. A manual clock exercises real phase/keepalive transitions without
/// sleeping through the quiet window or the 15s keepalive cadence.
/// SweepProgressPrinter is internal, so this goes through reflection like HomeCommandTests does.
/// </summary>
public class SweepProgressPrinterTests
{
    [Fact]
    public void InsideQuietWindow_PhaseLinesStaySilent()
    {
        using var progress = new ProgressHarness("header line");
        progress.OnPhase("running C# analysis");
        progress.Tick();
        Assert.Equal("", progress.Output.ToString());
    }

    [Fact]
    public void PastQuietWindow_PhaseLineCarriesHeaderAndElapsedStamp()
    {
        using var progress = new ProgressHarness("header line");
        progress.Clock.Advance(TimeSpan.FromSeconds(2));
        progress.OnPhase("running C# analysis");

        var text = progress.Output.ToString();
        Assert.StartsWith("header line", text);
        Assert.Contains("sweep: running C# analysis (", text);
        Assert.Contains("00:02 elapsed)", text);
    }

    [Theory]
    [InlineData("index: another unbramble process is updating the database -- waiting for its committed result...")]
    [InlineData("index: another unbramble process is updating the database -- waiting for its committed result (use 'unbramble monitor' to inspect a watcher, or 'unbramble stop' if the owner is stuck)")]
    [InlineData("index: waiting for active queries to finish before updating the database")]
    [InlineData("query: an index update began before the read snapshot was secured -- waiting for its committed result")]
    [InlineData("freshness: another process already owns the index (likely a watcher's first sweep) -- waiting for it instead of racing a duplicate sweep")]
    public void Keepalive_DuringLockWait_ReportsElapsedWaitingWithoutClaimingProgress(string phase)
    {
        using var progress = new ProgressHarness("header line");
        progress.OnPhase(phase); // Remember waits even when their initial line is quiet.
        progress.Clock.Advance(TimeSpan.FromSeconds(15));
        progress.Tick();
        progress.Clock.Advance(TimeSpan.FromSeconds(15));
        progress.Tick();

        var text = progress.Output.ToString();
        Assert.Contains("sweep: waiting -- " + phase + " (00:15 elapsed)", text);
        Assert.Contains("sweep: waiting -- " + phase + " (00:30 elapsed)", text);
        Assert.DoesNotContain("not stalled", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("still scanning", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, text.Split("header line", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Keepalive_AfterWaitEnds_ReportsCurrentIndexingPhase()
    {
        using var progress = new ProgressHarness(header: null);
        progress.OnPhase("index: waiting for active queries to finish before updating the database");
        progress.OnPhase("scan complete (12 entries); applying inventory diff");
        progress.Clock.Advance(TimeSpan.FromSeconds(15));
        progress.Tick();

        Assert.Equal("sweep: still applying inventory diff (00:15 elapsed)" + Environment.NewLine, progress.Output.ToString());
    }

    [Fact]
    public void ScanProgress_AfterLockWait_RestoresScanningKeepalive()
    {
        using var progress = new ProgressHarness(header: null);
        progress.OnPhase("index: waiting for active queries to finish before updating the database");
        progress.OnScanProgress(new ScanProgress(3, 12, "Assets"));
        progress.Clock.Advance(TimeSpan.FromSeconds(15));
        progress.Tick();

        Assert.Equal("sweep: still scanning (00:15 elapsed)" + Environment.NewLine, progress.Output.ToString());
    }

    [Fact]
    public void Keepalive_RespectsSilenceCadenceAndStopsAfterDispose()
    {
        using var progress = new ProgressHarness(header: null);
        progress.OnPhase("running C# analysis");
        progress.Clock.Advance(TimeSpan.FromSeconds(15));
        progress.Tick();
        var firstLine = progress.Output.ToString();
        progress.Clock.Advance(TimeSpan.FromSeconds(14));
        progress.Tick();
        Assert.Equal(firstLine, progress.Output.ToString());

        progress.Dispose();
        progress.Clock.Advance(TimeSpan.FromSeconds(30));
        progress.Tick();
        Assert.Equal(firstLine, progress.Output.ToString());
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
    }

    private sealed class ProgressHarness : IDisposable
    {
        private readonly IDisposable _printer;
        public ManualClock Clock { get; } = new();
        public StringWriter Output { get; } = new();
        public Action<string> OnPhase { get; }
        public Action<ScanProgress> OnScanProgress { get; }
        public Action Tick { get; }

        public ProgressHarness(string? header)
        {
            var type = typeof(Program).Assembly.GetType("UnBramble.Cli.SweepProgressPrinter")!;
            _printer = (IDisposable)Activator.CreateInstance(type, [header, Output, Clock])!;
            OnPhase = (Action<string>)Delegate.CreateDelegate(typeof(Action<string>), _printer, type.GetMethod("OnPhase", BindingFlags.Public | BindingFlags.Instance)!);
            OnScanProgress = (Action<ScanProgress>)Delegate.CreateDelegate(typeof(Action<ScanProgress>), _printer, type.GetMethod("OnScanProgress", BindingFlags.Public | BindingFlags.Instance)!);
            Tick = (Action)Delegate.CreateDelegate(typeof(Action), _printer, type.GetMethod("OnKeepaliveTick", BindingFlags.NonPublic | BindingFlags.Instance)!);
        }

        public void Dispose() => _printer.Dispose();
    }
}
