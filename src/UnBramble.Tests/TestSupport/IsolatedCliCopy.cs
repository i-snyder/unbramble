namespace UnBramble.Tests.TestSupport;

/// <summary>
/// A private, uniquely-renamed copy of the built `unbramble.exe` (plus the minimal runtime
/// dependency set it needs: managed assemblies and the win-x64 `e_sqlite3` native library) in a
/// fresh temp directory -- used by <c>StopCommandTests</c> so a real subprocess spawned and
/// killed by those tests can never collide, by image name, with any OTHER genuinely running
/// "unbramble.exe" process on the machine (a real dogfooding watcher for this repo or another
/// project, say). `unbramble stop` matches purely by image name, system-wide, with no project
/// scoping (see <c>Program.RunStop</c>'s own doc comment) -- exercising it against the real
/// shared "unbramble.exe" name from a test would risk exactly that collateral damage.
///
/// The apphost's target assembly name ("unbramble.dll") is embedded in the exe at build time,
/// not derived from the exe's own file name, so renaming the copied exe here is safe -- verified
/// live (`unbramble-rename-test.exe --version` still resolves and runs correctly after a plain
/// file rename).
/// </summary>
public sealed class IsolatedCliCopy : IDisposable
{
    private static readonly string SourceDir = AppContext.BaseDirectory;

    private static readonly string[] ManagedDependencyFileNames =
    [
        "unbramble.dll",
        "unbramble.deps.json",
        "unbramble.runtimeconfig.json",
        "UnBramble.Core.dll",
        "Microsoft.CodeAnalysis.dll",
        "Microsoft.CodeAnalysis.CSharp.dll",
        "Microsoft.Data.Sqlite.dll",
        "SQLitePCLRaw.batteries_v2.dll",
        "SQLitePCLRaw.core.dll",
        "SQLitePCLRaw.provider.e_sqlite3.dll",
    ];

    // These assemblies may be app-local or supplied by the shared framework. Copy them when
    // present so the helper works across SDK layouts.
    private static readonly string[] OptionalManagedDependencyFileNames =
    [
        "System.Collections.Immutable.dll",
        "System.Reflection.Metadata.dll",
    ];

    public string Root { get; }

    public string ExePath { get; }

    private IsolatedCliCopy(string root, string exePath)
    {
        Root = root;
        ExePath = exePath;
    }

    public static IsolatedCliCopy Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "unbramble-isolated-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "runtimes", "win-x64", "native"));

        foreach (var fileName in ManagedDependencyFileNames)
        {
            File.Copy(Path.Combine(SourceDir, fileName), Path.Combine(root, fileName));
        }

        foreach (var fileName in OptionalManagedDependencyFileNames)
        {
            var sourcePath = Path.Combine(SourceDir, fileName);
            if (File.Exists(sourcePath))
            {
                File.Copy(sourcePath, Path.Combine(root, fileName));
            }
        }

        File.Copy(
            Path.Combine(SourceDir, "runtimes", "win-x64", "native", "e_sqlite3.dll"),
            Path.Combine(root, "runtimes", "win-x64", "native", "e_sqlite3.dll"));

        // Unique per call so this process's image name can never be mistaken for, or collide
        // with, any other genuinely running "unbramble.exe" -- see this class's own doc comment.
        var exeName = "unbramble-stoptest-" + Guid.NewGuid().ToString("N")[..8] + ".exe";
        var exePath = Path.Combine(root, exeName);
        File.Copy(Path.Combine(SourceDir, "unbramble.exe"), exePath);

        return new IsolatedCliCopy(root, exePath);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup -- a just-killed process's module mapping can take a moment to
            // fully release on Windows; a stray temp directory left behind is a cosmetic cost,
            // not a test-correctness one (same reasoning FixtureCopy.Dispose uses).
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
