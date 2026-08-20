# Installing UnBramble

UnBramble supports Windows x64 and doesn't require a separate .NET installation.

## Install

Download `unbramble-win-x64.zip` and `unbramble-win-x64.zip.sha256` from the [latest release](https://github.com/i-snyder/unbramble/releases/latest). In the download directory, verify the ZIP:

```powershell
$expected = ((Get-Content ./unbramble-win-x64.zip.sha256 -Raw) -split '\s+')[0]
$actual = (Get-FileHash ./unbramble-win-x64.zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw 'Checksum mismatch' }
```

Extract every file to a folder you'll keep, such as `C:\Users\your-name\Apps\UnBramble`. Search the Start menu for **Edit environment variables for your account**, edit the user variable named `Path`, select **New**, and add that folder.

Open a new terminal at the root of a Unity project and run `unbramble`.

## Update or uninstall

To update, run `unbramble stop`, then replace every file in the installation folder with the new release.

To uninstall, run `unbramble stop`, remove the folder from your user `Path`, and delete it. Project `.unbramble/` indexes remain. If you accepted Defender exclusions, run `unbramble defender remove` from each affected project first.

To build from source, see [building.md](building.md).
