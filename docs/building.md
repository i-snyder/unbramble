# Building UnBramble

Most users should [install a release](installing.md). Build from source to inspect, modify, or contribute to UnBramble.

## Requirements

- Windows x64
- PowerShell 7
- .NET 10 SDK
- Visual Studio C++ build tools for NativeAOT; without them, the script falls back to a self-contained single-file build

## Build and test

```powershell
git clone https://github.com/i-snyder/unbramble.git
cd unbramble
./scripts/verify-all.ps1 -SkipPublish
```

The wrapper selects a complete x64 .NET 10 installation, builds with warnings as errors, and runs the test suite.

For a release-style publish and smoke test, run:

```powershell
./scripts/verify-all.ps1
```

`publish/` will contain the files used to assemble the release package. For a quick development build, run `dotnet build`.

For a local NativeAOT build that can be sampled with Windows Performance Recorder or inspected in a native debugger, run:

```powershell
./scripts/verify-all.ps1 -LocalDiagnostics
```

This keeps the matching PDB in `publish/` and embeds its local path in the executable. Don't distribute that build; rerun the wrapper without `-LocalDiagnostics` before packaging a release.

Read [architecture.md](architecture.md) before changing core behavior. Maintainers should follow [releasing.md](releasing.md).
