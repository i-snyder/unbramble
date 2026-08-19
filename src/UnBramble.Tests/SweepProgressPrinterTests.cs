using System.Reflection;
using UnBramble.Cli;

namespace UnBramble.Tests;

/// <summary>
/// The plain progress printer's contract (a minutes-long cold init
/// read as "done or stalled?"): silent inside the quiet window, and every line printed past it
/// carries an elapsed stamp. The 15s keepalive cadence is deliberately not timed out here — a
/// test that sleeps 15s+ costs more than the simple guarded path it would cover.
/// SweepProgressPrinter is internal, so this goes through reflection like HomeCommandTests does.
/// </summary>
public class SweepProgressPrinterTests
{
    private static (IDisposable Printer, Action<string> OnPhase, StringWriter Output) Create(string? header)
    {
        var type = typeof(Program).Assembly.GetType("UnBramble.Cli.SweepProgressPrinter")!;
        var output = new StringWriter();
        var printer = (IDisposable)Activator.CreateInstance(type, [header, output])!;
        var onPhase = (Action<string>)Delegate.CreateDelegate(typeof(Action<string>), printer, type.GetMethod("OnPhase", BindingFlags.Public | BindingFlags.Instance)!);
        return (printer, onPhase, output);
    }

    [Fact]
    public void InsideQuietWindow_PhaseLinesStaySilent()
    {
        var (printer, onPhase, output) = Create("header line");
        using (printer)
        {
            onPhase("running C# analysis");
            Assert.Equal("", output.ToString());
        }
    }

    [Fact]
    public void PastQuietWindow_PhaseLineCarriesHeaderAndElapsedStamp()
    {
        var (printer, onPhase, output) = Create("header line");
        using (printer)
        {
            Thread.Sleep(1700); // QuietWindow is 1.5s
            onPhase("running C# analysis");
        }

        var text = output.ToString();
        Assert.StartsWith("header line", text);
        Assert.Contains("sweep: running C# analysis (", text);
        Assert.Contains("elapsed)", text);
    }
}
