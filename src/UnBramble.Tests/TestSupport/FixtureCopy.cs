namespace UnBramble.Tests.TestSupport;

/// <summary>
/// Copies the committed fixture MiniProject to a fresh temp directory. Integration tests
/// must never index the committed fixture in place (it's shared/reused across test runs and
/// mutating it would corrupt other tests' ground truth).
/// </summary>
public sealed class FixtureCopy : IDisposable
{
    private static readonly string SourceRoot =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "MiniProject");

    public string Root { get; }

    private FixtureCopy(string root)
    {
        Root = root;
    }

    public static FixtureCopy Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "unbramble-test-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(SourceRoot, root);
        return new FixtureCopy(root);
    }

    public string Combine(params string[] segments) =>
        Path.Combine([Root, .. segments]);

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }

        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a stray open handle shouldn't fail the test run.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
