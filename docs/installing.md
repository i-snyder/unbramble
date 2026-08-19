# Installing UnBramble

UnBramble supports Windows x64 and doesn't require a separate .NET installation.

## Install

```powershell
winget install --exact --id i-snyder.unbramble
```

Open a new terminal at the root of a Unity project and run `unbramble`. The first run walks through project setup and builds the index.

## Upgrade

```powershell
winget upgrade --exact --id i-snyder.unbramble
```

## Uninstall

```powershell
unbramble stop
winget uninstall --exact --id i-snyder.unbramble
```

Uninstalling the program doesn't delete project `.unbramble/` indexes. If you accepted optional Windows Defender exclusions, run `unbramble defender remove` from each affected project before uninstalling.

## Manual install

Download `unbramble-win-x64.zip` and its SHA-256 checksum from the [latest GitHub release](https://github.com/i-snyder/unbramble/releases/latest). Verify the checksum, extract the complete archive to a permanent directory, and add that directory to your user `PATH`. Keep all extracted files together.

To build from source instead, see [building.md](building.md).
