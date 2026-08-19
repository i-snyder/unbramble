using System.Globalization;
using System.Text;
using System.Text.Json;
using UnBramble.Core.Config;

namespace UnBramble.Core.Monitoring;

/// <summary>Per-root scan cost for one completed <c>RunIndex</c> pass (see
/// <see cref="Model.ScanRootStat"/>, which this mirrors 1:1) -- the field that lets a slow pass
/// be diagnosed as "one specific junction target scanned slowly" (clustered under one
/// <see cref="ResolvedTarget"/>, low CPU -- Defender's verdict cache cold for that tree) versus
/// "a genuine code regression" (uniform slowdown across every root).</summary>
public sealed record IndexHistoryRootEntry(
    string ProjectPrefix,
    string? ResolvedTarget,
    bool IsJunction,
    long Dirs,
    long Files,
    double ElapsedMs);

public sealed record IndexHistorySlowDirEntry(string ProjectPrefix, double ElapsedMs);

/// <summary>One line of <c>.unbramble/index-history.log</c> -- see <see cref="IndexHistoryLog"/>
/// for why this exists as a separate, append-only file rather than reusing
/// <see cref="WatchStatusFile"/>.</summary>
public sealed record IndexHistoryEntry(
    DateTime TimestampUtc,
    int Pid,
    bool Full,
    string? TriggerSource,
    double ScanMs,
    double SweepMs,
    double ReparseMs,
    double CsMs,
    double TotalMs,
    int Added,
    int Changed,
    int Removed,
    int DirtyCount,
    long FilesSeen,
    long DirsVisited,
    IReadOnlyList<IndexHistoryRootEntry> Roots,
    IReadOnlyList<IndexHistorySlowDirEntry> SlowDirs);

/// <summary>
/// Append-only JSON-lines history of every completed <see cref="UnBramble.Core.UnBrambleEngine.RunIndex"/>
/// pass, written at <c>.unbramble/index-history.log</c> (<see cref="UnBramblePaths.IndexHistoryFileName"/>).
///
/// Exists to close an evidence-destruction gap in <see cref="WatchStatusFile"/>: that file only
/// ever retains the LAST completed pass, and an auto-spawned watcher's first fast no-op sweep
/// overwrites it within seconds of a slow cold run finishing -- permanently losing that run's
/// timing data before a human (or external tooling) can read it. This file is never overwritten,
/// only appended to (and occasionally rotated, see <see cref="Append"/>), so a slow pass's
/// numbers survive.
///
/// Hand-formatted via <see cref="Utf8JsonWriter"/> rather than a reflection-based serializer,
/// same reasoning as <see cref="WatchStatusFile"/>/<see cref="Freshness.HeartbeatFile"/>: a
/// fixed, hand-maintained shape keeps this NativeAOT-safe without a source-generated context.
///
/// Best-effort, same "never a correctness problem" philosophy as every other file in this
/// namespace: a failed append just means one pass's history line is lost, never a wrong answer
/// to any query.
/// </summary>
public static class IndexHistoryLog
{
    public const string RelativePath = UnBramblePaths.IndexHistoryRelativePath;

    /// <summary>Rotate once the file exceeds this size, keeping the most recent
    /// <see cref="MaxLinesAfterRotation"/> lines -- simple size-triggered, count-bounded rotation
    /// rather than a time-based policy, since a single slow pass's own JSON line is already only
    /// a few hundred bytes even with a full root/slow-dir breakdown. Public (not internal): this
    /// project has no InternalsVisibleTo grant onto UnBramble.Tests (see e.g.
    /// HomeCommandTests's own doc comment), and the rotation policy is a legitimate part of this
    /// class's observable contract, same standing as <see cref="RelativePath"/>.</summary>
    public const long RotateAtBytes = 1_000_000;

    public const int MaxLinesAfterRotation = 2000;

    private static readonly Encoding NoBomUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static string PathFor(string projectRoot) =>
        UnBramblePaths.RelativeTo(projectRoot, RelativePath);

    /// <summary>Appends one JSON line for <paramref name="entry"/>, rotating first if the file is
    /// already over <see cref="RotateAtBytes"/>. Best-effort: any I/O failure here is swallowed,
    /// matching every other write path in this namespace -- losing one history line is never
    /// treated as a correctness problem by any consumer.</summary>
    public static void Append(string projectRoot, IndexHistoryEntry entry)
    {
        var path = PathFor(projectRoot);
        try
        {
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);

            RotateIfNeeded(path);

            var line = SerializeLine(entry);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            // NoBomUtf8, not Encoding.UTF8: StreamWriter emits its encoding's preamble on the
            // first write to a stream at position 0 (i.e. exactly the very first Append call ever
            // for a project), which would plant a UTF-8 BOM ahead of that line's opening '{'.
            // .NET's own File.ReadLines/ReadAllLines strip it transparently, but this file is
            // meant to be readable by naive external JSON-lines tooling too (a future
            // `jq -R 'fromjson'` pipe) that shouldn't have to special-case line 1.
            using var writer = new StreamWriter(stream, NoBomUtf8);
            writer.Write(line);
            writer.Write('\n');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort only -- see this class's own doc comment.
        }
    }

    /// <summary>Keeps only the most recent <see cref="MaxLinesAfterRotation"/> lines once the
    /// file crosses <see cref="RotateAtBytes"/> -- bounded growth without a separate rotation
    /// process; rotation happens inline on whichever <see cref="Append"/> call notices the file
    /// is oversized. Never truncates mid-append: rewritten via a temp file + atomic move, so a
    /// concurrent reader never observes a partially-rotated file.</summary>
    private static void RotateIfNeeded(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < RotateAtBytes)
        {
            return;
        }

        var lines = File.ReadAllLines(path);
        if (lines.Length <= MaxLinesAfterRotation)
        {
            return;
        }

        var kept = lines[^MaxLinesAfterRotation..];
        var directory = Path.GetDirectoryName(path)!;
        var tempPath = Path.Combine(directory, $"index-history.tmp-{Guid.NewGuid():N}");
        File.WriteAllLines(tempPath, kept);
        File.Move(tempPath, path, overwrite: true);
    }

    private static string SerializeLine(IndexHistoryEntry e)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("timestampUtc", e.TimestampUtc.ToString("O", CultureInfo.InvariantCulture));
            w.WriteNumber("pid", e.Pid);
            w.WriteBoolean("full", e.Full);
            if (e.TriggerSource is null)
            {
                w.WriteNull("triggerSource");
            }
            else
            {
                w.WriteString("triggerSource", e.TriggerSource);
            }

            w.WriteNumber("scanMs", e.ScanMs);
            w.WriteNumber("sweepMs", e.SweepMs);
            w.WriteNumber("reparseMs", e.ReparseMs);
            w.WriteNumber("csMs", e.CsMs);
            w.WriteNumber("totalMs", e.TotalMs);
            w.WriteNumber("added", e.Added);
            w.WriteNumber("changed", e.Changed);
            w.WriteNumber("removed", e.Removed);
            w.WriteNumber("dirtyCount", e.DirtyCount);
            w.WriteNumber("filesSeen", e.FilesSeen);
            w.WriteNumber("dirsVisited", e.DirsVisited);

            w.WriteStartArray("roots");
            foreach (var r in e.Roots)
            {
                w.WriteStartObject();
                w.WriteString("root", r.ProjectPrefix);
                if (r.ResolvedTarget is null)
                {
                    w.WriteNull("resolvedTarget");
                }
                else
                {
                    w.WriteString("resolvedTarget", r.ResolvedTarget);
                }

                w.WriteBoolean("isJunction", r.IsJunction);
                w.WriteNumber("dirs", r.Dirs);
                w.WriteNumber("files", r.Files);
                w.WriteNumber("elapsedMs", r.ElapsedMs);
                w.WriteEndObject();
            }

            w.WriteEndArray();

            w.WriteStartArray("slowDirs");
            foreach (var d in e.SlowDirs)
            {
                w.WriteStartObject();
                w.WriteString("path", d.ProjectPrefix);
                w.WriteNumber("elapsedMs", d.ElapsedMs);
                w.WriteEndObject();
            }

            w.WriteEndArray();

            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>Reads every parseable line back, in file order (oldest first). Corrupt/unparseable
    /// lines are skipped rather than failing the whole read -- same "never wrong, only sometimes
    /// incomplete" contract as every other read path in this namespace. In-process consumers
    /// only, e.g. unit tests and any future `unbramble` verb that wants to summarize it.</summary>
    public static IReadOnlyList<IndexHistoryEntry> TryReadAll(string projectRoot)
    {
        var path = PathFor(projectRoot);
        if (!File.Exists(path))
        {
            return [];
        }

        var results = new List<IndexHistoryEntry>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var doc = JsonDocument.Parse(line);
                results.Add(ParseEntry(doc.RootElement));
            }
            catch (JsonException)
            {
                // Skip corrupt/partial lines -- see this method's own doc comment.
            }
        }

        return results;
    }

    private static IndexHistoryEntry ParseEntry(JsonElement root)
    {
        var roots = new List<IndexHistoryRootEntry>();
        foreach (var r in root.GetProperty("roots").EnumerateArray())
        {
            roots.Add(new IndexHistoryRootEntry(
                r.GetProperty("root").GetString()!,
                r.GetProperty("resolvedTarget").ValueKind == JsonValueKind.Null ? null : r.GetProperty("resolvedTarget").GetString(),
                r.GetProperty("isJunction").GetBoolean(),
                r.GetProperty("dirs").GetInt64(),
                r.GetProperty("files").GetInt64(),
                r.GetProperty("elapsedMs").GetDouble()));
        }

        var slowDirs = new List<IndexHistorySlowDirEntry>();
        foreach (var d in root.GetProperty("slowDirs").EnumerateArray())
        {
            slowDirs.Add(new IndexHistorySlowDirEntry(d.GetProperty("path").GetString()!, d.GetProperty("elapsedMs").GetDouble()));
        }

        return new IndexHistoryEntry(
            DateTime.Parse(root.GetProperty("timestampUtc").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            root.GetProperty("pid").GetInt32(),
            root.GetProperty("full").GetBoolean(),
            root.GetProperty("triggerSource").ValueKind == JsonValueKind.Null ? null : root.GetProperty("triggerSource").GetString(),
            root.GetProperty("scanMs").GetDouble(),
            root.GetProperty("sweepMs").GetDouble(),
            root.GetProperty("reparseMs").GetDouble(),
            root.GetProperty("csMs").GetDouble(),
            root.GetProperty("totalMs").GetDouble(),
            root.GetProperty("added").GetInt32(),
            root.GetProperty("changed").GetInt32(),
            root.GetProperty("removed").GetInt32(),
            root.GetProperty("dirtyCount").GetInt32(),
            root.GetProperty("filesSeen").GetInt64(),
            root.GetProperty("dirsVisited").GetInt64(),
            roots,
            slowDirs);
    }
}
