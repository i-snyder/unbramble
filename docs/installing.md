# Installing UnBramble

UnBramble supports Windows x64 and doesn't require a separate .NET installation.

## Install from GitHub

Download `unbramble-win-x64.zip` and `unbramble-win-x64.zip.sha256` from the [latest GitHub release](https://github.com/i-snyder/unbramble/releases/latest). In the download directory, verify the archive before extracting it:

```powershell
$expected = ((Get-Content ./unbramble-win-x64.zip.sha256 -Raw) -split '\s+')[0]
$actual = (Get-FileHash ./unbramble-win-x64.zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw 'Checksum mismatch' }
```

Extract the complete ZIP to a permanent directory and add that directory to your user `PATH`. Keep all extracted files together.

Open a new terminal at the root of a Unity project and run `unbramble`. The first run walks through project setup and builds the index.

## Upgrade a manual install

Run `unbramble stop`, then replace every file in the installation directory with the files from the new release ZIP.

## Uninstall a manual install

Run `unbramble stop`, remove the installation directory from your user `PATH`, and delete the directory.

Uninstalling the program doesn't delete project `.unbramble/` indexes. If you accepted optional Windows Defender exclusions, run `unbramble defender remove` from each affected project before uninstalling.

## WinGet

After `i-snyder.unbramble` is accepted into WinGet, install, upgrade, or uninstall it with:

```powershell
winget install --exact --id i-snyder.unbramble
winget upgrade --exact --id i-snyder.unbramble
winget uninstall --exact --id i-snyder.unbramble
```

To build from source instead, see [building.md](building.md).
