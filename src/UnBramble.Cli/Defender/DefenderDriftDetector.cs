using UnBramble.Core.Monitoring;

namespace UnBramble.Cli.Defender;

/// <summary>
/// Passive drift detection: after a normal index run, if the latest
/// <see cref="IndexHistoryLog"/> pass shows a root whose resolved target (a) isn't in the
/// recorded applied-entries set and (b) scanned meaningfully slower per file than its sibling
/// roots, that's the observable signature of a new junction target Defender is still cold-scanning
/// (see <see cref="Core.Scanning.Scanner.SlowDirThresholdMs"/>'s own doc comment on why this
/// specific I/O shape is Defender's signature, not a code regression). Never auto-elevates --
/// only ever a one-line warning pointing at `unbramble defender setup`.
/// </summary>
public static class DefenderDriftDetector
{
    /// <summary>Only worth comparing when there are at least two roots with real per-file
    /// throughput to compare against each other, and only flags a root as an outlier when it's
    /// both meaningfully slower than the sibling average AND not trivially fast in absolute terms
    /// (a handful of files at any speed is noise, not a signal).</summary>
    private const double OutlierMultiplier = 3.0;
    private const double MinMsPerFileToFlag = 0.5;

    public static string? CheckForWarning(IndexHistoryEntry latest, DefenderStateFileJson? state)
    {
        // Baseline throughput comes from EVERY root with real file counts (junction or not) --
        // an ordinary in-tree root is exactly the useful "Defender isn't slowing this one down"
        // baseline, and requiring it to also be a junction would leave too few data points to
        // compare against on a project with only one root-level link.
        var allWithThroughput = latest.Roots
            .Where(r => r.Files > 0)
            .Select(r => (Root: r, MsPerFile: r.ElapsedMs / r.Files))
            .ToList();

        if (allWithThroughput.Count < 2)
        {
            return null;
        }

        var coveredTargets = (state?.Entries ?? [])
            .Where(e => e.Type == "path" && e.Outcome is "added" or "already-covered")
            .Select(e => e.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Only a junction/symlink root (ResolvedTarget != null) is actually something a path
        // exclusion could cover -- an ordinary in-tree root is never a candidate, only ever part
        // of the comparison baseline above.
        var candidates = allWithThroughput.Where(x => x.Root.ResolvedTarget is not null);

        // Compared against the SIBLING average (every other root, excluding the candidate itself)
        // rather than the whole-set average -- with only a couple of roots, folding the outlier
        // into its own comparison baseline self-skews the average high enough that it can never
        // clear its own multiplier.
        foreach (var (root, msPerFile) in candidates.OrderByDescending(x => x.MsPerFile))
        {
            if (coveredTargets.Contains(root.ResolvedTarget!))
            {
                continue;
            }

            var siblings = allWithThroughput.Where(x => !ReferenceEquals(x.Root, root)).ToList();
            if (siblings.Count == 0)
            {
                continue;
            }

            var siblingAverage = siblings.Average(x => x.MsPerFile);
            if (msPerFile >= MinMsPerFileToFlag && msPerFile > siblingAverage * OutlierMultiplier)
            {
                return $"New junction target '{root.ResolvedTarget}' isn't covered by your Defender exclusions -- " +
                    "run 'unbramble defender setup'.";
            }
        }

        return null;
    }
}
