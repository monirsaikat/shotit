# Testing ShotIt

## Automated checks

Run everything from the repository root:

```
dotnet test
```

| Suite | What it covers |
|---|---|
| `ShotIt.Core.Tests` | Geometry, selection, annotations, the editor state machine, settings storage and migration, file names, history database |
| `ShotIt.Rendering.Tests` | Annotation rendering, export formats, the `.shotit` project file |
| `ShotIt.Capture.Tests` | Live desktop: monitors, window list, GDI capture, cursor, GPU backends and their fallback |
| `ShotIt.Upload.Tests` | Every upload destination against a loopback server; Amazon's published signature examples |
| `ShotIt.Ocr.Tests` | Text recognition on rendered text; QR and barcode scanning, including false-positive checks |
| `ShotIt.Shell.Tests` | Start-up, context menu, Send to, uninstall cleanup, Print Screen binding, secrets, policies (all against a scratch registry key) |

The capture tests read the real desktop and skip themselves on machines without one.
The text recognition tests skip themselves when Windows has no recognition language installed.

## Self-tests

The application can drive itself without a person. Each scenario writes `result.json` and pictures of
its windows into the folder you name, and exits with code 0 when everything passed.

```
ShotIt.exe --selftest overlay  C:\temp\st-overlay
ShotIt.exe --selftest editor   C:\temp\st-editor
ShotIt.exe --selftest windows  C:\temp\st-windows
ShotIt.exe --selftest policy   C:\temp\st-policy
ShotIt.exe --selftest capture  C:\temp\st-capture
```

| Scenario | What it proves |
|---|---|
| `overlay` | The overlay shows the screen pixel for pixel, selection, annotation, undo, keyboard nudging, save, history, last region, Esc, display change and sleep all behave |
| `editor` | The editor loads annotations, crops, frames, exports and saves |
| `windows` | Settings (every page), history (search, re-open with annotations, keep, clear, delete), text recognition, QR, upload, printing layout, shell integration, onboarding, reset, pin restore |
| `policy` | With the administrator policies set, nothing is uploaded, recorded in history or updated |
| `capture` | Timing of the real capture path, and that both GPU backends agree with GDI |

A self-test uses its own data folder, shows a synthetic picture instead of your screen (except
`capture`, which never stores pixels) and does its registry work under a scratch key that it removes.
It does copy test content to the clipboard.

## Manual release matrix

These cannot be automated on one machine. Tick each before a public release.

**Displays**

- [ ] Single monitor at 100 %, 125 %, 150 %, 200 %
- [ ] Two monitors with different scaling (for example 4K at 200 % beside 1080p at 100 %)
- [ ] Three monitors with one to the left of or above the primary (negative coordinates)
- [ ] A rotated (portrait) monitor
- [ ] HDR enabled
- [ ] Remote Desktop session
- [ ] Virtual machine without GPU acceleration

**Input**

- [ ] Mouse, touch, pen
- [ ] Keyboard only: capture, adjust, annotate, copy, cancel
- [ ] Narrator reads the Settings and History windows sensibly
- [ ] A high-contrast theme

**Applications to capture**

- [ ] Chrome or Edge, File Explorer, Windows Settings, Windows Terminal
- [ ] Task Manager running elevated (snapping should fall back to the window outline)
- [ ] Lock screen and a UAC prompt: the hotkey must do nothing harmful and must not hang
- [ ] A game in exclusive full screen
- [ ] Protected video (expected: black in the video area)

**Power and hardware changes**

- [ ] Sleep and resume, with and without an open capture
- [ ] Close and open a laptop lid
- [ ] Plug in and unplug a monitor during a capture

**Installation**

- [ ] `ShotIt-Setup-x64.exe`: install, start, update from an older version, uninstall. Afterwards no start-up entry, context-menu entry or Send-to shortcut remains.
- [ ] `ShotIt-Portable-x64.zip`: runs from a USB stick and keeps its data in the `data` folder beside the program
- [ ] `ShotIt-x64.msi`: `msiexec /i ShotIt-x64.msi /qn`, then again with `DISABLE_UPLOAD=1 DISABLE_HISTORY=1`, then uninstall
- [ ] The ADMX template loads in the Group Policy editor and each policy takes effect
- [ ] Everything above once on an ARM64 device
- [ ] Windows 10 22H2 and the current Windows 11
