namespace UnBramble.Core.Model;

/// <summary>
/// A persisted real-path -> project-relative-prefix mapping for one scan root (or one
/// junction target reached while walking a root). The watcher, possibly
/// running in a different process, rewrites real-path filesystem events back to project
/// paths using this same persisted mapping — never a recomputed one.
/// </summary>
public sealed record RootMapping(string RealPath, string ProjectPrefix);
