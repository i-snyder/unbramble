using System.Text.Json.Serialization;

namespace UnBramble.Core.Addressables;

/// <summary>`Packages/manifest.json`'s shape, scoped to the one field the Addressables detector
/// needs. Hand-mapped with a source-generated context — never reflection-based serialization
/// under NativeAOT (same discipline as <see cref="UnBramble.Core.CSharp.AsmdefJson"/>).</summary>
public sealed class PackageManifestJson
{
    [JsonPropertyName("dependencies")]
    public Dictionary<string, string>? Dependencies { get; set; }
}

/// <summary>
/// `Packages/packages-lock.json`'s shape, scoped to the one field the detector needs. Unity
/// writes this file with the ACTUALLY RESOLVED version of every package (including transitive
/// dependencies), which is more reliable than `manifest.json`'s requested version string — a
/// manifest entry can be a version range, a `file:`/git reference with no clean version at all,
/// or (rarely) resolved differently than requested; the lock file records what Unity's Package
/// Manager actually settled on. Preferred over the manifest when both are present.
/// </summary>
public sealed class PackagesLockJson
{
    [JsonPropertyName("dependencies")]
    public Dictionary<string, PackagesLockEntry>? Dependencies { get; set; }
}

public sealed class PackagesLockEntry
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

[JsonSerializable(typeof(PackageManifestJson))]
[JsonSerializable(typeof(PackagesLockJson))]
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
internal partial class PackageManifestJsonContext : JsonSerializerContext
{
}
