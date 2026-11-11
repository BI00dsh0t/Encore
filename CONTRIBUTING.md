# Contributing to Encore

This page is for people who want to build Encore, change it or check how releases are made. If you just want to use Encore, the [README](README.md) has everything you need.

## Build

You need Windows 11 24H2, the .NET SDK in `global.json`, the Visual Studio C++ build tools (for the NativeAOT engine) and PowerShell 7.

```powershell
pwsh tools/fetch-ffmpeg.ps1
dotnet build Encore.slnx -c Release
dotnet test --solution Encore.slnx -c Release --no-build
pwsh tools/publish.ps1
```

`tools/publish.ps1` composes the app in `publish/Encore/`. [`docs/code-map.md`](docs/code-map.md) explains how the code fits together, and [`docs/code-notes.md`](docs/code-notes.md) records the rules behind each class.

## Capture and anti-cheat

Encore captures through Windows' own capture APIs (Windows.Graphics.Capture and Desktop Duplication) and registers hotkeys with `RegisterHotKey`. It never injects into or hooks a game process.

## Releases

A pushed tag `vX.Y.Z` runs `.github/workflows/release.yml`. It builds and tests in GitHub Actions, packs the installer and the zip with Velopack, and creates a draft release with checksums, a software bill of materials and a build provenance attestation. A tag with a suffix, such as `v1.2.0-beta.1`, goes to the beta channel.

Release binaries are built only by the release workflow in this repository, from a tagged commit on `main` (or `develop`, for a beta).

## Code signing

Releases aren't code signed yet, so Windows SmartScreen may say "Windows protected your PC" the first time someone runs the installer.

To check a download is genuine, compare it with the release's `SHA256SUMS.txt`, or verify its build provenance with the [GitHub CLI](https://cli.github.com/):

```powershell
gh attestation verify EncoreApp-win-Setup.exe --repo BI00dsh0t/Encore
```

**Updates are signed.** Each release's update feed is signed with a key that only the release workflow can use, after the maintainer approves it. Encore carries the matching public key and ignores any feed whose signature doesn't verify. It then checks every update package's size and SHA-256 against that signed feed before installing it, and deletes a package that doesn't match.

## Where the installed app keeps its files

| What | Where |
|---|---|
| The app | `%LOCALAPPDATA%\EncoreApp` |
| Settings and the Library index | `%APPDATA%\Encore` |
| Rewind's buffer, when it's kept on disk | `%LOCALAPPDATA%\Encore` |
| Clips and Recordings | `Videos\Encore`, unless the user chooses another folder |

Test instances use their own folders, named after the instance, so they never touch the installed copy's settings or Library.
