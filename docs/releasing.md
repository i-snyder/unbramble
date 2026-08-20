# Releasing UnBramble

GitHub Releases holds the canonical Windows package.

1. Update the version in `src/UnBramble.Cli/UnBramble.Cli.csproj`.
2. From a clean `main`, run `./scripts/verify-all.ps1`.
3. Run `./scripts/package-release.ps1 -Version <version>`.
4. Confirm the ZIP contains only `unbramble.exe`, `e_sqlite3.dll`, and `LICENSES.md`.
5. Tag the commit as `v<version>` and publish `unbramble-win-x64.zip` with `unbramble-win-x64.zip.sha256`.
6. Download the release into a clean directory and verify the checksum, `unbramble --version`, and first-run setup in a representative Unity project.

`package-release.ps1` rejects missing or extra package files. Never distribute the executable alone or replace an asset under an existing public release tag.

For the initial public release, publish while the repository is private. After changing visibility, enable the security settings listed in `AGENTS.md` and confirm the README's release links work without authentication.
