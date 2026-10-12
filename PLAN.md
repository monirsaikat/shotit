# ShotIt — Complete Product, Installation & Engineering Plan (Windows, v1)

> Goal: a screenshot and annotation tool that is faster, cleaner, more private and far more capable
> than Lightshot, while staying as small and instant as Lightshot. Windows 10 2004+ and Windows 11,
> x64 and ARM64. Installs on any machine with one double-click and no prerequisites.
>
> "Bug-free" is engineered, not hoped for: every known Windows pitfall in §11 maps to a mitigation and
> a test in §13. Nothing ships unless the quality gates in §16 pass.

Changes vs the first draft (audit results)
- Self-contained build is now the default so installation needs no .NET runtime download (§9).
- ARM64 added (Surface, Snapdragon X laptops) — one build matrix entry, no code impact.
- Editor spec expanded from 13 tools to a full tool catalogue incl. highlighter, spotlight, callouts, redaction, magnifier inset, beautify/background, watermark, stickers (§6).
- Capture gains freeform/lasso, active-window, monitor-select, capture-from-file/clipboard, QR decode (§4).
- Full installation chapter: installer, portable, MSI for enterprise, winget, silent flags, shell integration, file associations, first-run, uninstall, updates (§9).
- Toast notifications need an AUMID registration for unpackaged apps — added to §9 and the pitfall register.
- Pitfall register extended to 38 items.

---

## 1. Positioning: why ShotIt beats Lightshot

| Lightshot weakness | ShotIt answer |
|---|---|
| Uploads go to prnt.sc with guessable, public, indexed URLs | Private by default. Random 128-bit unlisted links, expiry, one-click delete, or no upload at all |
| Installer bundles third-party offers; ads in web viewer | Clean signed installer + portable exe. Zero ads, zero bundled software |
| Editor is minimal: no highlighter modes, no blur, no numbered steps, no undo history | Full non-destructive editor: highlighter, spotlight, callouts, blur/pixelate/redact, numbered steps, magnifier inset, beautify, unlimited undo |
| No scrolling capture, no pin-to-screen, no OCR, no history | Pin, OCR, history in v1.0; scrolling capture and recording in v1.1 |
| Weak multi-monitor + mixed-DPI behaviour | Per-monitor overlay windows, Per-Monitor-V2 DPI aware, pixel-exact everywhere |
| No window / UI-element snapping | Hover auto-detects windows and controls; click to grab exactly |
| Needs internet for anything beyond copy/save | Everything except upload works fully offline |
| Rare updates, no transparency | Public roadmap, signed delta auto-updates, changelog in-app |
| Telemetry + account nag | No telemetry by default. No account for anything local |

What we must NOT lose vs Lightshot: instant overlay (<50 ms), tiny idle footprint, one key to capture, inline toolbar on the selection, "it just works" simplicity, a 10-second install.

Learn from: ShareX (power), Snipaste (pin UX), Flameshot (inline editor), Greenshot (window snapping, Office export), Snagit (scrolling, callouts), CleanShot/Shottr (beautify backgrounds), Windows Snipping Tool (OCR baseline).

---

## 2. Target users & principles

- Developers, support/QA staff, designers, students, anyone who presses PrtScr 20× a day.
- Principles: (1) capture must never fail or hang, (2) zero config needed, (3) private by default, (4) every action reachable by keyboard, (5) never surprise the user with network traffic.

---

## 3. Feature scope summary

| Area | v1.0 | v1.1 | v2.0 |
|---|---|---|---|
| Capture | region, window, element, full screen, monitor, freeform, delayed, last-region, from file/clipboard | scrolling capture, GIF/MP4 recording | — |
| Editor | full tool catalogue (§6) | recording trim, animated GIF annotation | collaborative comments |
| Output | copy, save, pin, print, upload (5 providers), Windows Share, open-with, OCR, QR | custom actions, workflows | cloud library |
| Shell | tray, hotkeys, settings, history, themes, installer, portable, MSI, winget, auto-update | localisation (10 languages) | macOS/Linux shells |

Out of scope for v1: accounts, social, browser extension, mobile.

---

## 4. Capture features (v1.0)

Modes (each with its own configurable hotkey)
- **Region**: frozen screen, dim overlay, crosshair, magnifier loupe (8×, hex colour, coordinates), live W×H + X,Y readout, 8 resize handles, keyboard nudge (arrows = 1 px, Shift = 10 px), aspect-lock (Shift), from-centre (Ctrl), move while dragging (Space).
- **Window**: hover highlights the deepest top-level window; one click captures it using extended frame bounds; rounded Win11 corners preserved as transparency; optional window shadow.
- **UI element**: hold Alt to descend into controls (buttons, panes, list items) via UI Automation; the highlight shows the element name.
- **Full screen**: current monitor or all monitors stitched in virtual-screen layout.
- **Monitor select**: numbered overlay badges (1…n) — press the number.
- **Freeform (lasso)**: draw any shape; outside area becomes transparent in PNG.
- **Fixed size**: preset W×H (e.g. 1280×720, 1920×1080, social sizes) centred on cursor; drag to position.
- **Last region**: repeat the previous rectangle instantly (for before/after shots).
- **Delayed**: 1–10 s countdown shown in tray badge; captures open menus, tooltips, hover states.
- **Active window** (no overlay): hotkey captures the focused window directly to clipboard/file.
- **From file / clipboard**: open any PNG/JPG/WebP/BMP/GIF frame or clipboard image in the editor (drag-drop onto tray icon or editor, Explorer "Edit with ShotIt", Ctrl+V in editor).
- Cursor include/exclude, per mode.
- Works correctly on multi-monitor, mixed DPI, HDR, RDP, VMs, hot-plugged displays.

Quick toolbar (appears on the selection)
- Copy · Save · Save As · Upload · Pin · Edit (inline tools expand) · OCR → copy text · QR/barcode decode · Search image (Google Lens / Bing) · Share (Windows Share sheet) · Open with… · Print · Cancel
- Enter = default action (configurable). All items have shortcut keys shown in tooltips.

---

## 5. Pin to screen

- Topmost floating image windows; drag anywhere; wheel = zoom (10–800 %), Ctrl+wheel = opacity (10–100 %), double-click = close, right-click menu (copy, save, edit, mirror, rotate, grayscale, click-through, always-on-top toggle, close all).
- Pins survive theme change and monitor changes (clamped back on-screen).
- Hotkey to hide/show all pins; pins restore after restart (optional).
- Pin from clipboard / file too.

---

## 6. Editor & annotation catalogue (v1.0)

The editor opens inline on the overlay (Lightshot style) and can expand to a full window. All tools are non-destructive vector annotations until export, except Redact which is intentionally destructive.

Markup tools
| Tool | Details |
|---|---|
| Highlighter (marker) | Translucent multiply-blend stroke, freehand or straight (Shift), widths 8–48 px, preset yellow/green/pink/blue, custom colour |
| Area highlight | Translucent filled rectangle/ellipse, adjustable opacity; "underline" variant for text |
| Spotlight / focus | Dims everything outside one or more rects/ellipses (feather edge adjustable) — the "look here" effect |
| Arrow | Single, double-headed, curved (drag the midpoint); head styles; dashed/solid; widths 1–20 px; optional outline for contrast |
| Line / polyline | Straight, Shift = 45° snapping; polyline with corner nodes |
| Rectangle / rounded rect / ellipse | Stroke, fill (none/solid/translucent), corner radius, shadow |
| Freehand pen | Pressure-aware (pen input), smoothing slider |
| Text | Font family/size/bold/italic, colour, optional background pill or outline, alignment, multi-line, emoji |
| Callout / speech bubble | Text box with pointer tail you can drag to the target; rounded / rectangular / thought styles |
| Numbered steps | Auto-increment badges (1, 2, 3…); drag to reorder; styles (circle/square), optional connecting lines; restart numbering |
| Magnifier inset | Pick a source region → renders a zoomed copy (2–8×) wherever you place it, with connector line |
| Blur | Gaussian, radius adjustable; rect or freehand brush |
| Pixelate | Block size adjustable; rect or brush |
| Redact (solid) | Permanently replaces pixels with solid colour at export; warns that it is irreversible; recommended for secrets |
| Stickers / emoji | Built-in set (checkmark, cross, warning, thumbs, arrows), any emoji, custom PNG stickers folder |
| Counter / checkbox | Quick ✓ / ✗ / ☐ marks for QA |
| Eraser | Removes freehand strokes/segments |
| Eyedropper | Picks colour from the image into current tool; shows HEX/RGB/HSL; copies to clipboard |
| Ruler / measure | Pixel distance and angle between two points; shows W×H of a dragged rect |
| Crop | Free, fixed ratios (1:1, 4:3, 16:9, custom), expand canvas (negative crop) |
| Canvas: padding/border/shadow/rounded corners | For presentable screenshots |
| Beautify background | Solid / gradient / wallpaper / blur-of-image behind the shot, with padding and shadow (CleanShot-style) |
| Watermark | Text or image, position presets, opacity; per-profile default |
| Resize / scale | Percent or exact px, high-quality resampling; DPI metadata set for Retina-style exports |
| Rotate / flip | 90° steps, mirror H/V |
| Image adjustments | Grayscale, invert, brightness/contrast, sharpen (lightweight, non-destructive) |

Editing model
- Every object selectable, movable, resizable, rotatable (where meaningful); multi-select, group/ungroup; align/distribute; z-order (front/back/forward/backward); duplicate (Ctrl+D); copy style (Alt+drag).
- Unlimited undo/redo; drag operations merged into one undo step.
- Smart guides (snap to edges/centres), optional grid.
- Tool presets: last-used colour/width per tool; 3 custom profiles (e.g. "docs", "bug report", "social").
- Keyboard: every tool has a key (H highlighter, A arrow, T text, R rect, E ellipse, N numbers, B blur, P pixelate, X redact, C crop, M magnifier, L line, S spotlight…). Ctrl+Z/Y, Delete, Ctrl+C/V objects, Ctrl+S save, Ctrl+Shift+C copy image, Ctrl+U upload, Esc close.
- Paste image into editor as a new layer object (compose two screenshots).
- `.shotit` project sidecar (JSON) saved with history items so annotations stay editable later.

Export
- PNG (default; optional metadata strip; optional 8-bit palette for size), JPEG (quality), WebP (lossy/lossless), BMP, PDF (single image), copy to clipboard (image + file), copy as Base64 data-URI, copy Markdown link (after upload).
- Filename template with tokens: date, time, app name, window title (sanitised), counter, dimensions.
- Save-as dialog remembers last folder; quick-save to configured folder.

---

## 7. OCR, QR, search, share

- **OCR**: Windows.Media.Ocr (offline). Auto-selects installed language packs; settings shows which are installed with a link to install more; keeps line breaks; option to copy as plain text or table-ish (tab separated when columns detected); result shown in a small editable window with "copy / search / translate (opens browser)".
- **QR / barcode**: ZXing.Net decode on the selected region; URLs offered to open.
- **Search image**: uploads via the selected provider (or temporary Imgur) then opens Google Lens / Bing Visual Search. Shown as a confirm because it leaves the machine.
- **Windows Share sheet**: DataTransferManager interop so the user can send to Mail, Teams, Phone Link, etc.
- **Open with**: list of image-capable apps (from registry OpenWithList) plus custom entries.
- **Print**: standard WPF print dialog, fit-to-page, orientation.

---

## 8. History library

- Every capture recorded (configurable: all / only saved / none) with 256 px thumbnail, timestamp, source app and window title, dimensions, file path, upload URL + delete URL, OCR text (if run), tags, "keep forever" flag.
- Library window: grid/list, search (FTS over filename/title/OCR/tags), filter by date/app/upload state, multi-select actions (copy, delete, re-upload, open folder, export zip).
- Retention: size cap and/or age; kept items exempt; "clear history" with confirm.
- Re-open any item in the editor with its annotations intact (sidecar).

---

## 9. Installation & distribution (complete Windows story)

Supported platforms
- Windows 10 version 2004 (build 19041) and later, Windows 11 all versions. x64 and ARM64 native builds (no x86).
- Older Windows 10 builds: installer refuses with a clear message and a link.
- Needs no .NET install: the app ships **self-contained** (runtime embedded, ReadyToRun compiled).

Artifacts per release (all code-signed with an EV certificate, SHA-256 hashes published)
| Artifact | Audience | Notes |
|---|---|---|
| `ShotIt-Setup-x64.exe`, `ShotIt-Setup-arm64.exe` | Everyone | Velopack per-user installer, no admin rights, ~35 MB. Installs to `%LOCALAPPDATA%\ShotIt`, creates Start Menu + optional desktop shortcut, registers uninstaller, offers "start with Windows" |
| `ShotIt-Portable-x64.zip`, `-arm64.zip` | USB / no-install users | Runs from any folder; stores data in `.\data` next to the exe; no registry writes except what the user enables explicitly (startup, context menu) and those are reversible from Settings |
| `ShotIt-x64.msi`, `ShotIt-arm64.msi` | IT departments | WiX per-machine MSI to `%ProgramFiles%\ShotIt`; supports `msiexec /i ShotIt.msi /qn`, Group Policy deployment, Intune; properties `DISABLE_UPDATES=1`, `DISABLE_UPLOAD=1`, `START_WITH_WINDOWS=0`; ADMX template for the same policies |
| winget manifest (`ShotIt.ShotIt`) | Power users | `winget install ShotIt`; auto-submitted by the release pipeline |
| Chocolatey / Scoop | Later | Community-friendly |

Installer behaviour (Setup.exe)
- Silent flags: `--silent`, `--no-shortcuts`, `--no-autostart`, `--install-dir <path>`.
- Detects a running instance and asks it to exit gracefully (named pipe) before updating; never kills mid-capture.
- Detects an existing portable copy in the same folder and offers migration of settings/history.
- Checks Windows build; checks disk space (200 MB headroom); shows the real error if anything fails.
- SmartScreen: EV certificate gives immediate reputation; binaries also submitted to Microsoft Defender and major AV vendors' false-positive portals before each release (automated script in `build/av-submit`).

First run
- 30-second onboarding: pick capture hotkey (detect if PrtScr is taken by Windows Snipping Tool and offer the one-click fix), choose save folder, choose copy/save default, opt-in crash reports, done. Everything skippable; defaults are sane.
- Tray balloon showing the hotkey once.

Shell integration (all optional toggles in Settings; all written per-user; all removed on uninstall)
- "Edit with ShotIt" context menu on PNG/JPG/JPEG/GIF/BMP/WebP (classic context menu; on Windows 11 it appears under "Show more options" because the new menu requires a packaged identity — documented limitation, revisit with a sparse MSIX identity package in v1.1).
- Optional default "Open with" association for image types (never taken without asking).
- "Send to → ShotIt" entry.
- Start with Windows via `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (installed) or Startup-folder shortcut (portable), shown and toggled in Windows Settings → Startup apps too.
- Toast notifications: registers an AUMID + shortcut so unpackaged-app toasts work (Windows.UI.Notifications); fallback to tray balloon when toasts are disabled by policy.

Updates
- Velopack delta updates. Check at start and every 24 h (configurable, can be disabled; MSI policy can lock it off). Downloads in background; applied on next launch or on "Restart now"; never interrupts a capture session.
- Manifest and packages signed; HTTPS; public key pinned in the app. Update channel: stable / beta.
- Rollback: previous version kept locally; "Reinstall previous version" in About.

Uninstall
- Appears in Apps & Features; removes binaries, shortcuts, startup entry, shell integration, AUMID registration.
- Asks whether to also delete captures/history/settings (default: keep captures, delete settings).
- Leaves nothing in registry other than what the user chose to keep.

Data locations
- Installed: `%LOCALAPPDATA%\ShotIt\` → `settings.json`, `history.db`, `captures\yyyy\MM\`, `logs\`, `crash\`.
- Portable: `<exe dir>\data\` (falls back to LocalAppData if not writable, with notice).
- Default capture folder for saved files: `%USERPROFILE%\Pictures\ShotIt\` (configurable, OneDrive-aware).

---

## 10. Technology & architecture

| Decision | Choice | Why / rejected alternatives |
|---|---|---|
| Language / runtime | C# 12, .NET 8 LTS (migrate to .NET 10 LTS after it ships stable) | Fastest path to a robust Windows app with WinRT access. C++/Win32 ≈ 3× dev time; Rust ecosystem for WGC/UIA thinner; Electron/Tauri rejected: overlay latency, RAM, webview DPI quirks |
| UI framework | WPF + Win32 interop via CsWin32 | Mature transparent/topmost/per-monitor-DPI windows. WinUI 3 rejected: unreliable transparent fullscreen overlays, heavier. Avalonia rejected for v1: overlay/DPI edge cases |
| Overlay/editor rendering | WPF DrawingVisual / WriteableBitmap; Direct2D (Vortice) only if profiling demands | |
| Screen capture | Windows.Graphics.Capture → DXGI Desktop Duplication → GDI BitBlt | Fallback chain, see below |
| Window/element detection | Win32 EnumWindows + DwmGetWindowAttribute; UI Automation (UIA3) | |
| OCR / QR | Windows.Media.Ocr via CsWinRT; ZXing.Net | Offline |
| Native interop | Microsoft.Windows.CsWin32 source generator | No hand-written P/Invoke → fewer marshalling bugs |
| Storage | SQLite (Microsoft.Data.Sqlite); JSON settings via source-generated System.Text.Json | |
| Hotkeys | RegisterHotKey; low-level hook only as explicit opt-in | Hooks fragile + AV-flagged |
| Packaging | Velopack (Setup + portable + delta updates), WiX 4 for MSI | MSIX rejected for primary channel (hotkey/startup restrictions) |
| Build | Self-contained, ReadyToRun, x64 + ARM64 | NativeAOT unsupported by WPF |
| Logging / crashes | Serilog rolling files; Sentry opt-in; local minidump always | |
| Tests | xunit, FluentAssertions, Verify, FsCheck, FlaUI, BenchmarkDotNet | |
| CI | GitHub Actions (hosted for unit; self-hosted Windows runner with GPU for capture/UI tests); signing via Azure Key Vault / SignPath | |

Solution layout
```
ShotIt.sln
├─ src/
│  ├─ ShotIt.App/        WPF shell: tray, hotkeys, overlay, editor, pins, history, settings, onboarding
│  ├─ ShotIt.Core/       Pure logic, zero UI deps: annotation model + commands, geometry/DPI math,
│  │                     stitching, export pipeline, history repo, settings + migrations, upload orchestration
│  ├─ ShotIt.Capture/    WGC / DXGI / GDI backends, monitor topology, window + UIA detection, cursor
│  ├─ ShotIt.Native/     CsWin32 generated interop + SafeHandle wrappers
│  ├─ ShotIt.Upload/     Imgur, S3-compatible, FTP/SFTP, custom HTTP, local/network folder
│  ├─ ShotIt.Ocr/        Windows.Media.Ocr + ZXing wrappers
│  └─ ShotIt.Shell/      Explorer integration, startup registration, AUMID/toasts, file associations
├─ tests/
│  ├─ ShotIt.Core.Tests/       unit + snapshot + property tests (every push, hosted runner)
│  ├─ ShotIt.Capture.Tests/    integration on a real desktop (self-hosted)
│  ├─ ShotIt.Upload.Tests/     MinIO / FTP / WireMock containers
│  ├─ ShotIt.Shell.Tests/      registry integration in a sandboxed hive
│  ├─ ShotIt.UI.Tests/         FlaUI end-to-end (self-hosted)
│  └─ ShotIt.Benchmarks/       perf budgets
├─ installer/            Velopack config, WiX MSI, ADMX templates, winget manifest template
├─ build/                CI scripts, signing, AV submission, SBOM
├─ docs/                 ADRs, pitfalls.md, test-matrix.md, user docs source
└─ PLAN.md
```

Rules
- Core references nothing UI/WinRT/native; fully unit-testable.
- All native handles are SafeHandles; no raw IntPtr leaves ShotIt.Native.
- Dependencies one-way: App → (Capture, Upload, Ocr, Shell) → Core. Capture/Shell → Native.
- `ICaptureBackend { bool IsAvailable(); CaptureResult Capture(CaptureRequest); }`; `CaptureBackendSelector` chooses per monitor and logs fallbacks.
- Feature folders with MVVM; no god classes; no static mutable state outside DI singletons.

Capture pipeline essentials
- Internal coordinates are **physical pixels in virtual-screen space, signed ints**. DIP conversion only at window boundaries. Manifest: PerMonitorV2, longPathAware.
- Backend chain per monitor: WGC (one frame, `IsBorderRequired=false`) → DXGI → GDI (`CAPTUREBLT`). HDR monitors default to GDI (DWM tone-mapped SDR).
- Window capture: `PrintWindow(PW_RENDERFULLCONTENT)` else crop by `DWMWA_EXTENDED_FRAME_BOUNDS`; filter `DWMWA_CLOAKED`.
- Cursor composited from `GetCursorInfo`/`DrawIconEx` with hotspot scaling.
- Overlay: one pre-created window per monitor; show = swap bitmap + `SW_SHOWNOACTIVATE`; session watchdog 10 min; Esc always cancels.
- Frozen bitmaps use pooled buffers, released at session end.

---

## 11. Windows pitfall register (each is an issue + a test)

| # | Pitfall | Mitigation |
|---|---|---|
| P1 | PrtScr taken by Windows Snipping Tool (`PrintScreenKeyForSnippingEnabled`) | Detect at onboarding; one-click HKCU fix or alternative hotkey |
| P2 | RegisterHotKey fails (key owned by another app) | Show which key failed + suggestions; re-register on resume and on `TaskbarCreated`; never crash |
| P3 | Mixed-DPI blurry/offset captures | Per-monitor overlays; physical-px coordinates; 100–200 % matrix |
| P4 | Negative virtual-screen coordinates | Signed ints everywhere; property tests with negative origins |
| P5 | Display hot-plug / lid close / RDP reconnect mid-session | `WM_DISPLAYCHANGE`, `WM_DEVICECHANGE` → abort session, rebuild overlays, re-register hotkeys |
| P6 | WGC yellow border / unsupported builds | Require 19041+; `IsBorderRequired=false`; fallback chain |
| P7 | HDR washed-out colours with WGC | Detect HDR (DXGI_OUTPUT_DESC1); GDI path by default |
| P8 | Secure desktop (UAC, lock screen) uncapturable | Detect `OpenInputDesktop` failure; skip with log; never hang |
| P9 | Elevated windows block UIA from non-elevated process | Degrade to window-rect snapping; optional run-elevated |
| P10 | Exclusive-fullscreen games | Capture via WGC/DXGI; toast instead of overlay when `QUNS_RUNNING_D3D_FULL_SCREEN` |
| P11 | GetWindowRect includes invisible borders | Always `DWMWA_EXTENDED_FRAME_BOUNDS` |
| P12 | Cloaked UWP / other-desktop windows look visible | Filter `DWMWA_CLOAKED` |
| P13 | Clipboard locked by another process | Retry 5× / 50 ms; set all formats in one open; never block UI > 250 ms |
| P14 | Stuck dim overlay after focus loss | Watchdog; session-scoped Esc hotkey; tray "Cancel capture" |
| P15 | Multiple instances (double launch, portable + installed) | Named mutex + named pipe with whitelisted commands |
| P16 | Startup registration blocked by policy | Show real error; MSI/ADMX path for enterprises |
| P17 | AV / SmartScreen flags | EV signing; AV pre-submission; no keyboard hook by default |
| P18 | Unicode / long / OneDrive-locked paths | longPathAware; FileShare.Read; retry; actionable error |
| P19 | System.Drawing GDI leaks | Banned by analyzer; WIC only |
| P20 | Topmost loses to other topmost windows | `HWND_TOPMOST` on show; re-assert on `WM_ACTIVATE` |
| P21 | Theme/accent switch while open | `WM_SETTINGCHANGE` / `UISettings.ColorValuesChanged`; dynamic resources |
| P22 | Sleep/hibernate mid-session | `PowerModeChanged` → abort session, release GPU |
| P23 | DWM off / Safe Mode / basic driver | Guard DWM calls; GDI path |
| P24 | Async callbacks after window closed | Dispatcher + session CancellationToken; global exception handlers |
| P25 | Duplicate touch/pen + mouse events | WM_POINTER exclusively on the overlay |
| P26 | Accessibility (Narrator, high contrast, keyboard only) | AutomationProperties everywhere; high-contrast theme; keyboard map |
| P27 | Culture bugs (decimal comma, RTL) | InvariantCulture for storage; FlowDirection tested |
| P28 | Accidental upload / EXIF leaks | Explicit action only; metadata strip; optional confirm |
| P29 | Update tampering | Signed packages; pinned key; HTTPS |
| P30 | Scrolling capture: sticky headers, smooth scroll, lazy load (v1.1) | Fixed-band detection; stable-frame wait; hash compare; step cap |
| P31 | Toasts silently fail for unpackaged apps | Register AUMID + Start Menu shortcut; balloon fallback |
| P32 | Installer runs while a capture is in progress | Setup asks the running instance to finish/exit via pipe; retries; never kills mid-session |
| P33 | Per-user install + per-machine MSI both present | Installer detects the other and offers to remove it; distinct AUMIDs/mutex names |
| P34 | Context menu entries orphaned after uninstall | Uninstaller enumerates and removes every key it ever wrote (manifest of registry writes kept in data dir) |
| P35 | ARM64: x64 native deps fail to load | Only managed or multi-arch native packages (SQLitePCLRaw bundle_e_sqlite3 ships arm64); CI runs tests on an ARM64 runner |
| P36 | Highlighter/multiply blend renders wrong on transparent PNG regions | Blend against an opaque composite of base+background; snapshot tests on transparent fixtures |
| P37 | Spotlight/blur on very large images (8K) stalls the UI | Effects rendered on a background thread with cancellation; preview at reduced resolution, full-res at export |
| P38 | Pen pressure/tilt from Wacom vs Surface differ | Normalise pressure 0–1; fall back to constant width when unavailable |

Register lives in `docs/pitfalls.md`; a PR closing a pitfall links its test.

---

## 12. Performance & footprint budgets

| Metric | Budget |
|---|---|
| Hotkey → overlay visible (warm, 1×1080p) | ≤ 50 ms |
| Hotkey → overlay visible (3×4K mixed DPI) | ≤ 120 ms |
| Selection drag frame time | ≤ 8 ms, zero allocations in the mouse-move path |
| Copy to clipboard after selection (1080p) | ≤ 80 ms |
| Editor: tool stroke latency | ≤ 16 ms |
| Editor: undo/redo at 50 objects | ≤ 16 ms |
| Blur/pixelate preview on 4K region | ≤ 100 ms (background), export ≤ 1 s |
| Idle RSS (tray) | ≤ 45 MB |
| Idle CPU | 0 % (no polling timers) |
| Cold start to tray ready | ≤ 600 ms |
| Installer size | ≤ 40 MB self-contained; portable zip ≤ 45 MB |
| Install time (SSD) | ≤ 10 s |

---

## 13. Testing strategy

1. **Unit (Core)**: DPI/geometry (property-based round-trips, negative origins), annotation commands + undo invariants (undo(do(x)) == x for every tool), blend math for highlighter, spotlight masks, filename sanitiser fuzzing, settings migrations from every prior schema, retention policy, stitching, clipboard packing.
2. **Snapshot (Verify)**: every tool rendered against golden PNGs on opaque and transparent fixtures at 100/150/200 %; toolbar placement for every edge case on every monitor-layout fixture; beautify backgrounds.
3. **Capture integration (self-hosted runner)**: each backend captures a checkerboard/colour-bar test window; pixel-exact assertions per DPI; display-change simulation; HDR fixture (where hardware available).
4. **Shell integration**: registry writes/removals in a redirected hive (`RegOverridePredefKey`); AUMID + toast activation; startup entry; context-menu entries appear and disappear.
5. **Installer**: fresh VM snapshots (Win10 19041, Win10 22H2, Win11 23H2, Win11 24H2, ARM64 Win11): install → run → capture → update from previous version → uninstall → verify zero leftovers (registry diff + filesystem diff). Silent install and MSI /qn paths included. Portable zip on a FAT32 USB fixture.
6. **UI end-to-end (FlaUI)**: hotkey → select → copy; window snap; element snap; pin; every editor tool via keyboard; OCR on known text; QR decode; settings round-trip; single-instance forwarding; crash recovery.
7. **Upload**: MinIO (S3), local FTP/SFTP, WireMock for Imgur/custom; failure injection (timeouts, 5xx, 401, no network) → visible error + retry, never a hang.
8. **Manual release matrix** (`docs/test-matrix.md`): monitors (single, dual mixed DPI, triple with negative coords, 4K 200 % + 1080p, HDR, RDP, GPU-less VM), inputs (mouse, touch, pen, keyboard-only, Narrator), apps (Chrome, Explorer, UWP Settings, elevated Task Manager, exclusive-fullscreen game, DRM video, Terminal), power (sleep/resume, lid close, hot-plug).
9. **Soak**: 1,000 scripted sessions + 500 editor sessions; assert no handle/GDI-object growth, RSS growth < 5 %.
10. **Static**: Nullable, warnings as errors, AnalysisLevel latest-all, banned APIs (System.Drawing, Thread.Sleep on UI, async void outside handlers), Roslynator.

Crash policy: every exception is handled at a feature boundary with an actionable message, or it is a bug. Global handlers log + minidump. The app never silently dies in the tray.

---

## 14. Security & privacy

- No network traffic except user-triggered uploads/search and the update check (version + OS build only; disableable; MSI policy can lock it off).
- Secrets via DPAPI (current user); never plain JSON.
- Uploads: metadata stripped, random names, HTTPS only, no cert-validation bypass.
- Privacy rules: skip history for window titles matching patterns (password managers, banking); "panic clear" hotkey optional.
- EV code signing, SBOM per release, pinned dependencies, Dependabot.
- Single-instance pipe: whitelisted command set only.

---

## 15. Settings surface (so nothing is hidden in code)

General (language, theme, start with Windows, tray behaviour, sounds) · Hotkeys (every mode + editor + pins, conflict detection) · Capture (cursor, delay, snapping, magnifier, HDR mode, backend override) · Editor (default tool presets, profiles, fonts, smart guides) · Output (default action, filename template, folder, formats, quality, metadata strip) · Upload (providers, privacy, confirm, expiry) · History (retention, exclusions) · Shell integration (context menu, open-with, send-to) · Updates (channel, auto) · Privacy (crash reports, exclusions) · Advanced (logs, reset, portable toggle, export/import settings) · About (version, changelog, licences, rollback).

---

## 16. Quality gates (Definition of Done)

PR merges only when: CI green (build with warnings as errors, unit/snapshot/property tests, analyzers, format); new behaviour has tests; pitfall fixes reference P-number + test; no TODO without an issue; nightly benchmarks within budget.

Release ships only when: capture + shell + UI + installer suites green on self-hosted runners and VM snapshots; manual matrix executed and signed off; soak passes; zero known crash bugs; zero open P1/P2; signed binaries + SBOM; update path verified from the previous two versions on a clean machine; uninstall leaves zero leftovers.

---

## 17. Roadmap (solo dev or 2-person team, ~16 weeks to v1.0)

| Milestone | Weeks | Deliverable (runnable end-to-end) | Exit criteria |
|---|---|---|---|
| M0 Walking skeleton | 1 | Solution, tray, single instance, RegisterHotKey, GDI full-screen → clipboard, logging, CI, analyzers, x64+ARM64 builds | Hotkey captures to clipboard on both archs; CI green |
| M1 Overlay | 2–3 | Frozen screen, per-monitor overlays, region/freeform/monitor select, magnifier, readout, keyboard adjust, all cancel paths, watchdog | P3–P5, P14, P20 pass; ≤ 50 ms warm |
| M2 Backends + snapping | 4–5 | WGC/DXGI/GDI chain, HDR, cursor, window + UIA snapping, active-window, delayed, quick toolbar copy/save/pin | P6–P12 pass; capture suite green |
| M3 Editor core | 6–7 | Annotation model, undo, selection/transform, arrow/line/shapes/pen/text/highlighter/area highlight/numbers/blur/pixelate/redact/crop, export encoders | Snapshot tests per tool; undo invariants |
| M4 Editor advanced | 8 | Spotlight, callouts, magnifier inset, stickers, eyedropper, ruler, beautify, watermark, resize/rotate, adjustments, profiles, sidecar | Snapshot tests; P36–P38 |
| M5 History + settings + onboarding | 9 | SQLite history, library, full settings UI, first-run wizard, themes, portable mode | Migration tests; soak baseline |
| M6 Output + upload + shell | 10–11 | Providers, privacy controls, Windows Share, open-with, print, OCR, QR, context menu, send-to, startup, toasts (AUMID) | Upload + shell suites green; P31 |
| M7 Installation | 12 | Velopack setup + portable, WiX MSI + ADMX, winget manifest, silent flags, uninstall cleanup, update channel, rollback | Installer suite green on all VM snapshots; P32–P34 |
| M8 Hardening | 13–14 | Pitfall register closed, perf budgets, crash reporting, accessibility pass, AV submissions, docs | Manual matrix executed |
| M9 Beta → v1.0 | 15–16 | Public beta, triage, polish, website, winget publish | Release gates §16 |
| v1.1 | +5 | Scrolling capture, GIF/MP4 recording with trim, custom actions, localisation, Win11 new-context-menu via sparse package | |

Rule: never start a milestone with a failing gate from the previous one. Installation (M7) and hardening (M8) are milestones, not leftovers.

---

## 18. Immediate next steps (M0, day 1)

1. `dotnet new sln`; projects per §10; `Directory.Build.props` with Nullable, TreatWarningsAsErrors, AnalysisLevel latest-all, LangVersion latest, TieredPGO, ReadyToRun, `RuntimeIdentifiers=win-x64;win-arm64`, SelfContained.
2. CsWin32 + `NativeMethods.txt` (RegisterHotKey, EnumDisplayMonitors, GetDpiForMonitor, BitBlt, DwmGetWindowAttribute, SetWindowPos, GetCursorInfo, PrintWindow, SHQueryUserNotificationState…).
3. App manifest: PerMonitorV2, longPathAware, supportedOS Win10/11.
4. Tray icon + hidden message window for WM_HOTKEY, WM_DISPLAYCHANGE, TaskbarCreated, WM_SETTINGCHANGE.
5. GDI capture of all monitors into pooled WriteableBitmaps; multi-format clipboard copy with retry.
6. Serilog, global exception handlers, named-mutex single instance + pipe.
7. GitHub Actions: build/test/format on push for x64 + ARM64; release workflow skeleton with Velopack (signing and AV submission wired in M7/M8).
8. Create `docs/pitfalls.md` (§11) and `docs/test-matrix.md` (§13.8); one issue per pitfall and per milestone.
