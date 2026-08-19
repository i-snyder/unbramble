using System.Diagnostics;
using UnBramble.Core;
using UnBramble.Tests.TestSupport;

namespace UnBramble.Tests;

/// <summary>
/// A unit-style check for scripts/compare-unity-deps.ps1
/// against a handcrafted unbramble-unity-deps.json (standing in for the real output of
/// scripts/unity/ExportDependencies.cs, which needs an actual Unity Editor to produce).
/// Exercises the real script as a subprocess -- not a reimplementation of its logic in C# --
/// so a regression in the script itself is what this test is meant to catch.
///
/// Uses the apphost `unbramble.exe` that a plain `dotnet build` already copies next to this
/// test assembly (ProjectReference -&gt; UnBramble.Cli), not the publish/ output -- verify-all.ps1
/// runs `dotnet test` before `dotnet publish`, so depending on publish/ here would break that
/// ordering.
/// </summary>
public class CompareUnityDepsTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string ScriptPath = Path.Combine(RepoRoot, "scripts", "compare-unity-deps.ps1");
    private static readonly string ExePath = Path.Combine(AppContext.BaseDirectory, "unbramble.exe");

    [Fact]
    public void TrueDependencies_ContainmentHolds_ExitsZero()
    {
        using var fixture = FixtureCopy.Create();
        IndexFixture(fixture);

        // Both are real, true direct dependencies of Level.unity per expected-edges.json.
        var depsJson = WriteDepsJson(fixture.Root, """
            {
              "Assets/Scenes/Level.unity": ["Assets/Materials/Rock.mat", "Assets/Prefabs/Player.prefab"]
            }
            """);

        var (exitCode, stdOut, stdErr) = RunScript(fixture.Root, depsJson);

        Assert.True(exitCode == 0, $"expected exit 0, got {exitCode}\n-- stdout --\n{stdOut}\n-- stderr --\n{stdErr}");
        Assert.Contains("PASS", stdOut);
    }

    [Fact]
    public void FakeDependency_ContainmentViolation_ExitsOneAndNamesIt()
    {
        using var fixture = FixtureCopy.Create();
        IndexFixture(fixture);

        // Enemy.prefab is NOT a direct dependency of Level.unity (only reachable transitively,
        // via Player.prefab) -- a fabricated containment violation.
        var depsJson = WriteDepsJson(fixture.Root, """
            {
              "Assets/Scenes/Level.unity": ["Assets/Materials/Rock.mat", "Assets/Prefabs/Player.prefab", "Assets/Prefabs/Enemy.prefab"]
            }
            """);

        var (exitCode, stdOut, _) = RunScript(fixture.Root, depsJson);

        Assert.Equal(1, exitCode);
        Assert.Contains("Enemy.prefab", stdOut);
    }

    private static void IndexFixture(FixtureCopy fixture)
    {
        using var engine = UnBrambleEngine.Open(fixture.Root);
        engine.RunIndex(full: false);
    }

    private static string WriteDepsJson(string projectRoot, string content)
    {
        var path = Path.Combine(projectRoot, "unbramble-unity-deps.json");
        File.WriteAllText(path, content);
        return path;
    }

    private static (int ExitCode, string StdOut, string StdErr) RunScript(string projectRoot, string depsJson)
    {
        var startInfo = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(ScriptPath);
        startInfo.ArgumentList.Add("-ProjectRoot");
        startInfo.ArgumentList.Add(projectRoot);
        startInfo.ArgumentList.Add("-DepsJson");
        startInfo.ArgumentList.Add(depsJson);
        startInfo.ArgumentList.Add("-ExePath");
        startInfo.ArgumentList.Add(ExePath);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("failed to start pwsh");
        var stdOut = process.StandardOutput.ReadToEnd();
        var stdErr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, stdOut, stdErr);
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "UnBramble.sln")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("could not locate repo root (UnBramble.sln) walking up from " + AppContext.BaseDirectory);
    }
}
