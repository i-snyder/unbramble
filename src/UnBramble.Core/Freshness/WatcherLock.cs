using System.Text;
using UnBramble.Core.Config;

namespace UnBramble.Core.Freshness;

/// <summary>
/// Single-active-watcher enforcement. Current watchers open `.unbramble/watcher.lock` with
/// shared access and hold an OS byte-range owner lock on byte zero; byte one is a short-lived
/// transition gate; byte two is held whenever the owner's index isn't safe to trust; and the rest
/// of the file records the owner's random session id. Queries hold the transition gate while
/// probing the other bytes and reading the owner session and heartbeat, so acquisition,
/// graceful disposal, and freshness publication appear atomic. Snapshots re-probe ownership to
/// detect process death, which releases byte locks without participating in the transition gate.
/// The shared handle remains incompatible with older watchers' FileShare.None handles in both
/// directions.
/// </summary>
public static class WatcherLock
{
    private const long OwnerLockOffset = 0;
    private const long TransitionLockOffset = 1;
    private const long FreshnessLockOffset = 2;
    private const long LockLength = 1;
    private const long SessionOffset = 3;
    private const int MaximumSessionBytes = 128;

    public const string RelativePath = UnBramblePaths.WatcherLockRelativePath;

    public static string PathFor(string projectRoot) =>
        UnBramblePaths.RelativeTo(projectRoot, RelativePath);

    /// <summary>Opens a shared guard that is compatible with current byte-range watchers but
    /// prevents any legacy FileShare.None watcher from starting. Hold it across freshness
    /// evaluation and the graph query that consumes that result.</summary>
    public static FileStream? TryAcquireLegacyGuard(string projectRoot) => TryOpenShared(projectRoot);

    /// <summary>Returns an owned watcher lease, or null while any current or older watcher owns
    /// the lock. Disposing the lease releases both the OS lock and the file handle.</summary>
    public static WatcherLease? TryAcquire(string projectRoot)
    {
        var stream = TryOpenShared(projectRoot);
        if (stream is null)
        {
            return null;
        }

        var sessionId = Guid.NewGuid().ToString("N");
        var ownsTransition = false;
        var ownsOwner = false;
        var ownsFreshness = false;
        try
        {
            try
            {
                Lock(stream, TransitionLockOffset);
                ownsTransition = true;
            }
            catch (IOException)
            {
                stream.Dispose();
                return null;
            }

            try
            {
                Lock(stream, OwnerLockOffset);
                ownsOwner = true;
            }
            catch (IOException)
            {
                Unlock(stream, TransitionLockOffset);
                stream.Dispose();
                return null;
            }

            Lock(stream, FreshnessLockOffset);
            ownsFreshness = true;

            var bytes = Encoding.UTF8.GetBytes(sessionId);
            stream.SetLength(SessionOffset);
            stream.Position = SessionOffset;
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            try
            {
                HeartbeatFile.Invalidate(projectRoot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The freshness byte is the authoritative fail-closed signal. Deleting the old
                // heartbeat is still useful, but a sharing-denied file must not prevent takeover.
            }
            Unlock(stream, TransitionLockOffset);
            ownsTransition = false;
            return new WatcherLease(stream, sessionId, freshnessSuppressed: true);
        }
        catch
        {
            if (ownsFreshness)
            {
                Unlock(stream, FreshnessLockOffset);
            }

            if (ownsOwner)
            {
                Unlock(stream, OwnerLockOffset);
            }

            if (ownsTransition)
            {
                Unlock(stream, TransitionLockOffset);
            }

            stream.Dispose();
            throw;
        }
    }

    public enum SnapshotUnavailableReason
    {
        None,
        ExclusiveOwner,
        Transitioning,
    }

    /// <summary>Captures a stable watcher ownership snapshot. The returned lease holds the
    /// transition gate until disposal, so its session and any heartbeat read by the caller can't
    /// straddle a watcher handoff.</summary>
    public static SnapshotLease? TryAcquireSnapshot(
        string projectRoot,
        out SnapshotUnavailableReason unavailableReason) =>
        TryAcquireSnapshot(projectRoot, out unavailableReason, afterInitialOwnerProbe: null);

    /// <summary>Deterministic test seam for simulating process death after the initial owner
    /// probe. Production callers use the public two-argument overload.</summary>
    internal static SnapshotLease? TryAcquireSnapshot(
        string projectRoot,
        out SnapshotUnavailableReason unavailableReason,
        Action? afterInitialOwnerProbe)
    {
        var stream = TryOpenShared(projectRoot);
        if (stream is null)
        {
            unavailableReason = SnapshotUnavailableReason.ExclusiveOwner;
            return null;
        }

        try
        {
            Lock(stream, TransitionLockOffset);
        }
        catch (IOException)
        {
            stream.Dispose();
            unavailableReason = SnapshotUnavailableReason.Transitioning;
            return null;
        }

        var ownsOwnerProbe = false;
        try
        {
            try
            {
                Lock(stream, OwnerLockOffset);
                ownsOwnerProbe = true;
                unavailableReason = SnapshotUnavailableReason.None;
                return new SnapshotLease(stream, isWatcherActive: false, sessionId: null, ownsOwnerProbe: true);
            }
            catch (IOException)
            {
                afterInitialOwnerProbe?.Invoke();
                var sessionId = ReadSession(stream);
                var freshnessSuppressed = IsLocked(stream, FreshnessLockOffset);

                // A process death releases byte-range locks without participating in the
                // transition gate. Re-probe after reading the session and freshness byte so an
                // owner that died between those observations is classified as absent instead of
                // turning a retained heartbeat into a false active/fresh snapshot.
                try
                {
                    Lock(stream, OwnerLockOffset);
                    ownsOwnerProbe = true;
                    unavailableReason = SnapshotUnavailableReason.None;
                    return new SnapshotLease(stream, isWatcherActive: false, sessionId: null, ownsOwnerProbe: true);
                }
                catch (IOException)
                {
                    unavailableReason = SnapshotUnavailableReason.None;
                    return new SnapshotLease(stream, isWatcherActive: true, sessionId, freshnessSuppressed, ownsOwnerProbe: false);
                }
            }
        }
        catch
        {
            if (ownsOwnerProbe)
            {
                Unlock(stream, OwnerLockOffset);
            }

            Unlock(stream, TransitionLockOffset);
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Returns a disposable probe lease when no watcher owns the lock, otherwise null.
    /// Unlike <see cref="TryAcquire"/>, this never changes the recorded owner session.</summary>
    public static IDisposable? TryProbe(string projectRoot)
    {
        var snapshot = TryAcquireSnapshot(projectRoot, out _);
        if (snapshot is null)
        {
            return null;
        }

        if (snapshot.IsWatcherActive)
        {
            snapshot.Dispose();
            return null;
        }

        return snapshot;
    }

    /// <summary>Returns the session recorded by the current lock owner. Null means there is no
    /// current owner, the owner is an older incompatible watcher, or the record can't be read.</summary>
    public static string? TryReadActiveSession(string projectRoot)
    {
        using var snapshot = TryAcquireSnapshot(projectRoot, out _);
        if (snapshot is null || !snapshot.IsWatcherActive)
        {
            return null;
        }

        return snapshot.SessionId;
    }

    private static string? ReadSession(FileStream stream)
    {
        try
        {
            var sessionLength = stream.Length - SessionOffset;
            if (sessionLength <= 0 || sessionLength > MaximumSessionBytes)
            {
                return null;
            }

            stream.Position = SessionOffset;
            Span<byte> bytes = stackalloc byte[(int)sessionLength];
            stream.ReadExactly(bytes);
            var sessionId = Encoding.UTF8.GetString(bytes);
            return Guid.TryParseExact(sessionId, "N", out _) ? sessionId : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return null;
        }
    }

    private static FileStream? TryOpenShared(string projectRoot)
    {
        var path = PathFor(projectRoot);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void Lock(FileStream stream, long offset)
    {
        if (OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("watcher locking isn't supported on macOS");
        }

        stream.Lock(offset, LockLength);
    }

    private static void Unlock(FileStream stream, long offset)
    {
        if (OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("watcher locking isn't supported on macOS");
        }

        stream.Unlock(offset, LockLength);
    }

    private static bool IsLocked(FileStream stream, long offset)
    {
        try
        {
            Lock(stream, offset);
            Unlock(stream, offset);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static void LockWhenAvailable(FileStream stream, long offset)
    {
        while (true)
        {
            try
            {
                Lock(stream, offset);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(5));
            }
        }
    }

    public sealed class WatcherLease : IDisposable
    {
        private readonly object _gate = new();
        private FileStream? _stream;
        private bool _freshnessSuppressed;

        internal WatcherLease(FileStream stream, string sessionId, bool freshnessSuppressed)
        {
            _stream = stream;
            SessionId = sessionId;
            _freshnessSuppressed = freshnessSuppressed;
        }

        public string SessionId { get; }

        public bool IsFreshnessSuppressed
        {
            get
            {
                lock (_gate)
                {
                    return _freshnessSuppressed;
                }
            }
        }

        /// <summary>Atomically makes the current heartbeat untrustworthy before pending work can
        /// mutate the store. The byte-range lock remains authoritative if heartbeat deletion is
        /// denied by another process.</summary>
        public void SuppressFreshness(Action invalidateHeartbeat)
        {
            ArgumentNullException.ThrowIfNull(invalidateHeartbeat);

            lock (_gate)
            {
                var stream = _stream;
                if (stream is null)
                {
                    return;
                }

                LockWhenAvailable(stream, TransitionLockOffset);
                try
                {
                    if (_freshnessSuppressed)
                    {
                        return;
                    }

                    Lock(stream, FreshnessLockOffset);
                    _freshnessSuppressed = true;
                    invalidateHeartbeat();
                }
                finally
                {
                    Unlock(stream, TransitionLockOffset);
                }
            }
        }

        /// <summary>Publishes a heartbeat and releases the fail-closed freshness byte as one
        /// transition. If publication throws, the byte stays locked.</summary>
        public void PublishFreshness(Action publishHeartbeat)
        {
            ArgumentNullException.ThrowIfNull(publishHeartbeat);

            lock (_gate)
            {
                var stream = _stream;
                if (stream is null)
                {
                    return;
                }

                LockWhenAvailable(stream, TransitionLockOffset);
                try
                {
                    publishHeartbeat();
                    if (_freshnessSuppressed)
                    {
                        Unlock(stream, FreshnessLockOffset);
                        _freshnessSuppressed = false;
                    }
                }
                finally
                {
                    Unlock(stream, TransitionLockOffset);
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                var stream = _stream;
                if (stream is null)
                {
                    return;
                }

                _stream = null;
                LockWhenAvailable(stream, TransitionLockOffset);
                try
                {
                    if (_freshnessSuppressed)
                    {
                        Unlock(stream, FreshnessLockOffset);
                    }

                    Unlock(stream, OwnerLockOffset);
                }
                finally
                {
                    Unlock(stream, TransitionLockOffset);
                    stream.Dispose();
                }
            }
        }
    }

    public sealed class SnapshotLease : IDisposable
    {
        private FileStream? _stream;
        private readonly bool _ownsOwnerProbe;

        internal SnapshotLease(
            FileStream stream,
            bool isWatcherActive,
            string? sessionId,
            bool freshnessSuppressed = false,
            bool ownsOwnerProbe = false)
        {
            _stream = stream;
            IsWatcherActive = isWatcherActive;
            SessionId = sessionId;
            IsFreshnessSuppressed = freshnessSuppressed;
            _ownsOwnerProbe = ownsOwnerProbe;
        }

        public bool IsWatcherActive { get; }

        public string? SessionId { get; }

        public bool IsFreshnessSuppressed { get; }

        public void Dispose()
        {
            var stream = Interlocked.Exchange(ref _stream, null);
            if (stream is null)
            {
                return;
            }

            if (_ownsOwnerProbe)
            {
                Unlock(stream, OwnerLockOffset);
            }

            Unlock(stream, TransitionLockOffset);
            stream.Dispose();
        }
    }
}
