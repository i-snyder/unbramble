namespace UnBramble.Tests.TestSupport;

/// <summary>
/// A fresh, empty temp directory (no fixture contents copied in) — for tests that need to
/// construct a minimal, purpose-built directory tree (e.g. the Addressables detector tests,
/// which need small manifest.json/packages-lock.json variants rather than a full Unity
/// project). See <see cref="FixtureCopy"/> for the "copy the committed MiniProject"
/// counterpart.
/// </summary>
public sealed class TempDir : IDisposable
{
    public string Root { get; }

    private TempDir(string root)
    {
        Root = root;
    }

    public static TempDir Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "unbramble-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new TempDir(root);
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
