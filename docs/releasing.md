# Releasing UnBramble

## Publish the Windows package

1. Update the version in `src/UnBramble.Cli/UnBramble.Cli.csproj` and write the matching `docs/release-notes/v<version>.md`.
2. Commit and push the version and notes to public `main`. On Windows, use a clean checkout of that commit with the x64 .NET SDK from `global.json`, ripgrep, and GitHub CLI signed in with release access.
3. Run `./scripts/publish-release.ps1` in PowerShell 7.3 or later. This publishes a real release. It runs the local build, tests, publish and smoke checks, lists available NuGet updates, checks direct and transitive vulnerability advisories, packages the ZIP and checksum, and publishes them under a new tag at the verified commit. Keep each published URL and checksum stable.

## Submit the WinGet update

1. Run `./scripts/prepare-winget.ps1 -Version <version>` on Windows with WinGet installed. It reads the public release, verifies the downloaded ZIP against its checksum, generates the three manifests with the pinned Microsoft generator, checks their package fields, and runs `winget validate`. The script prints the local output directory.
2. Review those files and submit that exact version directory under `manifests/i/i-snyder/unbramble/` in a pull request to `microsoft/winget-pkgs`. Keep Actions disabled on the personal manifest fork. Microsoft runs the catalog checks on the upstream pull request.
3. After merge and catalog publication, check `winget show --id i-snyder.unbramble --exact --source winget` for the new version, URL, and SHA-256.

For changes to packaging or installation behavior, use a disposable Windows VM to exercise installation, upgrade from the previous catalog version, and uninstall. The ordinary manifest check leaves the installed application and WinGet settings alone.
