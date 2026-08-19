using UnBramble.Core.Query;

namespace UnBramble.Core.Model;

public sealed record KindCounts(int Assets, int Scripts, int Folders, int Settings)
{
    public int Total => Assets + Scripts + Folders + Settings;
}

public sealed record StatsResult(
    KindCounts Files,
    int IdentityOnlyCount,
    int GuidLessCount,
    long DbSizeBytes,
    int SchemaVersion,
    EdgeStats Edges,
    IReadOnlyList<string> RefSourceExtensions,
    CsStats Cs);
