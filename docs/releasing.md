# Releasing UnBramble

GitHub Releases holds the canonical Windows package.

## Release

1. Update the version in `src/UnBramble.Cli/UnBramble.Cli.csproj`.
2. From a clean `main`, run `./scripts/verify-all.ps1`.
3. Run `./scripts/package-release.ps1 -Version <version>`.
4. Inspect the generated package and checksum under `artifacts/`.
5. Tag the commit as `v<version>` and publish the generated ZIP and checksum.
6. Download the release into a clean directory and verify its checksum, `unbramble --version`, first-run setup in a representative Unity project, project uninstall, and machine uninstall.

`package-release.ps1` rejects missing or extra package files. Never distribute the executable alone or replace an asset under an existing public release tag.
