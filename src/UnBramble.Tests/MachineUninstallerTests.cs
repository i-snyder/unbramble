using UnBramble.Cli;

namespace UnBramble.Tests;

public class MachineUninstallerTests
{
    [Fact]
    public void Inspect_RemovesOnlyMatchingUserPathEntries()
    {
        using var install = TestInstall.Create();
        var quotedWithTrailingSlash = "\"" + install.Directory + "\\\"";
        var userPath = $@"C:\Keep\One;{install.Directory};C:\Keep\Two;{quotedWithTrailingSlash}";

        var plan = MachineUninstaller.Inspect(install.Executable, userPath);

        Assert.Equal(2, plan.RemovedPathEntries);
        Assert.Equal(@"C:\Keep\One;C:\Keep\Two", plan.UpdatedUserPath);
        Assert.Equal(install.Directory, plan.InstallDirectory);
    }

    [Fact]
    public void Inspect_RefusesDirectoryContainingUnrelatedContent()
    {
        using var install = TestInstall.Create();
        File.WriteAllText(Path.Combine(install.Directory, "personal-notes.txt"), "keep");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MachineUninstaller.Inspect(install.Executable, install.Directory));

        Assert.Contains("files not owned by the release", exception.Message);
        Assert.Contains("personal-notes.txt", exception.Message);
    }

    [Fact]
    public void Execute_UpdatesPathThenSchedulesExactDirectoryAfterCurrentProcess()
    {
        using var install = TestInstall.Create();
        var originalPath = $@"C:\Keep;{install.Directory}";
        var plan = MachineUninstaller.Inspect(install.Executable, originalPath);
        var pathWrites = new List<string?>();
        string? scheduledDirectory = null;
        var scheduledPid = 0;

        MachineUninstaller.Execute(
            plan,
            new MachineUninstaller.Dependencies(
                pathWrites.Add,
                (directory, pid) => { scheduledDirectory = directory; scheduledPid = pid; },
                CurrentProcessId: 42));

        Assert.Equal([@"C:\Keep"], pathWrites);
        Assert.Equal(install.Directory, scheduledDirectory);
        Assert.Equal(42, scheduledPid);
    }

    [Fact]
    public void Execute_HelperLaunchFailure_RestoresOriginalPath()
    {
        using var install = TestInstall.Create();
        var originalPath = $@"C:\Keep;{install.Directory}";
        var plan = MachineUninstaller.Inspect(install.Executable, originalPath);
        var pathWrites = new List<string?>();

        Assert.Throws<InvalidOperationException>(() =>
            MachineUninstaller.Execute(
                plan,
                new MachineUninstaller.Dependencies(
                    pathWrites.Add,
                    (_, _) => throw new InvalidOperationException("launch failed"),
                    CurrentProcessId: 42)));

        Assert.Equal(2, pathWrites.Count);
        Assert.Equal(@"C:\Keep", pathWrites[0]);
        Assert.Equal(originalPath, pathWrites[1]);
    }

    [Fact]
    public void CleanupScript_WaitsForParentAndDeletesOnlyLiteralInstallDirectory()
    {
        var script = MachineUninstaller.BuildCleanupScript();

        Assert.Contains("Wait-Process -Id $ParentProcessId", script, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $InstallDirectory -Recurse -Force", script, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $ScriptPath", script, StringComparison.Ordinal);
    }

    private sealed class TestInstall : IDisposable
    {
        private readonly string _root;

        private TestInstall(string root, string directory, string executable)
        {
            _root = root;
            Directory = directory;
            Executable = executable;
        }

        public string Directory { get; }

        public string Executable { get; }

        public static TestInstall Create()
        {
            var root = Path.Combine(Path.GetTempPath(), $"unbramble-machine-uninstall-{Guid.NewGuid():N}");
            var directory = Path.Combine(root, "UnBramble");
            System.IO.Directory.CreateDirectory(directory);
            var executable = Path.Combine(directory, "unbramble.exe");
            File.WriteAllText(executable, "test");
            File.WriteAllText(Path.Combine(directory, "e_sqlite3.dll"), "test");
            File.WriteAllText(Path.Combine(directory, "LICENSES.md"), "test");
            return new TestInstall(root, directory, executable);
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(_root))
            {
                System.IO.Directory.Delete(_root, recursive: true);
            }
        }
    }
}
