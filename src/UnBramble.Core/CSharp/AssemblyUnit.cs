namespace UnBramble.Core.CSharp;

/// <summary>
/// One C# compilation unit: either a real `.asmdef` or one of Unity's
/// three predefined assemblies (Assembly-CSharp, Assembly-CSharp-firstpass,
/// Assembly-CSharp-Editor) synthesized for scripts with no ancestor asmdef.
/// </summary>
public sealed record AssemblyUnit(
    string Name,
    long? AsmdefFileId,
    string? AsmdefPath,
    IReadOnlyList<string> References,
    IReadOnlyList<(long FileId, string Path)> Scripts,
    bool NoEngineReferences)
{
    public bool IsPredefined => AsmdefFileId is null;
}
