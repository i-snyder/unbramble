#Requires -Version 7.3
[CmdletBinding()]
param(
    [Parameter(Mandatory)][version]$Version,
    [Parameter(Mandatory)][version]$PreviousVersion
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
if ($env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or !$IsWindows) {
    throw 'Package installation and machine uninstall require a disposable GitHub-hosted Windows runner.'
}

$root = Join-Path $env:RUNNER_TEMP 'unbramble-release'
$null = New-Item -ItemType Directory -Path $root
$fixture = Join-Path $root 'MiniProject'
Copy-Item (Join-Path $PSScriptRoot '../src/UnBramble.Tests/Fixtures/MiniProject') $fixture -Recurse
$env:UNBRAMBLE_DISABLE_AUTO_SPAWN = '1'
$env:UNBRAMBLE_DISABLE_DEFENDER_SETUP = '1'
$id = 'i-snyder.unbramble'
$repoUrl = 'https://github.com/i-snyder/unbramble'
$zipName = 'unbramble-win-x64.zip'
$zipUrl = "$repoUrl/releases/download/v$Version/$zipName"
$zipPath = Join-Path $root $zipName
$api = "https://api.github.com/repos/i-snyder/unbramble/releases/tags/v$Version"
$release = Invoke-RestMethod $api -Headers @{ Authorization = "Bearer $env:GH_TOKEN" } -MaximumRetryCount 3
$releaseDate = ([DateTimeOffset]$release.published_at).UtcDateTime.ToString('yyyy-MM-dd')
Invoke-WebRequest $zipUrl -OutFile $zipPath -MaximumRetryCount 3
$checksumFile = "$zipPath.sha256"
Invoke-WebRequest "$zipUrl.sha256" -OutFile $checksumFile -MaximumRetryCount 3
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
if ((Get-Content $checksumFile -Raw).Trim() -notmatch "^$hash\s+$([regex]::Escape($zipName))$") {
    throw "The published ZIP and checksum disagree. Downloaded SHA-256 is $hash."
}

function Test-InstalledCli([string]$Exe, [version]$ExpectedVersion) {
    if ((& $Exe --version) -ne "unbramble $ExpectedVersion") {
        throw "Expected UnBramble $ExpectedVersion at $Exe."
    }
    $result = & $Exe resolve bbbbbbbbbbbbbbbbbbbbbbbbbbbbbb02 -p $fixture --json | ConvertFrom-Json
    if (@($result.matches).Count -ne 1 -or $result.matches[0].path -ne 'Assets/Prefabs/Player.prefab') {
        throw "The installed CLI at $Exe failed its SQLite query."
    }
}

Write-Host 'Testing first-run setup and uninstall with the published ZIP.'
$manual = Join-Path $root 'zip-install'
Expand-Archive -LiteralPath $zipPath -DestinationPath $manual
$exe = Join-Path $manual 'unbramble.exe'
$initialUserPath = [Environment]::GetEnvironmentVariable('Path', 'User')
[Environment]::SetEnvironmentVariable('Path', "$initialUserPath;$manual", 'User')
& $exe init $fixture --json | Out-Null
Test-InstalledCli $exe $Version
$integration = '.unbramble', 'AGENTS.md', 'CLAUDE.md' | ForEach-Object { Join-Path $fixture $_ }
foreach ($item in $integration) {
    if (!(Test-Path -LiteralPath $item)) { throw "First-run setup omitted $item." }
}
& $exe uninstall $fixture --yes
foreach ($item in $integration) {
    if (Test-Path -LiteralPath $item) { throw "Project uninstall retained $item." }
}
& $exe uninstall --machine --yes
$deadline = [DateTime]::UtcNow.AddSeconds(30)
while ((Test-Path -LiteralPath $manual) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 250 }
if ((Test-Path -LiteralPath $manual) -or [Environment]::GetEnvironmentVariable('Path', 'User') -ne $initialUserPath) {
    throw 'Manual machine uninstall did not remove its folder and restore the user PATH.'
}

$env:Path += ";$env:LOCALAPPDATA\Microsoft\WindowsApps"
try {
    $clientVersion = ((winget.exe --version) -replace '^v') -as [version]
} catch {
    # A fresh user can have the App Installer alias before it is registered.
    $clientVersion = $null
}
if ($clientVersion -lt [version]'1.12') {
    Install-Module Microsoft.WinGet.Client -Repository PSGallery -Scope CurrentUser -Force
    Repair-WinGetPackageManager -Latest -AllUsers
    $clientVersion = ((winget.exe --version) -replace '^v') -as [version]
    if ($clientVersion -lt [version]'1.12') { throw 'WinGet 1.12 or later is required to validate the release manifest.' }
}
Write-Host "Testing with WinGet $clientVersion."
winget settings --enable LocalManifestFiles

# Pin the standalone manifest generator to Microsoft's published SHA-256.
$creator = Join-Path $root 'wingetcreate.exe'
Invoke-WebRequest 'https://github.com/microsoft/winget-create/releases/download/v1.12.13.0/wingetcreate.exe' -OutFile $creator -MaximumRetryCount 3
if ((Get-FileHash $creator -Algorithm SHA256).Hash -ne '24042bd37915805615e6cf969ac57c6439124c3fe85823327f5f3fb24bd9ffea') {
    throw 'WinGetCreate download failed its checksum.'
}
$out = Join-Path $root 'candidate'
$env:DOTNET_ROLL_FORWARD = 'Major'
& $creator update $id --urls $zipUrl --version $Version --release-date $releaseDate --release-notes-url $release.html_url --out $out --token $env:GH_TOKEN
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
winget validate --manifest $candidate

$install = '--disable-interactivity', '--accept-package-agreements', '--accept-source-agreements'
$uninstall = '--disable-interactivity', '--accept-source-agreements'
$processPath = $env:Path
function Get-InstalledExe {
    # Portable archive installs may add their package directory directly to PATH.
    $env:Path = "$processPath;$([Environment]::GetEnvironmentVariable('Path', 'User'));$([Environment]::GetEnvironmentVariable('Path', 'Machine'))"
    return (Get-Command unbramble.exe -CommandType Application -ErrorAction Stop).Source
}

Write-Host "Testing clean installation of the candidate manifest and the published $PreviousVersion catalog package."
winget install --manifest $candidate --scope machine @install
$installed = Get-InstalledExe
Test-InstalledCli $installed $Version
winget uninstall --manifest $candidate @uninstall
if (Test-Path -LiteralPath $installed) { throw 'WinGet retained the candidate executable after uninstall.' }
winget install --id $id --exact --version $PreviousVersion --source winget --scope machine @install
$installed = Get-InstalledExe
Test-InstalledCli $installed $PreviousVersion
winget uninstall --id $id --exact --source winget @uninstall
if (Test-Path -LiteralPath $installed) { throw 'WinGet retained the catalog executable after uninstall.' }

# WinGet includes the source ID in a portable package's identity. Use local manifests
# for both versions so this upgrades the original installation in place.
$previous = Join-Path $root 'previous'
$null = New-Item -ItemType Directory -Path $previous
$merged = "https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/i/i-snyder/unbramble/$PreviousVersion"
foreach ($file in $filenames) {
    Invoke-WebRequest "$merged/$file" -OutFile (Join-Path $previous $file) -MaximumRetryCount 3
}
Write-Host "Testing an in-place upgrade from the merged $PreviousVersion manifests to $Version."
winget install --manifest $previous --scope machine @install
$installed = Get-InstalledExe
Test-InstalledCli $installed $PreviousVersion
winget upgrade --manifest $candidate @install
if ((Get-InstalledExe) -ne $installed) { throw 'WinGet changed the executable path during the upgrade.' }
Test-InstalledCli $installed $Version
winget uninstall --manifest $candidate @uninstall
if (Test-Path -LiteralPath $installed) { throw 'WinGet retained the upgraded executable after uninstall.' }
