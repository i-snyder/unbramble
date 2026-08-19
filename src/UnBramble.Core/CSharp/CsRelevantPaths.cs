namespace UnBramble.Core.CSharp;

/// <summary>
/// Single definition of "a path whose presence/absence/content could affect C# assembly
/// analysis": `.cs` scripts and `.asmdef`/`.asmref`
/// assembly definitions. Used both for dirty-path checks (<c>UnBrambleEngine.RunCsAnalysis</c>'s
/// skip gate) and for the sweep diff's removed-path composition
/// (<see cref="UnBramble.Core.Store.SweepDiff.RemovedCsRelevant"/>) — kept as one shared
/// definition so the two checks can never drift apart.
/// </summary>
public static class CsRelevantPaths
{
    public static bool IsCsRelevant(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".asmref", StringComparison.OrdinalIgnoreCase);
}
