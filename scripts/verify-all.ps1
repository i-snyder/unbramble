#Requires -Version 7
[CmdletBinding()]
param(
    [switch]$SkipPublish
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$script:failed = @()

# Prefer the standard x64 installation, then try every host discoverable from PATH or DOTNET_ROOT.
# A stale per-user host may have a newer SDK but no net8.0 runtime, so validate the whole installation
# before selecting it. Invoke that exact executable below; do not rely on PATH after discovery.
$dotnetCandidates = [Collections.Generic.List[string]]::new()
$seenDotnetCandidates = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
function Add-DotnetCandidate([string]$Path) {
    if ($Path -and $seenDotnetCandidates.Add([IO.Path]::GetFullPath($Path))) {
        $dotnetCandidates.Add([IO.Path]::GetFullPath($Path))
    }
}

Add-DotnetCandidate (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
Get-Command dotnet -All -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandType -eq 'Application' } |
    ForEach-Object { Add-DotnetCandidate $_.Source }
if ($env:DOTNET_ROOT_X64) { Add-DotnetCandidate (Join-Path $env:DOTNET_ROOT_X64 'dotnet.exe') }
if ($env:DOTNET_ROOT) { Add-DotnetCandidate (Join-Path $env:DOTNET_ROOT 'dotnet.exe') }

foreach ($candidate in $dotnetCandidates) {
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }

    $candidateRoot = Split-Path -Parent $candidate
    $candidateRuntime = Get-ChildItem (Join-Path $candidateRoot 'shared\Microsoft.NETCore.App') -ErrorAction SilentlyContinue |
        Where-Object { $_.PSIsContainer -and $_.Name -like '8.*' } |
        Sort-Object { [version]$_.Name } -Descending |
        Select-Object -First 1
    $candidateSdk = Get-ChildItem (Join-Path $candidateRoot 'sdk') -ErrorAction SilentlyContinue |
        Where-Object { $_.PSIsContainer } |
        Select-Object -First 1
    if (-not $candidateRuntime -or -not $candidateSdk) { continue }

    $previousDotnetRoot = $env:DOTNET_ROOT
    $previousDotnetRootX64 = $env:DOTNET_ROOT_X64
    try {
        $env:DOTNET_ROOT = $candidateRoot
        $env:DOTNET_ROOT_X64 = $candidateRoot
        $dotnetInfo = (& $candidate --info 2>$null | Out-String)
        $dotnetInfoExitCode = $LASTEXITCODE
    } finally {
        $env:DOTNET_ROOT = $previousDotnetRoot
        $env:DOTNET_ROOT_X64 = $previousDotnetRootX64
    }
    if ($dotnetInfoExitCode -eq 0 -and $dotnetInfo -match '(?m)^\s*Architecture:\s*x64\s*$') {
        $script:dotnetExe = $candidate
        $script:dotnetRoot = $candidateRoot
        $script:net8RuntimeDir = $candidateRuntime
        break
    }
}

if (-not $script:dotnetExe) {
    throw 'No complete x64 .NET installation with an SDK and the .NET 8 runtime was found. Install the x64 .NET 8 SDK and rerun this script.'
}

$env:DOTNET_ROOT = $script:dotnetRoot
$env:DOTNET_ROOT_X64 = $script:dotnetRoot
Write-Verbose "Using $script:dotnetExe with .NET 8 runtime $($script:net8RuntimeDir.Name)."

# Keep ordinary query verbs from starting detached workers throughout the shared fixture run.
# `monitor` is an explicit request and deliberately bypasses this test-only guard.
$env:UNBRAMBLE_DISABLE_AUTO_SPAWN = '1'

function Step([string]$Name, [scriptblock]$Body) {
    Write-Host "== $Name ==" -ForegroundColor Cyan
    & $Body
    if ($LASTEXITCODE -ne 0) {
        $script:failed += $Name
        Write-Host "-- $Name FAILED (exit $LASTEXITCODE)" -ForegroundColor Red
    }
}

Step 'build' { & $script:dotnetExe build "$repo/UnBramble.sln" -c Release -warnaserror }
if ($script:failed) { Write-Host 'FAIL (build)'; exit 1 }

Step 'test'  { & $script:dotnetExe test "$repo/UnBramble.sln" -c Release --no-build }

if (-not $SkipPublish) {
    Step 'publish' {
        # An interrupted earlier run (killed mid-watch-smoke, before its finally could stop the
        # explicit watcher it started) can orphan a watcher still running THIS repo's publish
        # exe, which then blocks the copy below with a file lock -- found live. Kill by exact
        # executable path, never by bare process name: a real project's installed copy is also
        # named unbramble.exe and its watcher must survive this script.
        $publishExe = [IO.Path]::GetFullPath((Join-Path $repo 'publish/unbramble.exe'))
        Get-Process -Name unbramble -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and ([IO.Path]::GetFullPath($_.Path) -eq $publishExe) } |
            Stop-Process -Force -ErrorAction SilentlyContinue

        # Try NativeAOT first (the preferred path). Fall back to a
        # self-contained single-file publish if the local machine is missing native
        # publish tooling (e.g. the VS C++ workload).
        & $script:dotnetExe publish "$repo/src/UnBramble.Cli/UnBramble.Cli.csproj" -c Release -r win-x64 -p:DebugSymbols=false -p:DebugType=None -o "$repo/publish"
        if ($LASTEXITCODE -ne 0) {
            Write-Host "-- NativeAOT publish failed locally (likely missing native toolchain); falling back to self-contained single-file publish --" -ForegroundColor Yellow
            Remove-Item -Recurse -Force "$repo/publish" -ErrorAction SilentlyContinue
            & $script:dotnetExe publish "$repo/src/UnBramble.Cli/UnBramble.Cli.csproj" -c Release -r win-x64 --self-contained true -p:PublishAot=false -p:PublishSingleFile=true -p:DebugSymbols=false -p:DebugType=None -o "$repo/publish"
        }

        if ($LASTEXITCODE -ne 0) { return }

        # NativeAOT emits a separate PDB even with DebugType=None. Keep symbols out of the public
        # binary folder: they are very large and contain absolute build-machine paths. DebugType=None
        # also keeps that path out of the PE debug directory in unbramble.exe itself.
        Get-ChildItem -LiteralPath (Join-Path $repo 'publish') -Filter '*.pdb' -File |
            Remove-Item -Force
        $publishedExeText = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($publishExe))
        foreach ($privatePath in @($repo, $env:USERPROFILE)) {
            if ($privatePath -and $publishedExeText.Contains($privatePath, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Published executable contains a private build path: $privatePath"
            }
        }

        # Assemble the exact notices from the packages this publish resolved. The short checked-in
        # inventory is useful to humans; these upstream files carry the complete binary notices.
        $assetsPath = Join-Path $repo 'src/UnBramble.Cli/obj/project.assets.json'
        $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -AsHashtable
        $packageRoot = @($assets.packageFolders.Keys)[0]
        $libraryNames = @($assets.libraries.Keys)
        $roslynLibrary = $libraryNames | Where-Object { $_ -like 'Microsoft.CodeAnalysis.CSharp/*' } | Select-Object -First 1
        $ilcLibrary = $libraryNames | Where-Object { $_ -like 'Microsoft.DotNet.ILCompiler/*' } | Select-Object -First 1
        $nativeIlcLibrary = $libraryNames | Where-Object { $_ -like 'runtime.win-x64.Microsoft.DotNet.ILCompiler/*' } | Select-Object -First 1
        $netCoreRuntimeDependency = @($assets.project.frameworks['net8.0'].downloadDependencies) |
            Where-Object { $_.name -eq 'Microsoft.NETCore.App.Runtime.win-x64' } |
            Select-Object -First 1
        $netCoreRuntimeVersion = if ($netCoreRuntimeDependency) {
            [regex]::Match([string]$netCoreRuntimeDependency.version, '\d+\.\d+\.\d+').Value
        }
        $netCoreRuntimeLibrary = if ($netCoreRuntimeVersion) {
            "Microsoft.NETCore.App.Runtime.win-x64/$netCoreRuntimeVersion"
        }
        if (-not $packageRoot -or -not $roslynLibrary -or -not $ilcLibrary -or -not $nativeIlcLibrary -or -not $netCoreRuntimeLibrary) {
            throw 'Could not resolve the package paths needed to assemble third-party notices.'
        }

        function PackagePath([string]$libraryName, [string]$relativePath) {
            $parts = $libraryName.Split('/', 2)
            Join-Path $packageRoot (Join-Path $parts[0].ToLowerInvariant() (Join-Path $parts[1].ToLowerInvariant() $relativePath))
        }

        $noticeCopies = @(
            @{ Source = (Join-Path $repo 'THIRD-PARTY-NOTICES.md'); Destination = 'THIRD-PARTY-NOTICES.md' },
            @{ Source = (PackagePath $roslynLibrary 'ThirdPartyNotices.rtf'); Destination = 'ROSLYN-THIRD-PARTY-NOTICES.rtf' },
            @{ Source = (PackagePath $ilcLibrary 'THIRD-PARTY-NOTICES.TXT'); Destination = 'DOTNET-ILCOMPILER-THIRD-PARTY-NOTICES.txt' },
            @{ Source = (PackagePath $nativeIlcLibrary 'THIRD-PARTY-NOTICES.TXT'); Destination = 'DOTNET-NATIVEAOT-THIRD-PARTY-NOTICES.txt' },
            @{ Source = (PackagePath $netCoreRuntimeLibrary 'THIRD-PARTY-NOTICES.TXT'); Destination = 'DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt' },
            @{ Source = (PackagePath $netCoreRuntimeLibrary 'LICENSE.TXT'); Destination = 'DOTNET-LICENSE.txt' },
            @{ Source = (Join-Path $repo 'licenses/SQLitePCLRaw-LICENSE.txt'); Destination = 'SQLitePCLRaw-LICENSE.txt' },
            @{ Source = (Join-Path $repo 'licenses/SQLitePCLRaw-NOTICE.txt'); Destination = 'SQLitePCLRaw-NOTICE.txt' },
            @{ Source = (Join-Path $repo 'licenses/Microsoft.Data.Sqlite-LICENSE.txt'); Destination = 'Microsoft.Data.Sqlite-LICENSE.txt' }
        )
        foreach ($copy in $noticeCopies) {
            if (-not (Test-Path -LiteralPath $copy.Source)) {
                throw "Required distribution notice not found: $($copy.Source)"
            }
            Copy-Item -LiteralPath $copy.Source -Destination (Join-Path $repo "publish/$($copy.Destination)") -Force
        }

        $projectLicense = Join-Path $repo 'LICENSE'
        if (Test-Path -LiteralPath $projectLicense) {
            Copy-Item -LiteralPath $projectLicense -Destination (Join-Path $repo 'publish/LICENSE') -Force
        } else {
            Write-Host '-- Project LICENSE not selected yet; add it before public distribution --' -ForegroundColor Yellow
        }
    }
    Step 'smoke-version' {
        & "$repo/publish/unbramble.exe" --version
    }

    # The deliberate-failure self-test runs before the real gate below, so a
    # broken parity-detection mechanism (one that would silently PASS everything) is caught
    # before its PASS on the fixture is trusted. Builds its own throwaway fixture copy --
    # doesn't need $tmp.
    Step 'rg-parity-selftest' {
        & "$repo/scripts/rg-parity.ps1" -SelfTest -ExePath "$repo/publish/unbramble.exe"
    }

    # fixture-smoke and rg-parity share one temp fixture copy: rg-parity.ps1 requires an
    # existing UnBramble index, which fixture-smoke's 'init' call below already builds.
    $tmp = Join-Path ([IO.Path]::GetTempPath()) "unbramble-verify-$([guid]::NewGuid().ToString('n'))"
    Copy-Item "$repo/src/UnBramble.Tests/Fixtures/MiniProject" $tmp -Recurse
    try {
        Step 'fixture-smoke' {
            & "$repo/publish/unbramble.exe" init $tmp; if ($LASTEXITCODE) { exit 1 }
            & "$repo/publish/unbramble.exe" resolve Foo -p $tmp; if ($LASTEXITCODE) { exit 1 }
            & "$repo/publish/unbramble.exe" stats -p $tmp; if ($LASTEXITCODE) { exit 1 }

            # who-uses/uses smoke, incl. --json and the missing-query/explicit-CI exit contract.
            & "$repo/publish/unbramble.exe" who-uses Assets/Scripts/Foo.cs -p $tmp --json; if ($LASTEXITCODE) { exit 1 }
            & "$repo/publish/unbramble.exe" uses Assets/Scenes/Level.unity -p $tmp --missing-only
            if ($LASTEXITCODE -ne 0) { Write-Host 'findings incorrectly made a successful query fail'; exit 1 }
            & "$repo/publish/unbramble.exe" uses Assets/Scenes/Level.unity -p $tmp --missing-only --fail-if-found
            if ($LASTEXITCODE -ne 3) { Write-Host 'expected exit 3 from explicit --fail-if-found gate'; exit 1 }
            & "$repo/publish/unbramble.exe" uses Assets/Materials/Rock.mat -p $tmp --missing-only
            if ($LASTEXITCODE -ne 0) { Write-Host 'builtin wrongly counted as missing'; exit 1 }

            # cs-refs smoke -- CoreUtil.Ping must show Foo.cs as a referencer.
            $csRefsOut = & "$repo/publish/unbramble.exe" cs-refs CoreUtil.Ping -p $tmp --json
            if ($LASTEXITCODE -ne 0) { Write-Host 'cs-refs failed'; exit 1 }
            if ($csRefsOut -notmatch 'Assets/Scripts/Foo\.cs') { Write-Host "cs-refs did not report Foo.cs as a referencer of CoreUtil.Ping: $csRefsOut"; exit 1 }

            # Schema/name-hints smoke -- stats must report the current schema version and a
            # nonzero name_hints count.
            # The fixture's asset-side sources alone (Hit.anim's m_FunctionName, Player.prefab's
            # two guid-less UnityEvent bindings) guarantee >= 1 regardless of C# analysis mode,
            # so this catches the capture path silently breaking without depending on the
            # fixture landing in semantic mode.
            $statsOut = & "$repo/publish/unbramble.exe" stats -p $tmp --json
            if ($LASTEXITCODE -ne 0) { Write-Host 'stats failed'; exit 1 }
            if ($statsOut -notmatch '"schemaVersion":10') { Write-Host "stats did not report schema v10: $statsOut"; exit 1 }
            if ($statsOut -notmatch '"nameHints":[1-9]') { Write-Host "stats reported zero name_hints (capture silently broken?): $statsOut"; exit 1 }
        }

        # dead-candidates end to end through the published binary (the xUnit
        # suite in the 'test' step above already covers the exact-set ledger, the global
        # liveness invariant, and every gate variant in depth -- this step's job is to catch
        # anything specific to the PUBLISHED (possibly NativeAOT) binary, e.g. JSON source-gen
        # wiring for the new DeadCandidatesResultJson type, that a DLL-hosted test can't).
        Step 'dead-candidates-smoke' {
            $exe = "$repo/publish/unbramble.exe"

            # Base (unmodified) $tmp fixture has no generated csproj -> syntactic mode -> gate 3
            # fails -> "liveness unavailable", exit 1, zero candidates.
            & $exe dead-candidates -p $tmp 2> "$tmp/dc-unavailable.stderr.log"
            if ($LASTEXITCODE -ne 1) { Write-Host 'expected exit 1 (syntactic mode, liveness unavailable)'; exit 1 }
            $unavailableErr = Get-Content "$tmp/dc-unavailable.stderr.log" -Raw
            if ($unavailableErr -notmatch 'liveness unavailable') { Write-Host "expected 'liveness unavailable' on stderr: $unavailableErr"; exit 1 }

            # Force semantic mode (same throwaway-csproj trick the xUnit suite uses -- real
            # <Reference>/<HintPath> entries pointing at actual on-disk DLLs, since
            # CsModeSelector only counts a reference whose HintPath resolves). MUST be the
            # actual net8.0 shared runtime (the TFM unbramble.exe itself targets) -- pwsh's own
            # bundled runtime directory ([RuntimeEnvironment]::GetRuntimeDirectory() from
            # within pwsh) is a DIFFERENT, incompatible assembly set that silently produces
            # broken/partial semantic resolution (found the hard way: Helper.Handle's
            # method-group conversion failed to resolve against it, wrongly reporting Helper.cs
            # as provenDead).
            $msbuildNs = 'http://schemas.microsoft.com/developer/msbuild/2003'
            $runtimeDlls = Get-ChildItem -Path $script:net8RuntimeDir.FullName -Filter '*.dll'
            $referenceXml = ($runtimeDlls | ForEach-Object {
                $name = [IO.Path]::GetFileNameWithoutExtension($_.Name)
                "    <Reference Include=`"$name`"><HintPath>$($_.FullName)</HintPath></Reference>"
            }) -join "`n"
            foreach ($asmName in @('Core', 'Game')) {
                $csprojPath = Join-Path $tmp "$asmName.csproj"
                @"
<Project xmlns="$msbuildNs" ToolsVersion="Current">
  <ItemGroup>
$referenceXml
  </ItemGroup>
</Project>
"@ | Set-Content -Path $csprojPath -NoNewline
            }
            # --full: the assemblies from fixture-smoke's earlier 'init' call are already
            # recorded (syntactic mode) and nothing about their OWN .cs files changed (only the
            # csprojs, which live outside every scanned root and are therefore never "dirty"
            # paths) -- a plain incremental 'index' would never re-run CS analysis for them and
            # the mode would stay stuck at syntactic. --full forces a clean reindex.
            & $exe index -p $tmp --full; if ($LASTEXITCODE) { exit 1 }

            $dcOut = & $exe dead-candidates -p $tmp --json --include-advisory
            if ($LASTEXITCODE -ne 0) { Write-Host "dead-candidates --json failed: $dcOut"; exit 1 }
            if ($dcOut -notmatch '"available":true') { Write-Host "dead-candidates did not report available:true: $dcOut"; exit 1 }
            if ($dcOut -notmatch 'Assets/Prefabs/Enemy\.prefab') { Write-Host "dead-candidates missed the known-dead Enemy.prefab: $dcOut"; exit 1 }
            if ($dcOut -notmatch 'interface-dispatch-guard') { Write-Host "dead-candidates missed the interface-dispatch-guard screen: $dcOut"; exit 1 }

            # Stale-csproj gate variant: touch ProjectSettings.asset newer than the
            # Mode-A csproj -> liveness unavailable again, even though the assembly mode itself
            # hasn't changed.
            Start-Sleep -Milliseconds 50
            (Get-Item (Join-Path $tmp 'ProjectSettings/ProjectSettings.asset')).LastWriteTimeUtc = [DateTime]::UtcNow
            & $exe dead-candidates -p $tmp 2> "$tmp/dc-stale.stderr.log"
            if ($LASTEXITCODE -ne 1) { Write-Host 'expected exit 1 (stale csproj)'; exit 1 }
            $staleErr = Get-Content "$tmp/dc-stale.stderr.log" -Raw
            if ($staleErr -notmatch 'generated csproj older than project configuration') { Write-Host "expected the stale-csproj reason on stderr: $staleErr"; exit 1 }

            # The last external call above deliberately exited 1 (already validated as the
            # CORRECT outcome); reset $LASTEXITCODE so Step's own pass/fail check below reflects
            # this step's validation result, not the last subprocess's raw exit code.
            $global:LASTEXITCODE = 0
        }

        # Addressables-unavailable variant: an isolated minimal project whose manifest
        # names an unconfirmed Addressables version. Its own throwaway temp dir -- must not
        # touch $tmp, which the remaining steps below still depend on.
        Step 'dead-candidates-addressables-unavailable' {
            $exe = "$repo/publish/unbramble.exe"
            $addrTmp = Join-Path ([IO.Path]::GetTempPath()) "unbramble-verify-addr-$([guid]::NewGuid().ToString('n'))"
            New-Item -ItemType Directory -Force -Path "$addrTmp/ProjectSettings", "$addrTmp/Packages" | Out-Null
            try {
                Set-Content -Path "$addrTmp/ProjectSettings/ProjectVersion.txt" -Value "m_EditorVersion: 2022.3.30f1`n" -NoNewline
                Set-Content -Path "$addrTmp/ProjectSettings/EditorSettings.asset" -Value "%YAML 1.1`n--- !u!159 &1`nEditorSettings:`n  m_SerializationMode: 2`n" -NoNewline
                Set-Content -Path "$addrTmp/Packages/manifest.json" -Value '{ "dependencies": { "com.unity.addressables": "2.3.1" } }' -NoNewline

                & $exe dead-candidates -p $addrTmp 2> "$addrTmp/dc.stderr.log"
                if ($LASTEXITCODE -ne 1) { Write-Host 'expected exit 1 (unconfirmed Addressables version)'; exit 1 }
                $err = Get-Content "$addrTmp/dc.stderr.log" -Raw
                if ($err -notmatch 'unconfirmed root coverage') { Write-Host "expected the Addressables-unavailable reason on stderr: $err"; exit 1 }
            } finally {
                Remove-Item $addrTmp -Recurse -Force -ErrorAction SilentlyContinue
            }

            # Same reset as dead-candidates-smoke above: the last external call's exit 1 was
            # already validated as correct.
            $global:LASTEXITCODE = 0
        }

        # The real gate -- every guid in the fixture's index must produce an
        # exact rg-vs-who-uses set match, properly scoped. Same $tmp as fixture-smoke, still
        # indexed from the 'init' call above.
        Step 'rg-parity' {
            & "$repo/scripts/rg-parity.ps1" -ProjectRoot $tmp -ExePath "$repo/publish/unbramble.exe"
        }

        # `unbramble monitor` end to end -- let it start the worker, add an asset,
        # confirm a query picks it up via the watcher's heartbeat (NOT its own sweep -- the
        # --verbose freshness line is the assertion), kill the watcher, confirm queries stay
        # correct via the pull path once the heartbeat goes stale (never silently wrong, just
        # slower). Kept under ~30s.
        Step 'monitor-smoke' {
            $exe = "$repo/publish/unbramble.exe"
            $monitorOut = Join-Path $tmp 'monitor.stdout.log'
            $monitorErr = Join-Path $tmp 'monitor.stderr.log'
            $proc = Start-Process -FilePath $exe -ArgumentList @('monitor', $tmp) -PassThru -WindowStyle Hidden `
                -RedirectStandardOutput $monitorOut -RedirectStandardError $monitorErr
            try {
                $deadline = (Get-Date).AddSeconds(10)
                while ((Get-Date) -lt $deadline -and -not (Select-String -Path $monitorOut -Pattern 'status: (WATCHING|PROCESSING)' -Quiet -ErrorAction SilentlyContinue)) {
                    Start-Sleep -Milliseconds 200
                }
                if (-not (Select-String -Path $monitorOut -Pattern 'status: (WATCHING|PROCESSING)' -Quiet -ErrorAction SilentlyContinue)) {
                    Write-Host 'unbramble monitor never reported progress (see monitor.stdout.log/monitor.stderr.log)'; exit 1
                }

                $newAsset = Join-Path $tmp 'Assets/Data/WatchSmoke.asset'
                Set-Content -Path $newAsset -Value 'fake' -NoNewline
                Set-Content -Path "$newAsset.meta" -Value "fileFormatVersion: 2`nguid: 50505050505050505050505050505005`n" -NoNewline

                # Poll 'resolve --verbose' until it sees the new asset via a fresh watcher
                # heartbeat -- i.e. WITHOUT running its own sweep. That's the freshness gate:
                # the watcher, not the pull path, made this queryable.
                # Text-mode freshness lines land on STDOUT (one ordered stream for agent
                # harnesses -- see Program.PrintFreshness); capture all streams to be safe.
                $verboseLog = Join-Path $tmp 'resolve.verbose.log'
                $sawHeartbeat = $false
                $deadline = (Get-Date).AddSeconds(10)
                while ((Get-Date) -lt $deadline) {
                    & $exe resolve WatchSmoke -p $tmp --verbose *> $verboseLog
                    $exitCode = $LASTEXITCODE
                    $outText = Get-Content $verboseLog -Raw -ErrorAction SilentlyContinue
                    if ($exitCode -eq 0 -and $outText -match 'freshness: watcher heartbeat') {
                        $sawHeartbeat = $true
                        break
                    }
                    Start-Sleep -Milliseconds 300
                }
                if (-not $sawHeartbeat) {
                    Write-Host "query never saw 'freshness: watcher heartbeat' while the watcher was active (last output: $(Get-Content $verboseLog -Raw -ErrorAction SilentlyContinue))"
                    exit 1
                }
            } finally {
                Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
                & $exe stop | Out-Null
            }

            # Watcher is dead. Its last heartbeat is still within the 15s staleness window right
            # after the kill (by design -- staleness is time-based, not a liveness probe), so
            # wait it out, then confirm the pull path resumes and queries stay correct.
            Start-Sleep -Seconds 16
            $verboseLog2 = Join-Path $tmp 'resolve.verbose2.log'
            & $exe resolve WatchSmoke -p $tmp --verbose *> $verboseLog2
            if ($LASTEXITCODE -ne 0) { Write-Host 'query failed after the watcher was killed'; exit 1 }
            $outText2 = Get-Content $verboseLog2 -Raw -ErrorAction SilentlyContinue
            if ($outText2 -notmatch 'freshness: swept') {
                Write-Host "expected 'freshness: swept' once the heartbeat went stale after the watcher died (got: $outText2)"
                exit 1
            }
        }
    } finally { Remove-Item $tmp -Recurse -Force }
}

if ($script:failed) {
    Write-Host ("FAIL: " + ($script:failed -join ', ')) -ForegroundColor Red
    exit 1
}
Write-Host 'PASS' -ForegroundColor Green
exit 0
