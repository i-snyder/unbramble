# Releasing UnBramble

GitHub Releases holds the canonical Windows package.

## Release

1. Update the version in `src/UnBramble.Cli/UnBramble.Cli.csproj`.
2. From a clean `main`, run `./scripts/verify-all.ps1`.
3. Run `./scripts/package-release.ps1 -Version <version>`.
4. Inspect the generated package and checksum under `artifacts/`.
5. Tag the commit as `v<version>` and publish the generated ZIP and checksum.
6. Download the release into a clean directory and verify its checksum, `unbramble --version`, and first-run setup in a representative Unity project.

`package-release.ps1` rejects missing or extra package files. Never distribute the executable alone or replace an asset under an existing public release tag.

## Initial public release

Before changing repository visibility:

- Confirm the repository contains only the sanitized public history and re-audit the final tracked snapshot.
- Run the Verify workflow on final `main` and confirm it passes, including the dependency vulnerability check.
- Publish the release from its tagged commit while the repository is private.

After changing visibility, enable GitHub private vulnerability reporting and secret scanning, then confirm the README's release links work without authentication.
