# Releasing UnBramble

GitHub Releases holds the canonical Windows package. WinGet package `i-snyder.unbramble` becomes the normal installation path after its listing is accepted.

## Release

1. Update the version in `src/UnBramble.Cli/UnBramble.Cli.csproj`.
2. From a clean `main`, run `./scripts/verify-all.ps1`.
3. Run `./scripts/package-release.ps1 -Version <version>`.
4. Tag the commit as `v<version>` and publish a GitHub release containing `unbramble-win-x64.zip` and `unbramble-win-x64.zip.sha256`.
5. Update `i-snyder.unbramble` in [`microsoft/winget-pkgs`](https://github.com/microsoft/winget-pkgs) to the version-specific GitHub release URL and checksum.

`package-release.ps1` is the single packaging entry point. It packages the complete `publish/` directory and produces the ZIP and checksum under `artifacts/`. The ZIP includes `unbramble.exe`, `e_sqlite3.dll`, `LICENSE`, and every required third-party notice. Never distribute the executable alone or replace an asset under an existing release tag.

## Initial public release

WinGet can't validate an asset in a private repository. For the first release, publish the GitHub Release while the repository is private and keep GitHub ZIP installation as the README's initial path. Make the repository public, enable the security settings listed in `AGENTS.md`, then submit the WinGet manifest. Once the listing is accepted and the commands below pass, make WinGet the README's primary installation path.

## Check the release

```powershell
winget install --exact --id i-snyder.unbramble
unbramble --version
winget upgrade --exact --id i-snyder.unbramble
winget uninstall --exact --id i-snyder.unbramble
```

Before the WinGet listing exists, perform the same fresh-install and first-run checks from the GitHub ZIP. Once WinGet is available, confirm a fresh install works without .NET, first-run setup works in a representative Unity project, upgrades don't strand the background watcher or Defender exclusions, and uninstall leaves project `.unbramble/` state untouched.
