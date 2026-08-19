namespace UnBramble.Core.Model;

public sealed record ScanResult(
    IReadOnlyList<ScannedFileEntry> Entries,
    IReadOnlyList<RootMapping> Roots,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ScanRootStat>? RootStats = null,
    IReadOnlyList<SlowDirEntry>? SlowDirs = null)
{
    /// <summary>Per top-level scan root (an ordinary configured root, the identity-only
    /// Library/PackageCache root, or a root-level reparse-point/junction target), one entry
    /// each: the real-project-cost discriminator between "Defender cold-scanning one specific
    /// junction target" (clustered slowness under one <see cref="ScanRootStat.ResolvedTarget"/>,
    /// low CPU) and "a genuine code regression" (uniform slowdown across every root, or a delta
    /// outside the scan phase entirely). Defaults to empty for any caller that constructs a
    /// <see cref="ScanResult"/> without this (nearly every existing test) -- never null.</summary>
    public IReadOnlyList<ScanRootStat> RootStats { get; init; } = RootStats ?? [];

    /// <summary>Individual directories whose enumeration took longer than
    /// <see cref="Scanning.Scanner.SlowDirThresholdMs"/> -- the top offenders, capped by the
    /// caller (<see cref="Monitoring.IndexHistoryLog"/> keeps the top 20 per pass). Defaults to
    /// empty, same reasoning as <see cref="RootStats"/>.</summary>
    public IReadOnlyList<SlowDirEntry> SlowDirs { get; init; } = SlowDirs ?? [];
}

/// <param name="ProjectPrefix">The root's project-relative prefix, e.g. "Assets/SomePlugin".</param>
/// <param name="ResolvedTarget">
/// For a junction/reparse-point root, the real filesystem path it resolves to (e.g.
/// "D:\repos\[Package] BigPackage") -- null for an ordinary in-tree root. This is the field a
/// human (or `Add-MpPreference -ExclusionPath`) needs, since it's frequently OUTSIDE the project
/// root entirely and therefore invisible to a project-root-only exclusion.
/// </param>
public sealed record ScanRootStat(
    string ProjectPrefix,
    string? ResolvedTarget,
    bool IsJunction,
    long Dirs,
    long Files,
    double ElapsedMs);

public sealed record SlowDirEntry(string ProjectPrefix, double ElapsedMs);
