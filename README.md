# Encore

Encore records your games on Windows. Rewind keeps the last few minutes of gameplay ready, so a hotkey saves what just happened as a Clip. It also makes full Recordings and Monitor Recordings, and keeps them in a Library with a player and a clip editor.

## Install

1. Download `EncoreApp-win-Setup.exe` from the [latest release](https://github.com/BI00dsh0t/Encore/releases/latest).
2. Run it. It installs for your Windows account only, with no admin prompt, and opens Encore when it's done.

Encore checks for updates in the background and installs them the next time it starts. You can turn that off, or choose beta versions, in Settings › General › Updates.

**Requirements:** Windows 11 24H2 or later, 64-bit. A graphics card with a hardware video encoder (NVIDIA, AMD or Intel) is recommended; without one, Encore encodes on the CPU.

**Where things go:**

| What | Where |
|---|---|
| The app | `%LOCALAPPDATA%\EncoreApp` |
| Settings and the Library | `%APPDATA%\Encore` |
| Rewind's buffer | `%LOCALAPPDATA%\Encore` |
| Clips and Recordings | `Videos\Encore`, unless you choose another folder |

**Uninstall:** Settings › Apps › Installed apps › Encore › Uninstall. Your Clips and settings stay; delete the folders above to remove them too.

**Portable zip:** each release also has a zip. Unzip it anywhere and run `Encore.App.exe`. It keeps its own settings, separate from the installed copy, and doesn't update itself.

**Anti-cheat:** Encore captures through Windows' own capture APIs (Windows.Graphics.Capture and Desktop Duplication) and registers hotkeys with `RegisterHotKey`. It never injects into or hooks a game process.

## Build

You need Windows 11 24H2, the .NET SDK in `global.json`, the Visual Studio C++ build tools (for the NativeAOT engine) and PowerShell 7.

```powershell
pwsh tools/fetch-ffmpeg.ps1
dotnet build Encore.slnx -c Release
dotnet test --solution Encore.slnx -c Release --no-build
pwsh tools/publish.ps1
```

`tools/publish.ps1` composes the app in `publish/Encore/`. `docs/code-map.md` explains how the code fits together.

## Releases

A pushed tag `vX.Y.Z` runs `.github/workflows/release.yml`. It builds and tests in GitHub Actions, packs the installer and the zip, and creates a draft release with checksums, a software bill of materials and a build provenance attestation. A tag with a suffix, such as `v1.2.0-beta.1`, goes to the beta channel.

## Code signing

Releases aren't code signed yet, so Windows SmartScreen may say "Windows protected your PC" the first time you run the installer. Choose **More info**, then **Run anyway**.

To check a download is genuine, compare it with the release's `SHA256SUMS.txt`, or verify its build provenance with the [GitHub CLI](https://cli.github.com/):

```powershell
gh attestation verify EncoreApp-win-Setup.exe --repo BI00dsh0t/Encore
```

Release binaries are built only by the release workflow in this repository, from a tagged commit on `main` (or `develop`, for a beta).

**Updates are signed.** Each release's update feed is signed with a key that only the release workflow can use, after the maintainer approves it. Encore carries the matching public key and ignores any feed whose signature doesn't verify. It then checks every update package's size and SHA-256 against that signed feed before installing it, and deletes a package that doesn't match.

Encore sends nothing about you or your PC anywhere. It contacts GitHub only to check for updates, and you can turn that off.

## License

Encore is licensed under the [GNU General Public License v3.0](LICENSE). It ships FFmpeg, built with GPL components; [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) lists every third-party component, its license and where its source is.
