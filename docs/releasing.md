# Releasing UnBramble

GitHub Releases holds the canonical Windows package.

## Verify

Run the manual **Verify** workflow whenever you want a read-only GitHub check of a branch. It builds, tests, and checks dependencies, but never publishes anything. A separate Verify run isn't required before releasing because Release repeats those checks.

## Release

1. Update the version in `src/UnBramble.Cli/UnBramble.Cli.csproj`.
2. Commit the version change and push it to `main`.
3. On GitHub, open **Actions → Release → Run workflow**, select `main`, and run it.
4. The workflow reads the project version, runs the complete verification sequence, checks dependencies, packages the release, creates the `v<version>` tag at that exact commit, and publishes the ZIP and checksum with generated release notes.
5. Download the release into a clean directory and verify its checksum, `unbramble --version`, first-run setup in a representative Unity project, project uninstall, and machine uninstall.

The workflow fails unless it runs from `main` with a new stable project version. `package-release.ps1` rejects missing or extra package files. Never distribute the executable alone or replace an asset under an existing public release tag.
