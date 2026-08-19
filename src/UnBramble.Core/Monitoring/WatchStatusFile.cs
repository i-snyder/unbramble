using System.Globalization;
using System.Text.Json;
using UnBramble.Core.Config;

namespace UnBramble.Core.Monitoring;

/// <summary>
/// The watcher's `.unbramble/watch.status.json` file -- a structured, richer sibling of
/// <see cref="Freshness.HeartbeatFile"/> (same directory, same atomic-write discipline,
/// deliberately a SEPARATE file rather than repurposing the heartbeat, which exists for a
/// different consumer -- freshness staleness checks -- and is written on a different, tighter
/// cadence). Written by a live `watch` process (via <see cref="WatchStatusTracker"/>) on every
/// significant progress event; read by `unbramble monitor`, a separate process that attaches to
/// whatever `watch` process (started by anyone) is currently running against the same project.
///
/// Hand-formatted/parsed via <see cref="Utf8JsonWriter"/>/<see cref="JsonDocument"/> rather than
/// a reflection-based serializer, same reasoning as <see cref="Freshness.HeartbeatFile"/>: this
/// is a fixed, hand-maintained shape, and staying off <c>JsonSerializer</c> (without a
/// source-generated context) keeps this NativeAOT-safe for free (UnBramble.Cli publishes
/// PublishAot=true).
/// </summary>
public static class WatchStatusFile
{
    public const string RelativePath = UnBramblePaths.WatchStatusRelativePath;

    private const int SchemaVersion = 1;

    public static string PathFor(string projectRoot) =>
        UnBramblePaths.RelativeTo(projectRoot, RelativePath);

    /// <summary>
    /// Atomic write: temp file in the same directory, then <see cref="File.Move(string, string, bool)"/>
    /// (overwrite) -- a reader never sees a partial file. Best-effort, same as
    /// <see cref="Freshness.HeartbeatFile.Write"/>: a missed write just leaves the previous
    /// status in place a little longer, which is never treated as a correctness problem by any
    /// consumer -- this file is purely presentational and can never produce a wrong
    /// dependency-graph answer.
    /// </summary>
    public static void Write(string projectRoot, WatchStatusSnapshot snapshot)
    {
        var path = PathFor(projectRoot);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(directory, $"watch.status.tmp-{Guid.NewGuid():N}");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteSnapshot(writer, snapshot);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup only, same as HeartbeatFile.Write's own catch -- see its doc comment.
            }
        }
    }

    private static void WriteSnapshot(Utf8JsonWriter w, WatchStatusSnapshot s)
    {
        w.WriteStartObject();
        w.WriteNumber("schemaVersion", s.SchemaVersion);
        w.WriteNumber("pid", s.Pid);
        w.WriteString("projectRoot", s.ProjectRoot);
        w.WriteString("state", s.State.ToString());
        w.WriteString("startedUtc", s.StartedUtc.ToString("O", CultureInfo.InvariantCulture));
        w.WriteString("lastUpdatedUtc", s.LastUpdatedUtc.ToString("O", CultureInfo.InvariantCulture));

        if (s.Current is { } current)
        {
            w.WriteStartObject("current");
            w.WriteString("kind", current.Kind);
            w.WriteString("startedUtc", current.StartedUtc.ToString("O", CultureInfo.InvariantCulture));
            w.WriteString("lastStepUtc", current.LastStepUtc.ToString("O", CultureInfo.InvariantCulture));
            WriteNullableString(w, "unit", current.Unit);
            WriteNullableString(w, "phase", current.Phase);
            WriteNullableString(w, "detail", current.Detail);
            w.WriteEndObject();
        }
        else
        {
            w.WriteNull("current");
        }

        w.WriteStartObject("totals");
        w.WriteNumber("batchesApplied", s.Totals.BatchesApplied);
        w.WriteNumber("selfHealSweeps", s.Totals.SelfHealSweeps);
        w.WriteNumber("selfHealSweepsSkipped", s.Totals.SelfHealSweepsSkipped);
        w.WriteNumber("errorResyncs", s.Totals.ErrorResyncs);
        w.WriteNumber("cacheFullRebuilds", s.Totals.CacheFullRebuilds);
        w.WriteNumber("cacheIncrementalBuilds", s.Totals.CacheIncrementalBuilds);
        w.WriteNumber("cacheReusedNoOp", s.Totals.CacheReusedNoOp);
        w.WriteNumber("filesAdded", s.Totals.FilesAdded);
        w.WriteNumber("filesChanged", s.Totals.FilesChanged);
        w.WriteNumber("filesRemoved", s.Totals.FilesRemoved);
        w.WriteEndObject();

        if (s.LastPass is { } lastPass)
        {
            w.WriteStartObject("lastPass");
            w.WriteString("kind", lastPass.Kind);
            w.WriteString("completedUtc", lastPass.CompletedUtc.ToString("O", CultureInfo.InvariantCulture));
            w.WriteNumber("totalMs", lastPass.TotalMs);
            w.WriteStartArray("steps");
            foreach (var step in lastPass.Steps)
            {
                w.WriteStartObject();
                w.WriteString("label", step.Label);
                w.WriteNumber("ms", step.Ms);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            WriteNullableNumber(w, "added", lastPass.Added);
            WriteNullableNumber(w, "changed", lastPass.Changed);
            WriteNullableNumber(w, "removed", lastPass.Removed);
            w.WriteEndObject();
        }
        else
        {
            w.WriteNull("lastPass");
        }

        w.WriteEndObject();
    }

    private static void WriteNullableString(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null)
        {
            w.WriteNull(name);
        }
        else
        {
            w.WriteString(name, value);
        }
    }

    private static void WriteNullableNumber(Utf8JsonWriter w, string name, int? value)
    {
        if (value is null)
        {
            w.WriteNull(name);
        }
        else
        {
            w.WriteNumber(name, value.Value);
        }
    }

    /// <summary>Null for any absent/unreadable/corrupt/unrecognized-shape status file -- same
    /// "never wrong, only sometimes stale" contract as <see cref="Freshness.HeartbeatFile.TryRead"/>:
    /// a caller (the `monitor` render loop) treats null as "no data yet / can't tell", never as
    /// an error worth crashing over.</summary>
    public static WatchStatusSnapshot? TryRead(string projectRoot)
    {
        var path = PathFor(projectRoot);
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(stream);
            return ParseSnapshot(doc.RootElement);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            return null;
        }
    }

    private static WatchStatusSnapshot ParseSnapshot(JsonElement root)
    {
        var schemaVersion = root.GetProperty("schemaVersion").GetInt32();
        var pid = root.GetProperty("pid").GetInt32();
        var projectRoot = root.GetProperty("projectRoot").GetString()!;
        var state = Enum.Parse<WatchActivityState>(root.GetProperty("state").GetString()!);
        var startedUtc = ParseUtc(root.GetProperty("startedUtc"));
        var lastUpdatedUtc = ParseUtc(root.GetProperty("lastUpdatedUtc"));

        CurrentActivity? current = null;
        if (root.TryGetProperty("current", out var currentEl) && currentEl.ValueKind == JsonValueKind.Object)
        {
            current = new CurrentActivity(
                currentEl.GetProperty("kind").GetString()!,
                ParseUtc(currentEl.GetProperty("startedUtc")),
                ParseUtc(currentEl.GetProperty("lastStepUtc")),
                GetNullableString(currentEl, "unit"),
                GetNullableString(currentEl, "phase"),
                GetNullableString(currentEl, "detail"));
        }

        var totalsEl = root.GetProperty("totals");
        var totals = new WatchTotals(
            totalsEl.GetProperty("batchesApplied").GetInt64(),
            totalsEl.GetProperty("selfHealSweeps").GetInt64(),
            totalsEl.GetProperty("selfHealSweepsSkipped").GetInt64(),
            totalsEl.GetProperty("errorResyncs").GetInt64(),
            totalsEl.GetProperty("cacheFullRebuilds").GetInt64(),
            totalsEl.GetProperty("cacheIncrementalBuilds").GetInt64(),
            totalsEl.GetProperty("cacheReusedNoOp").GetInt64(),
            totalsEl.GetProperty("filesAdded").GetInt64(),
            totalsEl.GetProperty("filesChanged").GetInt64(),
            totalsEl.GetProperty("filesRemoved").GetInt64());

        LastPassSummary? lastPass = null;
        if (root.TryGetProperty("lastPass", out var lastPassEl) && lastPassEl.ValueKind == JsonValueKind.Object)
        {
            var steps = new List<PassStep>();
            foreach (var stepEl in lastPassEl.GetProperty("steps").EnumerateArray())
            {
                steps.Add(new PassStep(stepEl.GetProperty("label").GetString()!, stepEl.GetProperty("ms").GetDouble()));
            }

            lastPass = new LastPassSummary(
                lastPassEl.GetProperty("kind").GetString()!,
                ParseUtc(lastPassEl.GetProperty("completedUtc")),
                lastPassEl.GetProperty("totalMs").GetDouble(),
                steps,
                GetNullableInt(lastPassEl, "added"),
                GetNullableInt(lastPassEl, "changed"),
                GetNullableInt(lastPassEl, "removed"));
        }

        return new WatchStatusSnapshot(schemaVersion, pid, projectRoot, state, startedUtc, lastUpdatedUtc, current, totals, lastPass);
    }

    private static DateTime ParseUtc(JsonElement element) =>
        DateTime.Parse(element.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static string? GetNullableString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static int? GetNullableInt(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt32() : null;
}
