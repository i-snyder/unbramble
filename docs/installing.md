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

## Update

To update, run `unbramble stop`, then replace every file in the installation folder with the new release.

## Uninstall

Run this once from the root of each Unity project where you set up UnBramble:

```powershell
unbramble uninstall
```

From anywhere else, pass the project path: `unbramble uninstall <path-to-unity-project>`.

The command stops every live UnBramble process across all projects, removes Defender exclusions that UnBramble added for this project, restores or cleans its `AGENTS.md`, `CLAUDE.md`, and VCS-ignore changes, then deletes `.unbramble/`. Unrelated content and edits made after setup are preserved. If the Defender administrator prompt is dismissed or cleanup can't be confirmed, uninstall stops before changing project files so you can retry safely.

Before changing anything, the command lists exactly what it will remove and asks for confirmation. Use `-y` or `--yes` only when deliberately running it non-interactively.

After cleaning every project, remove the CLI itself from anywhere:

```powershell
unbramble uninstall --machine
```

Before asking for confirmation, the machine command warns that it doesn't discover or remove project integrations. It shows the exact installation directory and user `Path` change, stops all live UnBramble processes, removes any matching `Path` entries, then deletes the installation directory after the running process exits. It refuses to recursively delete a directory containing anything outside the known release files. UnBramble doesn't install agent hooks or modify Unity assets or source files, so nothing else needs to be unwound.

To build from source, see [building.md](building.md).
