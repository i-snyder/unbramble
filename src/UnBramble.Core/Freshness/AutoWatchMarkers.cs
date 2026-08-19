using System.Globalization;
using UnBramble.Core.Config;

namespace UnBramble.Core.Freshness;

/// <summary>
/// Two small timestamp marker files under `.unbramble/` that support the auto-spawn
/// watcher feature (docs/architecture.md, "Auto-spawn watcher"). Each is written atomically
/// (unique temp file + <see cref="File.Move(string, string, bool)"/> onto the shared
/// destination), the same pattern <see cref="HeartbeatFile.Write"/> uses -- see that method's
/// own doc comment for why atomic-write-then-rename is the right shape and what it does and
/// doesn't protect against. Like the heartbeat file, a write here is inherently best-effort:
/// a missed/failed write just leaves the previous value in place a little longer, which every
/// reader already treats as "old" or "absent", never as corruption.
///
/// <list type="bullet">
/// <item><b>LastQuery</b> (<see cref="TouchLastQuery"/>/<see cref="TryReadLastQuery"/>): touched
/// by <see cref="UnBrambleEngine.EnsureFresh"/>'s fast (heartbeat-fresh) path on every query that
/// skips the sweep. An `--auto` watcher reads this file's age on its own self-heal tick (see
/// <see cref="AutoIdleGate"/>) to decide whether it's still useful to stay alive. A normal
/// (non-`--auto`) `watch` process never reads it, and no query's freshness ANSWER ever depends
/// on it -- see EnsureFresh's own doc comment: this file is pure telemetry for the auto-spawn
/// feature, never part of the freshness proof itself.</item>
/// <item><b>SpawnAttempt</b> (<see cref="RecordSpawnAttempt"/>/<see cref="TryReadLastSpawnAttempt"/>):
/// touched by the query-path auto-spawn logic (<c>Program.MaybeAutoSpawnWatch</c>) immediately
/// before every spawn attempt, regardless of outcome -- this is the crash-loop guard's own
/// state, consulted by <see cref="AutoSpawnPolicy.ShouldSpawn"/>.</item>
/// </list>
/// </summary>
public static class AutoWatchMarkers
{
    public const string LastQueryRelativePath = UnBramblePaths.LastQueryRelativePath;

    public const string SpawnAttemptRelativePath = UnBramblePaths.SpawnAttemptRelativePath;

    public static void TouchLastQuery(string projectRoot, DateTime utcNow) =>
        WriteTimestamp(PathFor(projectRoot, LastQueryRelativePath), utcNow);

    public static DateTime? TryReadLastQuery(string projectRoot) =>
        TryReadTimestamp(PathFor(projectRoot, LastQueryRelativePath));

    public static void RecordSpawnAttempt(string projectRoot, DateTime utcNow) =>
        WriteTimestamp(PathFor(projectRoot, SpawnAttemptRelativePath), utcNow);

    public static DateTime? TryReadLastSpawnAttempt(string projectRoot) =>
        TryReadTimestamp(PathFor(projectRoot, SpawnAttemptRelativePath));

    private static string PathFor(string projectRoot, string relativePath) =>
        UnBramblePaths.RelativeTo(projectRoot, relativePath);

    private static void WriteTimestamp(string path, DateTime utcNow)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $"{Path.GetFileName(path)}.tmp-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(tempPath, utcNow.ToString("O", CultureInfo.InvariantCulture));
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
                // Best-effort cleanup only -- see this class's own doc comment.
            }
        }
    }

    private static DateTime? TryReadTimestamp(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            return DateTime.Parse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            return null;
        }
    }
}
