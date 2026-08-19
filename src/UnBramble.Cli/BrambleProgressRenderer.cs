using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnBramble.Core.Scanning;

namespace UnBramble.Cli;

/// <summary>
/// The decorated, ANSI-rendered progress experience for the zero-args home command's Case C
/// (first-time indexing) -- see <see cref="HomeCommand"/>. Selected only when
/// <see cref="ConsoleCapabilities.SupportsAnsi"/>; every other terminal gets the plain
/// <see cref="SweepProgressPrinter"/> stderr lines instead, fed by the exact same
/// <c>RunIndex(onScanProgress, onPhase)</c> callbacks -- one progress data pipeline, two
/// renderings.
///
/// The activity indicator tells the product story without pretending the scanner knows a total:
/// a thorny run of carets unbrambles into tildes, then grows thorny again and loops until the
/// actual indexing work completes.
/// </summary>
internal sealed class BrambleProgressRenderer : IDisposable
{
    // ============================================================================================
    // VISUAL DESIGN -- tune freely. Nothing in the state machine below this block needs to change
    // to reskin the thorn-to-vine indicator.
    // ============================================================================================

    /// <summary>Below this terminal width the second (current-path) line is dropped entirely
    /// rather than rendered blank.</summary>
    private const int Line2MinTerminalWidth = 60;

    private const int DefaultTerminalWidth = 80;
    private const int MaxIndicatorWidth = 12;
    private const int MediumIndicatorWidth = 8;
    private const int MinIndicatorWidth = 3;

    /// <summary>Bramble brown -- the thorny/start anchor of the transformation.</summary>
    private static readonly (byte R, byte G, byte B) BarStartColor =
        (AnsiStyle.Palette.Bark.R, AnsiStyle.Palette.Bark.G, AnsiStyle.Palette.Bark.B);

    /// <summary>Leaf green -- the smooth-vine/end anchor of the transformation.</summary>
    private static readonly (byte R, byte G, byte B) BarEndColor =
        (AnsiStyle.Palette.Leaf.R, AnsiStyle.Palette.Leaf.G, AnsiStyle.Palette.Leaf.B);

    /// <summary>256-color fallback ramp (brown -> green) for terminals that support ANSI but not
    /// 24-bit truecolor -- picked by nearest bucket, see <see cref="AnsiForCell"/>.</summary>
    private static readonly int[] Ansi256BrownToGreenRamp = [130, 136, 142, 106, 71, 34];

    private static readonly TimeSpan RedrawInterval = TimeSpan.FromSeconds(1);

    // ============================================================================================
    // STATE MACHINE / PLUMBING -- shouldn't need to change for a visual redesign.
    // ============================================================================================

    private const string HideCursorCode = "\x1b[?25l";
    private const string ShowCursorCode = "\x1b[?25h";
    private const string ClearLineCode = "\x1b[2K";

    private readonly string _version;
    private readonly bool _trueColor;
    private readonly object _gate = new();
    private readonly Timer _tickTimer;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();

    private string _statusText = "scanning — 0 files / 0 dirs";
    private string? _currentPath;
    private int _tick;
    private bool _markPrinted;
    private bool _scanEnded;
    private bool _done;
    private bool _disposed;

    public BrambleProgressRenderer(string version)
    {
        _version = version;
        _trueColor = ConsoleCapabilities.SupportsTrueColor;
        Console.CancelKeyPress += OnCancelKeyPress;
        // One-shot, self-re-arming: only ever one pending callback, re-armed from inside OnTick
        // after its own Redraw() completes -- structurally impossible for callbacks to pile up
        // no matter how slow the console I/O is (see OnTick).
        _tickTimer = new Timer(_ => OnTick(), null, RedrawInterval, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Prints the startup mascot when requested and draws the initial frame. A no-op if called
    /// more than once.
    ///
    /// <paramref name="showMark"/> false still starts the frame, just without re-drawing the art:
    /// the interactive first-run flow already showed the mascot above its consent prompts.
    /// </summary>
    public void PrintMark(bool showMark = true)
    {
        lock (_gate)
        {
            if (_markPrinted)
            {
                return;
            }

            _markPrinted = true;
            if (showMark)
            {
                var width = GetTerminalWidth();
                Console.Out.Write(StartupMascotRenderer.Render(_version, _trueColor, width));
            }

            Console.Out.Write(HideCursorCode);
            Redraw();
        }
    }

    public void OnScanProgress(ScanProgress progress)
    {
        lock (_gate)
        {
            if (_scanEnded || _done)
            {
                // A late callback arriving after OnPhase already ended the scan (or Complete()
                // already ran) -- ignore rather than let it move the status text backward.
                return;
            }

            _statusText = $"scanning — {progress.FilesSeen.ToString("N0", CultureInfo.InvariantCulture)} files / {progress.DirsVisited.ToString("N0", CultureInfo.InvariantCulture)} dirs";
            _currentPath = progress.CurrentPath;
        }
    }

    public void OnPhase(string label)
    {
        lock (_gate)
        {
            if (label.StartsWith("scan complete", StringComparison.Ordinal))
            {
                _scanEnded = true;
                _statusText = "linking references";
                _currentPath = null;
            }
            else if (label.StartsWith("parsing", StringComparison.Ordinal))
            {
                _statusText = label.Replace(" for references", string.Empty, StringComparison.Ordinal);
            }
            else if (label.StartsWith("running C# analysis", StringComparison.Ordinal))
            {
                _statusText = "analyzing C# (this is the long one on big projects)";
            }
        }
    }

    /// <summary>Marks indexing done, clears the transient activity frame, and restores the cursor.
    /// The durable completion message is printed immediately afterward by <see cref="HomeCommand"/>.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            if (_done)
            {
                return;
            }

            _done = true;
            _currentPath = null;
            var lineCount = GetTerminalWidth() < Line2MinTerminalWidth ? 1 : 2;
            var sb = new StringBuilder().Append('\r').Append(ClearLineCode);
            if (lineCount == 2)
            {
                sb.Append('\n').Append(ClearLineCode).Append("\x1b[1A\r");
            }

            Console.Out.Write(sb.Append(ShowCursorCode).ToString());
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _tickTimer.Dispose();
            Console.CancelKeyPress -= OnCancelKeyPress;
            if (_markPrinted)
            {
                // Idempotent even if Complete() already restored it -- an exception path that
                // skipped Complete() must still never leave the terminal cursorless.
                Console.Out.Write(ShowCursorCode);
            }
        }
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        // Best-effort only: restore the cursor immediately so Ctrl+C mid-index never leaves the
        // terminal cursorless. Doesn't set e.Cancel -- this command doesn't own the process's
        // Ctrl+C handling (unlike `watch`), so the default terminate behavior is left alone.
        Console.Out.Write(ShowCursorCode);
    }

    private void OnTick()
    {
        lock (_gate)
        {
            if (_disposed || _done)
            {
                return; // do NOT re-arm -- let the timer die here
            }

            _tick++;
            Redraw();
            _tickTimer.Change(RedrawInterval, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Redraws in place: carriage-return + clear line 1, then (if line 2 exists this
    /// frame) newline + clear line 2 + move back up -- so the next redraw always overwrites the
    /// same one or two terminal rows instead of scrolling.</summary>
    private void Redraw()
    {
        var width = GetTerminalWidth();
        var frame = BuildFrame(width, _elapsed.Elapsed, _statusText, _currentPath, _tick, _trueColor);

        var sb = new StringBuilder();
        sb.Append('\r').Append(ClearLineCode).Append(frame.Line1);
        if (frame.Line2 is not null)
        {
            sb.Append('\n').Append(ClearLineCode).Append(frame.Line2).Append("\x1b[1A\r");
        }

        Console.Out.Write(sb.ToString());
    }

    private static int GetTerminalWidth()
    {
        try
        {
            var width = Console.WindowWidth;
            return width > 0 ? width : DefaultTerminalWidth;
        }
        catch (Exception)
        {
            return DefaultTerminalWidth;
        }
    }

    private const string AnsiResetCode = "\x1b[0m";

    internal readonly record struct ProgressFrame(string Line1, string? Line2);

    /// <summary>Pure string-building (no Console I/O), mirroring <c>WatchDashboard.BuildFrame</c>'s
    /// own testability pattern -- directly assertable without a real console.</summary>
    internal static ProgressFrame BuildFrame(int terminalWidth, TimeSpan elapsed, string statusText, string? currentPath, int tick, bool trueColor)
    {
        var indicatorWidth = terminalWidth >= 80
            ? MaxIndicatorWidth
            : terminalWidth >= Line2MinTerminalWidth ? MediumIndicatorWidth : MinIndicatorWidth;
        var indicator = RenderUnbrambleIndicator(indicatorWidth, tick, trueColor);
        var line1 = $"  Unbrambling...  {indicator}  {elapsed:hh\\:mm\\:ss}";
        if (terminalWidth < Line2MinTerminalWidth)
        {
            return new ProgressFrame(line1, null);
        }

        var detail = currentPath is null ? statusText : statusText + "  " + currentPath;
        var line2 = "  " + TruncateMiddle(detail, Math.Max(10, terminalWidth - 2));

        return new ProgressFrame(line1, line2);
    }

    /// <summary>ASCII-only activity loop: brown thorns become a green vine one cell per tick,
    /// then thorns grow back from the left. It never implies a percentage or freezes early.</summary>
    internal static string RenderUnbrambleIndicator(int width, int tick, bool trueColor)
    {
        width = Math.Max(1, width);
        tick = Math.Max(0, tick);
        var phase = tick % (width * 2);
        var firstIsSmooth = phase <= width;
        var firstCount = firstIsSmooth ? phase : phase - width;
        var secondCount = width - firstCount;
        var sb = new StringBuilder();
        if (firstCount > 0)
        {
            sb.Append(AnsiForCell(firstIsSmooth ? 1.0 : 0.0, trueColor))
              .Append(firstIsSmooth ? new string('~', firstCount) : new string('^', firstCount));
        }

        if (secondCount > 0)
        {
            sb.Append(AnsiForCell(firstIsSmooth ? 0.0 : 1.0, trueColor))
              .Append(firstIsSmooth ? new string('^', secondCount) : new string('~', secondCount));
        }

        return sb.Append(AnsiResetCode).ToString();
    }

    internal static string TruncateMiddle(string text, int maxWidth)
    {
        if (maxWidth <= 0 || text.Length <= maxWidth)
        {
            return text;
        }

        if (maxWidth <= 3)
        {
            return text[..maxWidth];
        }

        var keep = maxWidth - 3;
        var head = (keep + 1) / 2;
        var tail = keep - head;
        return text[..head] + "..." + text[^tail..];
    }

    /// <summary>Truecolor (24-bit) SGR code when available, else the nearest bucket of the
    /// 256-color fallback ramp -- both anchored to the same <see cref="BarStartColor"/>/
    /// <see cref="BarEndColor"/> brown-to-green story.</summary>
    internal static string AnsiForCell(double t, bool trueColor)
    {
        if (trueColor)
        {
            var (r, g, b) = InterpolateColor(t);
            return $"\x1b[38;2;{r.ToString(CultureInfo.InvariantCulture)};{g.ToString(CultureInfo.InvariantCulture)};{b.ToString(CultureInfo.InvariantCulture)}m";
        }

        var idx = Ansi256BrownToGreenRamp[(int)Math.Round(Math.Clamp(t, 0, 1) * (Ansi256BrownToGreenRamp.Length - 1), MidpointRounding.AwayFromZero)];
        return $"\x1b[38;5;{idx.ToString(CultureInfo.InvariantCulture)}m";
    }

    internal static (byte R, byte G, byte B) InterpolateColor(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        byte Lerp(byte a, byte b) => (byte)Math.Round(a + ((b - a) * t), MidpointRounding.AwayFromZero);
        return (Lerp(BarStartColor.R, BarEndColor.R), Lerp(BarStartColor.G, BarEndColor.G), Lerp(BarStartColor.B, BarEndColor.B));
    }
}
