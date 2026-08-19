namespace UnBramble.Core.Scanning;

/// <summary>
/// Mirrors Unity's own hidden-asset rules so the index doesn't diverge from what Unity
/// itself sees. Exclusion applies to the item and everything under it (directories are
/// never recursed into once excluded). Rules:
///   - names starting with '.'
///   - folders (not files) whose name ends with '~' (e.g. Samples~, Documentation~)
///   - files/folders named "cvs" (case-insensitive)
///   - files ending in ".tmp"
/// A ".meta" file's own hidden-ness additionally follows its owner: "foo.tmp.meta" is
/// hidden because "foo.tmp" (its owner) would be.
/// </summary>
public static class HiddenAssetRules
{
    private const string MetaSuffix = ".meta";

    public static bool IsHidden(string name, bool isDirectory)
    {
        if (!isDirectory && name.EndsWith(MetaSuffix, StringComparison.OrdinalIgnoreCase))
        {
            var ownerName = name[..^MetaSuffix.Length];
            // The owner could be a file or a folder; the .meta name alone doesn't say which,
            // so a meta is hidden if either interpretation of its owner would be.
            return IsHiddenCore(ownerName, isDirectory: false) || IsHiddenCore(ownerName, isDirectory: true);
        }

        return IsHiddenCore(name, isDirectory);
    }

    private static bool IsHiddenCore(string name, bool isDirectory)
    {
        if (name.Length == 0)
        {
            return false;
        }

        if (name[0] == '.')
        {
            return true;
        }

        if (string.Equals(name, "cvs", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (isDirectory && name[^1] == '~')
        {
            return true;
        }

        if (!isDirectory && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}
