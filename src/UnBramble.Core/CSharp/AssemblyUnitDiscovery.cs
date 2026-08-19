using System.Text.Json;
using UnBramble.Core.Model;

namespace UnBramble.Core.CSharp;

/// <summary>
/// Discovers C# compilation units from the current file inventory. Pure string/JSON
/// work — no Roslyn — so it's cheap to rerun on every index.
/// </summary>
public static class AssemblyUnitDiscovery
{
    public const string PredefinedAssemblyCSharp = "Assembly-CSharp";
    public const string PredefinedFirstPass = "Assembly-CSharp-firstpass";
    public const string PredefinedEditor = "Assembly-CSharp-Editor";

    private sealed record AsmdefEntry(long FileId, string Path, string Dir, string? Guid, string Name, List<string> RawReferences, bool NoEngineReferences);

    /// <summary>
    /// Builds the current set of assembly units from the store's file inventory.
    /// <paramref name="toFullPath"/> maps a project-relative path to an absolute path for
    /// reading .asmdef/.asmref content (never loaded from the DB — the store doesn't persist
    /// file content).
    /// </summary>
    public static (IReadOnlyList<AssemblyUnit> Units, IReadOnlyList<string> Warnings) Discover(
        IReadOnlyList<CsFileEntry> files, Func<string, string> toFullPath)
    {
        var warnings = new List<string>();
        var asmdefs = new List<AsmdefEntry>();
        var asmrefTargetByDir = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var scripts = new List<(long FileId, string Path)>();

        // Pass 1: collect asmdef/asmref/script rows, parsing asmdef JSON along the way.
        foreach (var row in files)
        {
            if (row.IdentityOnly)
            {
                continue; // Library/PackageCache scripts never participate as their own assembly.
            }

            if (row.Kind == FileKind.Script)
            {
                scripts.Add((row.Id, row.Path));
                continue;
            }

            var extension = Path.GetExtension(row.Path);
            if (extension.Equals(".asmdef", StringComparison.OrdinalIgnoreCase))
            {
                var dir = DirOf(row.Path);
                var parsed = TryParseAsmdef(toFullPath(row.Path), row.Path, warnings);
                if (parsed is null)
                {
                    continue;
                }

                var name = string.IsNullOrWhiteSpace(parsed.Name) ? Path.GetFileNameWithoutExtension(row.Path) : parsed.Name!;
                asmdefs.Add(new AsmdefEntry(row.Id, row.Path, dir, row.Guid, name, parsed.References ?? [], parsed.NoEngineReferences));
            }
            else if (extension.Equals(".asmref", StringComparison.OrdinalIgnoreCase))
            {
                var dir = DirOf(row.Path);
                var parsed = TryParseAsmref(toFullPath(row.Path), row.Path, warnings);
                if (parsed?.Reference is { Length: > 0 } reference)
                {
                    asmrefTargetByDir[dir] = reference;
                }
            }
        }

        // Pass 2: resolve GUID: references and asmref targets to assembly names.
        var guidToName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asmdef in asmdefs)
        {
            if (asmdef.Guid is not null)
            {
                guidToName[asmdef.Guid] = asmdef.Name;
            }
        }

        string ResolveReferenceToken(string token)
        {
            if (token.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase))
            {
                var guid = token["GUID:".Length..];
                return guidToName.TryGetValue(guid, out var name) ? name : token;
            }

            return token;
        }

        // dir -> assembly name, for every directory that OWNS an asmdef or asmref. Nearest-
        // ancestor lookup for scripts walks up from the script's own directory against this map.
        var ownerDirToName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asmdef in asmdefs)
        {
            ownerDirToName[asmdef.Dir] = asmdef.Name;
        }

        foreach (var (dir, token) in asmrefTargetByDir)
        {
            if (ownerDirToName.ContainsKey(dir))
            {
                continue; // An asmdef in the same folder as an asmref wins (shouldn't normally happen).
            }

            var resolved = ResolveReferenceToken(token);
            ownerDirToName[dir] = resolved;
        }

        // Pass 3: assign every script to its nearest ancestor owner dir, else a predefined
        // assembly (Plugins/Standard Assets -> firstpass; /Editor/ outside any asmdef -> Editor;
        // else Assembly-CSharp). Per-platform variants and other asmdef exotica aren't handled.
        var scriptsByAssembly = new Dictionary<string, List<(long, string)>>(StringComparer.Ordinal);

        void AddScript(string assemblyName, (long FileId, string Path) script)
        {
            if (!scriptsByAssembly.TryGetValue(assemblyName, out var list))
            {
                list = [];
                scriptsByAssembly[assemblyName] = list;
            }

            list.Add(script);
        }

        foreach (var script in scripts)
        {
            var owner = FindNearestOwnerDir(DirOf(script.Path), ownerDirToName);
            if (owner is not null)
            {
                AddScript(owner, script);
                continue;
            }

            AddScript(ClassifyPredefined(script.Path), script);
        }

        // Assemble AssemblyUnit list: every discovered asmdef (even with zero scripts, so it
        // can still participate in the reference graph/topological order) plus any predefined
        // assemblies that ended up with at least one script.
        var units = new List<AssemblyUnit>();
        foreach (var asmdef in asmdefs)
        {
            var refs = asmdef.RawReferences.Select(ResolveReferenceToken).Distinct(StringComparer.Ordinal).ToList();
            scriptsByAssembly.TryGetValue(asmdef.Name, out var assignedScripts);
            units.Add(new AssemblyUnit(asmdef.Name, asmdef.FileId, asmdef.Path, refs, assignedScripts ?? [], asmdef.NoEngineReferences));
        }

        foreach (var predefinedName in new[] { PredefinedAssemblyCSharp, PredefinedFirstPass, PredefinedEditor })
        {
            if (scriptsByAssembly.TryGetValue(predefinedName, out var assignedScripts) && assignedScripts.Count > 0)
            {
                units.Add(new AssemblyUnit(predefinedName, null, null, [], assignedScripts, NoEngineReferences: false));
            }
        }

        return (units, warnings);
    }

    /// <summary>
    /// Topological order over asmdef references (compile dependencies first). Undefined
    /// references (an asmdef referencing a name not present in this project, e.g. a built-in
    /// Unity module) are simply absent from the graph and don't block ordering. A reference
    /// cycle (shouldn't occur in a valid Unity project, but never crash on bad input) degrades
    /// to appending the remaining units in their original order, with a warning.
    /// </summary>
    public static (IReadOnlyList<AssemblyUnit> Ordered, IReadOnlyList<string> Warnings) TopologicalOrder(IReadOnlyList<AssemblyUnit> units)
    {
        var byName = units.ToDictionary(u => u.Name, StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<AssemblyUnit>();
        var warnings = new List<string>();
        var cyclic = false;

        void Visit(AssemblyUnit unit)
        {
            if (visited.Contains(unit.Name) || cyclic)
            {
                return;
            }

            if (!visiting.Add(unit.Name))
            {
                cyclic = true;
                warnings.Add($"warning: circular .asmdef reference chain detected involving '{unit.Name}' — falling back to declaration order for C# analysis");
                return;
            }

            foreach (var refName in unit.References)
            {
                if (byName.TryGetValue(refName, out var refUnit))
                {
                    Visit(refUnit);
                    if (cyclic)
                    {
                        return;
                    }
                }
            }

            visiting.Remove(unit.Name);
            visited.Add(unit.Name);
            ordered.Add(unit);
        }

        foreach (var unit in units)
        {
            Visit(unit);
            if (cyclic)
            {
                return (units, warnings);
            }
        }

        return (ordered, warnings);
    }

    private static string? FindNearestOwnerDir(string scriptDir, Dictionary<string, string> ownerDirToName)
    {
        var dir = scriptDir;
        while (true)
        {
            if (ownerDirToName.TryGetValue(dir, out var name))
            {
                return name;
            }

            var idx = dir.LastIndexOf('/');
            if (idx < 0)
            {
                return ownerDirToName.TryGetValue(dir, out var rootName) ? rootName : null;
            }

            dir = dir[..idx];
        }
    }

    private static string ClassifyPredefined(string scriptPath)
    {
        if (scriptPath.StartsWith("Assets/Plugins/", StringComparison.OrdinalIgnoreCase) ||
            scriptPath.StartsWith("Assets/Standard Assets/", StringComparison.OrdinalIgnoreCase))
        {
            return PredefinedFirstPass;
        }

        foreach (var segment in scriptPath.Split('/'))
        {
            if (segment.Equals("Editor", StringComparison.OrdinalIgnoreCase))
            {
                return PredefinedEditor;
            }
        }

        return PredefinedAssemblyCSharp;
    }

    private static string DirOf(string projectRelativePath)
    {
        var idx = projectRelativePath.LastIndexOf('/');
        return idx < 0 ? "" : projectRelativePath[..idx];
    }

    private static AsmdefJson? TryParseAsmdef(string fullPath, string ownerPath, List<string> warnings)
    {
        try
        {
            var text = File.ReadAllText(fullPath);
            return JsonSerializer.Deserialize(text, AsmdefJsonContext.Default.AsmdefJson);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            warnings.Add($"warning: could not parse asmdef '{ownerPath}': {ex.Message}");
            return null;
        }
    }

    private static AsmrefJson? TryParseAsmref(string fullPath, string ownerPath, List<string> warnings)
    {
        try
        {
            var text = File.ReadAllText(fullPath);
            return JsonSerializer.Deserialize(text, AsmdefJsonContext.Default.AsmrefJson);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            warnings.Add($"warning: could not parse asmref '{ownerPath}': {ex.Message}");
            return null;
        }
    }
}
