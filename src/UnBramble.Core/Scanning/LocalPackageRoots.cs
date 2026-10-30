using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UnBramble.Core.Config;
using UnBramble.Core.Model;

namespace UnBramble.Core.Scanning;

/// <summary>Local folder dependencies use Unity's virtual Packages/name path even when their
/// files live outside the project. Relative file: paths are relative to Packages/manifest.json.</summary>
public static class LocalPackageRoots
{
    public const string ManifestProjectPath = "Packages/manifest.json";

    /// <summary>Fingerprints every discovered dependency, including aliases whose files an
    /// earlier scan root owns. Windows path casing, separators, and enumeration order don't
    /// change topology; changing a physical target or logical package name does.</summary>
    public static string Fingerprint(IReadOnlyList<RootMapping> roots)
    {
        var tokens = roots.Select(root =>
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.RealPath.Replace('/', Path.DirectorySeparatorChar)))
                    .Replace('\\', '/').ToUpperInvariant()
                + '\0' + root.ProjectPrefix.Replace('\\', '/').TrimEnd('/').ToUpperInvariant())
            .OrderBy(token => token, StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', tokens))));
    }

    /// <summary>Discovers current local folder dependencies, resolving root links so callers
    /// can compare the result with persisted scan roots. Missing targets and malformed JSON
    /// warn; transient filesystem failures propagate rather than publishing a partial index.</summary>
    public static IReadOnlyList<RootMapping> Discover(string projectRoot, UnBrambleConfig config, List<string> warnings)
    {
        if (!config.Roots.Any(root => root.Replace('\\', '/').TrimEnd('/').Equals("Packages", StringComparison.OrdinalIgnoreCase))
            || config.ExcludeDirs.Contains("Packages", StringComparer.OrdinalIgnoreCase))
        {
            return [];
        }

        var packagesPath = Path.Combine(projectRoot, "Packages");
        JsonDocument document;
        try
        {
            using var stream = new FileStream(Path.Combine(packagesPath, "manifest.json"), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            document = JsonDocument.Parse(stream);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
        catch (JsonException ex)
        {
            warnings.Add($"warning: could not parse '{ManifestProjectPath}' for local package dependencies: {ex.Message}");
            return [];
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                warnings.Add($"warning: '{ManifestProjectPath}' must contain a JSON object to discover local packages");
                return [];
            }

            if (!document.RootElement.TryGetProperty("dependencies", out var dependencies))
            {
                return [];
            }

            if (dependencies.ValueKind != JsonValueKind.Object)
            {
                warnings.Add($"warning: dependencies in '{ManifestProjectPath}' must be a JSON object to discover local packages");
                return [];
            }

            HashSet<string>? embeddedNames = null;
            var results = new List<RootMapping>();
            foreach (var dependency in dependencies.EnumerateObject().OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Name, StringComparer.Ordinal))
            {
                if (dependency.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var version = dependency.Value.GetString()!;
                if (!version.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                    || version.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // Registry/Git dependencies are resolved from PackageCache.
                }

                var name = dependency.Name;
                if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                    || name.Contains('/') || name.Contains('\\') || HiddenAssetRules.IsHidden(name, isDirectory: true))
                {
                    warnings.Add($"warning: ignoring local package dependency with invalid name '{name}' in '{ManifestProjectPath}'");
                    continue;
                }

                if (config.ExcludeDirs.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                // An embedded package takes precedence over the manifest dependency. The
                // ordinary Packages scan already owns this path and its reference sources.
                embeddedNames ??= FindEmbeddedNames(packagesPath, config, warnings);
                if (embeddedNames.Contains(name) || TryGetDirectoryAttributes(Path.Combine(packagesPath, name)) is not null)
                {
                    continue;
                }

                var localPath = version["file:".Length..];
                if (localPath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)
                    || localPath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // Unity expands tarballs into PackageCache; don't scan archives.
                }

                if (string.IsNullOrWhiteSpace(localPath))
                {
                    warnings.Add($"warning: local package '{name}' has an empty file: path in '{ManifestProjectPath}'");
                    continue;
                }

                string fullPath;
                try
                {
                    fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localPath.Replace('/', Path.DirectorySeparatorChar), packagesPath));
                }
                catch (ArgumentException ex)
                {
                    warnings.Add($"warning: local package '{name}' has an invalid file: path: {ex.Message}");
                    continue;
                }

                var attributes = TryGetDirectoryAttributes(fullPath);
                if (attributes is null)
                {
                    warnings.Add($"warning: local package '{name}' folder is missing or isn't a directory: '{fullPath}'");
                    continue;
                }

                if (attributes.Value.HasFlag(FileAttributes.ReparsePoint))
                {
                    var target = Directory.ResolveLinkTarget(fullPath, returnFinalTarget: true);
                    if (target is null || TryGetDirectoryAttributes(target.FullName) is null)
                    {
                        warnings.Add($"warning: skipping dangling local package link 'Packages/{name}'");
                        continue;
                    }

                    fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.FullName));
                }

                results.Add(new RootMapping(fullPath, $"Packages/{name}"));
            }

            return results;
        }
    }

    private static HashSet<string> FindEmbeddedNames(string packagesPath, UnBrambleConfig config, List<string> warnings)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.EnumerateDirectories(packagesPath).OrderBy(path => path, StringComparer.Ordinal))
        {
            var directoryName = Path.GetFileName(directory);
            if (HiddenAssetRules.IsHidden(directoryName, isDirectory: true)
                || config.ExcludeDirs.Contains(directoryName, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                using var stream = new FileStream(Path.Combine(directory, "package.json"), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                using var package = JsonDocument.Parse(stream);
                if (package.RootElement.ValueKind == JsonValueKind.Object
                    && package.RootElement.TryGetProperty("name", out var name)
                    && name.ValueKind == JsonValueKind.String
                    && name.GetString() is { Length: > 0 } packageName)
                {
                    names.Add(packageName);
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Ordinary directories aren't necessarily embedded packages.
            }
            catch (JsonException ex)
            {
                warnings.Add($"warning: could not parse 'Packages/{directoryName}/package.json' for embedded package precedence: {ex.Message}");
            }
        }

        return names;
    }

    private static FileAttributes? TryGetDirectoryAttributes(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Directory) ? attributes : null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}
