namespace UnBramble.Core.Config;

/// <summary>
/// Single source of truth for UnBramble's own project-root state directory (`.unbramble/`) and
/// the well-known file names inside it: the SQLite DB, the watcher heartbeat, the watcher and
/// finite index-writer locks, the watch-status snapshot, setup rollback receipt, and auto-spawn marker files. Centralized here --
/// rather than each consumer (<c>HeartbeatFile</c>, <c>WatcherLock</c>, <c>WatchStatusFile</c>,
/// <c>AutoWatchMarkers</c>, <see cref="UnBrambleConfig"/>) carrying its own copy of the directory
/// name -- so those paths can never drift apart on the directory name, and relocating the
/// state directory again in the future is a one-line change.
///
/// Deliberately lives at the project root, not under `Library/`: the auto-spawn watcher runs
/// detached and hidden, holding an exclusive <see cref="System.IO.FileShare.None"/> lock on
/// `watcher.lock` plus open SQLite handles on the DB. If state lived under `Library/`, the
/// standard Unity troubleshooting move ("close Unity, delete `Library/`, reopen") would fail with
/// an undiagnosable "folder in use" error caused by an invisible background process the user has
/// no reason to suspect. A project-root state directory keeps that workflow safe, with zero data
/// loss: the watcher's own <see cref="System.IO.FileSystemWatcher"/>s watch `Assets`/`Packages`/
/// etc., never `Library/`, so wiping `Library/` never touches anything this tool depends on
/// either.
/// </summary>
public static class UnBramblePaths
{
    /// <summary>The state directory's name, relative to a project root.</summary>
    public const string StateDirName = ".unbramble";

    public const string DbFileName = "unbramble.db";
    public const string HeartbeatFileName = "watcher.heartbeat";
    public const string WatcherLockFileName = "watcher.lock";
    public const string IndexWriterLockFileName = "index-writer.lock";
    public const string WatchStatusFileName = "watch.status.json";
    public const string LastQueryFileName = "watch.lastquery";
    public const string SpawnAttemptFileName = "auto-spawn.lastattempt";
    public const string ProjectInstallStateFileName = "project-install.json";

    /// <summary>
    /// Append-only JSON-lines history of every completed <c>RunIndex</c> pass (<see
    /// cref="Monitoring.IndexHistoryLog"/>), one line per pass -- unlike <see
    /// cref="WatchStatusFileName"/> (which only ever retains the LAST completed pass and gets
    /// overwritten within seconds by an auto-spawned watcher's first no-op sweep), this file is
    /// never overwritten, only appended to and occasionally rotated, so a slow cold-index pass's
    /// timing data survives even when a watcher immediately starts sweeping the same project
    /// afterward.
    /// </summary>
    public const string IndexHistoryFileName = "index-history.log";

    /// <summary>Project-root-relative, forward-slash-separated (matches the convention every
    /// other stored/config path in this codebase uses -- callers needing an OS path combine it
    /// via <see cref="StateDirFor"/>/<see cref="RelativeTo"/> or replace the separator themselves,
    /// same as the pre-relocation per-file <c>RelativePath</c> constants did).</summary>
    public const string DbRelativePath = $"{StateDirName}/{DbFileName}";

    public const string HeartbeatRelativePath = $"{StateDirName}/{HeartbeatFileName}";

    public const string WatcherLockRelativePath = $"{StateDirName}/{WatcherLockFileName}";

    public const string IndexWriterLockRelativePath = $"{StateDirName}/{IndexWriterLockFileName}";

    public const string WatchStatusRelativePath = $"{StateDirName}/{WatchStatusFileName}";

    public const string LastQueryRelativePath = $"{StateDirName}/{LastQueryFileName}";

    public const string SpawnAttemptRelativePath = $"{StateDirName}/{SpawnAttemptFileName}";

    public const string IndexHistoryRelativePath = $"{StateDirName}/{IndexHistoryFileName}";

    /// <summary>The state directory's full path under <paramref name="projectRoot"/>.</summary>
    public static string StateDirFor(string projectRoot) =>
        Path.Combine(projectRoot, StateDirName);

    /// <summary>Combines <paramref name="projectRoot"/> with a `/`-separated path relative to it
    /// (one of this class's own <c>*RelativePath</c> constants, typically), converting to the
    /// OS-native separator.</summary>
    public static string RelativeTo(string projectRoot, string relativePath) =>
        Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
}
