# Releasing UnBramble

GitHub Releases holds the canonical Windows package. WinGet is the default installation path and receives each version through a separate manifest pull request after the GitHub release is verified.

## Verify

Run the manual **Verify** workflow whenever you want a read-only GitHub check of a branch. It builds, tests, and checks dependencies, but never publishes anything. A separate Verify run isn't required before releasing because Release repeats those checks.

## Release

1. Update the version in `src/UnBramble.Cli/UnBramble.Cli.csproj`.
2. Add concise user-facing release notes at `docs/release-notes/v<version>.md`. Explain why the release matters and any upgrade action; don't duplicate the version heading or generated changelog.
3. Commit the version and release notes, then push them to `main`.
4. On GitHub, open **Actions → Release → Run workflow** and select `main`. To verify the published ZIP and prepare the WinGet submission on a fresh Windows runner, enter a published WinGet version in `winget_upgrade_from`. Leaving it blank skips the installation job and manifest artifact.
5. The workflow reads the project version, requires matching nonempty curated notes, runs the complete verification sequence, checks dependencies, packages the release, creates the `v<version>` tag at that exact commit, and publishes the ZIP and checksum with the curated notes followed by GitHub's generated changelog.
6. Verify the downloaded ZIP checksum, `unbramble --version`, first-run project setup, project uninstall, and machine uninstall. The optional test runs these checks against a clean copy of the Unity fixture. It also validates the candidate manifest, tests a clean install of both the candidate and the previous catalog package, and upgrades the previous version's merged manifests to the candidate. The upgrade uses two local manifests because WinGet includes the source in a portable package's identity. Each installation runs a SQLite-backed asset lookup and is removed when testing ends.
7. Download and review the `winget-manifest-<version>` artifact from the Release run within three days. It contains the exact three YAML files used for the candidate installation and upgrade. If the installation job fails after publication, use **Re-run failed jobs** to repeat it against the same published assets. If you skipped the runner tests, generate the update with WinGetCreate from the published ZIP, set the release date and version-specific license and release-notes URLs, and run `winget validate --manifest <manifest-directory>` before testing installation and upgrade in a disposable Windows environment.
8. Open the manifest pull request against `microsoft/winget-pkgs`. After it merges, confirm the publish pipeline succeeds and `winget show --id i-snyder.unbramble --exact` returns the new version, installer URL, and checksum.

The workflow fails unless it runs from `main` with a new stable project version. `package-release.ps1` rejects missing or extra package files. Never distribute the executable alone or replace an asset under an existing public release tag.
