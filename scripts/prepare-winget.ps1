#Requires -Version 7.3
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$wingetExe = (Get-Command winget.exe -CommandType Application).Source
$root = Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts/winget-$Version-$([guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $root
$id = 'i-snyder.unbramble'
$repoUrl = 'https://github.com/i-snyder/unbramble'
$zipName = 'unbramble-win-x64.zip'
$zipUrl = "$repoUrl/releases/download/v$Version/$zipName"
$zipPath = Join-Path $root $zipName
$api = "https://api.github.com/repos/i-snyder/unbramble/releases/tags/v$Version"
$release = Invoke-RestMethod $api -MaximumRetryCount 3
$releaseDate = ([DateTimeOffset]$release.published_at).UtcDateTime.ToString('yyyy-MM-dd')
Invoke-WebRequest $zipUrl -OutFile $zipPath -MaximumRetryCount 3
$checksumFile = "$zipPath.sha256"
Invoke-WebRequest "$zipUrl.sha256" -OutFile $checksumFile -MaximumRetryCount 3
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
if ((Get-Content $checksumFile -Raw).Trim() -notmatch "^$hash\s+$([regex]::Escape($zipName))$") {
    throw "The published ZIP and checksum disagree. Downloaded SHA-256 is $hash."
}

function Get-VerifiedDownload([string]$Url, [string]$Sha256) {
    $path = Join-Path $root ([uri]$Url).Segments[-1]
    Invoke-WebRequest $Url -OutFile $path -MaximumRetryCount 3
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $Sha256) {
        throw "SHA-256 mismatch for $Url."
    }
    return $path
}

# Pin the standalone manifest generator to Microsoft's published SHA-256.
$creator = Get-VerifiedDownload 'https://github.com/microsoft/winget-create/releases/download/v1.12.13.0/wingetcreate.exe' '24042bd37915805615e6cf969ac57c6439124c3fe85823327f5f3fb24bd9ffea'
$out = Join-Path $root 'candidate'
$oldRollForward = $env:DOTNET_ROLL_FORWARD
try {
    $env:DOTNET_ROLL_FORWARD = 'Major'
    & $creator update $id --urls $zipUrl --version $Version --release-date $releaseDate --release-notes-url $release.html_url --out $out
} finally {
    $env:DOTNET_ROLL_FORWARD = $oldRollForward
}
$candidate = Join-Path $out "manifests/i/i-snyder/unbramble/$Version"
$installer = Get-Content (Join-Path $candidate "$id.installer.yaml") -Raw
$localePath = Join-Path $candidate "$id.locale.en-US.yaml"
$locale = Get-Content $localePath -Raw
$license = "$repoUrl/blob/v$Version/LICENSE"
$locale = $locale -replace '(?m)^LicenseUrl:[^\r\n]+', "LicenseUrl: $license"
[IO.File]::WriteAllText($localePath, $locale)

function Assert-ManifestLine([string]$Yaml, [string]$Line) {
    if ($Yaml -notmatch ('(?m)^[ -]*' + [regex]::Escape($Line) + '\s*$')) {
        throw "Missing or incorrect WinGet manifest field '$Line'."
    }
}
foreach ($line in @("PackageVersion: $Version", 'InstallerType: zip', 'NestedInstallerType: portable',
        'RelativeFilePath: unbramble.exe', 'PortableCommandAlias: unbramble', 'ArchiveBinariesDependOnPath: true',
        'Architecture: x64', "InstallerUrl: $zipUrl", "InstallerSha256: $hash", "ReleaseDate: $releaseDate")) {
    Assert-ManifestLine $installer $line
}
Assert-ManifestLine $locale "ReleaseNotesUrl: $repoUrl/releases/tag/v$Version"
Assert-ManifestLine $locale "LicenseUrl: $license"
$filenames = "$id.installer.yaml", "$id.locale.en-US.yaml", "$id.yaml"
if (Compare-Object $filenames @(Get-ChildItem $candidate -Name)) {
    throw 'Unexpected WinGet manifest files.'
}
& $wingetExe validate --manifest $candidate

Write-Host "Validated WinGet manifests in $candidate"
