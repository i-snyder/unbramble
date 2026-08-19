using System.Globalization;
using System.Text.Json;
using UnBramble.Core.Config;
using UnBramble.Core.Store;

namespace UnBramble.Core.Freshness;

/// <summary>
/// The watcher's `.unbramble/watcher.heartbeat` file: JSON `{"pid": N, "utc": "...", "schema": V}`,
/// written atomically (temp file + rename) so a reader never observes a half-written file.
/// Hand-formatted/parsed via <see cref="JsonDocument"/> rather than a reflection-based
/// serializer — trivial fixed shape, and JsonDocument (unlike JsonSerializer without a
/// source-generated context) needs no reflection, so it stays NativeAOT-safe for free.
///
/// `schema` (the writer's <see cref="UnBrambleStore.CurrentSchemaVersion"/>) exists because a
/// heartbeat is a claim about a SPECIFIC store shape: a still-running watcher from an older
/// binary keeps writing fresh heartbeats right through a schema-bump upgrade, and a newer query
/// binary that trusted one would answer from the store it just dropped and recreated empty —
/// silently wrong, the one thing freshness must never be. A missing `schema` field (a pre-stamp
/// binary's heartbeat) parses as 0, which never matches a real version — old watchers can never
/// vouch for a new binary's store.
/// </summary>
public static class HeartbeatFile
{
    public const string RelativePath = UnBramblePaths.HeartbeatRelativePath;

    public static string PathFor(string projectRoot) =>
        UnBramblePaths.RelativeTo(projectRoot, RelativePath);

    /// <summary>
    /// Atomic write: temp file in the same directory, then <see cref="File.Move(string, string, bool)"/>
    /// (overwrite) — a reader never sees a partial file.
    ///
    /// This pattern
    /// is only safe against a READER racing a writer (each writer's temp file is uniquely named)
    /// -- it is NOT safe against two WRITERS racing each other, since both ultimately
    /// <see cref="File.Move(string, string, bool)"/> to the SAME shared <paramref name="projectRoot"/>-
    /// relative destination path. Two concurrent moves to that one destination can throw
    /// (observed live as <see cref="UnauthorizedAccessException"/>), which is fatal to an entire
    /// watch process when raised unguarded from a <see cref="Timer"/> callback -- exactly what
    /// happened before <see cref="WatcherHost"/> started serializing its own calls through one
    /// gate (see that class's <c>_heartbeatGate</c> doc comment). That in-process fix removes the
    /// race WatcherHost itself can cause; this method's own try/catch is defense-in-depth for any
    /// OTHER actor this class can't control (a transient AV-scanner lock, a stray external
    /// process) -- a heartbeat write is inherently best-effort ("never wrong, only sometimes
    /// slower": a missed write just leaves the PREVIOUS heartbeat value in place a little longer,
    /// which a reader already treats as staleness -- see <see cref="HeartbeatFreshness"/> -- never
    /// as corruption or a crash). The orphaned temp file is best-effort cleaned up too so a run of
    /// transient failures doesn't leak files under `.unbramble/` forever.
    /// </summary>
    public static void Write(string projectRoot, int pid, DateTime utcNow)
    {
        var path = PathFor(projectRoot);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        var json = $$"""{"pid":{{pid.ToString(CultureInfo.InvariantCulture)}},"utc":"{{utcNow.ToString("O", CultureInfo.InvariantCulture)}}","schema":{{UnBrambleStore.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture)}}}""";
        var tempPath = Path.Combine(directory, $"watcher.heartbeat.tmp-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup only -- a leaked temp file is a cosmetic cost, not a
                // correctness one, and is never mistaken for the real heartbeat (readers open the
                // fixed `RelativePath`, never glob for `.tmp-*`).
            }
        }
    }

    /// <summary><see cref="Schema"/> is 0 for a heartbeat written before the schema stamp existed
    /// — deliberately never equal to any real <see cref="UnBrambleStore.CurrentSchemaVersion"/>.</summary>
    public readonly record struct Heartbeat(int Pid, DateTime UtcTimestamp, int Schema = 0);

    /// <summary>Null for any absent/unreadable/corrupt heartbeat — callers treat null exactly like a stale one (fall back to sweeping).</summary>
    public static Heartbeat? TryRead(string projectRoot)
    {
        var path = PathFor(projectRoot);
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            var pid = root.GetProperty("pid").GetInt32();
            var utcText = root.GetProperty("utc").GetString();
            if (utcText is null)
            {
                return null;
            }

            var schema = root.TryGetProperty("schema", out var schemaElement) ? schemaElement.GetInt32() : 0;
            var utc = DateTime.Parse(
                utcText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            return new Heartbeat(pid, utc, schema);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }
}
