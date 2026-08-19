namespace UnBramble.Core.Config;

/// <summary>
/// Project-level configuration, optionally overridden by an unbramble.json at the project
/// root. Absent unbramble.json is the normal case; these defaults apply then.
/// </summary>
public sealed class UnBrambleConfig
{
    public static readonly string[] DefaultRoots = ["Assets", "Packages", "LocalPackages", "ProjectSettings"];

    public static readonly string[] DefaultExcludeDirs =
        ["Library", "Temp", "Obj", "obj", "Logs", ".git", ".plastic", ".svn", ".vs", ".idea", ".collab", UnBramblePaths.StateDirName];

    public const string DefaultDbPath = UnBramblePaths.DbRelativePath;

    public string[] Roots { get; set; } = DefaultRoots;

    public string[] ExcludeDirs { get; set; } = DefaultExcludeDirs;

    public string DbPath { get; set; } = DefaultDbPath;

    public LivenessConfig Liveness { get; set; } = new();

    public WatchConfig Watch { get; set; } = new();
}

/// <summary>
/// `unbramble.json`'s `watch` section (see "Auto-spawn watcher" in docs/architecture.md).
/// <see cref="AutoStart"/> defaults to <c>true</c>: when a query has to
/// run the inline sweep because no fresh watcher heartbeat exists, it fires off a detached
/// hidden background watcher worker so the next query can skip the sweep instead. Set to <c>false</c> to
/// opt a project out entirely -- queries stay exactly as correct either way (see
/// <see cref="UnBrambleEngine.EnsureFresh"/>'s own invariant), just never faster than a sweep on
/// their own.
/// </summary>
public sealed class WatchConfig
{
    public bool AutoStart { get; set; } = true;
}

/// <summary>
/// `unbramble.json`'s `liveness.allowlist` — glob patterns (matched against project-relative
/// paths) whose files are SEEDED into LiveFiles's fixed point, not merely suppressed from
/// output. A file kept alive for reflection reasons keeps its own dependencies alive too;
/// suppression-only would let an allowlisted file's private dependency chain surface as proven
/// dead (a false positive by construction) — the same semantics screens use (screens are the
/// tool-computed member of this family; the allowlist is the user-supplied one).
/// </summary>
public sealed class LivenessConfig
{
    public string[] Allowlist { get; set; } = [];
}
