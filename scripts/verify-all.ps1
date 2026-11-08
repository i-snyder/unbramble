#Requires -Version 7
[CmdletBinding()]
param(
    [switch]$SkipPublish,
    [switch]$LocalDiagnostics,
    [ValidateNotNullOrEmpty()]
    [string]$PublishDirectory = 'publish'
)
$ErrorActionPreference = 'Stop'
if ($SkipPublish -and $LocalDiagnostics) {
    throw '-LocalDiagnostics requires a publish; don''t combine it with -SkipPublish.'
}
$repo = Split-Path -Parent $PSScriptRoot
$script:failed = @()
$minimumSdkVersion = [version](Get-Content -LiteralPath (Join-Path $repo 'global.json') -Raw | ConvertFrom-Json).sdk.version
$minimumRuntimeVersion = [version]'10.0.14'

# An alternate output keeps verification from replacing a local build that another session
# is using. Recursive cleanup is allowed only for publish/ or a child of artifacts/.
$publishDir = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($PublishDirectory, $repo))
$publishExe = Join-Path $publishDir 'unbramble.exe'
function Assert-PublishDirectory {
    $defaultPublishDir = Join-Path $repo 'publish'
    $artifactsPrefix = (Join-Path $repo 'artifacts') + [IO.Path]::DirectorySeparatorChar
    if ($publishDir -ne $defaultPublishDir -and -not $publishDir.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "PublishDirectory must be publish/ or a subdirectory of artifacts/: $publishDir"
    }

    # Resolve-Path alone leaves junctions in the displayed path. Reject them instead of
    # letting a valid-looking output redirect cleanup outside its bounded directory.
    for ($path = $publishDir; $path; $path = [IO.Path]::GetDirectoryName($path)) {
        if (Test-Path -LiteralPath $path) {
            $item = Get-Item -LiteralPath $path -Force
            if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "PublishDirectory cannot contain a file or reparse point in its path: $path"
            }
        }
    }
    if (Test-Path -LiteralPath $publishDir) {
        $linkedEntry = Get-ChildItem -LiteralPath $publishDir -Recurse -Force |
            Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } |
            Select-Object -First 1
        if ($linkedEntry) { throw "PublishDirectory contains a reparse point: $($linkedEntry.FullName)" }
    }
}
Assert-PublishDirectory

# Prefer the standard x64 installation, then try every host discoverable from PATH or DOTNET_ROOT.
# A stale per-user host may have a different SDK/runtime mix, so validate the whole installation
# before selecting it. Invoke that exact executable below; do not rely on PATH after discovery.
$dotnetCandidates = [Collections.Generic.List[string]]::new()
$seenDotnetCandidates = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
function Add-DotnetCandidate([string]$Path) {
    if ($Path -and $seenDotnetCandidates.Add([IO.Path]::GetFullPath($Path))) {
        $dotnetCandidates.Add([IO.Path]::GetFullPath($Path))
    }
}

Add-DotnetCandidate (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
if ($env:DOTNET_ROOT_X64) { Add-DotnetCandidate (Join-Path $env:DOTNET_ROOT_X64 'dotnet.exe') }
if ($env:DOTNET_ROOT) { Add-DotnetCandidate (Join-Path $env:DOTNET_ROOT 'dotnet.exe') }
Get-Command dotnet -All -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandType -eq 'Application' } |
    ForEach-Object { Add-DotnetCandidate $_.Source }

foreach ($candidate in $dotnetCandidates) {
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }

    $candidateRoot = Split-Path -Parent $candidate
    $candidateRuntime = Get-ChildItem (Join-Path $candidateRoot 'shared\Microsoft.NETCore.App') -ErrorAction SilentlyContinue |
        Where-Object { $_.PSIsContainer -and $_.Name -match '^10\.0\.\d+$' -and [version]$_.Name -ge $minimumRuntimeVersion } |
        Sort-Object { [version]$_.Name } -Descending |
        Select-Object -First 1
    if (-not $candidateRuntime) { continue }

    $previousDotnetRoot = $env:DOTNET_ROOT
    $previousDotnetRootX64 = $env:DOTNET_ROOT_X64
    Push-Location -LiteralPath $repo
    try {
        $env:DOTNET_ROOT = $candidateRoot
        $env:DOTNET_ROOT_X64 = $candidateRoot
        # Ask the resolver from the repository root so global.json rejects older SDKs,
        # including when this script was invoked from a different working directory.
        $sdkVersionText = (& $candidate --version 2>$null | Out-String).Trim()
        $sdkVersionExitCode = $LASTEXITCODE
        $dotnetInfo = (& $candidate --info 2>$null | Out-String)
        $dotnetInfoExitCode = $LASTEXITCODE
    } finally {
        Pop-Location
        $env:DOTNET_ROOT = $previousDotnetRoot
        $env:DOTNET_ROOT_X64 = $previousDotnetRootX64
    }
    if ($sdkVersionExitCode -eq 0 -and $sdkVersionText -match '^10\.0\.\d+$' -and
        [version]$sdkVersionText -ge $minimumSdkVersion -and
        $dotnetInfoExitCode -eq 0 -and $dotnetInfo -match '(?m)^\s*Architecture:\s*x64\s*$') {
        $script:dotnetExe = $candidate
        $script:dotnetRoot = $candidateRoot
        $script:dotnetSdkVersion = $sdkVersionText
        $script:net10RuntimeDir = $candidateRuntime
        break
    }
}

if (-not $script:dotnetExe) {
    throw "No complete x64 .NET installation satisfies global.json (SDK $minimumSdkVersion or a later 10.0.4xx patch) and runtime $minimumRuntimeVersion or later. Install the patched x64 .NET 10 SDK and rerun this script."
}

$env:DOTNET_ROOT = $script:dotnetRoot
$env:DOTNET_ROOT_X64 = $script:dotnetRoot
Write-Verbose "Using $script:dotnetExe with SDK $script:dotnetSdkVersion and .NET runtime $($script:net10RuntimeDir.Name)."

# Keep ordinary query verbs from starting detached workers throughout the shared fixture run.
# `monitor` is an explicit request and deliberately bypasses this test-only guard.
$env:UNBRAMBLE_DISABLE_AUTO_SPAWN = '1'

function Step([string]$Name, [scriptblock]$Body) {
    Write-Host "== $Name ==" -ForegroundColor Cyan
    Push-Location -LiteralPath $repo
    try {
        & $Body
        if ($LASTEXITCODE -ne 0) {
            $script:failed += $Name
            Write-Host "-- $Name FAILED (exit $LASTEXITCODE)" -ForegroundColor Red
        }
    } finally {
        Pop-Location
    }
}

function Remove-VerificationTempDirectory([string]$Path, [string]$ExpectedName) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $resolved = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Path).ProviderPath)
    if ([IO.Path]::GetDirectoryName($resolved) -ne $tempParent -or [IO.Path]::GetFileName($resolved) -ne $ExpectedName) {
        throw "Refusing cleanup outside the generated verification directory: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
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
        Assert-PublishDirectory
        Get-Process -Name unbramble -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and ([IO.Path]::GetFullPath($_.Path) -eq $publishExe) } |
            Stop-Process -Force -ErrorAction SilentlyContinue

        Remove-Item -LiteralPath $publishDir -Recurse -Force -ErrorAction SilentlyContinue

        # Try NativeAOT first (the preferred path). Local diagnostics keep the native PDB and
        # its matching PE debug record; ordinary verification stays release-identical and
        # strips both. Fall back to a
        # self-contained single-file publish if the local machine is missing native
        # publish tooling (e.g. the VS C++ workload).
        $debugSymbols = if ($LocalDiagnostics) { 'true' } else { 'false' }
        $debugType = if ($LocalDiagnostics) { 'full' } else { 'None' }
        & $script:dotnetExe publish "$repo/src/UnBramble.Cli/UnBramble.Cli.csproj" -c Release -r win-x64 "-p:DebugSymbols=$debugSymbols" "-p:DebugType=$debugType" -o $publishDir
        if ($LASTEXITCODE -ne 0) {
            if ($LocalDiagnostics) {
                throw 'The local diagnostic NativeAOT publish failed. Install or repair the native toolchain; a managed fallback cannot reproduce NativeAOT-only failures.'
            }
            Write-Host "-- NativeAOT publish failed locally (likely missing native toolchain); falling back to self-contained single-file publish --" -ForegroundColor Yellow
            Assert-PublishDirectory
            Remove-Item -LiteralPath $publishDir -Recurse -Force -ErrorAction SilentlyContinue
            & $script:dotnetExe publish "$repo/src/UnBramble.Cli/UnBramble.Cli.csproj" -c Release -r win-x64 --self-contained true -p:PublishAot=false -p:PublishSingleFile=true "-p:DebugSymbols=$debugSymbols" "-p:DebugType=$debugType" -o $publishDir
        }

        if ($LASTEXITCODE -ne 0) { return }

        if ($LocalDiagnostics) {
            $nativePdb = Join-Path $publishDir 'unbramble.pdb'
            if (-not (Test-Path -LiteralPath $nativePdb -PathType Leaf)) {
                throw "The local diagnostic NativeAOT publish did not produce its matching PDB: $nativePdb"
            }
            Write-Host '-- Local diagnostic publish: symbols retained; do not distribute this build --' -ForegroundColor Yellow
        }
        else {
            # Keep symbols out of the public binary folder: they are very large and contain
            # absolute build-machine paths. DebugType=None also keeps that path out of the PE
            # debug directory in unbramble.exe itself.
            Get-ChildItem -LiteralPath $publishDir -Filter '*.pdb' -File |
                Remove-Item -Force
            $publishedExeText = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($publishExe))
            foreach ($privatePath in @($repo, $env:USERPROFILE)) {
                if ($privatePath -and $publishedExeText.Contains($privatePath, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "Published executable contains a private build path: $privatePath"
                }
            }
        }

        # Consolidate the exact notices from the packages this publish resolved. The release keeps
        # one readable Markdown file instead of exposing the package manager's internal file layout.
        $assetsPath = Join-Path $repo 'src/UnBramble.Cli/obj/project.assets.json'
        $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -AsHashtable
        $packageRoot = @($assets.packageFolders.Keys)[0]
        $libraryNames = @($assets.libraries.Keys)
        $roslynLibrary = $libraryNames | Where-Object { $_ -like 'Microsoft.CodeAnalysis.CSharp/*' } | Select-Object -First 1
        $ilcLibrary = $libraryNames | Where-Object { $_ -like 'Microsoft.DotNet.ILCompiler/*' } | Select-Object -First 1
        $nativeIlcLibrary = $libraryNames | Where-Object { $_ -like 'runtime.win-x64.Microsoft.DotNet.ILCompiler/*' } | Select-Object -First 1
        $netCoreRuntimeDependency = @($assets.project.frameworks['net10.0'].downloadDependencies) |
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

        $projectLicense = Join-Path $repo 'LICENSE'
        $roslynNotice = PackagePath $roslynLibrary 'ThirdPartyNotices.rtf'
        $dotnetLicense = PackagePath $netCoreRuntimeLibrary 'LICENSE.TXT'
        $dotnetNotices = @(
            @{ Title = '.NET runtime third-party notices'; Path = (PackagePath $netCoreRuntimeLibrary 'THIRD-PARTY-NOTICES.TXT') },
            @{ Title = '.NET NativeAOT third-party notices'; Path = (PackagePath $nativeIlcLibrary 'THIRD-PARTY-NOTICES.TXT') },
            @{ Title = '.NET ILCompiler third-party notices'; Path = (PackagePath $ilcLibrary 'THIRD-PARTY-NOTICES.TXT') }
        )
        $microsoftDataSqliteLicense = Join-Path $repo 'licenses/Microsoft.Data.Sqlite-LICENSE.txt'
        $sqlitePclRawLicense = Join-Path $repo 'licenses/SQLitePCLRaw-LICENSE.txt'
        $sqlitePclRawNotice = Join-Path $repo 'licenses/SQLitePCLRaw-NOTICE.txt'
        $requiredNoticeSources = @(
            $projectLicense,
            $roslynNotice,
            $dotnetLicense,
            $microsoftDataSqliteLicense,
            $sqlitePclRawLicense,
            $sqlitePclRawNotice
        ) + @($dotnetNotices.Path)
        foreach ($source in $requiredNoticeSources) {
            if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
                throw "Required distribution notice not found: $source"
            }
        }

        Add-Type -AssemblyName System.Windows.Forms
        $rtfReader = [Windows.Forms.RichTextBox]::new()
        try {
            $rtfReader.Rtf = Get-Content -LiteralPath $roslynNotice -Raw
            $roslynNoticeText = $rtfReader.Text
        } finally {
            $rtfReader.Dispose()
        }

        $licenses = [Text.StringBuilder]::new()
        [void]$licenses.AppendLine('# UnBramble licenses and notices')
        [void]$licenses.AppendLine()
        [void]$licenses.AppendLine('This file contains the license terms and attribution notices for UnBramble and the third-party software included in its Windows release. Duplicate notice texts emitted by multiple resolved .NET packages are included once.')

        function Append-LicenseSection([string]$Title, [string]$Text) {
            [void]$licenses.AppendLine()
            [void]$licenses.AppendLine("## $Title")
            [void]$licenses.AppendLine()
            [void]$licenses.AppendLine($Text.Trim())
            [void]$licenses.AppendLine()
        }

        Append-LicenseSection 'UnBramble' (Get-Content -LiteralPath $projectLicense -Raw)
        Append-LicenseSection '.NET runtime and NativeAOT' (Get-Content -LiteralPath $dotnetLicense -Raw)
        $seenDotnetNoticeHashes = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($notice in $dotnetNotices) {
            $noticeHash = (Get-FileHash -LiteralPath $notice.Path -Algorithm SHA256).Hash
            if ($seenDotnetNoticeHashes.Add($noticeHash)) {
                Append-LicenseSection $notice.Title (Get-Content -LiteralPath $notice.Path -Raw)
            }
        }
        Append-LicenseSection 'Roslyn (.NET Compiler Platform)' $roslynNoticeText
        Append-LicenseSection 'Microsoft.Data.Sqlite' (Get-Content -LiteralPath $microsoftDataSqliteLicense -Raw)
        Append-LicenseSection 'SQLitePCLRaw — Apache License 2.0' (Get-Content -LiteralPath $sqlitePclRawLicense -Raw)
        Append-LicenseSection 'SQLitePCLRaw and SQLite notices' (Get-Content -LiteralPath $sqlitePclRawNotice -Raw)

        [IO.File]::WriteAllText(
            (Join-Path $publishDir 'LICENSES.md'),
            $licenses.ToString(),
            [Text.UTF8Encoding]::new($false)
        )
    }
    Step 'smoke-version' {
        & $publishExe --version
    }

    # The deliberate-failure self-test runs before the real gate below, so a
    # broken parity-detection mechanism (one that would silently PASS everything) is caught
    # before its PASS on the fixture is trusted. Builds its own throwaway fixture copy --
    # doesn't need $tmp.
    Step 'rg-parity-selftest' {
        & "$repo/scripts/rg-parity.ps1" -SelfTest -ExePath $publishExe
    }

    # fixture-smoke and rg-parity share one temp fixture copy: rg-parity.ps1 requires an
    # existing UnBramble index, which fixture-smoke's 'init' call below already builds.
    $fixtureName = "unbramble-verify-$([guid]::NewGuid().ToString('n'))"
    $tmp = Join-Path ([IO.Path]::GetTempPath()) $fixtureName
    Copy-Item "$repo/src/UnBramble.Tests/Fixtures/MiniProject" $tmp -Recurse
    try {
        Step 'fixture-smoke' {
            $initialWatcherOut = & $publishExe watcher-status $tmp --json
            if ($LASTEXITCODE) { exit 1 }
            $initialWatcher = $initialWatcherOut | ConvertFrom-Json
            if ($initialWatcher.state -ne 'absent' -or $initialWatcher.ownerActive -ne $false -or
                $initialWatcher.indexReadinessChecked -ne $false -or (Test-Path -LiteralPath (Join-Path $tmp '.unbramble'))) {
                throw "watcher-status changed a fresh project or misreported its owner: $initialWatcherOut"
            }
            & $publishExe init $tmp; if ($LASTEXITCODE) { exit 1 }
            & $publishExe resolve Foo -p $tmp; if ($LASTEXITCODE) { exit 1 }
            & $publishExe stats -p $tmp; if ($LASTEXITCODE) { exit 1 }

            # Check bounded and uncapped resolve envelopes in the published JSON serializer.
            $resolveLimitedOut = & $publishExe resolve '.cs' -p $tmp --limit 1 --json
            if ($LASTEXITCODE) { exit 1 }
            $resolveLimited = $resolveLimitedOut | ConvertFrom-Json
            if ($resolveLimited.matches.Count -ne 1 -or $resolveLimited.limit -ne 1 -or $resolveLimited.truncated -ne $true) {
                throw "resolve did not disclose its truncated result: $resolveLimitedOut"
            }
            $resolveAllOut = & $publishExe resolve '.cs' -p $tmp --all --json
            if ($LASTEXITCODE) { exit 1 }
            $resolveAll = $resolveAllOut | ConvertFrom-Json
            if ($resolveAll.matches.Count -le 1 -or $resolveAllOut -notmatch '"limit":null' -or $resolveAll.truncated -ne $false) {
                throw "resolve --all did not report complete uncapped results: $resolveAllOut"
            }

            # who-uses/uses smoke, incl. --json and the missing-query/explicit-CI exit contract.
            & $publishExe who-uses Assets/Scripts/Foo.cs -p $tmp --json; if ($LASTEXITCODE) { exit 1 }
            & $publishExe uses Assets/Scenes/Level.unity -p $tmp --missing-only
            if ($LASTEXITCODE -ne 0) { Write-Host 'findings incorrectly made a successful query fail'; exit 1 }
            & $publishExe uses Assets/Scenes/Level.unity -p $tmp --missing-only --fail-if-found
            if ($LASTEXITCODE -ne 3) { Write-Host 'expected exit 3 from explicit --fail-if-found gate'; exit 1 }
            & $publishExe uses Assets/Materials/Rock.mat -p $tmp --missing-only
            if ($LASTEXITCODE -ne 0) { Write-Host 'builtin wrongly counted as missing'; exit 1 }

            # cs-refs smoke -- CoreUtil.Ping must show Foo.cs as a referencer.
            $csRefsOut = & $publishExe cs-refs CoreUtil.Ping -p $tmp --json
            if ($LASTEXITCODE -ne 0) { Write-Host 'cs-refs failed'; exit 1 }
            if ($csRefsOut -notmatch 'Assets/Scripts/Foo\.cs') { Write-Host "cs-refs did not report Foo.cs as a referencer of CoreUtil.Ping: $csRefsOut"; exit 1 }

            # Schema/name-hints smoke -- stats must report the current schema version and a
            # nonzero name_hints count.
            # The fixture's asset-side sources alone (Hit.anim's m_FunctionName, Player.prefab's
            # two guid-less UnityEvent bindings) guarantee >= 1 regardless of C# analysis mode,
            # so this catches the capture path silently breaking without depending on the
            # fixture landing in semantic mode.
            $statsOut = & $publishExe stats -p $tmp --json
            if ($LASTEXITCODE -ne 0) { Write-Host 'stats failed'; exit 1 }
            $storeSource = Get-Content -LiteralPath (Join-Path $repo 'src/UnBramble.Core/Store/UnBrambleStore.cs') -Raw
            $schemaMatch = [regex]::Match($storeSource, 'public const int CurrentSchemaVersion = (\d+);')
            if (-not $schemaMatch.Success) { throw 'Could not read the current index schema version.' }
            $expectedSchema = [int]$schemaMatch.Groups[1].Value
            if (($statsOut | ConvertFrom-Json).db.schemaVersion -ne $expectedSchema) { Write-Host "stats did not report schema v${expectedSchema}: $statsOut"; exit 1 }
            if ($statsOut -notmatch '"nameHints":[1-9]') { Write-Host "stats reported zero name_hints (capture silently broken?): $statsOut"; exit 1 }
        }

        # dead-candidates end to end through the published binary (the xUnit
        # suite in the 'test' step above already covers the exact-set ledger, the global
        # liveness invariant, and every gate variant in depth -- this step's job is to catch
        # anything specific to the PUBLISHED (possibly NativeAOT) binary, e.g. JSON source-gen
        # wiring for the new DeadCandidatesResultJson type, that a DLL-hosted test can't).
        Step 'dead-candidates-smoke' {
            $exe = $publishExe

            # Base (unmodified) $tmp fixture has no generated csproj -> syntactic mode -> gate 3
            # fails -> "liveness unavailable", exit 1, zero candidates.
            & $exe dead-candidates -p $tmp 2> "$tmp/dc-unavailable.stderr.log"
            if ($LASTEXITCODE -ne 1) { Write-Host 'expected exit 1 (syntactic mode, liveness unavailable)'; exit 1 }
            $unavailableErr = Get-Content "$tmp/dc-unavailable.stderr.log" -Raw
            if ($unavailableErr -notmatch 'liveness unavailable') { Write-Host "expected 'liveness unavailable' on stderr: $unavailableErr"; exit 1 }

            # Force semantic mode (same throwaway-csproj trick the xUnit suite uses -- real
            # <Reference>/<HintPath> entries pointing at actual on-disk DLLs, since
            # CsModeSelector only counts a reference whose HintPath resolves). MUST be the
            # actual net10.0 shared runtime (the TFM unbramble.exe itself targets) -- pwsh's own
            # bundled runtime directory ([RuntimeEnvironment]::GetRuntimeDirectory() from
            # within pwsh) is a DIFFERENT, incompatible assembly set that silently produces
            # broken/partial semantic resolution (found the hard way: Helper.Handle's
            # method-group conversion failed to resolve against it, wrongly reporting Helper.cs
            # as provenDead).
            $msbuildNs = 'http://schemas.microsoft.com/developer/msbuild/2003'
            # Shared runtime folders also contain native DLLs. Only managed metadata belongs
            # in a generated C# project's references; native files produce CS0009 diagnostics.
            $runtimeDlls = Get-ChildItem -Path $script:net10RuntimeDir.FullName -Filter '*.dll' | Where-Object {
                try { [void][Reflection.AssemblyName]::GetAssemblyName($_.FullName); $true }
                catch [BadImageFormatException] { $false }
            }
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
            # Exercise a clean semantic rebuild here. Project-file-only incremental transitions
            # are covered by CsBindingSafetyTests and the published binding smoke below.
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

        Step 'cs-binding-smoke' {
            & "$repo/scripts/cs-binding-smoke.ps1" -ExePath $publishExe -RuntimeDirectory $script:net10RuntimeDir.FullName
        }

        # Addressables-unavailable variant: an isolated minimal project whose manifest
        # names an unconfirmed Addressables version. Its own throwaway temp dir -- must not
        # touch $tmp, which the remaining steps below still depend on.
        Step 'dead-candidates-addressables-unavailable' {
            $exe = $publishExe
            $addrFixtureName = "unbramble-verify-addr-$([guid]::NewGuid().ToString('n'))"
            $addrTmp = Join-Path ([IO.Path]::GetTempPath()) $addrFixtureName
            New-Item -ItemType Directory -Force -Path "$addrTmp/ProjectSettings", "$addrTmp/Packages" | Out-Null
            try {
                Set-Content -Path "$addrTmp/ProjectSettings/ProjectVersion.txt" -Value "m_EditorVersion: 2022.3.30f1`n" -NoNewline
                Set-Content -Path "$addrTmp/ProjectSettings/EditorSettings.asset" -Value "%YAML 1.1`n--- !u!159 &1`nEditorSettings:`n  m_SerializationMode: 2`n" -NoNewline
                Set-Content -Path "$addrTmp/Packages/manifest.json" -Value '{ "dependencies": { "com.unity.addressables": "99.0.0" } }' -NoNewline

                & $exe dead-candidates -p $addrTmp 2> "$addrTmp/dc.stderr.log"
                if ($LASTEXITCODE -ne 1) { Write-Host 'expected exit 1 (unconfirmed Addressables version)'; exit 1 }
                $err = Get-Content "$addrTmp/dc.stderr.log" -Raw
                if ($err -notmatch 'unconfirmed root coverage') { Write-Host "expected the Addressables-unavailable reason on stderr: $err"; exit 1 }
            } finally {
                Remove-VerificationTempDirectory -Path $addrTmp -ExpectedName $addrFixtureName
            }

            # Same reset as dead-candidates-smoke above: the last external call's exit 1 was
            # already validated as correct.
            $global:LASTEXITCODE = 0
        }

        # The real gate -- every guid in the fixture's index must produce an
        # exact rg-vs-who-uses set match, properly scoped. Same $tmp as fixture-smoke, still
        # indexed from the 'init' call above.
        Step 'rg-parity' {
            & "$repo/scripts/rg-parity.ps1" -ProjectRoot $tmp -ExePath $publishExe
        }

        # `unbramble monitor` end to end -- let it start the worker, add an asset,
        # confirm a query picks it up via the watcher's heartbeat (NOT its own sweep -- the
        # --verbose freshness line is the assertion), kill the watcher, confirm queries stay
        # correct via the pull path once the heartbeat goes stale (never silently wrong, just
        # slower). Kept under ~30s.
        Step 'monitor-smoke' {
            # `stop` matches image names system-wide. A private copy with a unique image name
            # keeps both the monitor and its detached worker separate from installed watchers.
            $monitorCliDir = Join-Path $tmp '.monitor-cli'
            Copy-Item -LiteralPath $publishDir -Destination $monitorCliDir -Recurse
            $monitorImageName = "unbramble-smoke-$([guid]::NewGuid().ToString('n'))"
            $exe = Join-Path $monitorCliDir "$monitorImageName.exe"
            Rename-Item -LiteralPath (Join-Path $monitorCliDir 'unbramble.exe') -NewName "$monitorImageName.exe"
            $monitorOut = Join-Path $tmp 'monitor.stdout.log'
            $monitorErr = Join-Path $tmp 'monitor.stderr.log'
            $proc = $null
            try {
                $proc = Start-Process -FilePath $exe -ArgumentList @('monitor', "`"$tmp`"") -PassThru -WindowStyle Hidden `
                    -RedirectStandardOutput $monitorOut -RedirectStandardError $monitorErr
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

                # Attaching another monitor starts a redundant worker. Its successful early
                # exit must leave the owner's status alone instead of publishing its own STOPPED.
                $statusPath = Join-Path $tmp '.unbramble/watch.status.json'
                $ownerStatus = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
                if ($ownerStatus.pid -le 0 -or $ownerStatus.state -eq 'Stopped') {
                    throw 'The active smoke-test watcher did not have a healthy status snapshot.'
                }
                $lastQueryPath = Join-Path $tmp '.unbramble/watch.lastquery'
                $lastQueryBeforeStatus = Get-Content -LiteralPath $lastQueryPath -Raw
                $watcherHealthOut = & $exe watcher-status $tmp --json
                if ($LASTEXITCODE) { exit 1 }
                $watcherHealth = $watcherHealthOut | ConvertFrom-Json
                if ($watcherHealth.state -ne 'watching' -or $watcherHealth.ownerActive -ne $true -or
                    $watcherHealth.pid -ne $ownerStatus.pid -or -not $watcherHealth.watcherSessionId -or
                    $watcherHealth.heartbeatFresh -ne $true -or $watcherHealth.indexReadinessChecked -ne $false) {
                    throw "watcher-status did not verify the active owner: $watcherHealthOut"
                }
                if ((Get-Content -LiteralPath $lastQueryPath -Raw) -ne $lastQueryBeforeStatus) {
                    throw 'watcher-status unexpectedly renewed the idle marker.'
                }
                $duplicateWorker = $null
                try {
                    $duplicateWorker = Start-Process -FilePath $exe -ArgumentList @('watch-worker', "`"$tmp`"") -PassThru -WindowStyle Hidden `
                        -RedirectStandardOutput (Join-Path $tmp 'duplicate-worker.stdout.log') `
                        -RedirectStandardError (Join-Path $tmp 'duplicate-worker.stderr.log')
                    if (-not $duplicateWorker.WaitForExit(10000)) {
                        throw 'The redundant smoke-test worker did not exit within 10 seconds.'
                    }
                    if ($duplicateWorker.ExitCode -ne 0) {
                        throw "The redundant smoke-test worker exited $($duplicateWorker.ExitCode), expected 0."
                    }
                    $statusAfterDuplicate = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
                    if ($statusAfterDuplicate.pid -ne $ownerStatus.pid -or $statusAfterDuplicate.state -eq 'Stopped') {
                        throw 'The redundant smoke-test worker replaced the active owner status.'
                    }
                } finally {
                    if ($duplicateWorker) {
                        try {
                            if (-not $duplicateWorker.HasExited) {
                                Stop-Process -InputObject $duplicateWorker -Force -ErrorAction SilentlyContinue
                                if (-not $duplicateWorker.WaitForExit(5000)) { throw 'The redundant smoke-test worker could not be stopped.' }
                            }
                        } finally { $duplicateWorker.Dispose() }
                    }
                }
            } finally {
                $monitorStopped = $true
                if ($proc) {
                    if (-not $proc.HasExited) {
                        Stop-Process -InputObject $proc -Force -ErrorAction SilentlyContinue
                        $monitorStopped = $proc.WaitForExit(5000)
                    }
                    $proc.Dispose()
                }
                & $exe stop -p $tmp | Out-Null
                $stopExitCode = $LASTEXITCODE
                # Also wait for (or terminate) a worker that was still starting when stop ran.
                foreach ($ownedProcess in @(Get-Process -Name $monitorImageName -ErrorAction SilentlyContinue)) {
                    try {
                        if ($ownedProcess.Path -eq $exe) {
                            Stop-Process -InputObject $ownedProcess -Force -ErrorAction SilentlyContinue
                            if (-not $ownedProcess.WaitForExit(5000)) { throw 'The smoke-test watcher did not exit.' }
                        }
                    } finally { $ownedProcess.Dispose() }
                }
                if (-not $monitorStopped) { throw 'The smoke-test monitor did not exit.' }
                if ($stopExitCode -ne 0) { throw "The isolated smoke-test stop command failed (exit $stopExitCode)." }
            }

            # A retained fresh heartbeat must not hide that its owner exited.
            $stoppedWatcherOut = & $exe watcher-status $tmp --json
            if ($LASTEXITCODE) { exit 1 }
            $stoppedWatcher = $stoppedWatcherOut | ConvertFrom-Json
            if ($stoppedWatcher.state -ne 'absent' -or $stoppedWatcher.ownerActive -ne $false) {
                throw "watcher-status trusted a stopped owner's retained heartbeat: $stoppedWatcherOut"
            }

            # Its last heartbeat is still within the 15s staleness window right
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
    } finally { Remove-VerificationTempDirectory -Path $tmp -ExpectedName $fixtureName }
}

if ($script:failed) {
    Write-Host ("FAIL: " + ($script:failed -join ', ')) -ForegroundColor Red
    exit 1
}
Write-Host 'PASS' -ForegroundColor Green
exit 0
