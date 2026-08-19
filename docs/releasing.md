# Releasing UnBramble

GitHub Releases holds the canonical Windows package. WinGet package `i-snyder.unbramble` is the normal installation path.

## Release

1. Update the version in `src/UnBramble.Cli/UnBramble.Cli.csproj`.
2. From a clean `main`, run `./scripts/verify-all.ps1`.
3. Run `./scripts/package-release.ps1 -Version <version>`.
4. Tag the commit as `v<version>` and publish a GitHub release containing `unbramble-win-x64.zip` and `unbramble-win-x64.zip.sha256`.
5. Update `i-snyder.unbramble` in [`microsoft/winget-pkgs`](https://github.com/microsoft/winget-pkgs) to the version-specific GitHub release URL and checksum.

`package-release.ps1` is the single packaging entry point. It packages the complete `publish/` directory and produces the ZIP and checksum under `artifacts/`. The ZIP includes `unbramble.exe`, `e_sqlite3.dll`, `LICENSE`, and every required third-party notice. Never distribute the executable alone or replace an asset under an existing release tag.

## Check the release

```powershell
winget install --exact --id i-snyder.unbramble
unbramble --version
winget upgrade --exact --id i-snyder.unbramble
winget uninstall --exact --id i-snyder.unbramble
```

Confirm a fresh install works without .NET, first-run setup works in a representative Unity project, upgrades do not strand the background watcher or Defender exclusions, and uninstall leaves project `.unbramble/` state untouched.
