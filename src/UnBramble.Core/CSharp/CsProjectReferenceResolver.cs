using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace UnBramble.Core.CSharp;

/// <summary>Package-owned generated project references and binding-only package source context.
/// Owned source assemblies always compile from indexed sources.</summary>
public static class CsProjectReferenceResolver
{
    public sealed record Resolution(IReadOnlyList<string> MetadataPaths, IReadOnlyList<string> ExternalAssemblyNames, string? Inputs);
    public sealed record BindingContext(IReadOnlyList<string> SourcePaths, string? Inputs);

    public static Resolution Resolve(string projectRoot, IReadOnlyList<string> projectReferencePaths)
    {
        var metadataPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var externalNames = new HashSet<string>(StringComparer.Ordinal);
        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var include in projectReferencePaths)
        {
            var targetPath = FullPath(include, projectRoot);
            if (targetPath is null || inputs.ContainsKey(targetPath))
            {
                continue;
            }

            // Record BEFORE reading. A concurrent replacement then invalidates the decision
            // next pass instead of blessing old parsed data with the replacement's timestamp.
            inputs[targetPath] = CsProjectReferenceInputs.CaptureFile(targetPath);
            var name = Path.GetFileNameWithoutExtension(targetPath);
            var dllPath = Path.Combine(projectRoot, "Library", "ScriptAssemblies", name + ".dll");
            inputs[dllPath] = CsProjectReferenceInputs.CaptureFile(dllPath);
            try
            {
                var target = XDocument.Load(targetPath);
                if (FindPackageSources(projectRoot, name, targetPath, target, inputs, trackSources: false).Count == 0)
                {
                    continue;
                }

                externalNames.Add(name);
                if (File.Exists(dllPath) && string.Equals(AssemblyName.GetAssemblyName(dllPath).Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    metadataPaths.Add(dllPath);
                }
            }
            catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException or BadImageFormatException)
            {
                // Missing/unusable package metadata remains incomplete binding, never a
                // guessed source assembly or an invocation of the referenced project.
            }
        }

        return new Resolution(metadataPaths.Order(StringComparer.Ordinal).ToList(), externalNames.Order(StringComparer.Ordinal).ToList(),
            Snapshot(inputs));
    }

    public static BindingContext GetBindingContext(string projectRoot, string assemblyName, string projectPath, XDocument project)
    {
        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sources = FindPackageSources(projectRoot, assemblyName, projectPath, project, inputs, trackSources: true);
        return new BindingContext(sources, Snapshot(inputs));
    }

    private static IReadOnlyList<string> FindPackageSources(string projectRoot, string assemblyName, string projectPath,
        XDocument project, Dictionary<string, string> inputs, bool trackSources)
    {
        var ns = project.Root?.Name.Namespace ?? XNamespace.None;
        var declaredName = project.Descendants(ns + "AssemblyName").LastOrDefault()?.Value.Trim();
        if (!string.IsNullOrEmpty(declaredName) && !string.Equals(declaredName, assemblyName, StringComparison.Ordinal))
        {
            return [];
        }

        var packageRoot = Path.GetFullPath(Path.Combine(projectRoot, "Library", "PackageCache"));
        var packagePrefix = packageRoot + Path.DirectorySeparatorChar;
        var sources = project.Descendants(ns + "Compile")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => FullPath(path!, Path.GetDirectoryName(projectPath)!))
            .ToList();
        if (sources.Any(path => path is null))
        {
            return [];
        }

        var cacheSources = sources.Where(path => path!.StartsWith(packagePrefix, StringComparison.OrdinalIgnoreCase))
            .Select(path => path!).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        if (cacheSources.Count == 0)
        {
            return [];
        }

        // A mixed project can be a real registry assembly extended through owned asmrefs.
        // Prove that its cache sources belong to that assembly; an arbitrary mixed XML
        // project must never gain permission to substitute a compiled DLL for owned code.
        var owners = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool HasMatchingOwner(string directory)
        {
            if (owners.TryGetValue(directory, out var known))
            {
                return known;
            }

            inputs.TryAdd(directory, CsProjectReferenceInputs.CaptureFile(directory));
            bool matches;
            try
            {
                var asmdefs = Directory.GetFiles(directory, "*.asmdef", SearchOption.TopDirectoryOnly);
                if (asmdefs.Length != 0)
                {
                    foreach (var path in asmdefs)
                    {
                        inputs.TryAdd(path, CsProjectReferenceInputs.CaptureFile(path));
                    }

                    var definition = asmdefs.Length == 1
                        ? JsonSerializer.Deserialize(File.ReadAllText(asmdefs[0]), AsmdefJsonContext.Default.AsmdefJson)
                        : null;
                    matches = definition?.Name == assemblyName;
                }
                else
                {
                    var parent = Path.GetDirectoryName(directory);
                    matches = parent is not null && (parent.StartsWith(packagePrefix, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(parent, packageRoot, StringComparison.OrdinalIgnoreCase)) && HasMatchingOwner(parent);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                matches = false;
            }

            owners[directory] = matches;
            return matches;
        }

        if (cacheSources.Count != sources.Count && cacheSources.Any(path => !HasMatchingOwner(Path.GetDirectoryName(path)!)))
        {
            return [];
        }

        if (trackSources)
        {
            foreach (var path in cacheSources)
            {
                inputs.TryAdd(path, CsProjectReferenceInputs.CaptureFile(path));
            }
        }

        return cacheSources;
    }

    private static string? Snapshot(Dictionary<string, string> inputs) => inputs.Count == 0 ? null
        : string.Join('\n', inputs.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value));

    private static string? FullPath(string path, string directory)
    {
        if (path.IndexOfAny(['\t', '\r', '\n']) >= 0)
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar), directory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}

/// <summary>Stat-only persisted snapshot; paths and numeric fields are separated by tabs.
/// Windows paths cannot contain tabs/newlines. No reflection JSON is required under AOT.</summary>
public static class CsProjectReferenceInputs
{
    public static string CaptureFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists)
            {
                return string.Join('\t', path, "1", info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture), info.Length.ToString(CultureInfo.InvariantCulture));
            }

            var directory = new DirectoryInfo(path);
            if (directory.Exists)
            {
                return string.Join('\t', path, "2", directory.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture), "0");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Stat failure is not evidence that a previously usable input is still usable.
        }

        return path + "\t0\t0\t0";
    }

    public static bool Matches(string? snapshot, Dictionary<string, string>? capturedFiles = null)
    {
        if (string.IsNullOrEmpty(snapshot))
        {
            return true;
        }

        foreach (var line in snapshot.Split('\n'))
        {
            var separator = line.IndexOf('\t');
            if (separator <= 0)
            {
                return false;
            }

            var path = line[..separator];
            if (capturedFiles is null || !capturedFiles.TryGetValue(path, out var captured))
            {
                captured = CaptureFile(path);
                capturedFiles?.Add(path, captured);
            }

            var capturedSeparator = captured.IndexOf('\t');
            if (capturedSeparator < 0 || !line.AsSpan(separator).SequenceEqual(captured.AsSpan(capturedSeparator)))
            {
                return false;
            }
        }

        return true;
    }
}
