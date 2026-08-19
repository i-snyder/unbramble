#Requires -Version 7
<#
.SYNOPSIS
  Compares a Unity-native dependency export against UnBramble's `uses` output. Ground truth for
  path-based refs (.uxml/.uss/.tss), which rg-parity can't verify (no guid text on disk to grep
  for).

.DESCRIPTION
  Comparison rule is CONTAINMENT, not equality: every dependency Unity's own
  AssetDatabase.GetDependencies reports must appear in `unbramble uses <path> --json`'s resolved
  targets. UnBramble may legitimately report extra unresolved/external/builtin entries Unity's
  API omits -- that's not a violation. The reverse (UnBramble missing something Unity reports)
  is the only real failure mode this script checks for.

.PARAMETER ProjectRoot
  Path to a Unity project root that already has an UnBramble index.

.PARAMETER DepsJson
  Path to the JSON produced by scripts/unity/ExportDependencies.cs's
  Tools > UnBramble > Export Dependencies menu item (or a handcrafted file with the same shape,
  for testing). Defaults to <ProjectRoot>/unbramble-unity-deps.json.

.PARAMETER ExePath
  Path to unbramble.exe. Defaults to <repo>/publish/unbramble.exe (sibling of this scripts/ dir).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ProjectRoot,

    [string]$DepsJson,

    [string]$ExePath
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot

if (-not $ExePath) {
    $ExePath = Join-Path $RepoRoot 'publish/unbramble.exe'
}

if (-not $DepsJson) {
    $DepsJson = Join-Path $ProjectRoot 'unbramble-unity-deps.json'
}

if (-not (Test-Path -LiteralPath $ExePath)) {
    Write-Error "unbramble executable not found at '$ExePath'"
    exit 1
}

if (-not (Test-Path -LiteralPath $DepsJson)) {
    Write-Error "Unity deps export not found at '$DepsJson' -- run Tools > UnBramble > Export Dependencies in the Unity Editor first (scripts/unity/ExportDependencies.cs)."
    exit 1
}

$deps = Get-Content -LiteralPath $DepsJson -Raw | ConvertFrom-Json

$assetNames = @($deps.PSObject.Properties.Name)
if ($assetNames.Count -eq 0) {
    Write-Host "compare-unity-deps: nothing to check (empty '$DepsJson')" -ForegroundColor Yellow
    exit 0
}

$violationCount = 0

foreach ($assetPath in $assetNames) {
    $expectedDeps = @($deps.$assetPath)

    $json = & $ExePath uses $assetPath -p $ProjectRoot --json
    if ($LASTEXITCODE -ne 0) {
        $violationCount++
        Write-Host "FAIL  $assetPath : 'unbramble uses' failed (exit $LASTEXITCODE): $json" -ForegroundColor Red
        continue
    }

    $obj = $json | ConvertFrom-Json
    $resolvedTargets = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($r in @($obj.results)) {
        if ($r.resolved -and $r.target) {
            [void]$resolvedTargets.Add([string]$r.target)
        }
    }

    $missing = @($expectedDeps | Where-Object { $_ -ne $assetPath -and -not $resolvedTargets.Contains($_) })

    if ($missing.Count -gt 0) {
        $violationCount++
        Write-Host "FAIL  $assetPath" -ForegroundColor Red
        Write-Host ('  Unity reports a dependency UnBramble does not (containment violation): ' + ($missing -join ', ')) -ForegroundColor Red
    } else {
        Write-Host "PASS  $assetPath  ($($expectedDeps.Count) Unity-reported deps, all contained)" -ForegroundColor DarkGray
    }
}

Write-Host ''
if ($violationCount -gt 0) {
    Write-Host "compare-unity-deps: FAIL ($violationCount of $($assetNames.Count) assets had containment violations)" -ForegroundColor Red
    exit 1
}

Write-Host "compare-unity-deps: PASS ($($assetNames.Count) of $($assetNames.Count) assets contained)" -ForegroundColor Green
exit 0
