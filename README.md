# ShotIt

A fast screenshot and annotation tool for Windows 10 and 11.
Press Print Screen, drag over what you want, mark it up right there, and copy, save, pin or share it.

No account, no telemetry, no advertising. Nothing leaves the computer unless you upload it.

## What it does

**Capture**

- Region, window or control (hover to snap), full screen, active window, freeform shape, fixed size, delayed, repeat last region
- A magnifier with pixel colour while selecting, and keyboard nudging to the pixel
- Works across several monitors, including ones left of or above the primary
- Falls back to GPU capture for full-screen games and video

**Annotate, on the overlay or in the full editor**

- Arrow, line, rectangle, ellipse, pen
- Highlighter (a real multiply blend, like a marker), area highlight, spotlight
- Text, callout, numbered steps, stickers
- Blur, pixelate, solid redaction
- Magnifier inset, ruler with pixel measurements
- Crop, rotate, flip, resize, brightness and contrast
- A presentation frame: padding, gradient background, rounded corners, shadow, watermark
- Unlimited undo; every annotation stays movable and editable

**Do something with it**

- Copy, save (PNG, JPEG, BMP, PDF), save as, print, Windows share sheet
- Pin to the screen: always on top, zoomable, click-through, optionally back after a restart
- Read the text in the capture (Windows text recognition, fully offline)
- Read QR codes and barcodes
- Upload to Imgur, S3-compatible storage, SFTP, FTP, your own web address or a folder, and get the link
- Search the web for the image

**Keep track**

- A searchable history, including the recognised text
- Re-open any capture with its annotations still editable
- Automatic clean-up by age and size; "Keep" protects a capture

## Install

Download from the releases page:

| File | Choose it when |
|---|---|
| `ShotIt-Setup-x64.exe` | You want the normal installer. No administrator rights needed. Updates itself. |
| `ShotIt-Portable-x64.zip` | You want to run it from a folder or USB stick without installing |
| `ShotIt-x64.msi` | You deploy software to many computers |

ARM64 versions of each are provided too. Windows 10 version 2004 or later is required.

## Use

| Press | To |
|---|---|
| Print Screen | Capture a region |
| Shift + Print Screen | Capture a window or control |
| Ctrl + Print Screen | Capture the full screen |
| Alt + Print Screen | Capture the active window |
| Ctrl + Shift + Print Screen | Repeat the last region |
| Ctrl + Shift + P | Show or hide pinned images |

Opening ShotIt starts a region capture. Drag over the area you want and release to copy it to the clipboard; the annotation toolbar stays open for drawing and text.
While selecting: Ctrl+C copies, Ctrl+S saves, Enter does your default action, Esc cancels.
Every hotkey can be changed in Settings. Right-click the tray icon for everything else.

Command line:

```
ShotIt.exe --capture region|window|fullscreen|active|freeform|delayed
ShotIt.exe --edit "picture.png"
ShotIt.exe --pin "picture.png"
ShotIt.exe --history | --settings | --quit
```

## Build from source

You need Windows and the .NET 10 SDK.

```
dotnet build
dotnet test
dotnet run --project src/ShotIt.App
```

To build installers see [docs/releasing.md](docs/releasing.md).

## Repository layout

| Folder | Contents |
|---|---|
| `src/ShotIt.Core` | Platform-independent logic: geometry, annotations, editor state machine, settings, history, file names |
| `src/ShotIt.Native` | Win32 interop: monitors, windows, GDI, hotkeys, tray icon |
| `src/ShotIt.Capture` | Screen capture (GDI, Desktop Duplication, Windows Graphics Capture) and window snapping |
| `src/ShotIt.Rendering` | Drawing annotations, image codecs, export, `.shotit` project files |
| `src/ShotIt.Upload` | Upload destinations |
| `src/ShotIt.Ocr` | Text recognition and code scanning |
| `src/ShotIt.Shell` | Start-up, context menu, Send to, secrets, administrator policies |
| `src/ShotIt.App` | The application: tray, overlay, editor, pins, history, settings |
| `tests/` | Unit and integration tests |
| `build/` | Release script, MSI project, Group Policy templates, winget templates |
| `docs/` | [Pitfall register](docs/pitfalls.md), [test matrix](docs/test-matrix.md), [release guide](docs/releasing.md) |

## Current state

Version 0.1. The application is feature-complete against the version 1.0 scope in [PLAN.md](PLAN.md)
except for the items listed as open or partial in the [pitfall register](docs/pitfalls.md).
Before a public release it still needs a code-signing certificate, an update address, and the manual
test matrix (mixed-DPI, HDR, touch, ARM64 hardware). Scrolling capture and screen recording are planned for 1.1.

Differences from the original plan, and why:

- **.NET 10** instead of .NET 8, as the plan allowed once it was stable.
- **GDI is the default capture method**, with the GPU methods as fallbacks. GDI works in every session
  type, draws no capture border and returns correct colours on HDR displays.
- **History search uses SQL `LIKE`** rather than a full-text index. It is simple and fast at the sizes a
  personal history reaches.
- **WebP export is not included.** Windows has no built-in WebP encoder; adding one means shipping a native library.
- **Notifications use the tray icon's balloon API**, which Windows shows as toasts without any registration.
- **The installer is larger than planned** (about 80 MB) because it carries the .NET runtime and the Windows SDK projection.
- **The Setup.exe installs to `%LOCALAPPDATA%\ShotIt.App`** so that uninstalling can never remove user data in `%LOCALAPPDATA%\ShotIt`.
