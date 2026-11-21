#Requires -Version 7.3
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$repo = 'i-snyder/unbramble'
Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    $commit = git rev-parse HEAD
    $main = gh api "repos/$repo/branches/main" --jq '.commit.sha'
    if ((git branch --show-current) -ne 'main' -or $commit -ne $main -or (git status --porcelain)) {
        throw 'Publish from a clean checkout of the current public main commit.'
    }
    [xml]$project = Get-Content ./src/UnBramble.Cli/UnBramble.Cli.csproj -Raw
    $version = $project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
    if ($version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') { throw "Invalid stable version '$version'." }
    $tag = "v$version"
    $notes = "./docs/release-notes/$tag.md"
    if ([string]::IsNullOrWhiteSpace((Get-Content $notes -Raw))) { throw "Missing release notes for $tag." }
    if (git ls-remote --tags "https://github.com/$repo.git" "refs/tags/$tag") { throw "Tag $tag already exists." }

    pwsh -NoProfile -File ./scripts/verify-all.ps1
    if ($LASTEXITCODE) { throw 'Local verification failed.' }
    dotnet package list --project UnBramble.sln --outdated
    $audit = dotnet package list --project UnBramble.sln --vulnerable --include-transitive --format json | Out-String | ConvertFrom-Json
    if (!$audit.projects -or $audit.problems) { throw 'NuGet did not complete the vulnerability audit.' }
    foreach ($projectReport in $audit.projects) {
        foreach ($framework in $projectReport.frameworks) {
            foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
                if ($package.vulnerabilities) { throw "Review the NuGet advisory for $($package.id) $($package.resolvedVersion) before publishing." }
            }
        }
    }
    & ./scripts/package-release.ps1 -Version $version
    if ((git rev-parse HEAD) -ne $commit -or (git status --porcelain)) { throw 'Source changed during release verification.' }
    gh release create $tag ./artifacts/unbramble-win-x64.zip ./artifacts/unbramble-win-x64.zip.sha256 --repo $repo --target $commit --title "UnBramble $tag" --notes-file $notes --generate-notes --fail-on-no-commits
} finally {
    Pop-Location
}
