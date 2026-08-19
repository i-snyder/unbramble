using UnBramble.Core.Config;

namespace UnBramble.Core.Scanning;

/// <summary>
/// One root-level junction/symlink: <paramref name="ProjectPrefix"/> is the project-relative
/// path the link lives at (e.g. "Assets/SomePlugin"), <paramref name="ResolvedTarget"/> is the real
/// filesystem path it resolves to -- frequently outside the project root entirely, which is
/// exactly the case a project-root-only Defender path exclusion misses (see
/// docs/validation-runbook.md's Defender cold-scan findings).
/// </summary>
public readonly record struct RootLinkTarget(string ProjectPrefix, string ResolvedTarget);

/// <summary>
/// Standalone enumeration of every root-level junction/symlink target for a project's configured
/// roots (plus Library/PackageCache), without performing a full recursive <see cref="Scanner.Scan"/>.
/// Extracted so the Windows-Defender-exclusion setup (Defender's real-time scanning is
/// the dominant cost of a cold index, concentrated in root-level junction targets that live
/// outside the project root) can list exclusion candidates BEFORE the very first sweep ever runs
/// -- <see cref="Scanner"/> only produces this information as a byproduct of a full scan, which is
/// too late for "make the first cold index already fast."
///
/// "Root-level" here means exactly what <see cref="Scanner"/> itself treats as a top-level
/// junction fan-out point: the root directory itself, or one of its own immediate children (see
/// <see cref="Scanner.Walk"/>'s `deferReparseChildren` parameter, which only defers/replays a
/// reparse point encountered in a root's own top-level `Walk` call -- anything deeper is walked
/// inline as part of whichever job already claimed it). Reusing <see cref="Scanner.IsReparsePoint"/>
/// and <see cref="Scanner.TryResolveLinkTarget"/> directly (rather than re-deriving equivalent
/// logic) means this can never disagree with the real scan about what counts as a link or where
/// it resolves to.
/// </summary>
public static class RootLinkTargets
{
    /// <summary>
    /// Enumerates every root-level junction/symlink target across <paramref name="config"/>'s
    /// configured roots and the identity-only Library/PackageCache root. Order is deterministic
    /// (roots in configured order, then PackageCache; within a root, directory-enumeration order)
    /// and duplicate real-path targets (two links pointing at the same place) are collapsed to
    /// their first occurrence, matching <see cref="Scanner"/>'s own realpath-dedupe convention.
    /// Fails soft: a root that doesn't exist, or a directory this process can't enumerate, is
    /// silently skipped -- exactly like <see cref="Scanner.ScanRoot"/>/<see cref="Scanner.Walk"/>
    /// already do, since this is an optional convenience feature, never a correctness-load-bearing
    /// path.
    /// </summary>
    public static IReadOnlyList<RootLinkTarget> Enumerate(string projectRoot, UnBrambleConfig config)
    {
        var results = new List<RootLinkTarget>();
        var seenRealTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rootRelative in config.Roots)
        {
            CollectRoot(projectRoot, rootRelative, results, seenRealTargets);
        }

        CollectRoot(projectRoot, Path.Combine("Library", "PackageCache"), results, seenRealTargets);

        return results;
    }

    private static void CollectRoot(
        string projectRoot, string rootRelativeOnDisk, List<RootLinkTarget> results, HashSet<string> seenRealTargets)
    {
        var full = Path.Combine(projectRoot, rootRelativeOnDisk.Replace('/', Path.DirectorySeparatorChar));
        if (!Directory.Exists(full))
        {
            return;
        }

        var projectPrefix = rootRelativeOnDisk.Replace('\\', '/');
        var realPath = Path.GetFullPath(full);

        if (Scanner.IsReparsePoint(realPath))
        {
            TryAdd(projectPrefix, realPath, results, seenRealTargets);

            // The root itself is a link -- its children live under the resolved target, which is
            // already recorded as one exclusion candidate covering the whole subtree. No need to
            // separately enumerate its immediate children the way the non-link branch below does.
            return;
        }

        List<FileSystemInfo> entries;
        try
        {
            entries = [.. new DirectoryInfo(realPath).EnumerateFileSystemInfos()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var info in entries)
        {
            FileAttributes attributes;
            try
            {
                attributes = info.Attributes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (!attributes.HasFlag(FileAttributes.Directory) || !attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            if (HiddenAssetRules.IsHidden(info.Name, isDirectory: true))
            {
                continue;
            }

            TryAdd($"{projectPrefix}/{info.Name}", info.FullName, results, seenRealTargets);
        }
    }

    private static void TryAdd(string projectPrefix, string linkPath, List<RootLinkTarget> results, HashSet<string> seenRealTargets)
    {
        var target = Scanner.TryResolveLinkTarget(linkPath);
        if (target is null)
        {
            return; // Dangling link -- nothing to exclude; Scan() itself would warn, this helper just skips.
        }

        if (seenRealTargets.Add(target))
        {
            results.Add(new RootLinkTarget(projectPrefix, target));
        }
    }
}
