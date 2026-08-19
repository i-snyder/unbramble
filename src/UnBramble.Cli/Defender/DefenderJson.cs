using System.Text.Json.Serialization;

namespace UnBramble.Cli.Defender;

/// <summary>
/// On-disk DTOs for the Defender-exclusion feature's three small JSON files, all under
/// `.unbramble/`: the plan handed from the non-elevated parent to the single elevated
/// `powershell.exe` hop (<see cref="DefenderPlanFileJson"/>), that hop's result handed back
/// (<see cref="DefenderResultFileJson"/> -- "stdout can't cross the elevation boundary cleanly,"
/// see <c>DefenderApply</c>'s own doc comment), and the durable idempotency record
/// (<see cref="DefenderStateFileJson"/>). Source-generated (NativeAOT-safe), same convention as
/// <c>UnBramble.Cli.Json.CliJsonContext</c> -- kept as a separate context rather than folded into
/// that one since these are internal state files, not part of any `--json` CLI output contract.
/// </summary>
public sealed class DefenderPlanEntryJson
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("value")]
    public required string Value { get; init; }
}

public sealed class DefenderPlanFileJson
{
    [JsonPropertyName("entries")]
    public required List<DefenderPlanEntryJson> Entries { get; init; }
}

public sealed class DefenderResultEntryJson
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("value")]
    public required string Value { get; init; }

    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

public sealed class DefenderResultFileJson
{
    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("results")]
    public required List<DefenderResultEntryJson> Results { get; init; }
}

public sealed class DefenderStateEntryJson
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("value")]
    public required string Value { get; init; }

    [JsonPropertyName("outcome")]
    public required string Outcome { get; init; }
}

public sealed class DefenderStateFileJson
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("decision")]
    public required string Decision { get; init; }

    [JsonPropertyName("appliedAtUtc")]
    public string? AppliedAtUtc { get; init; }

    [JsonPropertyName("exePath")]
    public string? ExePath { get; init; }

    [JsonPropertyName("entries")]
    public required List<DefenderStateEntryJson> Entries { get; init; }
}

[JsonSerializable(typeof(DefenderPlanFileJson))]
[JsonSerializable(typeof(DefenderResultFileJson))]
[JsonSerializable(typeof(DefenderStateFileJson))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class DefenderJsonContext : JsonSerializerContext
{
}
