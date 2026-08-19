using System.Text;

namespace UnBramble.Cli.Defender;

/// <summary>
/// Builds the self-contained PowerShell script that the single elevated <c>powershell.exe</c> hop
/// runs (see <see cref="IElevatedPowerShellRunner"/> for why the elevation targets PowerShell
/// directly rather than relaunching <c>unbramble.exe</c>). The script is genuinely plan-in /
/// result-out: it reads the plan file the non-elevated parent wrote, does ONE pass covering every
/// entry (reads `Get-MpPreference` once, skips entries already covered, calls
/// `Add-MpPreference`/`Remove-MpPreference` for the rest, then reads `Get-MpPreference` back a
/// SECOND time to confirm -- the "might appear to succeed but is actually blocked" check), and
/// writes the parsed per-entry outcome to the result file (<see cref="DefenderResultFile"/>) that
/// the parent reads back. Nothing crosses the elevation boundary except those two files -- there is
/// no elevated <c>unbramble.exe</c> process in the middle any more.
/// </summary>
public static class DefenderApply
{
    /// <summary>Builds the check-then-add-then-verify script. <paramref name="planPath"/> is read
    /// for the entries to apply; the confirmed per-entry outcome is written to
    /// <paramref name="resultPath"/> in the exact <see cref="DefenderResultFileJson"/> shape
    /// <see cref="DefenderResultFile.TryRead"/> expects: read current exclusions once, skip
    /// entries already covered (path: prefix match; process: exact match, both
    /// case-insensitive -- Windows paths/image names are case-insensitive), call
    /// `Add-MpPreference` for the rest inside a try/catch (an exception
    /// there is an "error" outcome), then re-read `Get-MpPreference` and classify every
    /// non-"already-covered", non-"error" entry as "added" or "blocked" depending on whether it's
    /// actually present now.</summary>
    public static string BuildApplyScript(string planPath, string resultPath)
    {
        var sb = new StringBuilder();
        AppendPreamble(sb, planPath, resultPath);
        sb.AppendLine("""
              try { $before = Get-MpPreference } catch { $before = $null }
              $beforeLists = Get-Lists $before

              $pending = @()
              foreach ($e in $entries) {
                if (Test-Covered $e $beforeLists) {
                  $results += [pscustomobject]@{ type = $e.type; value = $e.value; outcome = 'already-covered' }
                  continue
                }

                try {
                  if ($e.type -eq 'path') { Add-MpPreference -ExclusionPath $e.value -ErrorAction Stop }
                  else { Add-MpPreference -ExclusionProcess $e.value -ErrorAction Stop }
                  $pending += $e
                } catch {
                  $results += [pscustomobject]@{ type = $e.type; value = $e.value; outcome = 'error'; error = $_.Exception.Message }
                }
              }

              try { $after = Get-MpPreference } catch { $after = $null }
              $afterLists = Get-Lists $after

              foreach ($e in $pending) {
                $present = Test-Covered $e $afterLists
                $outcome = if ($present) { 'added' } else { 'blocked' }
                $results += [pscustomobject]@{ type = $e.type; value = $e.value; outcome = $outcome }
              }
            """);
        AppendResultTail(sb);
        return sb.ToString();
    }

    /// <summary>The `defender remove` mirror of <see cref="BuildApplyScript"/>: unconditionally
    /// calls `Remove-MpPreference` for exactly the recorded entries (never touches anything else --
    /// this is scoped removal, not "clear all exclusions"), then reads back to classify each as
    /// 'removed' or 'blocked' (still present after the remove attempt).</summary>
    public static string BuildRemoveScript(string planPath, string resultPath)
    {
        var sb = new StringBuilder();
        AppendPreamble(sb, planPath, resultPath);
        sb.AppendLine("""
              foreach ($e in $entries) {
                try {
                  if ($e.type -eq 'path') { Remove-MpPreference -ExclusionPath $e.value -ErrorAction SilentlyContinue }
                  else { Remove-MpPreference -ExclusionProcess $e.value -ErrorAction SilentlyContinue }
                } catch {}
              }

              try { $after = Get-MpPreference } catch { $after = $null }
              $afterLists = Get-Lists $after

              foreach ($e in $entries) {
                $stillPresent = Test-Present $e $afterLists
                $outcome = if ($stillPresent) { 'blocked' } else { 'removed' }
                $results += [pscustomobject]@{ type = $e.type; value = $e.value; outcome = $outcome }
              }
            """);
        AppendResultTail(sb);
        return sb.ToString();
    }

    /// <summary>Common head of both scripts: strict error handling, the embedded (single-quoted,
    /// PS-escaped) plan/result paths, the shared list-extraction/coverage helpers, and the plan
    /// read that populates `$entries`. Everything the two mode-specific middles then operate on.
    /// The plan read is wrapped so a bad/unreadable plan still produces a well-formed result file
    /// (every entry it managed to read marked 'error') rather than a silent no-op.</summary>
    private static void AppendPreamble(StringBuilder sb, string planPath, string resultPath)
    {
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"$planPath = '{PsEscape(planPath)}'");
        sb.AppendLine($"$resultPath = '{PsEscape(resultPath)}'");
        sb.AppendLine("try { Import-Module Defender -ErrorAction SilentlyContinue } catch {}");
        sb.AppendLine("""
            function Get-Lists($pref) {
              $paths = @()
              $procs = @()
              if ($pref) {
                if ($pref.ExclusionPath) { $paths = @($pref.ExclusionPath) }
                if ($pref.ExclusionProcess) { $procs = @($pref.ExclusionProcess) }
              }
              return @{ paths = $paths; procs = $procs }
            }

            function Test-Covered($entry, $lists) {
              if ($entry.type -eq 'path') {
                foreach ($p in $lists.paths) {
                  if ($p -and $entry.value.ToLowerInvariant().StartsWith($p.ToLowerInvariant())) { return $true }
                }
              } else {
                foreach ($p in $lists.procs) {
                  if ($p -and $p.ToLowerInvariant() -eq $entry.value.ToLowerInvariant()) { return $true }
                }
              }
              return $false
            }

            function Test-Present($entry, $lists) {
              if ($entry.type -eq 'path') {
                foreach ($p in $lists.paths) { if ($p -and $p.ToLowerInvariant() -eq $entry.value.ToLowerInvariant()) { return $true } }
              } else {
                foreach ($p in $lists.procs) { if ($p -and $p.ToLowerInvariant() -eq $entry.value.ToLowerInvariant()) { return $true } }
              }
              return $false
            }

            $entries = @()
            $results = @()
            try {
              $plan = Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json
              $entries = @($plan.entries)
            """);
    }

    /// <summary>Common tail of both scripts: closes the plan-read `try` with a catch that maps every
    /// known entry to 'error' (so a mid-run failure still yields a complete, honest result file),
    /// then writes `{success, results}` to the result file. The JSON is assembled by hand rather
    /// than with a single `ConvertTo-Json` on the whole object because Windows PowerShell 5.1's
    /// `ConvertTo-Json` collapses a one-element array to a bare object -- which would make a
    /// single-entry plan's `results` fail to deserialize as an array. Each element is still
    /// `ConvertTo-Json`'d individually (so paths/messages are correctly escaped), then joined and
    /// wrapped in `[...]` by hand. The whole write is best-effort/guarded so a failure to write
    /// never throws an unhandled error out of the elevated process.</summary>
    private static void AppendResultTail(StringBuilder sb)
    {
        sb.AppendLine("""
            } catch {
              $msg = $_.Exception.Message
              $results = @($entries | ForEach-Object { [pscustomobject]@{ type = $_.type; value = $_.value; outcome = 'error'; error = $msg } })
            }

            $failed = @($results | Where-Object { $_.outcome -eq 'blocked' -or $_.outcome -eq 'error' })
            $successJson = if ($failed.Count -eq 0) { 'true' } else { 'false' }
            $itemsJson = (@($results) | ForEach-Object { $_ | ConvertTo-Json -Compress }) -join ","
            $json = '{"success":' + $successJson + ',"results":[' + $itemsJson + ']}'
            try {
              $dir = Split-Path -Parent $resultPath
              if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
              Set-Content -LiteralPath $resultPath -Value $json -Encoding UTF8
            } catch {}
            """);
    }

    /// <summary>PowerShell single-quoted string escaping: doubling an embedded `'` is the only
    /// escape a single-quoted PS string needs (no backslash processing at all inside one, so
    /// Windows paths pass through unmodified).</summary>
    private static string PsEscape(string value) => value.Replace("'", "''");
}
