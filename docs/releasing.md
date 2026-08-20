# Releasing UnBramble

GitHub Releases holds the canonical Windows package.

## Release

1. Update the version in `src/UnBramble.Cli/UnBramble.Cli.csproj`.
2. From a clean `main`, run `./scripts/verify-all.ps1`.
3. Run `./scripts/package-release.ps1 -Version <version>`.
4. Tag the commit as `v<version>` and publish a GitHub release containing `unbramble-win-x64.zip` and `unbramble-win-x64.zip.sha256`.

`package-release.ps1` is the single packaging entry point. It produces the ZIP and checksum under `artifacts/` and rejects anything other than `unbramble.exe`, `e_sqlite3.dll`, and the consolidated `LICENSES.md`. Never distribute the executable alone or replace an asset under an existing public release tag.

## Initial public release

Publish the GitHub Release while the repository is private. Make the repository public, enable the security settings listed in `AGENTS.md`, then confirm the README's release links work without authentication.

## Check the release

Download the release ZIP into a clean directory and confirm its checksum, `unbramble --version`, and first-run setup in a representative Unity project. Confirm an upgrade doesn't strand the background watcher or Defender exclusions, and uninstall leaves project `.unbramble/` state untouched.
