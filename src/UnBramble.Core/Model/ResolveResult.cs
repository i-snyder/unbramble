namespace UnBramble.Core.Model;

/// <summary>A path/GUID lookup or bounded substring search. A null limit means uncapped.</summary>
public sealed record ResolveResult(IReadOnlyList<ResolveMatch> Matches, bool Truncated, int? Limit)
{
    public const int DefaultLimit = 20;
}
