using System.Text.Json;

namespace UnBramble.Core.Config;

/// <summary>
/// Loads the optional unbramble.json at a project's root. Uses the JsonDocument/JsonElement
/// DOM API rather than POCO deserialization — fully NativeAOT-safe without needing a
/// source-generated serializer context for such a small, loosely-typed shape. Unknown keys
/// produce a warning, not an error; a missing file is the normal case and yields defaults.
/// </summary>
public static class UnBrambleConfigLoader
{
    private static readonly HashSet<string> KnownKeys =
        new(StringComparer.OrdinalIgnoreCase) { "roots", "excludeDirs", "dbPath", "liveness", "watch" };

    public static UnBrambleConfig Load(string projectRoot, out List<string> warnings)
    {
        warnings = [];
        var config = new UnBrambleConfig();
        var path = Path.Combine(projectRoot, "unbramble.json");

        if (!File.Exists(path))
        {
            return config;
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            warnings.Add($"warning: could not read unbramble.json: {ex.Message}");
            return config;
        }
        catch (UnauthorizedAccessException ex)
        {
            warnings.Add($"warning: could not read unbramble.json: {ex.Message}");
            return config;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            warnings.Add($"warning: unbramble.json is not valid JSON ({ex.Message}); using defaults");
            return config;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                warnings.Add("warning: unbramble.json root is not an object; using defaults");
                return config;
            }

            foreach (var property in root.EnumerateObject())
            {
                if (!KnownKeys.Contains(property.Name))
                {
                    warnings.Add($"warning: unbramble.json has unknown key '{property.Name}' (ignored)");
                }
            }

            if (root.TryGetProperty("roots", out var rootsElement) && rootsElement.ValueKind == JsonValueKind.Array)
            {
                config.Roots = ReadStringArray(rootsElement);
            }

            if (root.TryGetProperty("excludeDirs", out var excludeElement) && excludeElement.ValueKind == JsonValueKind.Array)
            {
                config.ExcludeDirs = ReadStringArray(excludeElement);
            }

            if (root.TryGetProperty("dbPath", out var dbPathElement) && dbPathElement.ValueKind == JsonValueKind.String)
            {
                config.DbPath = dbPathElement.GetString() ?? config.DbPath;
            }

            if (root.TryGetProperty("liveness", out var livenessElement) && livenessElement.ValueKind == JsonValueKind.Object)
            {
                if (livenessElement.TryGetProperty("allowlist", out var allowlistElement) && allowlistElement.ValueKind == JsonValueKind.Array)
                {
                    config.Liveness.Allowlist = ReadStringArray(allowlistElement);
                }
            }

            if (root.TryGetProperty("watch", out var watchElement) && watchElement.ValueKind == JsonValueKind.Object)
            {
                if (watchElement.TryGetProperty("autoStart", out var autoStartElement)
                    && autoStartElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    config.Watch.AutoStart = autoStartElement.GetBoolean();
                }
            }
        }

        return config;
    }

    private static string[] ReadStringArray(JsonElement array)
    {
        var result = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var value = item.GetString();
                if (!string.IsNullOrEmpty(value))
                {
                    result.Add(value);
                }
            }
        }

        return [.. result];
    }
}
