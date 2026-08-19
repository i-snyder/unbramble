namespace UnBramble.Tests.TestSupport;

/// <summary>
/// Polls a condition until it's true or a generous timeout elapses (CI runners can be slow).
/// Used by the watcher tests, which observe state that changes asynchronously on background
/// timer/FileSystemWatcher threads.
/// </summary>
public static class Poll
{
    public static void Until(Func<bool> condition, TimeSpan? timeout = null, TimeSpan? interval = null, string? message = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        var pollInterval = interval ?? TimeSpan.FromMilliseconds(100);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(pollInterval);
        }

        Assert.Fail(message ?? "condition not met within timeout");
    }
}
