using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UnBramble.Cli;

/// <summary>
/// The one place in this codebase that asks "is there a human on the other end of this
/// terminal, and can we draw on it": zero-args <c>unbramble</c> (<see cref="HomeCommand"/>) is
/// the first command that ever needs to know. Nothing else in <c>src/</c> references
/// <see cref="Console.IsOutputRedirected"/>/<see cref="Console.IsInputRedirected"/>/VT modes
/// before this file -- this is meant to become the one convention for it, not a one-off.
/// </summary>
internal static class ConsoleCapabilities
{
    /// <summary>
    /// Prompting requires a human on BOTH ends: stdin to answer, stdout to see the question.
    /// A pipe/redirect on either side means nobody is there to respond, and blocking on
    /// <see cref="Console.ReadLine"/> in that situation just hangs a script or agent forever.
    /// </summary>
    public static bool IsInteractive => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    /// <summary>
    /// Decorative ANSI escape-code rendering is safe: interactive stdout, <c>NO_COLOR</c> isn't
    /// set, <c>TERM</c> isn't <c>dumb</c>, and (Windows only) virtual-terminal processing was
    /// enabled successfully on the output handle. Computed once -- terminal capabilities don't
    /// change mid-process.
    /// </summary>
    public static bool SupportsAnsi { get; }

    /// <summary>
    /// 24-bit color is safe to emit: either <c>COLORTERM</c> advertises it, or (Windows) the
    /// console accepted virtual-terminal processing.
    ///
    /// That Windows inference is load-bearing, not a guess. Windows sets <c>COLORTERM</c>
    /// nowhere -- not conhost, not Windows Terminal, not PowerShell -- so a COLORTERM-only test
    /// answers "no" on every terminal that exists on the only platform this tool ships for, and
    /// the truecolor path silently never runs. Meanwhile the only Windows consoles that accept
    /// <see cref="EnableVirtualTerminalProcessing"/> at all are Win10 1703+ conhost and Windows
    /// Terminal, both of which render 24-bit color; anything older fails that call and has
    /// already fallen back to no color whatsoever. So VT-enabled implies truecolor here, and the
    /// 256-color path this leaves behind is for non-Windows terminals that set no COLORTERM.
    /// </summary>
    public static bool SupportsTrueColor { get; }

    /// <summary>
    /// Usable column count for laying text out, or <c>null</c> when there is no terminal whose
    /// width means anything -- piped/redirected output, or a console that won't report a width.
    /// A null width means "emit each statement as one unwrapped line": that's the right answer
    /// for the two audiences on the other end of a pipe (an agent parsing text output, and
    /// grep), both of which are hurt rather than helped by prose broken across lines.
    ///
    /// Deliberately NOT gated on <see cref="SupportsAnsi"/>, and deliberately not cached the way
    /// the color capabilities above are. Layout and color are separate capabilities: a terminal
    /// with <c>NO_COLOR</c> set still wants wrapped, indented prose, it just wants it
    /// uncolorized. And unlike color support, width changes under the process when the user
    /// resizes the window -- which <c>watch</c> is long-lived enough to see.
    /// </summary>
    public static int? TerminalWidth
    {
        get
        {
            // Explicit override wins over everything, redirection included: it's the only way to
            // see (or assert on) the wrapped rendering without a real attached terminal, since
            // capturing the output at all is what suppresses wrapping. Named rather than the
            // conventional COLUMNS because some shells export COLUMNS into every child process,
            // which would silently switch piped output over to the human layout.
            if (int.TryParse(Environment.GetEnvironmentVariable("UNBRAMBLE_COLUMNS"), out var forced) && forced > 0)
            {
                return forced;
            }

            if (Console.IsOutputRedirected)
            {
                return null;
            }

            try
            {
                var width = Console.WindowWidth;
                return width > 0 ? width : null;
            }
            catch (Exception)
            {
                // Same defensive posture as TryEnableWindowsVirtualTerminal: no console attached,
                // handle closed under us, whatever -- an unknowable width is just the unwrapped
                // path, never a crash.
                return null;
            }
        }
    }

    static ConsoleCapabilities()
    {
        var colorTerm = Environment.GetEnvironmentVariable("COLORTERM");
        var colorTermAdvertisesTrueColor = colorTerm is not null
            && (colorTerm.Contains("truecolor", StringComparison.OrdinalIgnoreCase)
                || colorTerm.Contains("24bit", StringComparison.OrdinalIgnoreCase));

        if (!IsInteractive || Environment.GetEnvironmentVariable("NO_COLOR") is not null)
        {
            SupportsAnsi = false;
            SupportsTrueColor = false;
            return;
        }

        SupportsAnsi = OperatingSystem.IsWindows()
            ? TryEnableWindowsVirtualTerminal()
            : Environment.GetEnvironmentVariable("TERM") != "dumb";

        // Derived after the VT check, never before it: the Windows half of this is exactly "did
        // that call succeed" -- see the property's own remarks.
        SupportsTrueColor = SupportsAnsi && (colorTermAdvertisesTrueColor || OperatingSystem.IsWindows());
    }

    /// <summary>
    /// Legacy conhost.exe needs virtual-terminal processing explicitly turned on before it'll
    /// honor ANSI escape codes at all; Windows Terminal already has it on and this is a
    /// harmless no-op there. Any failure along the way (no console attached, access denied,
    /// whatever) just means ANSI is unavailable -- never a crash, since the plain-text fallback
    /// (<see cref="SweepProgressPrinter"/>) always exists and costs nothing extra.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool TryEnableWindowsVirtualTerminal()
    {
        try
        {
            var handle = GetStdHandle(StdOutputHandle);
            if (handle == IntPtr.Zero || handle == InvalidHandleValue)
            {
                return false;
            }

            if (!GetConsoleMode(handle, out var mode))
            {
                return false;
            }

            if ((mode & EnableVirtualTerminalProcessing) != 0)
            {
                return true;
            }

            return SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}
