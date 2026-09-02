using UnBramble.Core.Config;

namespace UnBramble.Core.Freshness;

/// <summary>
/// Cross-process ownership for one finite index mutation (schema setup, a full sweep, or one
/// watcher batch). This is deliberately separate from <see cref="WatcherLock"/>: a watcher owns
/// that lock for its whole lifetime, while it owns this one only while touching SQLite. The
/// writer handle requests write access while sharing read access; <see cref="IndexReadLock"/>
/// handles request read access but don't share write access. Windows' bidirectional share check
/// therefore permits concurrent readers but never a reader and writer or two writers together.
/// </summary>
public static class IndexWriterLock
{
    public const string RelativePath = UnBramblePaths.IndexWriterLockRelativePath;
    public const string IntentRelativePath = UnBramblePaths.IndexWriterIntentLockRelativePath;
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public static string PathFor(string projectRoot) => UnBramblePaths.RelativeTo(projectRoot, RelativePath);

    public static string IntentPathFor(string projectRoot) => UnBramblePaths.RelativeTo(projectRoot, IntentRelativePath);

    public static IDisposable? TryAcquire(string projectRoot)
    {
        var intent = TryAcquireIntent(projectRoot, FileAccess.ReadWrite, FileShare.None);
        if (intent is null)
        {
            return null;
        }

        var writer = TryAcquireWriterFile(projectRoot);
        if (writer is null)
        {
            intent.Dispose();
            return null;
        }

        return new WriterLease(intent, writer);
    }

    /// <summary>Waits until the current writer commits or exits. The OS releases the handle on
    /// process death, so there is no stale-file cleanup and no timeout that can expire during a
    /// legitimate multi-minute cold index.</summary>
    public static IDisposable Acquire(string projectRoot, Action<string>? onWait = null)
    {
        var announced = false;
        FileStream intent;
        while (true)
        {
            var candidate = TryAcquireIntent(projectRoot, FileAccess.ReadWrite, FileShare.None);
            if (candidate is not null)
            {
                intent = candidate;
                break;
            }

            if (!announced)
            {
                onWait?.Invoke("index: another unbramble process is updating the database -- waiting for its committed result (use 'unbramble monitor' to inspect a watcher, or 'unbramble stop' if the owner is stuck)");
                announced = true;
            }

            Thread.Sleep(PollInterval);
        }

        // Retain exclusive writer intent while existing readers drain. New readers take a
        // shared intent handle before their data lease, so none can jump ahead now and extend
        // the wait indefinitely.
        var readerWaitAnnounced = false;
        try
        {
            while (true)
            {
                var writer = TryAcquireWriterFile(projectRoot);
                if (writer is not null)
                {
                    return new WriterLease(intent, writer);
                }

                if (!readerWaitAnnounced)
                {
                    onWait?.Invoke("index: waiting for active queries to finish before updating the database");
                    readerWaitAnnounced = true;
                }

                Thread.Sleep(PollInterval);
            }
        }
        catch
        {
            intent.Dispose();
            throw;
        }
    }

    internal static FileStream? TryAcquireReadIntent(string projectRoot) =>
        TryAcquireIntent(projectRoot, FileAccess.Read, FileShare.Read);

    private static FileStream? TryAcquireIntent(string projectRoot, FileAccess access, FileShare share)
    {
        var path = IntentPathFor(projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var mode = access == FileAccess.Read ? FileMode.Open : FileMode.OpenOrCreate;
            return new FileStream(path, mode, access, share);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static FileStream? TryAcquireWriterFile(string projectRoot)
    {
        var path = PathFor(projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private sealed class WriterLease(FileStream intent, FileStream writer) : IDisposable
    {
        public void Dispose()
        {
            writer.Dispose();
            intent.Dispose();
        }
    }
}

/// <summary>
/// Cross-process shared lease held from a successful freshness decision through the actual graph
/// query. Multiple query processes can coexist, but every <see cref="IndexWriterLock"/> mutation
/// waits until all of them finish, preventing a resolve/query sequence from observing different
/// committed phases of one watcher batch.
/// </summary>
public static class IndexReadLock
{
    public static FileStream? TryAcquire(string projectRoot)
    {
        using var admission = IndexWriterLock.TryAcquireReadIntent(projectRoot);
        if (admission is null)
        {
            return null;
        }

        return TryAcquireDataFile(projectRoot);
    }

    private static FileStream? TryAcquireDataFile(string projectRoot)
    {
        var path = IndexWriterLock.PathFor(projectRoot);
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static FileStream Acquire(string projectRoot, Action<string>? onWait = null)
    {
        var path = IndexWriterLock.PathFor(projectRoot);
        if (!File.Exists(path) || !File.Exists(IndexWriterLock.IntentPathFor(projectRoot)))
        {
            // Every real freshness path creates this file through the writer lock first. Keep the
            // read primitive sound for a hand-authored heartbeat or a pre-lock-era cache too.
            using var initializer = IndexWriterLock.Acquire(projectRoot, onWait);
        }

        var announced = false;
        while (true)
        {
            using var admission = IndexWriterLock.TryAcquireReadIntent(projectRoot);
            if (admission is not null)
            {
                var handle = TryAcquireDataFile(projectRoot);
                if (handle is not null)
                {
                    return handle;
                }
            }

            if (!announced)
            {
                onWait?.Invoke("query: an index update began before the read snapshot was secured -- waiting for its committed result");
                announced = true;
            }

            Thread.Sleep(IndexWriterLock.PollInterval);
        }
    }
}
