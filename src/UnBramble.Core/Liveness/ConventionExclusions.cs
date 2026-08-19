namespace UnBramble.Core.Liveness;

/// <summary>
/// Infrastructure the graph structurally cannot reach because Unity/tooling consumes it by
/// convention rather than by reference — nothing points at an asmdef, and roots never reach it,
/// yet deleting it silently restructures compilation or IL-stripping. Excluded from the
/// candidate universe outright (never proven dead, never advisory dead — simply never
/// considered), counted separately in output. Shipped as a reviewed constant list.
/// </summary>
public static class ConventionExclusions
{
    /// <summary>
    /// True if <paramref name="projectRelativePath"/> is excluded from the dead-candidates
    /// universe by convention: `*.asmdef`, `*.asmref`, `link.xml`, `csc.rsp` (anywhere), and any
    /// file literally named `package.json` (including the special-cased `Packages/manifest.json`,
    /// which is not itself named `package.json` but plays the same "consumed by convention,
    /// never referenced" role for the top-level project manifest).
    ///
    /// Matches on the bare filename regardless of root: `unbramble.json` allows arbitrary
    /// configured scan roots (`LocalPackages` is even a listed default), so an embedded package's
    /// manifest can live outside `Packages/` (e.g. `LocalPackages/com.studio.shared/package.json`).
    /// Restricting the match to a `Packages/` root would miss that file — it has no inbound refs
    /// (a package manifest is never referenced by anything) and would get proven dead. Prefer
    /// over-exclusion to under-exclusion for infrastructure Unity/UPM consumes by convention.
    /// </summary>
    public static bool IsExcluded(string projectRelativePath)
    {
        var extension = Path.GetExtension(projectRelativePath);
        if (string.Equals(extension, ".asmdef", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".asmref", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var fileName = GetFileName(projectRelativePath);
        if (string.Equals(fileName, "link.xml", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "csc.rsp", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(projectRelativePath, "Packages/manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(fileName, "package.json", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static string GetFileName(string projectRelativePath)
    {
        var idx = projectRelativePath.LastIndexOf('/');
        return idx < 0 ? projectRelativePath : projectRelativePath[(idx + 1)..];
    }
}
