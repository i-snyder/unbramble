using System.Globalization;
using System.Text.Json;
using UnBramble.Core.Config;

namespace UnBramble.Cli.Defender;

/// <summary>The plan file handed from the non-elevated parent to the single elevated
/// `powershell.exe` hop (which reads it via <c>Get-MpPreference</c>-style processing built by
/// <see cref="DefenderApply"/>): `.unbramble/defender-plan.json`.</summary>
public static class DefenderPlanFile
{
    private const string FileName = "defender-plan.json";

    public static string PathFor(string projectRoot) => Path.Combine(UnBramblePaths.StateDirFor(projectRoot), FileName);

    public static void Write(string path, IReadOnlyList<DefenderEntry> entries)
    {
        var dto = new DefenderPlanFileJson
        {
            Entries = [.. entries.Select(e => new DefenderPlanEntryJson { Type = e.TypeString, Value = e.Value })],
        };

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(dto, DefenderJsonContext.Default.DefenderPlanFileJson));
    }

    public static IReadOnlyList<DefenderEntry>? TryRead(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize(json, DefenderJsonContext.Default.DefenderPlanFileJson);
            return dto?.Entries.Select(e => new DefenderEntry(DefenderEntry.ParseType(e.Type), e.Value)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>The result file the single elevated `powershell.exe` hop writes back for the
/// non-elevated parent to read: `.unbramble/defender-result.json`. Source of truth instead of
/// stdout, since stdout can't cross the elevation boundary cleanly (the `runas` verb requires
/// `UseShellExecute = true`, which precludes redirected streams -- which is exactly why the elevated
/// PowerShell writes this file itself).</summary>
public static class DefenderResultFile
{
    private const string FileName = "defender-result.json";

    public static string PathFor(string projectRoot) => Path.Combine(UnBramblePaths.StateDirFor(projectRoot), FileName);

    public static void Write(string path, bool success, IReadOnlyList<DefenderEntryResult> results)
    {
        var dto = new DefenderResultFileJson
        {
            Success = success,
            Results = [.. results.Select(r => new DefenderResultEntryJson
            {
                Type = r.Entry.TypeString,
                Value = r.Entry.Value,
                Outcome = DefenderDecisionText.ToText(r.Outcome),
                Error = r.Error,
            })],
        };

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(dto, DefenderJsonContext.Default.DefenderResultFileJson));
    }

    /// <summary>Best-effort delete of any stale result file from a previous run, so the parent
    /// never mistakes a leftover result for the current elevated run's output (the current run's
    /// elevated PowerShell writes this file itself; if it never launches, there should be no file
    /// to read back).</summary>
    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort only.
        }
    }

    public static (bool Success, IReadOnlyList<DefenderEntryResult> Results)? TryRead(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize(json, DefenderJsonContext.Default.DefenderResultFileJson);
            if (dto is null)
            {
                return null;
            }

            var results = dto.Results
                .Select(r => new DefenderEntryResult(
                    new DefenderEntry(DefenderEntry.ParseType(r.Type), r.Value),
                    DefenderDecisionText.ParseOutcome(r.Outcome),
                    r.Error))
                .ToList();
            return (dto.Success, results);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>The durable idempotency record: `.unbramble/defender-exclusions.json`. Re-running
/// `init` recomputes the desired entry set and diffs it against this file's recorded entries
/// (see <c>DefenderExclusionSetup.ComputeDelta</c>) -- an up-to-date project neither re-prompts
/// nor re-elevates.</summary>
public static class DefenderStateFile
{
    private const string FileName = "defender-exclusions.json";

    public static string PathFor(string projectRoot) => Path.Combine(UnBramblePaths.StateDirFor(projectRoot), FileName);

    public static DefenderStateFileJson? TryRead(string projectRoot)
    {
        try
        {
            var json = File.ReadAllText(PathFor(projectRoot));
            return JsonSerializer.Deserialize(json, DefenderJsonContext.Default.DefenderStateFileJson);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Atomic write (temp file + rename), same convention as
    /// <see cref="Core.Freshness.AutoWatchMarkers"/>/<see cref="Core.Freshness.HeartbeatFile"/>:
    /// best-effort, a failed write just leaves the previous state in place a little longer.</summary>
    public static void Write(string projectRoot, DefenderDecision decision, string? exePath, IReadOnlyList<DefenderEntryResult> entries)
    {
        var dto = new DefenderStateFileJson
        {
            Version = 1,
            Decision = DefenderDecisionText.ToText(decision),
            AppliedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ExePath = exePath,
            Entries = [.. entries.Select(e => new DefenderStateEntryJson
            {
                Type = e.Entry.TypeString,
                Value = e.Entry.Value,
                Outcome = DefenderDecisionText.ToText(e.Outcome),
            })],
        };

        var path = PathFor(projectRoot);
        try
        {
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var tempPath = Path.Combine(directory, $"{FileName}.tmp-{Guid.NewGuid():N}");
            File.WriteAllText(tempPath, JsonSerializer.Serialize(dto, DefenderJsonContext.Default.DefenderStateFileJson));
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort only -- see this method's own doc comment.
        }
    }

    public static void Delete(string projectRoot)
    {
        try
        {
            File.Delete(PathFor(projectRoot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort only.
        }
    }
}
