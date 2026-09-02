using System.Text;

namespace UnBramble.Core.Parsing;

/// <summary>
/// Reads physical text lines without ever allowing one line to grow without bound. Unity can
/// serialize texture and mesh payloads as hundreds of megabytes of hexadecimal text on one YAML
/// line, which makes <see cref="TextReader.ReadLine"/> attempt one equally enormous .NET string.
/// </summary>
internal sealed class BoundedLineReader : TextReader
{
    internal const int MaxMaterializedLineChars = 1024 * 1024;

    private const int ReadBufferChars = 16 * 1024;

    private readonly StreamReader _reader;
    private readonly string _fullPath;
    private readonly OversizedLinePolicy _oversizedLinePolicy;
    private readonly char[] _readBuffer = new char[ReadBufferChars];
    private char[] _lineBuffer = new char[1024];
    private int _readPosition;
    private int _readLength;
    private int _completedLineCount;
    private bool _disposed;

    public BoundedLineReader(string fullPath, OversizedLinePolicy oversizedLinePolicy)
    {
        // Watch-mode parsing races normal editor saves. Allow readers and atomic delete/replace
        // operations, but deliberately deny concurrent in-place writes: this handle must see one
        // internally consistent file version. A replacement schedules its new version through
        // the normal watcher event path.
        var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        _reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true);
        _fullPath = fullPath;
        _oversizedLinePolicy = oversizedLinePolicy;
    }

    public override string? ReadLine()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var lineLength = 0;
        var sawContent = false;
        var compacted = false;
        string? structuralLine = null;
        var validator = new HexScalarValidator();

        while (true)
        {
            if (!EnsureBuffered())
            {
                if (!sawContent)
                {
                    return null;
                }

                if (compacted && !validator.SawHex)
                {
                    throw OversizedLineException();
                }

                _completedLineCount++;
                return compacted ? structuralLine : new string(_lineBuffer, 0, lineLength);
            }

            var remaining = _readBuffer.AsSpan(_readPosition, _readLength - _readPosition);
            var terminatorOffset = remaining.IndexOfAny('\r', '\n');
            var content = terminatorOffset < 0 ? remaining : remaining[..terminatorOffset];
            if (!content.IsEmpty)
            {
                if (ContainsDisallowedControl(content))
                {
                    throw InvalidControlCharacterException();
                }

                sawContent = true;
                if (!compacted)
                {
                    var available = MaxMaterializedLineChars - lineLength;
                    if (content.Length <= available)
                    {
                        AppendToLineBuffer(content, ref lineLength);
                    }
                    else
                    {
                        AppendToLineBuffer(content[..available], ref lineLength);
                        if (_oversizedLinePolicy != OversizedLinePolicy.CompactHexYamlScalar
                            || !TryStartHexScalarCompaction(_lineBuffer.AsSpan(0, lineLength), out structuralLine, ref validator))
                        {
                            throw OversizedLineException();
                        }

                        compacted = true;
                        if (!validator.TryConsume(content[available..]))
                        {
                            throw OversizedLineException();
                        }
                    }
                }
                else if (!validator.TryConsume(content))
                {
                    throw OversizedLineException();
                }
            }

            _readPosition += content.Length;
            if (terminatorOffset < 0)
            {
                continue;
            }

            var terminator = _readBuffer[_readPosition++];
            if (terminator == '\r')
            {
                ConsumeOptionalLineFeed();
            }

            if (compacted && !validator.SawHex)
            {
                throw OversizedLineException();
            }

            _completedLineCount++;
            return compacted ? structuralLine : new string(_lineBuffer, 0, lineLength);
        }
    }

    private bool EnsureBuffered()
    {
        if (_readPosition < _readLength)
        {
            return true;
        }

        _readLength = _reader.Read(_readBuffer, 0, _readBuffer.Length);
        _readPosition = 0;
        return _readLength > 0;
    }

    private void ConsumeOptionalLineFeed()
    {
        if (EnsureBuffered() && _readBuffer[_readPosition] == '\n')
        {
            _readPosition++;
        }
    }

    private void AppendToLineBuffer(ReadOnlySpan<char> content, ref int lineLength)
    {
        var required = lineLength + content.Length;
        if (required > _lineBuffer.Length)
        {
            var newLength = _lineBuffer.Length;
            while (newLength < required)
            {
                newLength = Math.Min(newLength * 2, MaxMaterializedLineChars);
            }

            Array.Resize(ref _lineBuffer, newLength);
        }

        content.CopyTo(_lineBuffer.AsSpan(lineLength));
        lineLength = required;
    }

    private bool TryStartHexScalarCompaction(
        ReadOnlySpan<char> materialized,
        out string? structuralLine,
        ref HexScalarValidator validator)
    {
        var colon = FindYamlKeyTerminator(materialized);
        if (colon >= 0)
        {
            if (!validator.TryConsume(materialized[(colon + 1)..]))
            {
                structuralLine = null;
                return false;
            }

            structuralLine = new string(materialized[..(colon + 1)]);
            return true;
        }

        var sequenceMarker = FindYamlSequenceMarker(materialized);
        if (sequenceMarker < 0 || !validator.TryConsume(materialized[(sequenceMarker + 1)..]))
        {
            structuralLine = null;
            return false;
        }

        // Preserve indentation, an optional sequence marker, and the key itself. Feeding this
        // small surrogate through YamlPropertyPathTracker keeps property/sequence state exact;
        // the discarded value is proven unable to contain "guid" or any other non-hex token.
        structuralLine = new string(materialized[..(sequenceMarker + 1)]);
        return true;
    }

    private static int FindYamlKeyTerminator(ReadOnlySpan<char> line)
    {
        for (var i = 1; i < line.Length; i++)
        {
            if (line[i] == ':' && (i + 1 == line.Length || line[i + 1] == ' '))
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindYamlSequenceMarker(ReadOnlySpan<char> line)
    {
        var position = 0;
        while (position < line.Length && line[position] == ' ')
        {
            position++;
        }

        return position + 1 < line.Length && line[position] == '-' && line[position + 1] == ' '
            ? position
            : -1;
    }

    private InvalidDataException OversizedLineException() => new(
        $"'{_fullPath}' contains an oversized text line at line {_completedLineCount + 1:N0}. " +
        $"Lines longer than {MaxMaterializedLineChars:N0} characters are rejected unless the value is a Unity YAML scalar containing only hexadecimal payload data.");

    private InvalidDataException InvalidControlCharacterException() => new(
        $"'{_fullPath}' contains a binary control character in text at line {_completedLineCount + 1:N0}.");

    private static bool ContainsDisallowedControl(ReadOnlySpan<char> content)
    {
        foreach (var c in content)
        {
            if (c == '\0' || c < '\t' || c is '\v' or '\f' or >= '\u000e' and <= '\u001f' or >= '\u007f' and <= '\u009f')
            {
                return true;
            }
        }

        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _reader.Dispose();
        }

        _disposed = true;
        base.Dispose(disposing);
    }

    private struct HexScalarValidator
    {
        private HexScalarState _state;

        public bool SawHex { get; private set; }

        public bool TryConsume(ReadOnlySpan<char> value)
        {
            foreach (var c in value)
            {
                if (IsHex(c))
                {
                    if (_state == HexScalarState.TrailingWhitespace)
                    {
                        return false;
                    }

                    _state = HexScalarState.Hex;
                    SawHex = true;
                    continue;
                }

                if (c is ' ' or '\t')
                {
                    if (_state == HexScalarState.Hex)
                    {
                        _state = HexScalarState.TrailingWhitespace;
                    }

                    continue;
                }

                return false;
            }

            return true;
        }

        private static bool IsHex(char c) =>
            c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
    }

    private enum HexScalarState
    {
        LeadingWhitespace,
        Hex,
        TrailingWhitespace,
    }
}

internal enum OversizedLinePolicy
{
    Reject,
    CompactHexYamlScalar,
}
