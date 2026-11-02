#Requires -Version 7
<#
.SYNOPSIS
  Checks generated project binding and fail-closed liveness through the published executable.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ExePath,

    [Parameter(Mandatory)]
    [string]$RuntimeDirectory
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

$ExePath = (Resolve-Path -LiteralPath $ExePath).ProviderPath
$RuntimeDirectory = (Resolve-Path -LiteralPath $RuntimeDirectory).ProviderPath
if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf) -or [IO.Path]::GetExtension($ExePath) -ne '.exe') {
    throw 'ExePath must identify the published unbramble.exe.'
}
$coreLibrary = Join-Path $RuntimeDirectory 'System.Private.CoreLib.dll'
if ([Reflection.AssemblyName]::GetAssemblyName($coreLibrary).Version.Major -ne 10) {
    throw 'RuntimeDirectory must contain the .NET 10 shared runtime.'
}

# The runtime directory also contains native DLLs. Inspect metadata without loading or executing
# these assemblies; native PE files must never become Roslyn metadata references.
$runtimeReferences = @(Get-ChildItem -LiteralPath $RuntimeDirectory -Filter '*.dll' -File | Where-Object {
    try {
        $null = [Reflection.AssemblyName]::GetAssemblyName($_.FullName)
        $true
    }
    catch [BadImageFormatException] {
        $false
    }
})
if ($runtimeReferences.Count -eq 0) {
    throw 'No managed runtime references were found.'
}

$temporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
$fixtureName = 'unbramble-cs-binding-smoke-' + [guid]::NewGuid().ToString('N')
$fixture = [IO.Path]::GetFullPath((Join-Path $temporaryParent $fixtureName))
$previousAutoSpawn = $env:UNBRAMBLE_DISABLE_AUTO_SPAWN

function Assert-Smoke([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        throw $Message
    }
}

function Write-FixtureFile([string]$RelativePath, [string]$Content) {
    $path = Join-Path $fixture $RelativePath
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force
    [IO.File]::WriteAllText($path, $Content)
}

function Write-GeneratedProject([string]$Assembly, [bool]$ProjectReference, [bool]$ExternalProjectReference = $false) {
    $namespace = 'http://schemas.microsoft.com/developer/msbuild/2003'
    $project = [xml]::new()
    $root = $project.CreateElement('Project', $namespace)
    $null = $project.AppendChild($root)
    $items = $project.CreateElement('ItemGroup', $namespace)
    $null = $root.AppendChild($items)
    foreach ($dll in $runtimeReferences) {
        $reference = $project.CreateElement('Reference', $namespace)
        $reference.SetAttribute('Include', [IO.Path]::GetFileNameWithoutExtension($dll.Name))
        $hint = $project.CreateElement('HintPath', $namespace)
        $hint.InnerText = $dll.FullName
        $null = $reference.AppendChild($hint)
        $null = $items.AppendChild($reference)
    }
    if ($ProjectReference) {
        $reference = $project.CreateElement('ProjectReference', $namespace)
        $reference.SetAttribute('Include', 'EventsAssembly.csproj')
        $name = $project.CreateElement('Name', $namespace)
        $name.InnerText = 'EventsAssembly'
        $null = $reference.AppendChild($name)
        $null = $items.AppendChild($reference)
    }
    if ($ExternalProjectReference) {
        $reference = $project.CreateElement('ProjectReference', $namespace)
        $reference.SetAttribute('Include', 'UnBramble.Core.csproj')
        $name = $project.CreateElement('Name', $namespace)
        $name.InnerText = 'UnBramble.Core'
        $null = $reference.AppendChild($name)
        $null = $items.AppendChild($reference)
    }
    $project.Save((Join-Path $fixture ($Assembly + '.csproj')))
}

function Invoke-CliJson([string[]]$Arguments, [int]$ExpectedExit = 0) {
    $token = [guid]::NewGuid().ToString('N')
    $stdoutPath = Join-Path $fixture ($token + '.stdout.json')
    $stderrPath = Join-Path $fixture ($token + '.stderr.log')
    & $ExePath @Arguments 1> $stdoutPath 2> $stderrPath
    $commandExit = $LASTEXITCODE
    $stdout = Get-Content -LiteralPath $stdoutPath -Raw
    $stderr = Get-Content -LiteralPath $stderrPath -Raw
    if ($commandExit -ne $ExpectedExit) {
        throw "unbramble $($Arguments -join ' ') exited $commandExit, expected ${ExpectedExit}: $stderr $stdout"
    }
    try {
        return $stdout | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "unbramble $($Arguments -join ' ') did not return valid JSON: $stdout"
    }
}

try {
    $env:UNBRAMBLE_DISABLE_AUTO_SPAWN = '1'
    [xml]$cliProject = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../src/UnBramble.Cli/UnBramble.Cli.csproj') -Raw
    $expectedVersion = $cliProject.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
    $actualVersion = (& $ExePath --version | Out-String).Trim()
    Assert-Smoke ($LASTEXITCODE -eq 0 -and $actualVersion -eq "unbramble $expectedVersion") "Unexpected published version: $actualVersion"

    $builtCore = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../src/UnBramble.Core/bin/Release/net10.0/UnBramble.Core.dll')).ProviderPath

    $null = New-Item -ItemType Directory -Path $fixture
    Write-FixtureFile 'ProjectSettings/ProjectVersion.txt' "m_EditorVersion: 2022.3.30f1`n"
    Write-FixtureFile 'ProjectSettings/EditorSettings.asset' "%YAML 1.1`n--- !u!159 &1`nEditorSettings:`n  m_SerializationMode: 2`n"
    Write-FixtureFile 'Packages/manifest.json' '{"dependencies":{}}'
    Write-FixtureFile 'Assets/EventLibrary/EventsAssembly.asmdef' '{"name":"EventsAssembly"}'
    Write-FixtureFile 'Assets/EventLibrary/EventsAssembly.asmdef.meta' "fileFormatVersion: 2`nguid: 22222222222222222222222222222222`n"
    Write-FixtureFile 'Assets/EventLibrary/EventSource.cs' 'namespace SmokeEvents { public static class EventSource { public static event System.Action Removed; } }'
    Write-FixtureFile 'Assets/EventLibrary/EventSource.cs.meta' "fileFormatVersion: 2`nguid: 33333333333333333333333333333333`n"
    Write-FixtureFile 'Assets/Scripts/Entry.cs' 'using SmokeEvents; public static class Entry { private const string Marker = UnBramble.Core.Query.BlindSpots.CsprojStale; public static void Main() { EventSource.Removed += Handle; EventSource.Removed -= Handle; } private static void Handle() {} }'
    Write-FixtureFile 'Assets/Scripts/Entry.cs.meta' "fileFormatVersion: 2`nguid: 44444444444444444444444444444444`n"
    Write-FixtureFile 'Library/PackageCache/com.example.core@1.0.0/Core.cs' '// Identity-only source placeholder; the consumer binds the compiled package metadata.'
    Write-FixtureFile 'Library/PackageCache/com.example.core@1.0.0/Core.cs.meta' "fileFormatVersion: 2`nguid: 55555555555555555555555555555555`n"
    Write-FixtureFile 'UnBramble.Core.csproj' '<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><PropertyGroup><AssemblyName>UnBramble.Core</AssemblyName></PropertyGroup><ItemGroup><Compile Include="Library/PackageCache/com.example.core@1.0.0/Core.cs" /></ItemGroup></Project>'
    $null = New-Item -ItemType Directory -Path (Join-Path $fixture 'Library/ScriptAssemblies') -Force
    $externalOutput = Join-Path $fixture 'Library/ScriptAssemblies/UnBramble.Core.dll'
    Copy-Item -LiteralPath $builtCore -Destination $externalOutput
    Write-GeneratedProject 'EventsAssembly' $false
    Write-GeneratedProject 'Assembly-CSharp' $true $true

    $null = Invoke-CliJson -Arguments @('index', '-p', $fixture, '--full', '--json')
    $bound = Invoke-CliJson -Arguments @('cs-refs', 'E:SmokeEvents.EventSource.Removed', '-p', $fixture, '--json')
    Assert-Smoke ($bound.count -eq 2 -and @($bound.results).Count -eq 2) 'ProjectReference did not produce both event references.'
    $incorrect = @($bound.results | Where-Object { $_.confidence -ne 'semantic' -or $_.source -ne 'Assets/Scripts/Entry.cs' -or $_.refKind -ne 'member-access' })
    Assert-Smoke ($incorrect.Count -eq 0) 'Event reference source, kind, or semantic confidence was incorrect.'
    Assert-Smoke ($bound.blindSpots -notcontains 'cs-binding-incomplete') 'Healthy source dependencies reported incomplete binding.'
    $healthyStats = Invoke-CliJson -Arguments @('stats', '-p', $fixture, '--json')
    Assert-Smoke (@($healthyStats.bindingFailures).Count -eq 0 -and $healthyStats.cs.bindingIncompleteAssemblies -eq 0) 'Healthy assemblies retained binding failures.'
    $live = Invoke-CliJson -Arguments @('dead-candidates', '-p', $fixture, '--json', '--include-advisory')
    Assert-Smoke ($live.available -eq $true -and $live.roots.entryPointFileCount -eq 1) 'Healthy event-only fixture did not pass liveness gates.'
    $deadPublisher = @($live.provenDead + $live.advisoryDead | Where-Object { $_.path -eq 'Assets/EventLibrary/EventSource.cs' })
    Assert-Smoke ($deadPublisher.Count -eq 0) 'The referenced event-only publisher was classified as dead.'

    # PackageCache sources stay identity-only. Their exact compiled output must be refreshed
    # when it appears/disappears even if the consuming generated project hasn't changed.
    $consumerProject = Join-Path $fixture 'Assembly-CSharp.csproj'
    $consumerProjectMtime = (Get-Item -LiteralPath $consumerProject).LastWriteTimeUtc.Ticks
    Remove-Item -LiteralPath $externalOutput
    $null = Invoke-CliJson -Arguments @('index', '-p', $fixture, '--json')
    $missingExternal = Invoke-CliJson -Arguments @('stats', '-p', $fixture, '--json')
    $externalFailures = @($missingExternal.bindingFailures)
    Assert-Smoke ($externalFailures.Count -eq 1 -and $externalFailures[0].assembly -eq 'Assembly-CSharp' -and $externalFailures[0].errorCount -gt 0) 'Removing external project output did not invalidate metadata binding.'
    $externalQuery = Invoke-CliJson -Arguments @('cs-refs', 'E:SmokeEvents.EventSource.Removed', '-p', $fixture, '--json')
    Assert-Smoke ($externalQuery.blindSpots -contains 'cs-binding-incomplete') 'Missing external project output did not qualify query coverage.'
    Copy-Item -LiteralPath $builtCore -Destination $externalOutput
    $null = Invoke-CliJson -Arguments @('index', '-p', $fixture, '--json')
    $restoredExternal = Invoke-CliJson -Arguments @('stats', '-p', $fixture, '--json')
    Assert-Smoke (@($restoredExternal.bindingFailures).Count -eq 0) 'Restoring external project output did not clear binding failures.'
    Assert-Smoke ((Get-Item -LiteralPath $consumerProject).LastWriteTimeUtc.Ticks -eq $consumerProjectMtime) 'External output smoke unexpectedly changed the consuming project.'
    $restoredLiveness = Invoke-CliJson -Arguments @('dead-candidates', '-p', $fixture, '--json', '--include-advisory')
    Assert-Smoke ($restoredLiveness.available -eq $true) 'Restored external metadata did not recover liveness availability.'

    # Keep valid BCL inputs and semantic mode while removing only the source dependency.
    Write-GeneratedProject 'Assembly-CSharp' $false $true
    $null = Invoke-CliJson -Arguments @('index', '-p', $fixture, '--full', '--json')
    $unbound = Invoke-CliJson -Arguments @('cs-refs', 'E:SmokeEvents.EventSource.Removed', '-p', $fixture, '--json')
    Assert-Smoke ($unbound.count -eq 0 -and $unbound.blindSpots -contains 'cs-binding-incomplete') 'Missing source dependency did not expose incomplete binding.'
    $brokenStats = Invoke-CliJson -Arguments @('stats', '-p', $fixture, '--json')
    $failures = @($brokenStats.bindingFailures)
    Assert-Smoke ($brokenStats.cs.syntacticAssemblies -eq 0 -and $brokenStats.cs.bindingIncompleteAssemblies -eq 1) 'Incomplete semantic binding was not counted separately from syntactic mode.'
    Assert-Smoke ($failures.Count -eq 1 -and $failures[0].assembly -eq 'Assembly-CSharp' -and $failures[0].errorCount -gt 0 -and $failures[0].errorCodes -match 'CS\d+') 'Stats omitted the affected assembly or compiler error codes.'
    $unavailable = Invoke-CliJson -Arguments @('dead-candidates', '-p', $fixture, '--json', '--include-advisory') -ExpectedExit 1
    Assert-Smoke ($unavailable.available -eq $false -and @($unavailable.provenDead).Count -eq 0 -and @($unavailable.advisoryDead).Count -eq 0) 'Incomplete binding emitted deletion candidates.'
    Assert-Smoke (($unavailable.unavailableReasons -join '; ') -match 'incomplete C# binding') 'Liveness omitted its incomplete-binding reason.'

    Write-Host "PASS: $actualVersion source/package binding, metadata freshness, event liveness, and incomplete-binding gates"
    $global:LASTEXITCODE = 0
}
finally {
    if ($null -eq $previousAutoSpawn) {
        Remove-Item Env:UNBRAMBLE_DISABLE_AUTO_SPAWN -ErrorAction SilentlyContinue
    }
    else {
        $env:UNBRAMBLE_DISABLE_AUTO_SPAWN = $previousAutoSpawn
    }
    if (Test-Path -LiteralPath $fixture) {
        $resolvedFixture = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $fixture).ProviderPath)
        $parentPrefix = $temporaryParent + [IO.Path]::DirectorySeparatorChar
        if (-not $resolvedFixture.StartsWith($parentPrefix, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolvedFixture) -ne $fixtureName -or $resolvedFixture -ne $fixture) {
            throw "Refusing cleanup outside the generated fixture: $resolvedFixture"
        }
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
    }
}
