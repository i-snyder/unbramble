using UnBramble.Core.Config;

namespace UnBramble.Core.Freshness;

/// <summary>
/// Cross-process ownership for one finite index mutation (schema setup, a full sweep, or one
/// watcher batch). This is deliberately separate from <see cref="WatcherLock"/>: a watcher owns
/// that lock for its whole lifetime, while it owns this one only while touching SQLite. Readers
/// can therefore keep using WAL snapshots and explicit indexing can wait for a quiet gap without
/// waiting for the watcher process to exit.
/// </summary>
public static class IndexWriterLock
{
    public const string RelativePath = UnBramblePaths.IndexWriterLockRelativePath;
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public static string PathFor(string projectRoot) => UnBramblePaths.RelativeTo(projectRoot, RelativePath);

    public static FileStream? TryAcquire(string projectRoot)
    {
        var path = PathFor(projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Waits until the current writer commits or exits. The OS releases the handle on
    /// process death, so there is no stale-file cleanup and no timeout that can expire during a
    /// legitimate multi-minute cold index.</summary>
    public static FileStream Acquire(string projectRoot, Action<string>? onWait = null)
    {
        var announced = false;
        while (true)
        {
            var handle = TryAcquire(projectRoot);
            if (handle is not null)
            {
                return handle;
            }

            if (!announced)
            {
                onWait?.Invoke("index: another unbramble process is updating the database -- waiting for its committed result (use 'unbramble monitor' to inspect a watcher, or 'unbramble stop' if the owner is stuck)");
                announced = true;
            }

            Thread.Sleep(PollInterval);
        }
    }
}
