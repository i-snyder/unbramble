using UnBramble.Core.Config;

namespace UnBramble.Core;

public enum StatsSnapshotStatus { Missing, Incompatible, Incomplete, Available }

/// <summary>Project settings and the last committed index visible to a stats reader. An
/// available engine owns a single SQLite read transaction until the snapshot is disposed.</summary>
public sealed record StatsSnapshot(
    StatsSnapshotStatus Status,
    string ProjectRoot,
    string UnityVersion,
    string DbPath,
    UnBrambleConfig Config,
    IReadOnlyList<string> Warnings,
    int? StoredSchemaVersion,
    UnBrambleEngine? Engine) : IDisposable
{
    public void Dispose() => Engine?.Dispose();
}
