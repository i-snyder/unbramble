using System.Text.Json.Serialization;

namespace UnBramble.Core.CSharp;

/// <summary>DTO for a `.asmdef`/`.asmref` file's JSON shape. Hand-mapped
/// with a source-generated context — never reflection-based serialization under NativeAOT.</summary>
public sealed class AsmdefJson
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("references")]
    public List<string>? References { get; set; }

    [JsonPropertyName("includePlatforms")]
    public List<string>? IncludePlatforms { get; set; }

    [JsonPropertyName("excludePlatforms")]
    public List<string>? ExcludePlatforms { get; set; }

    [JsonPropertyName("defineConstraints")]
    public List<string>? DefineConstraints { get; set; }

    [JsonPropertyName("noEngineReferences")]
    public bool NoEngineReferences { get; set; }
}

/// <summary>An `.asmref`'s JSON shape: joins its target assembly by name or by `GUID:xxxx`.</summary>
public sealed class AsmrefJson
{
    [JsonPropertyName("reference")]
    public string? Reference { get; set; }
}

[JsonSerializable(typeof(AsmdefJson))]
[JsonSerializable(typeof(AsmrefJson))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class AsmdefJsonContext : JsonSerializerContext
{
}
