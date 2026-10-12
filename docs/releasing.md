# Building and releasing ShotIt

## What you need

- Windows 10 or 11, 64-bit
- The .NET 10 SDK (the exact version is pinned in `global.json`)
- Nothing else. The packaging tools (Velopack `vpk`, WiX 5) are restored from NuGet.

## Day-to-day

```
dotnet build
dotnet test
dotnet run --project src/ShotIt.App
```

## Making a release

```
powershell -ExecutionPolicy Bypass -File build/package.ps1 -Version 1.0.0
```

The script runs the tests, publishes self-contained builds for x64 and ARM64, runs the published
program's self-tests, and writes everything to `artifacts/release`:

| File | For | Notes |
|---|---|---|
| `ShotIt-Setup-x64.exe`, `ShotIt-Setup-arm64.exe` | Everyone | Per-user installer, no administrator rights. Installs to `%LOCALAPPDATA%\ShotIt.App`. Updates itself. |
| `ShotIt-Portable-x64.zip`, `-arm64.zip` | USB sticks, locked-down PCs | Unzip and run. Keeps settings and history in the `data` folder beside the program. Does not update itself. |
| `ShotIt-x64.msi`, `ShotIt-arm64.msi` | IT departments | Per-machine, silent install with `msiexec /i … /qn`. Does not update itself. |
| `ShotIt-Policy-Templates.zip` | IT departments | ADMX and ADML files for Group Policy |
| `feed/` | Your update server | The files the installed app downloads when it updates |
| `SHA256SUMS.txt` | Everyone | Hashes of every file above |
| `winget/<version>/` | winget | Manifest files, written only when `-PackageUrl` is given |

Useful switches:

| Switch | Effect |
|---|---|
| `-Runtimes win-x64` | Build one architecture only |
| `-UpdateFeedUrl https://…` | Where installed copies look for updates. A GitHub repository address or a folder that serves `feed/`. Without it, the build never updates itself. |
| `-SignParams '…'` | Arguments for `signtool sign`. Without it nothing is signed. |
| `-PackageUrl https://…` | Public folder the installers will be downloaded from; enables the winget manifest |
| `-SkipMsi`, `-SkipTests` | Faster local runs |

## Things only the publisher can do

These need accounts or purchases, so the repository cannot do them for you.

1. **Code signing.** Buy an OV or EV code-signing certificate (or use Azure Trusted Signing). Until the
   files are signed, Windows SmartScreen shows a warning on download and some antivirus tools are suspicious.
   Pass the signing arguments with `-SignParams`.
2. **An update address.** Create a GitHub repository (or any https file host), upload the contents of
   `feed/` with each release, and build with `-UpdateFeedUrl`.
3. **winget.** Submit the generated manifest to `microsoft/winget-pkgs` with `wingetcreate submit`
   or a pull request. The first submission is reviewed by Microsoft.
4. **A real ARM64 test.** The ARM64 packages are built but have never run on ARM64 hardware.

## MSI properties

| Property | Default | Effect |
|---|---|---|
| `START_WITH_WINDOWS` | `1` | Starts ShotIt for every user at sign-in |
| `DISABLE_UPDATES` | `1` | The MSI copy is updated by deploying a newer MSI |
| `DISABLE_UPLOAD` | `0` | `1` blocks every upload |
| `DISABLE_HISTORY` | `0` | `1` stops ShotIt keeping a history |

Example: `msiexec /i ShotIt-x64.msi /qn DISABLE_UPLOAD=1 START_WITH_WINDOWS=0`

The same restrictions can be set later through Group Policy (`Software\Policies\ShotIt`).
A machine-wide policy always wins over a per-user one.

## Where ShotIt keeps things

| What | Where |
|---|---|
| Settings, history database, logs, crash reports, internal copies of captures, editable projects | `%LOCALAPPDATA%\ShotIt` (portable: `data` beside the program) |
| Screenshots the user saves | `Pictures\ShotIt` unless changed in Settings |
| Program files (Setup.exe) | `%LOCALAPPDATA%\ShotIt.App` |
| Program files (MSI) | `%ProgramFiles%\ShotIt` |
| Registry, only when the user turns the feature on | `HKCU\…\Run`, `HKCU\Software\Classes\SystemFileAssociations\image\shell\ShotIt.*`, and a shortcut in the Send-to folder |

Uninstalling removes the program and those registrations. It deliberately leaves the user's
screenshots, history and settings in place.
