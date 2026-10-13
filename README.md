# ShotIt

A fast screenshot and annotation tool for Windows 10 and 11.
Press Print Screen, drag over what you want, mark it up right there, and copy, save, pin or share it.

No account, no telemetry, no advertising. Nothing leaves the computer unless you upload it.

## What it does

**Capture**

- Region, window or control (hover to snap), full screen, active window, freeform shape, fixed size, delayed, repeat last region
- Scrolling capture: select the part of a window that scrolls and ShotIt scrolls it to the end and joins everything into one tall picture
- A magnifier with pixel colour while selecting, and keyboard nudging to the pixel
- Works across several monitors, including ones left of or above the primary
- Falls back to GPU capture for full-screen games and video

**Record**

- Record any area of the screen as an MP4 video or an animated GIF
- The mouse pointer is included and clicks are marked with a ring; keyboard shortcuts can be shown as a caption
- Ordinary typing is never shown, so passwords stay out of a recording
- No sound is recorded

**Edit video**

- Trim the start and end of a recording or any other video
- Crop to part of the picture by dragging a frame
- Add an audio file as a soundtrack, and set how loud it and the video's own sound are
- Save the result as a new MP4 or as an animated GIF; the original file is never changed

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
- Read the text in the capture, fully offline: the languages Windows has installed, plus Bengali (বাংলা), which ShotIt reads itself
- Read QR codes and barcodes
- Upload to Imgur, S3-compatible storage, SFTP, FTP, your own web address or a folder, and get the link
- Search the web for the image

**Keep track**

- A searchable history, including the recognised text
- Re-open any capture with its annotations still editable
- Automatic clean-up by age and size; "Keep" protects a capture

## Install

Download from the [releases page](https://github.com/monirsaikat/shotit/releases):

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

Scrolling capture is in the tray menu and under "More actions" on the capture toolbar, and can be given a hotkey in Settings.
Select the area that scrolls (a click on a window or pane selects it). ShotIt then turns the mouse wheel for you until the end of
the page. Press Esc or move the mouse to stop early and keep what was captured; the Cancel button throws it away.

Recording is in the same places: "Record video (MP4)" and "Record GIF" in the tray menu, "More actions" on the capture toolbar,
and two hotkeys you can set in Settings. Select the area and recording starts at once. Stop with Esc, the tick on the small panel,
a click on the tray icon, or the same hotkey again. The file goes to your screenshots folder and onto the clipboard, ready to paste.

To edit a video, choose "Edit last recording" or "Edit video…" in the tray menu, or turn on "Open the video editor when a video
recording finishes" in Settings › Capture. In the editor, move to a moment and press "Start here" or "End here" to trim, tick
"Keep only part of the picture" and drag the frame to crop, and use "Add audio…" for a soundtrack. Space plays and pauses.

Command line:

```
ShotIt.exe --capture region|window|fullscreen|active|freeform|delayed|scrolling
ShotIt.exe --record video|gif
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

Version 0.2, an early release. Capture, scrolling capture, recording, annotation, pinning, text recognition, QR, upload, history,
settings and the installers all work and are covered by automated tests.

Not built yet from the version 1.0 scope in [PLAN.md](PLAN.md):

- Editor: blur and pixelate brushes, rotating objects, grouping, smart guides, text alignment, arrow head styles, custom sticker folder, image watermark
- Export: WebP, copy as data URI, copy as Markdown link
- Capture: monitor selection by number, "Open with", fixed-size presets
- History: filters by date and app, list view, tags, export as zip
- Everything marked open or partial in the [pitfall register](docs/pitfalls.md)

The installers are not code-signed yet, so Windows SmartScreen shows a warning on first run.
Mixed-DPI, HDR, touch and ARM64 hardware have not been tested.
Bengali text recognition is new: it uses the Tesseract engine and language data bundled with ShotIt (Apache 2.0 licence, about 20 MB),
reads printed and on-screen text but not handwriting, is not included in the ARM64 build, and needs the Microsoft Visual C++ runtime
(already on nearly every PC; without it Bengali is simply not offered).
With the language set to Automatic, checking a full-screen capture for Bengali adds about two seconds.
Recording is new: it records no sound and cannot be paused, recordings are not listed in History, and MP4 needs the video encoder
that ships with Windows (missing on "N" editions without the Media Feature Pack; GIF works everywhere).
The video editor is new too: it edits one video at a time, and cannot change speed, join clips, or draw or write on the video.
Scrolling capture is new: it is tested against a page of known content, but not yet across browsers and other applications.

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
