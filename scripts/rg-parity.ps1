#Requires -Version 7
<#
.SYNOPSIS
  The mechanical who-uses/rg parity acceptance gate.

.DESCRIPTION
  Claim being verified: for any guid, `unbramble who-uses <guid>` (direct, guid-kind edges)
  returns exactly the same source-file set as a *properly scoped* ripgrep search for that
  guid. The scoping IS the gate's design -- a naive unscoped `rg <guid>` produces false
  mismatches (hits in .cs comments, Library/, hidden folders, non-indexed extensions) that
  look like parser bugs but aren't. Known-by-design cases this scoping must not flag: Foo.cs's
  comment guid (excluded by extension globs -- .cs is never a reference-source extension) and
  Skipped.mat under Assets/Ignored~ (excluded by the '~'-dir glob).

  rg's -g/--glob flag "always overrides any other ignore logic" (per `rg --help`), which
  means rg's normal default hidden-file skip does NOT apply once any -g pattern is given --
  contrary to what a naive reading of "don't pass --hidden" would suggest. This script adds
  an explicit `-g '!**/.*'` exclusion to restore that behavior; verified empirically against
  the fixture's Assets/.hidden.mat negative-fixture file (ripgrep 15.1.0).

.PARAMETER ProjectRoot
  Path to a Unity project root that already has an UnBramble index (run `unbramble index` first).

.PARAMETER Guids
  Explicit guid set to check. If omitted, every guid found in the project's own .meta files
  (under the configured roots plus the always-scanned Library/PackageCache) is used, or a
  random sample of -Sample of them if that set is larger than -Sample.

.PARAMETER ExePath
  Path to unbramble.exe. Defaults to <repo>/publish/unbramble.exe (sibling of this scripts/ dir).

.PARAMETER Sample
  Max guids to check when -Guids isn't given. Default 200. On a project as small as the
  fixture, the whole guid set is under this, so
  "-Sample 200" and "check everything" naturally coincide -- no fixture-only special case needed.

.PARAMETER Quiet
  Suppress per-guid PASS lines; print only FAIL details and the final summary.

.PARAMETER SelfTest
  Deliberate-failure self-test (guards against the gate silently passing on stale data): builds
  a fresh temp fixture copy, indexes it, appends a new guid reference to a .prefab WITHOUT
  re-indexing, then re-invokes this same script (as a nested script -- see note below) against
  the now-stale index and asserts it exits 1 and names the injected guid. Exits 0 if the
  self-test's own assertions hold (i.e. the gate correctly caught the staleness), 1 otherwise.

  Note: since every query verb now stat-sweeps before answering (the pull path), a plain
  "mutate disk, don't reindex" no longer produces staleness at all -- who-uses would just
  self-heal before this gate ever saw a
  mismatch. This self-test forges a fresh watcher.heartbeat file first, which is the one
  remaining (by-design, accepted) way a query can see stale data: a fresh heartbeat tells the
  pull path to trust that a live watcher already owns freshness and skip its own sweep. That's
  exactly the risk surface worth guarding here.

.NOTES
  A script invoked via the call operator (&) runs as a nested script scope: `exit N` inside it
  only terminates that nested script and sets $LASTEXITCODE, it does not kill the parent's
  session (verified empirically -- differs from `exit` inside a scriptblock/function, which
  would terminate the whole process). That's what makes both (a) verify-all.ps1's
  `& rg-parity.ps1 ...; if ($LASTEXITCODE...)` wiring and (b) -SelfTest's self-invocation via
  `& $PSCommandPath ...` safe.
#>
[CmdletBinding()]
param(
    # Mandatory only in the normal (non -SelfTest) mode -- checked manually below rather than
    # via [Parameter(Mandatory)] so `-SelfTest` alone (which builds its own throwaway fixture
    # copy and never looks at -ProjectRoot) doesn't force a caller to pass an unused value.
    [string]$ProjectRoot,

    [string[]]$Guids,

    [string]$ExePath,

    [int]$Sample = 200,

    [switch]$Quiet,

    [switch]$SelfTest
)

$ErrorActionPreference = 'Stop'

$RepoRoot = Split-Path -Parent $PSScriptRoot

if (-not $ExePath) {
    $ExePath = Join-Path $RepoRoot 'publish/unbramble.exe'
}

# Mirrors UnBrambleConfig's defaults (src/UnBramble.Core/Config/UnBrambleConfig.cs) and the
# scanner's ExcludeDirs/HiddenAssetRules -- kept independent on purpose (a PowerShell-side
# reimplementation, not a call into Core) so this script is a genuine second opinion on the
# index, not the same code path checking itself.
$DefaultRoots = @('Assets', 'Packages', 'LocalPackages', 'ProjectSettings')
$DefaultExcludeDirs = @('Library', 'Temp', 'Obj', 'obj', 'Logs', '.git', '.plastic', '.svn', '.vs', '.idea', '.collab', '.unbramble')

function Get-ProjectConfig {
    param([string]$Root)

    $result = [PSCustomObject]@{
        Roots  = $DefaultRoots
        DbPath = '.unbramble/unbramble.db'
    }

    $configPath = Join-Path $Root 'unbramble.json'
    if (-not (Test-Path -LiteralPath $configPath)) {
        return $result
    }

    try {
        $json = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    } catch {
        return $result
    }

    if ($json.roots) { $result.Roots = @($json.roots) }
    if ($json.dbPath) { $result.DbPath = $json.dbPath }
    return $result
}

function Test-HiddenName {
    param([string]$Name, [bool]$IsDirectory)

    if ($Name.Length -eq 0) { return $false }
    if ($Name[0] -eq '.') { return $true }
    if ($Name -ieq 'cvs') { return $true }
    if ($IsDirectory -and $Name.EndsWith('~')) { return $true }
    if (-not $IsDirectory -and $Name.ToLowerInvariant().EndsWith('.tmp')) { return $true }
    return $false
}

# Walks a directory tree collecting guid -> project-relative-owner-path from every .meta
# file's "guid:" line, applying Unity's hidden-asset rules (dot-prefixed, '~'-suffixed dirs,
# "cvs", ".tmp") plus (when $ApplyExcludeDirs) the configured ExcludeDirs. Used only to build
# the candidate guid set and to know each guid's own path for self-reference exclusion --
# NOT used to scope the rg search itself (that scoping is separate, see Get-RgIncludeGlobs /
# the per-guid search roots).
function Add-MetaGuids {
    param(
        [string]$Dir,
        [string]$ProjectRoot,
        [bool]$ApplyExcludeDirs,
        [System.Collections.Generic.Dictionary[string, string]]$Map
    )

    $entries = $null
    try {
        $entries = Get-ChildItem -LiteralPath $Dir -Force -ErrorAction Stop
    } catch {
        return
    }

    foreach ($entry in $entries) {
        $name = $entry.Name
        if ($entry.PSIsContainer) {
            if (Test-HiddenName -Name $name -IsDirectory $true) { continue }
            if ($ApplyExcludeDirs -and ($DefaultExcludeDirs -contains $name)) { continue }
            Add-MetaGuids -Dir $entry.FullName -ProjectRoot $ProjectRoot -ApplyExcludeDirs $ApplyExcludeDirs -Map $Map
            continue
        }

        if (Test-HiddenName -Name $name -IsDirectory $false) { continue }
        if (-not $name.ToLowerInvariant().EndsWith('.meta')) { continue }

        $match = Select-String -LiteralPath $entry.FullName -Pattern '^guid:\s*([0-9a-fA-F]{32})\s*$' -List -ErrorAction SilentlyContinue
        if (-not $match) { continue }

        $guid = $match.Matches[0].Groups[1].Value.ToLowerInvariant()
        $ownerFull = $entry.FullName.Substring(0, $entry.FullName.Length - 5) # strip ".meta"
        $rel = [System.IO.Path]::GetRelativePath($ProjectRoot, $ownerFull).Replace('\', '/')
        $Map[$guid] = $rel
    }
}

function Get-IndexedGuidMap {
    param([string]$ProjectRoot, [string[]]$ConfiguredRoots)

    $map = [System.Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)

    foreach ($root in $ConfiguredRoots) {
        $full = Join-Path $ProjectRoot ($root -replace '/', [IO.Path]::DirectorySeparatorChar)
        if (Test-Path -LiteralPath $full) {
            Add-MetaGuids -Dir $full -ProjectRoot $ProjectRoot -ApplyExcludeDirs $true -Map $map
        }
    }

    # Identity-only: Library/PackageCache guids are real graph nodes (referenceable) even
    # though PackageCache itself is never an rg *search* root (see Get-RgSearchRoots).
    $packageCache = Join-Path $ProjectRoot 'Library/PackageCache'
    if (Test-Path -LiteralPath $packageCache) {
        Add-MetaGuids -Dir $packageCache -ProjectRoot $ProjectRoot -ApplyExcludeDirs $false -Map $map
    }

    return $map
}

function Get-RefSourceExtensions {
    param([string]$ExePath, [string]$ProjectRoot)

    $json = & $ExePath stats -p $ProjectRoot --json
    if ($LASTEXITCODE -ne 0) {
        throw "'unbramble stats --json' failed (exit $LASTEXITCODE): $json"
    }

    $obj = $json | ConvertFrom-Json
    if (-not $obj.refSourceExtensions -or $obj.refSourceExtensions.Count -eq 0) {
        throw "'unbramble stats --json' did not report refSourceExtensions -- binary/script have drifted"
    }

    return @($obj.refSourceExtensions)
}

# Search only the project's configured roots, NEVER Library -- UnBramble sources no
# edges from PackageCache, so including Library would let rg see identity-only guids' own
# files and manufacture mismatches the index was never meant to have an opinion on.
function Get-RgSearchRoots {
    param([string]$ProjectRoot, [string[]]$ConfiguredRoots)

    $roots = @()
    foreach ($root in $ConfiguredRoots) {
        $full = Join-Path $ProjectRoot ($root -replace '/', [IO.Path]::DirectorySeparatorChar)
        if (Test-Path -LiteralPath $full) { $roots += $full }
    }
    return $roots
}

function Get-RgIncludeGlobs {
    param([string[]]$Extensions)

    $globs = [System.Collections.Generic.List[string]]::new()
    foreach ($ext in $Extensions) {
        $globs.Add('-g'); $globs.Add("*$ext")
    }
    $globs.Add('-g'); $globs.Add('*.meta')

    # Restores rg's normal hidden-file skip: an explicit -g pattern "always overrides any
    # other ignore logic" (rg --help), which silently defeats the default hidden-file skip
    # once any -g is present -- see the .NOTES/header comment. Do NOT pass --hidden instead;
    # that would ADD hidden files back in, the opposite of what's needed here.
    $globs.Add('-g'); $globs.Add('!**/.*')
    # Unity-hidden shapes rg has no other way to know about.
    $globs.Add('-g'); $globs.Add('!**/*~/**')
    $globs.Add('-g'); $globs.Add('!**/cvs/**')
    $globs.Add('-g'); $globs.Add('!**/*.tmp')

    return $globs
}

# rg side of the comparison for one guid: properly-scoped `rg -l`, realpath-deduped,
# X.meta hits mapped to owning asset X, the target's own file/.meta dropped (identity, not a
# reference), converted to project-relative forward-slash paths.
function Get-RgReferencers {
    param(
        [string]$Guid,
        [string]$ProjectRoot,
        [string[]]$SearchRoots,
        [string[]]$IncludeGlobs,
        [System.Collections.Generic.Dictionary[string, string]]$GuidMap
    )

    $result = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    if ($SearchRoots.Count -eq 0) { return $result }

    $rgArgs = @('-il', '--follow', '--no-ignore', '--crlf') + $IncludeGlobs + @('--', $Guid) + $SearchRoots
    $hits = & rg @rgArgs 2>$null
    if ($LASTEXITCODE -gt 1) {
        throw "rg failed (exit $LASTEXITCODE) searching for guid '$Guid'"
    }

    $ownPath = $null
    $GuidMap.TryGetValue($Guid, [ref]$ownPath) | Out-Null

    foreach ($hit in $hits) {
        if ([string]::IsNullOrWhiteSpace($hit)) { continue }

        $resolved = $hit
        try {
            $item = Get-Item -LiteralPath $hit -Force -ErrorAction Stop
            $resolved = $item.FullName
        } catch {
            # Dangling link or a transient race; keep the raw hit rather than dropping it
            # silently (never-wrong-only-sometimes-slower extends to this gate too).
        }

        $rel = [System.IO.Path]::GetRelativePath($ProjectRoot, $resolved).Replace('\', '/')
        if ($rel.ToLowerInvariant().EndsWith('.meta')) {
            $rel = $rel.Substring(0, $rel.Length - 5)
        }

        if ($ownPath -and ($rel -ieq $ownPath)) { continue } # identity, not a reference

        [void]$result.Add($rel)
    }

    return $result
}

# UnBramble side: direct (depth 1), guid-kind edges only. Path-kind edges have no guid text for
# rg to find -- excluding them is correct, not cheating.
function Get-UnBrambleReferencers {
    param([string]$Guid, [string]$ExePath, [string]$ProjectRoot)

    $json = & $ExePath who-uses $Guid -p $ProjectRoot --json
    if ($LASTEXITCODE -ne 0) {
        throw "'unbramble who-uses $Guid --json' failed (exit $LASTEXITCODE): $json"
    }

    $obj = $json | ConvertFrom-Json
    $result = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($r in @($obj.results)) {
        if ($r.kind -eq 'guid' -and $r.depth -eq 1) {
            [void]$result.Add([string]$r.source)
        }
    }

    return $result
}

function Invoke-RgParity {
    param(
        [string]$ProjectRoot,
        [string[]]$Guids,
        [string]$ExePath,
        [int]$Sample,
        [bool]$Quiet
    )

    if (-not (Get-Command rg -ErrorAction SilentlyContinue)) {
        Write-Error "ripgrep ('rg') not found on PATH. Install it: choco install ripgrep"
        return 1
    }

    if (-not (Test-Path -LiteralPath $ExePath)) {
        Write-Error "unbramble executable not found at '$ExePath'"
        return 1
    }

    $config = Get-ProjectConfig -Root $ProjectRoot
    $dbFull = Join-Path $ProjectRoot ($config.DbPath -replace '/', [IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $dbFull)) {
        Write-Error "no UnBramble index found at '$ProjectRoot' (expected '$dbFull') -- run 'unbramble index' first."
        return 1
    }

    $refSourceExtensions = Get-RefSourceExtensions -ExePath $ExePath -ProjectRoot $ProjectRoot
    $includeGlobs = Get-RgIncludeGlobs -Extensions $refSourceExtensions
    $searchRoots = Get-RgSearchRoots -ProjectRoot $ProjectRoot -ConfiguredRoots $config.Roots
    $guidMap = Get-IndexedGuidMap -ProjectRoot $ProjectRoot -ConfiguredRoots $config.Roots

    if ($Guids -and $Guids.Count -gt 0) {
        $guidsToTest = @($Guids | ForEach-Object { $_.ToLowerInvariant() })
    } else {
        $allGuids = @($guidMap.Keys)
        if ($allGuids.Count -gt $Sample) {
            $guidsToTest = @(Get-Random -InputObject $allGuids -Count $Sample)
        } else {
            $guidsToTest = $allGuids
        }
    }

    if ($guidsToTest.Count -eq 0) {
        Write-Host 'rg-parity: no guids to check (empty index?)' -ForegroundColor Yellow
        return 0
    }

    $mismatchCount = 0
    foreach ($guid in $guidsToTest) {
        $rgSet = Get-RgReferencers -Guid $guid -ProjectRoot $ProjectRoot -SearchRoots $searchRoots -IncludeGlobs $includeGlobs -GuidMap $guidMap
        $unbrambleSet = Get-UnBrambleReferencers -Guid $guid -ExePath $ExePath -ProjectRoot $ProjectRoot

        $rgOnly = @($rgSet | Where-Object { -not $unbrambleSet.Contains($_) })
        $unbrambleOnly = @($unbrambleSet | Where-Object { -not $rgSet.Contains($_) })

        if ($rgOnly.Count -gt 0 -or $unbrambleOnly.Count -gt 0) {
            $mismatchCount++
            Write-Host "FAIL  $guid" -ForegroundColor Red
            if ($rgOnly.Count -gt 0) { Write-Host ('  rg only:      ' + ($rgOnly -join ', ')) -ForegroundColor Red }
            if ($unbrambleOnly.Count -gt 0) { Write-Host ('  unbramble only: ' + ($unbrambleOnly -join ', ')) -ForegroundColor Red }
        } elseif (-not $Quiet) {
            Write-Host "PASS  $guid" -ForegroundColor DarkGray
        }
    }

    Write-Host ''
    if ($mismatchCount -gt 0) {
        Write-Host "rg-parity: FAIL ($mismatchCount of $($guidsToTest.Count) guids mismatched)" -ForegroundColor Red
        return 1
    }

    Write-Host "rg-parity: PASS (0 of $($guidsToTest.Count) guids mismatched)" -ForegroundColor Green
    return 0
}

function Invoke-SelfTest {
    param([string]$ExePath)

    Write-Host '== rg-parity self-test: deliberate-failure guard ==' -ForegroundColor Cyan

    $fixtureSource = Join-Path $RepoRoot 'src/UnBramble.Tests/Fixtures/MiniProject'
    if (-not (Test-Path -LiteralPath $fixtureSource)) {
        Write-Error "fixture not found at '$fixtureSource'"
        return 1
    }

    $tmp = Join-Path ([IO.Path]::GetTempPath()) "unbramble-rgparity-selftest-$([guid]::NewGuid().ToString('n'))"
    Copy-Item -LiteralPath $fixtureSource -Destination $tmp -Recurse

    try {
        & $ExePath init $tmp | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Error "self-test setup: 'unbramble init' failed (exit $LASTEXITCODE)"
            return 1
        }

        # Forge a fresh watcher.heartbeat BEFORE mutating the fixture, so the
        # nested rg-parity invocation's `who-uses` call below trusts it and skips its own
        # pull-path sweep -- otherwise the sweep would self-heal the induced staleness before
        # this gate ever saw it (see the .PARAMETER SelfTest note above).
        # The forgery must carry the binary's CURRENT schema stamp (read live from stats, never
        # hardcoded) -- an unstamped or mismatched heartbeat is deliberately never trusted
        # (upgrade protection; see HeartbeatFile), which would make the pull path self-heal and
        # this self-test silently stop testing anything.
        $statsJson = & $ExePath stats -p $tmp --json
        if ($statsJson -notmatch '"schemaVersion":(\d+)') {
            Write-Error "self-test setup: could not read schemaVersion from 'stats --json' output: $statsJson"
            return 1
        }
        $schemaVersion = $Matches[1]
        $heartbeatDir = Join-Path $tmp '.unbramble'
        New-Item -ItemType Directory -Force -Path $heartbeatDir | Out-Null
        $utcNow = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffffffZ')
        Set-Content -LiteralPath (Join-Path $heartbeatDir 'watcher.heartbeat') -Value "{`"pid`":999999,`"utc`":`"$utcNow`",`"schema`":$schemaVersion}" -NoNewline

        # Sanity check: the injected guid must currently NOT be a referencer of Enemy.prefab,
        # or this self-test wouldn't actually be injecting anything new.
        $injectedGuid = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa01' # Assets/Scripts/Foo.cs
        $enemyPrefab = Join-Path $tmp 'Assets/Prefabs/Enemy.prefab'
        Add-Content -LiteralPath $enemyPrefab -Value "  m_InjectedSelfTestRef: {fileID: 11500000, guid: $injectedGuid, type: 3} # deliberately unindexed"

        # Deliberately do NOT run 'unbramble index' again -- the stored index now disagrees
        # with what's on disk, on purpose, and (thanks to the forged heartbeat above) the
        # pull path won't self-heal it either. Re-invoke this same script (nested; see the
        # header .NOTES on why `exit` inside it won't kill this self-test) against the now-
        # stale index and capture both its exit code and everything it printed.
        $output = & $PSCommandPath -ProjectRoot $tmp -Guids $injectedGuid -ExePath $ExePath -Quiet *>&1 | Out-String
        $gateExitCode = $LASTEXITCODE

        $exitOk = ($gateExitCode -eq 1)
        $namesGuid = ($output -match [regex]::Escape($injectedGuid))

        if ($exitOk -and $namesGuid) {
            Write-Host "self-test PASS: gate correctly failed (exit $gateExitCode) and named the stale guid" -ForegroundColor Green
            return 0
        }

        Write-Host 'self-test FAIL: the gate did not catch the deliberately-stale index' -ForegroundColor Red
        Write-Host "  expected exit 1, got $gateExitCode; expected guid '$injectedGuid' in output: $namesGuid" -ForegroundColor Red
        Write-Host '  --- captured gate output ---'
        Write-Host $output
        return 1
    } finally {
        Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($SelfTest) {
    exit (Invoke-SelfTest -ExePath $ExePath)
}

if (-not $ProjectRoot) {
    Write-Error "-ProjectRoot is required (unless -SelfTest is given)"
    exit 1
}

exit (Invoke-RgParity -ProjectRoot $ProjectRoot -Guids $Guids -ExePath $ExePath -Sample $Sample -Quiet:$Quiet.IsPresent)
