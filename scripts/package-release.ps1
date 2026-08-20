#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z.-]+)?$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repo 'publish'
$artifactsDir = Join-Path $repo 'artifacts'
$zipName = 'unbramble-win-x64.zip'
$checksumName = "$zipName.sha256"
$zipPath = Join-Path $artifactsDir $zipName
$checksumPath = Join-Path $artifactsDir $checksumName
$projectPath = Join-Path $repo 'src/UnBramble.Cli/UnBramble.Cli.csproj'

if (-not (Test-Path -LiteralPath $publishDir -PathType Container)) {
    throw "Publish output not found at '$publishDir'. Run ./scripts/verify-all.ps1 first."
}

[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$projectVersionNode = $project.SelectSingleNode('/Project/PropertyGroup/Version')
$projectVersion = $projectVersionNode.InnerText
if (-not $projectVersion -or $projectVersion -ne $Version) {
    throw "Requested version '$Version' doesn't match the project version '$projectVersion'."
}

$requiredFiles = @(
    'unbramble.exe',
    'e_sqlite3.dll',
    'LICENSE',
    'THIRD-PARTY-NOTICES.md',
    'ROSLYN-THIRD-PARTY-NOTICES.rtf',
    'DOTNET-ILCOMPILER-THIRD-PARTY-NOTICES.txt',
    'DOTNET-NATIVEAOT-THIRD-PARTY-NOTICES.txt',
    'DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt',
    'DOTNET-LICENSE.txt',
    'SQLitePCLRaw-LICENSE.txt',
    'SQLitePCLRaw-NOTICE.txt',
    'Microsoft.Data.Sqlite-LICENSE.txt'
)
foreach ($file in $requiredFiles) {
    $path = Join-Path $publishDir $file
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required release file not found: $path"
    }
}

$pdbFiles = @(Get-ChildItem -LiteralPath $publishDir -Recurse -File -Filter '*.pdb')
if ($pdbFiles.Count -gt 0) {
    throw "Publish output contains debug symbols: $($pdbFiles.FullName -join ', ')"
}

$versionOutput = (& (Join-Path $publishDir 'unbramble.exe') --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $versionOutput -ne "unbramble $Version") {
    throw "Published executable reported '$versionOutput'; expected 'unbramble $Version'."
}

New-Item -ItemType Directory -Path $artifactsDir -Force | Out-Null
$tempZipPath = Join-Path $artifactsDir "$zipName.tmp-$([guid]::NewGuid().ToString('n'))"
try {
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $publishDir,
        $tempZipPath,
        [IO.Compression.CompressionLevel]::Optimal,
        $false
    )

    $expectedEntries = @(
        Get-ChildItem -LiteralPath $publishDir -Recurse -File |
            ForEach-Object {
                [IO.Path]::GetRelativePath($publishDir, $_.FullName).Replace('\', '/')
            } |
            Sort-Object
    )
    $archive = [IO.Compression.ZipFile]::OpenRead($tempZipPath)
    try {
        $actualEntries = @(
            $archive.Entries |
                Where-Object { $_.Name } |
                ForEach-Object { $_.FullName.Replace('\', '/') } |
                Sort-Object
        )
    } finally {
        $archive.Dispose()
    }

    if (Compare-Object -ReferenceObject $expectedEntries -DifferenceObject $actualEntries) {
        throw 'Release archive contents differ from the verified publish directory.'
    }

    Move-Item -LiteralPath $tempZipPath -Destination $zipPath -Force
} finally {
    Remove-Item -LiteralPath $tempZipPath -Force -ErrorAction SilentlyContinue
}

$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumText = "$hash  $zipName`n"
[IO.File]::WriteAllText($checksumPath, $checksumText, [Text.UTF8Encoding]::new($false))

Write-Host "Created $zipPath" -ForegroundColor Green
Write-Host "Created $checksumPath" -ForegroundColor Green
Write-Host "SHA-256 $hash"
