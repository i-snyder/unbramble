using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace UnBramble.Cli;

/// <summary>Renders the first-run mascot at 64 terminal cells wide. Each lower-half block carries
/// two source pixels, matching the approved preview from the tui-mascots tool; fully transparent
/// rows around the source art are cropped so layout spacing stays explicit.</summary>
internal static class StartupMascotRenderer
{
    internal const int CellWidth = 64;
    internal const int TerminalRows = 24;
    internal const int MinTerminalWidth = CellWidth + 2;

    private const int PixelHeight = TerminalRows * 2;
    private const string Reset = "\x1b[0m";
    private const string DefaultBackground = "\x1b[49m";
    private const int BackgroundThreshold = 24;

    private static readonly Lazy<byte[]> Pixels = new(InflatePixels);
    private static readonly Lazy<bool[]> TransparentPixels = new(() => BuildTransparencyMask(Pixels.Value));

    internal static string Render(string version, bool trueColor, int terminalWidth)
    {
        if (terminalWidth < MinTerminalWidth)
        {
            return $"unbramble {version}{Environment.NewLine}Clearing a path through complex Unity projects{Environment.NewLine}";
        }

        var sb = new StringBuilder();
        sb.Append("  ").Append(AnsiStyle.Label($"unbramble {version}", ansiEnabled: true))
          .Append(Environment.NewLine);
        sb.Append("  ").Append(AnsiStyle.Alive("Clearing a path through complex Unity projects", ansiEnabled: true))
          .Append(Environment.NewLine)
          .Append(Environment.NewLine)
          .Append(Environment.NewLine);
        AppendArt(sb, trueColor);
        sb.Append(Environment.NewLine).Append(Environment.NewLine);
        return sb.ToString();
    }

    private static void AppendArt(StringBuilder sb, bool trueColor)
    {
        var pixels = Pixels.Value;
        var transparent = TransparentPixels.Value;
        var (firstRow, lastRow) = FindVisibleTerminalRows(transparent);
        for (var row = firstRow; row <= lastRow; row++)
        {
            var y = row * 2;
            sb.Append(' ');
            var lastBackground = -1;
            var lastForeground = -1;
            for (var x = 0; x < CellWidth; x++)
            {
                var topTransparent = transparent[(y * CellWidth) + x];
                var bottomTransparent = transparent[((y + 1) * CellWidth) + x];
                var top = ReadRgb(pixels, x, y);
                var bottom = ReadRgb(pixels, x, y + 1);

                if (topTransparent && bottomTransparent)
                {
                    if (lastBackground != -1)
                    {
                        sb.Append(DefaultBackground);
                        lastBackground = -1;
                    }

                    sb.Append(' ');
                    continue;
                }

                var foreground = bottomTransparent ? top : bottom;
                var renderedForeground = trueColor ? foreground : ToAnsi256(foreground);
                if (renderedForeground != lastForeground)
                {
                    AppendColor(sb, foreground, renderedForeground, background: false, trueColor);
                    lastForeground = renderedForeground;
                }

                if (topTransparent || bottomTransparent)
                {
                    if (lastBackground != -1)
                    {
                        sb.Append(DefaultBackground);
                        lastBackground = -1;
                    }

                    sb.Append(topTransparent ? '\u2584' : '\u2580');
                    continue;
                }

                var renderedTop = trueColor ? top : ToAnsi256(top);
                if (renderedTop != lastBackground)
                {
                    AppendColor(sb, top, renderedTop, background: true, trueColor);
                    lastBackground = renderedTop;
                }

                sb.Append('\u2584');
            }

            sb.Append(Reset).Append(Environment.NewLine);
        }
    }

    private static (int First, int Last) FindVisibleTerminalRows(bool[] transparent)
    {
        var first = 0;
        while (first < TerminalRows && IsTerminalRowTransparent(transparent, first)) first++;

        var last = TerminalRows - 1;
        while (last >= first && IsTerminalRowTransparent(transparent, last)) last--;

        return (first, last);
    }

    private static bool IsTerminalRowTransparent(bool[] transparent, int row)
    {
        var firstPixel = row * 2 * CellWidth;
        return transparent.AsSpan(firstPixel, CellWidth * 2).IndexOf(false) < 0;
    }

    private static int ReadRgb(byte[] pixels, int x, int y)
    {
        var offset = ((y * CellWidth) + x) * 3;
        return (pixels[offset] << 16) | (pixels[offset + 1] << 8) | pixels[offset + 2];
    }

    /// <summary>The source has an opaque black canvas. Flood-fill only near-black pixels connected
    /// to the image edge so the canvas becomes the terminal's background while enclosed dark
    /// details such as the eye and scissors remain deliberate foreground.</summary>
    private static bool[] BuildTransparencyMask(byte[] pixels)
    {
        var transparent = new bool[CellWidth * PixelHeight];
        var visited = new bool[transparent.Length];
        var pending = new Queue<int>();

        for (var x = 0; x < CellWidth; x++)
        {
            pending.Enqueue(x);
            pending.Enqueue(((PixelHeight - 1) * CellWidth) + x);
        }

        for (var y = 1; y < PixelHeight - 1; y++)
        {
            pending.Enqueue(y * CellWidth);
            pending.Enqueue((y * CellWidth) + CellWidth - 1);
        }

        while (pending.TryDequeue(out var index))
        {
            if (visited[index])
            {
                continue;
            }

            visited[index] = true;
            var rgb = ReadRgb(pixels, index % CellWidth, index / CellWidth);
            var maxChannel = Math.Max((rgb >> 16) & 0xff, Math.Max((rgb >> 8) & 0xff, rgb & 0xff));
            if (maxChannel > BackgroundThreshold)
            {
                continue;
            }

            transparent[index] = true;
            var x = index % CellWidth;
            var y = index / CellWidth;
            if (x > 0) pending.Enqueue(index - 1);
            if (x + 1 < CellWidth) pending.Enqueue(index + 1);
            if (y > 0) pending.Enqueue(index - CellWidth);
            if (y + 1 < PixelHeight) pending.Enqueue(index + CellWidth);
        }

        return transparent;
    }

    private static void AppendColor(StringBuilder sb, int rgb, int ansi256, bool background, bool trueColor)
    {
        var channel = background ? 48 : 38;
        if (trueColor)
        {
            sb.Append("\x1b[").Append(channel).Append(";2;")
              .Append((rgb >> 16) & 0xff).Append(';')
              .Append((rgb >> 8) & 0xff).Append(';')
              .Append(rgb & 0xff).Append('m');
        }
        else
        {
            sb.Append("\x1b[").Append(channel).Append(";5;")
              .Append(ansi256.ToString(CultureInfo.InvariantCulture)).Append('m');
        }
    }

    private static int ToAnsi256(int rgb)
    {
        var r = (rgb >> 16) & 0xff;
        var g = (rgb >> 8) & 0xff;
        var b = rgb & 0xff;

        var ri = (int)Math.Round(r / 255.0 * 5, MidpointRounding.AwayFromZero);
        var gi = (int)Math.Round(g / 255.0 * 5, MidpointRounding.AwayFromZero);
        var bi = (int)Math.Round(b / 255.0 * 5, MidpointRounding.AwayFromZero);
        var cubeR = ri == 0 ? 0 : 55 + (40 * ri);
        var cubeG = gi == 0 ? 0 : 55 + (40 * gi);
        var cubeB = bi == 0 ? 0 : 55 + (40 * bi);
        var cubeDistance = DistanceSquared(r, g, b, cubeR, cubeG, cubeB);

        var grayIndex = Math.Clamp((int)Math.Round((r + g + b) / 3.0 - 8, MidpointRounding.AwayFromZero) / 10, 0, 23);
        var gray = 8 + (grayIndex * 10);
        var grayDistance = DistanceSquared(r, g, b, gray, gray, gray);

        return grayDistance < cubeDistance ? 232 + grayIndex : 16 + (36 * ri) + (6 * gi) + bi;
    }

    private static int DistanceSquared(int r1, int g1, int b1, int r2, int g2, int b2)
    {
        var dr = r1 - r2;
        var dg = g1 - g2;
        var db = b1 - b2;
        return (dr * dr) + (dg * dg) + (db * db);
    }

    private static byte[] InflatePixels()
    {
        var compressed = Convert.FromBase64String(CompressedRgb);
        using var source = new MemoryStream(compressed, writable: false);
        using var deflate = new DeflateStream(source, CompressionMode.Decompress);
        var pixels = new byte[CellWidth * PixelHeight * 3];
        deflate.ReadExactly(pixels);
        return pixels;
    }

    // Nearest-neighbor 64x48 RGB sample of tui-mascots/images/01-pruner.png. Kept compressed so
    // the approved image is compiled into the single-file executable without a runtime imaging
    // dependency or a loose asset beside the binary.
    private const string CompressedRgb = """
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
""";
}
