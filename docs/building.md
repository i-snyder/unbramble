# Building UnBramble

Most users should [install a release](installing.md). Build from source when you want to inspect, modify, or contribute to UnBramble.

## Requirements

- Windows x64
- PowerShell 7
- .NET 8 SDK
- Visual Studio C++ build tools for the preferred NativeAOT publish; the script falls back to a self-contained single-file build when they're unavailable

## Clone and verify

```powershell
git clone https://github.com/i-snyder/unbramble.git
cd unbramble
./scripts/verify-all.ps1 -SkipPublish
```

This builds the solution with warnings treated as errors and runs the full test suite. The wrapper selects a complete x64 .NET installation even if an inherited `DOTNET_ROOT` points somewhere incomplete.

## Build a local distribution

For the same publish and smoke-test sequence used for releases:

```powershell
./scripts/verify-all.ps1
```

The result is written to `publish/`. NativeAOT is attempted first; if the native toolchain is unavailable, the script produces a self-contained single-file managed executable instead. The publish directory also contains the bundled native SQLite library, the MIT license, and all required third-party notices. Keep those files together.

For a quick development build without publish or smoke tests:

```powershell
dotnet build
```

Implementation details and design invariants live in [architecture.md](architecture.md).

Maintainers should follow [releasing.md](releasing.md) to turn a verified publish into GitHub and WinGet releases.
