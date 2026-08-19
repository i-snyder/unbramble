using System.Text;

namespace UnBramble.Core.Parsing;

/// <summary>
/// Streaming, best-effort tracker of the serialized property path at the parser's current line
/// — the answer to "which FIELD of this component holds the reference?" (e.g.
/// <c>m_Settings.m_VolumeProfile</c>, <c>m_Materials[2]</c>,
/// <c>m_OnClick.m_PersistentCalls.m_Calls[0].m_Target</c>). Motivated by real field feedback:
/// who-uses proved a MonoBehaviour on line N referenced the asset, but naming the owning field
/// still required opening Unity.
///
/// Same discipline as the rest of the parser: line-based indent/key tracking, NOT a real YAML
/// parser. It maintains a stack of (indent, key, sequence-index) from each line's leading
/// indentation, <c>- </c> sequence markers, and first <c>key:</c> token. Display metadata only
/// — a wrong path can mislabel an edge's field but can never add/drop/misresolve the edge
/// itself, which is why best-effort is acceptable here and nowhere else in the parser. Known
/// degradations (all benign): a wrapped flow mapping's continuation line can append the flow key
/// (e.g. <c>m_Script.guid</c>), and a wrapped plain-scalar value containing ": " can push a
/// bogus key until the next real key at its indent pops it.
///
/// Unity-YAML-specific choices: sequence items sit at the SAME indent as their owning key
/// (Unity's emitter never indents block sequences), so a dash line pops strictly-deeper entries
/// and indexes the entry at/above its own indent; the document's root class key (indent 0,
/// e.g. <c>MonoBehaviour:</c>) is excluded from rendered paths — it duplicates
/// <c>source_classid</c>, which display output already renders as the class name.
/// </summary>
internal sealed class YamlPropertyPathTracker
{
    private const int MaxRenderedLength = 200;

    private readonly List<Entry> _stack = [];

    private struct Entry
    {
        public int Indent;
        public string Key;
        public int SeqIndex; // -1 until a sequence dash is seen for this key
    }

    /// <summary>Document boundary (<c>--- !u!…</c>) — a fresh object graph, nothing carries over.</summary>
    public void Reset() => _stack.Clear();

    /// <summary>Feed every non-boundary line, in order, BEFORE inspecting it for refs — the
    /// line's own key must already be on the stack when its guid is captured.</summary>
    public void OnLine(string line)
    {
        var indent = 0;
        while (indent < line.Length && line[indent] == ' ')
        {
            indent++;
        }

        if (indent >= line.Length)
        {
            return; // blank line
        }

        var pos = indent;
        var effectiveIndent = indent;

        // Sequence-item dashes. Each "- " indexes the owning key (the nearest entry at or above
        // the dash's own indent — Unity emits items at the SAME indent as the key) and shifts
        // the rest of the line's effective indent past the marker. Looping handles the
        // (rare-in-Unity) nested bare sequence by simply consuming the extra markers.
        while (pos < line.Length && line[pos] == '-' && (pos + 1 >= line.Length || line[pos + 1] == ' '))
        {
            PopDeeperThan(effectiveIndent);
            if (_stack.Count > 0)
            {
                var top = _stack[^1];
                top.SeqIndex++;
                _stack[^1] = top;
            }

            pos += 2;
            effectiveIndent = pos;
        }

        if (pos >= line.Length)
        {
            return; // bare "-" item with no inline content
        }

        // Flow content, comments, and directives are never block keys.
        var first = line[pos];
        if (first is '{' or '[' or '#' or '%')
        {
            return;
        }

        var key = TryExtractKey(line.AsSpan(pos));
        if (key is null)
        {
            return; // scalar continuation / flow tail — the stack stays as-is, deliberately.
        }

        PopAtOrDeeperThan(effectiveIndent);
        _stack.Add(new Entry { Indent = effectiveIndent, Key = key, SeqIndex = -1 });
    }

    /// <summary>The rendered dotted path for the current line, or null when only the document
    /// root key (or nothing) is on the stack. Only called on ref-carrying lines — the string
    /// build is not paid on the hot every-line path.</summary>
    public string? CurrentPath
    {
        get
        {
            if (_stack.Count == 0)
            {
                return null;
            }

            var sb = new StringBuilder();
            foreach (var e in _stack)
            {
                if (sb.Length == 0 && e.Indent == 0)
                {
                    continue; // root class key — redundant with source_classid
                }

                if (sb.Length > 0)
                {
                    sb.Append('.');
                }

                sb.Append(e.Key);
                if (e.SeqIndex >= 0)
                {
                    sb.Append('[').Append(e.SeqIndex).Append(']');
                }
            }

            if (sb.Length == 0)
            {
                return null;
            }

            return sb.Length > MaxRenderedLength ? sb.ToString(0, MaxRenderedLength) : sb.ToString();
        }
    }

    /// <summary>First ':' followed by a space or end-of-line ends the key — the same first-token
    /// rule Unity's emitter guarantees for block mapping lines. Null when no such colon exists
    /// (plain scalar continuation) or the would-be key is empty.</summary>
    private static string? TryExtractKey(ReadOnlySpan<char> rest)
    {
        var colon = rest.IndexOf(':');
        if (colon <= 0)
        {
            return null;
        }

        if (colon + 1 < rest.Length && rest[colon + 1] != ' ')
        {
            return null;
        }

        var key = rest[..colon].TrimEnd();
        return key.IsEmpty ? null : key.ToString();
    }

    private void PopDeeperThan(int indent)
    {
        var keep = _stack.Count;
        while (keep > 0 && _stack[keep - 1].Indent > indent)
        {
            keep--;
        }

        if (keep < _stack.Count)
        {
            _stack.RemoveRange(keep, _stack.Count - keep);
        }
    }

    private void PopAtOrDeeperThan(int indent)
    {
        var keep = _stack.Count;
        while (keep > 0 && _stack[keep - 1].Indent >= indent)
        {
            keep--;
        }

        if (keep < _stack.Count)
        {
            _stack.RemoveRange(keep, _stack.Count - keep);
        }
    }
}
