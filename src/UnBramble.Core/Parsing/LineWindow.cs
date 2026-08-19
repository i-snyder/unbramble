namespace UnBramble.Core.Parsing;

/// <summary>
/// Wraps a <see cref="TextReader"/> with a small bounded lookahead buffer. Never
/// materializes a whole file (scenes can be hundreds of MB) — this is what lets the
/// UnityEvent m_MethodName capture peek a few lines ahead without abandoning single-pass
/// streaming. Buffer size is bounded by the largest lookahead ever requested (a handful of
/// lines), not by file size.
/// </summary>
internal sealed class LineWindow(TextReader reader) : IDisposable
{
    private readonly Queue<string> _buffer = new();
    private bool _exhausted;

    /// <summary>Advances to and returns the next line, or null at end of stream.</summary>
    public string? Advance()
    {
        Fill(1);
        return _buffer.Count == 0 ? null : _buffer.Dequeue();
    }

    /// <summary>Peeks up to <paramref name="count"/> lines ahead of the current position without consuming them.</summary>
    public IReadOnlyList<string> PeekAhead(int count)
    {
        Fill(count);
        return [.. _buffer.Take(count)];
    }

    private void Fill(int count)
    {
        while (!_exhausted && _buffer.Count < count)
        {
            var line = reader.ReadLine();
            if (line is null)
            {
                _exhausted = true;
                break;
            }

            _buffer.Enqueue(line);
        }
    }

    public void Dispose() => reader.Dispose();
}
