# Building UnBramble

Most users should [install a release](installing.md). Build from source to inspect, modify, or contribute to UnBramble.

## Requirements

- Windows x64
- PowerShell 7
- .NET 8 SDK
- Visual Studio C++ build tools for NativeAOT; without them, the script falls back to a self-contained single-file build

## Build and test

```powershell
git clone https://github.com/i-snyder/unbramble.git
cd unbramble
./scripts/verify-all.ps1 -SkipPublish
```

The wrapper selects a complete x64 .NET installation, builds with warnings as errors, and runs the test suite.

For a release-style publish and smoke test, run:

```powershell
./scripts/verify-all.ps1
```

`publish/` will contain the files used to assemble the release package. For a quick development build, run `dotnet build`.

Read [architecture.md](architecture.md) before changing core behavior. Maintainers should follow [releasing.md](releasing.md).
