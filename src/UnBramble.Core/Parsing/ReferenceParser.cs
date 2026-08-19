using System.Globalization;
using System.Text.RegularExpressions;

namespace UnBramble.Core.Parsing;

/// <summary>
/// Line-streaming reference extraction. Never loads a whole file into memory — single pass per file, small bounded lookahead for
/// UnityEvent method-name capture only. Dispatch is by file extension, not by
/// <c>files.kind</c>, so guid-less reference sources (ProjectSettings/*.asset) get the same
/// YAML treatment as any other .asset.
/// </summary>
public sealed class ReferenceParser
{
    private const int MethodNameLookaheadLines = 8;
    private const int ContextMaxLength = 160;
    private const string NullGuid = "00000000000000000000000000000000";
    private const string DatabasePrefix = "project://database/";

    // Unity-YAML extensions: document-boundary state machine, GameObject/component
    // tracking, UnityEvent method-name lookahead.
    private static readonly HashSet<string> YamlExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".prefab", ".unity", ".asset", ".mat", ".anim", ".controller", ".overrideController",
        ".physicMaterial", ".physicsMaterial2D", ".mixer", ".flare", ".fontsettings", ".guiskin",
        ".renderTexture", ".spriteatlas", ".terrainlayer", ".brush", ".playable", ".mask",
        ".preset", ".shadervariants", ".lighting", ".giparams", ".cubemap",
    };

    // JSON-ish extensions: no document state, guid regex run per line only.
    private static readonly HashSet<string> JsonIshExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".shadergraph", ".vfx", ".shadersubgraph", ".asmdef", ".asmref",
    };

    // UI Toolkit extensions: guid-form pass (no document state) plus a path-form pass.
    private static readonly HashSet<string> UiToolkitExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".uxml", ".uss", ".tss",
    };

    /// <summary>
    /// Parses one reference-source file's content. <paramref name="sourceProjectPath"/> is
    /// used only for UI Toolkit path-ref normalization (relative-to-source-directory
    /// resolution); dispatch itself is purely by extension. Returns
    /// <see cref="ParsedFileRefs.Empty"/> for extensions with no recognized content pass
    /// (binary assets participate only via their .meta).
    /// </summary>
    public ParsedFileRefs ParseContentSource(string fullPath, string sourceProjectPath, string? ownGuid)
    {
        var extension = Path.GetExtension(sourceProjectPath);

        if (YamlExtensions.Contains(extension))
        {
            var isAnimFile = string.Equals(extension, ".anim", StringComparison.OrdinalIgnoreCase);
            return ParseYaml(fullPath, ownGuid, isAnimFile);
        }

        if (string.Equals(extension, ".asmdef", StringComparison.OrdinalIgnoreCase))
        {
            var (asmdefGuidRefs, dllRefs) = ScanAsmdef(fullPath, ownGuid);
            return new ParsedFileRefs(asmdefGuidRefs, [], [], [], [], dllRefs);
        }

        if (JsonIshExtensions.Contains(extension))
        {
            var refs = ScanGuidOnlyLines(fullPath, ownGuid);
            return new ParsedFileRefs(refs, [], [], [], []);
        }

        if (UiToolkitExtensions.Contains(extension))
        {
            return ParseUiToolkit(fullPath, sourceProjectPath, ownGuid);
        }

        return ParsedFileRefs.Empty;
    }

    /// <summary>
    /// Parses a sibling .meta file's importer section for refs (e.g. a ScriptedImporter's
    /// `script:` guid), attributed to the owner asset. The owner's own identity line
    /// self-filters via the generic self-reference rule. No document state (source_classid /
    /// source_fileid are NULL, per the DDL comment).
    /// </summary>
    public IReadOnlyList<GuidRefRow> ParseMetaOwnerRefs(string metaFullPath, string? ownGuid) =>
        File.Exists(metaFullPath) ? ScanGuidOnlyLines(metaFullPath, ownGuid) : [];

    private static List<GuidRefRow> ScanGuidOnlyLines(string fullPath, string? ownGuid)
    {
        var result = new List<GuidRefRow>();
        using var reader = new StreamReader(fullPath);

        string? line;
        var lineNo = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            var matches = RegexPatterns.GuidRef().Matches(line);
            if (matches.Count == 0)
            {
                continue;
            }

            var context = TrimContext(line);
            foreach (Match m in matches)
            {
                var guid = m.Groups[1].Value.ToLowerInvariant();
                if (ShouldKeep(guid, ownGuid))
                {
                    result.Add(new GuidRefRow(guid, lineNo, null, null, null, context));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// `.asmdef`: the ordinary guid pass (its `"references": ["GUID:…"]` entries) plus a second
    /// extraction for `precompiledReferences`, which names managed plugin assemblies by FILE NAME
    /// with no guid anywhere — invisible to the guid regex, so before this every such edge was
    /// silently absent. That absence was not merely an under-report on `who-uses SomePlugin.dll`:
    /// a plugin DLL is an ordinary `dead-candidates` candidate, so one referenced only this way
    /// had no inbound edge at all and could be emitted as `provenDead`, the exact false-positive
    /// class the asymmetric-risk invariant exists to forbid.
    ///
    /// Stays a line-streaming state machine rather than a JSON parse (the array is routinely split
    /// across lines, and Unity writes one entry per line) because the line NUMBER is half the
    /// answer — `System.Text.Json` would resolve the values and lose where they came from.
    /// </summary>
    private static (List<GuidRefRow> GuidRefs, List<DllRefRow> DllRefs) ScanAsmdef(string fullPath, string? ownGuid)
    {
        var guidRefs = new List<GuidRefRow>();
        var dllRefs = new List<DllRefRow>();
        using var reader = new StreamReader(fullPath);

        string? line;
        var lineNo = 0;
        var insidePrecompiledArray = false;

        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            var context = TrimContext(line);

            foreach (Match m in RegexPatterns.GuidRef().Matches(line))
            {
                var guid = m.Groups[1].Value.ToLowerInvariant();
                if (ShouldKeep(guid, ownGuid))
                {
                    guidRefs.Add(new GuidRefRow(guid, lineNo, null, null, null, context));
                }
            }

            var scanFrom = 0;
            if (!insidePrecompiledArray)
            {
                var keyIndex = line.IndexOf("\"precompiledReferences\"", StringComparison.OrdinalIgnoreCase);
                if (keyIndex < 0)
                {
                    continue;
                }

                insidePrecompiledArray = true;
                var openIndex = line.IndexOf('[', keyIndex);
                if (openIndex < 0)
                {
                    // The '[' is on a later line; nothing on THIS line can be an entry, and
                    // scanning from 0 would re-capture the key name itself as a value.
                    continue;
                }

                scanFrom = openIndex + 1;
            }

            var closeIndex = line.IndexOf(']', scanFrom);
            var scanTo = closeIndex < 0 ? line.Length : closeIndex;
            foreach (Match m in RegexPatterns.JsonStringLiteral().Matches(line[scanFrom..scanTo]))
            {
                var raw = m.Groups[1].Value.Trim();
                if (raw.Length > 0)
                {
                    dllRefs.Add(new DllRefRow(raw, NormalizeDllName(raw), lineNo, context));
                }
            }

            if (closeIndex >= 0)
            {
                insidePrecompiledArray = false;
            }
        }

        return (guidRefs, dllRefs);
    }

    /// <summary>
    /// The match key for a `precompiledReferences` entry: Unity resolves these against the plugin
    /// assembly's FILE NAME, so the last path segment lowercased is exactly the identity to store.
    /// <see cref="Path.GetFileName(string)"/> is defensive — the serialized value is a bare name in
    /// every real asmdef, but a hand-edited one carrying a directory prefix should still match
    /// rather than silently resolve to nothing.
    /// </summary>
    private static string NormalizeDllName(string raw)
    {
        var name = Path.GetFileName(raw.Replace('\\', '/'));
        return (name.Length == 0 ? raw : name).ToLowerInvariant();
    }

    private static ParsedFileRefs ParseYaml(string fullPath, string? ownGuid, bool isAnimFile)
    {
        var guidRefs = new List<GuidRefRow>();
        var gameObjects = new List<GameObjectRow>();
        var componentLinks = new List<ComponentGameObjectRow>();
        var nameHints = new List<NameHintRow>();

        using var streamReader = new StreamReader(fullPath);
        using var window = new LineWindow(streamReader);

        int? currentClassId = null;
        string? currentFileId = null;
        var currentIsGameObjectDoc = false;
        var pathTracker = new YamlPropertyPathTracker();

        string? line;
        var lineNo = 0;
        while ((line = window.Advance()) is not null)
        {
            lineNo++;

            var boundary = RegexPatterns.YamlDocumentBoundary().Match(line);
            if (boundary.Success)
            {
                currentClassId = int.Parse(boundary.Groups[1].Value, CultureInfo.InvariantCulture);
                currentFileId = boundary.Groups[2].Value;
                currentIsGameObjectDoc = currentClassId == 1;
                pathTracker.Reset();
                continue;
            }

            // Before ANY of the early-continue branches below: every line shapes the property-
            // path stack, whether or not it carries a ref itself.
            pathTracker.OnLine(line);

            if (currentIsGameObjectDoc && currentFileId is not null)
            {
                var nameMatch = RegexPatterns.YamlName().Match(line);
                if (nameMatch.Success)
                {
                    var name = nameMatch.Groups[1].Value.TrimEnd('\r');
                    if (name.Length > 0)
                    {
                        gameObjects.Add(new GameObjectRow(currentFileId, name));
                    }

                    // m_Name is a plain string field; it is never also a guid-ref line.
                    continue;
                }
            }

            if (currentFileId is not null)
            {
                var goLinkMatch = RegexPatterns.YamlGameObjectLink().Match(line);
                if (goLinkMatch.Success)
                {
                    var goFileId = goLinkMatch.Groups[1].Value;
                    if (goFileId != "0")
                    {
                        componentLinks.Add(new ComponentGameObjectRow(currentFileId, goFileId));
                    }

                    // m_GameObject is a same-file fileID-only link; never also a guid-ref line.
                    continue;
                }
            }

            if (RegexPatterns.AddressablesGroupSelfGuidField().IsMatch(line))
            {
                // The Addressables AssetGroup's own top-level internal identity — excluded
                // outright rather than captured-then-permanently-unresolved. See RegexPatterns
                // for the exact structural distinction from a real m_SerializeEntries target ref.
                continue;
            }

            if (isAnimFile)
            {
                // Animation-clip events: a by-name invocation with no
                // adjacent guid at all -- never an edge, captured as negative evidence only.
                var functionNameMatch = RegexPatterns.YamlFunctionName().Match(line);
                if (functionNameMatch.Success)
                {
                    var value = functionNameMatch.Groups[1].Value.TrimEnd('\r').Trim();
                    if (value.Length > 0)
                    {
                        nameHints.Add(new NameHintRow(value, "anim-event", lineNo, null));
                    }

                    continue; // m_FunctionName is a plain string field, never also a guid-ref line.
                }
            }

            var guidMatches = RegexPatterns.GuidRef().Matches(line);
            var hasTarget = line.Contains("m_Target:", StringComparison.Ordinal);

            if (guidMatches.Count == 0)
            {
                if (hasTarget)
                {
                    // Guid-less m_Target: the common
                    // same-asset UnityEvent binding case (button and handler in one prefab).
                    // There is no resolvable target guid at all -- captured as a name_hints row
                    // (never a refs row) so the bound method name still enters the later
                    // liveness screen set instead of vanishing silently.
                    var (localMethodName, localTargetTypeName) = CaptureEventCallLookahead(window);
                    if (localMethodName is not null)
                    {
                        nameHints.Add(new NameHintRow(localMethodName, "unityevent-local", lineNo, ExtractTypePart(localTargetTypeName)));
                    }
                }

                continue;
            }

            var (methodName, targetTypeName) = hasTarget ? CaptureEventCallLookahead(window) : (null, null);
            var context = TrimContext(line);
            var propertyPath = pathTracker.CurrentPath;

            foreach (Match m in guidMatches)
            {
                var guid = m.Groups[1].Value.ToLowerInvariant();
                if (ShouldKeep(guid, ownGuid))
                {
                    guidRefs.Add(new GuidRefRow(guid, lineNo, currentClassId, currentFileId, methodName, context, targetTypeName, propertyPath));
                }
            }
        }

        return new ParsedFileRefs(guidRefs, [], gameObjects, componentLinks, nameHints);
    }

    /// <summary>
    /// Bounded lookahead from a `m_Target:` line (guid-carrying or guid-less) for the two
    /// fields a UnityEvent persistent-call entry serializes alongside it: `m_MethodName` and
    /// (Unity 2019.1+) `m_TargetAssemblyTypeName`. Stops the moment
    /// another `m_Target:` line or a new document boundary is reached, returning whatever of
    /// the two fields was found before that point (never attributes a field found past the
    /// boundary to the wrong call entry).
    /// </summary>
    private static (string? MethodName, string? TargetTypeName) CaptureEventCallLookahead(LineWindow window)
    {
        var lookahead = window.PeekAhead(MethodNameLookaheadLines);
        string? methodName = null;
        string? targetTypeName = null;

        foreach (var candidate in lookahead)
        {
            if (candidate.Contains("m_Target:", StringComparison.Ordinal))
            {
                break; // another m_Target line reached first — stop.
            }

            if (RegexPatterns.YamlDocumentBoundary().IsMatch(candidate))
            {
                break; // a new document boundary reached first — stop.
            }

            if (targetTypeName is null)
            {
                var typeMatch = RegexPatterns.YamlTargetAssemblyTypeName().Match(candidate);
                if (typeMatch.Success)
                {
                    var value = typeMatch.Groups[1].Value.TrimEnd('\r').Trim();
                    targetTypeName = value.Length == 0 ? null : value;
                    continue;
                }
            }

            if (methodName is null)
            {
                var methodMatch = RegexPatterns.YamlMethodName().Match(candidate);
                if (methodMatch.Success)
                {
                    var value = methodMatch.Groups[1].Value.TrimEnd('\r');
                    methodName = value.Length == 0 ? null : value;
                }
            }
        }

        return (methodName, targetTypeName);
    }

    /// <summary>Extracts the type part of a raw `m_TargetAssemblyTypeName` value ("Foo, Game" -&gt; "Foo") for the guid-less name_hints capture. Null in, null out.</summary>
    private static string? ExtractTypePart(string? rawTargetTypeName)
    {
        if (rawTargetTypeName is null)
        {
            return null;
        }

        var commaIdx = rawTargetTypeName.IndexOf(',');
        var typePart = (commaIdx < 0 ? rawTargetTypeName : rawTargetTypeName[..commaIdx]).Trim();
        return typePart.Length == 0 ? null : typePart;
    }

    private static ParsedFileRefs ParseUiToolkit(string fullPath, string sourceProjectPath, string? ownGuid)
    {
        var guidRefs = new List<GuidRefRow>();
        var pathRefs = new List<PathRefRow>();
        var sourceDir = GetProjectDir(sourceProjectPath);

        using var reader = new StreamReader(fullPath);
        string? line;
        var lineNo = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            var context = TrimContext(line);

            // Pass one: guid=... / guid:... forms (same regex as every other source).
            foreach (Match m in RegexPatterns.GuidRef().Matches(line))
            {
                var guid = m.Groups[1].Value.ToLowerInvariant();
                if (ShouldKeep(guid, ownGuid))
                {
                    guidRefs.Add(new GuidRefRow(guid, lineNo, null, null, null, context));
                }
            }

            // Pass two: path-based forms (src=, @import, url()). resource("...") is a
            // documented exclusion — it simply never matches any of these patterns.
            foreach (var raw in ExtractPathCandidates(line))
            {
                if (raw.Contains("guid=", StringComparison.OrdinalIgnoreCase))
                {
                    // Already captured by pass one — do not double-count.
                    continue;
                }

                var norm = NormalizePathRef(raw, sourceDir);
                pathRefs.Add(new PathRefRow(raw, norm, lineNo, context));
            }
        }

        return new ParsedFileRefs(guidRefs, pathRefs, [], [], []);
    }

    private static IEnumerable<string> ExtractPathCandidates(string line)
    {
        var importMatch = RegexPatterns.UssImport().Match(line);
        if (importMatch.Success)
        {
            yield return importMatch.Groups[1].Success ? importMatch.Groups[1].Value : importMatch.Groups[2].Value;
            yield break; // an @import line is fully handled by this one match.
        }

        foreach (Match m in RegexPatterns.UssUrl().Matches(line))
        {
            yield return m.Groups[1].Value;
        }

        foreach (Match m in RegexPatterns.UxmlSrc().Matches(line))
        {
            yield return m.Groups[1].Value;
        }
    }

    private static string GetProjectDir(string projectRelativePath)
    {
        var idx = projectRelativePath.LastIndexOf('/');
        return idx < 0 ? "" : projectRelativePath[..idx];
    }

    /// <summary>
    /// Computes target_path_norm exactly as specified: strip the project://database/ prefix
    /// and any ?query/#fragment; a leading '/' is project-root-relative; a value already
    /// starting with Assets/ or Packages/ is used as-is; otherwise it is relative to the
    /// source file's own directory. Safe to compute at parse time — it depends only on the
    /// source file's own path. Resolution to a target file id happens ONLY at query time.
    /// </summary>
    private static string NormalizePathRef(string raw, string sourceDir)
    {
        var value = raw;

        var cutIdx = value.IndexOfAny(['?', '#']);
        if (cutIdx >= 0)
        {
            value = value[..cutIdx];
        }

        if (value.StartsWith(DatabasePrefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[DatabasePrefix.Length..];
        }

        value = value.Replace('\\', '/');

        string combined;
        if (value.StartsWith('/'))
        {
            combined = value[1..];
        }
        else if (value.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                 value.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
        {
            combined = value;
        }
        else
        {
            combined = string.IsNullOrEmpty(sourceDir) ? value : $"{sourceDir}/{value}";
        }

        return NormalizeDotSegments(combined);
    }

    private static string NormalizeDotSegments(string path)
    {
        var stack = new List<string>();
        foreach (var segment in path.Split('/'))
        {
            switch (segment)
            {
                case "" or ".":
                    continue;
                case "..":
                    if (stack.Count > 0)
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }

                    continue;
                default:
                    stack.Add(segment);
                    break;
            }
        }

        return string.Join('/', stack);
    }

    private static bool ShouldKeep(string targetGuid, string? ownGuid)
    {
        if (string.Equals(targetGuid, NullGuid, StringComparison.Ordinal))
        {
            return false;
        }

        if (ownGuid is not null && string.Equals(targetGuid, ownGuid, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static string TrimContext(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length > ContextMaxLength ? trimmed[..ContextMaxLength] : trimmed;
    }
}
