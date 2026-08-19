using UnBramble.Core.Config;

namespace UnBramble.Core.Freshness;

/// <summary>
/// Single-active-watcher enforcement: an exclusive <see cref="FileShare.None"/> handle on
/// `.unbramble/watcher.lock`. Whoever holds the handle is the active watcher; everyone
/// else fails to open it and must retry later. No stale-lock detection/cleanup is needed or
/// wanted: the OS releases the handle the instant
/// the owning process exits or disposes it, so a "stale" lock cannot exist.
/// </summary>
public static class WatcherLock
{
    public const string RelativePath = UnBramblePaths.WatcherLockRelativePath;

    public static string PathFor(string projectRoot) =>
        UnBramblePaths.RelativeTo(projectRoot, RelativePath);

    /// <summary>Returns the held handle on success (caller owns disposal — disposing releases the lock), or null if another writer already holds it.</summary>
    public static FileStream? TryAcquire(string projectRoot)
    {
        var path = PathFor(projectRoot);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }
}
