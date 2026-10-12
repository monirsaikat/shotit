# Windows pitfall register

Every known way Windows can break a screenshot tool, what ShotIt does about it, and how that is checked.
The numbering follows section 11 of [PLAN.md](../PLAN.md).

Status meanings:

- **Done** – implemented and covered by an automated check.
- **Partial** – implemented, but part of the mitigation or its verification is missing. The gap is stated.
- **Open** – not implemented yet.

"Self-test" means `ShotIt.exe --selftest <scenario> <folder>`; see [test-matrix.md](test-matrix.md).

| # | Pitfall | Status | What ShotIt does | Checked by |
|---|---|---|---|---|
| P1 | Print Screen opens Snipping Tool | Done | `PrintScreenBinding` detects the Windows setting (explicit value, or the Windows 11 default). Onboarding and Settings › Hotkeys offer to turn it off. | `PrintScreenBindingTests`; self-test `windows` |
| P2 | `RegisterHotKey` fails because another app owns the key | Done | Failed keys are listed by name in a notification and next to the field in Settings. Hotkeys are re-registered on resume and when Explorer restarts. A key assigned twice is reported as a duplicate. | `HotkeyTests`; recorder rules in self-test `windows`. The failure notification itself is checked by hand. |
| P3 | Mixed-DPI captures are blurry or offset | Partial | One overlay window per monitor, all coordinates in physical pixels, DIP conversion only at the window edge. **Gap:** verified pixel-exact only on monitors at 100 %; mixed scaling still needs the manual matrix. | Self-test `overlay` (pixel mismatch must be 0) |
| P4 | Negative virtual-screen coordinates | Done | Signed integers throughout; document space is virtual pixels minus the virtual origin. | `GeometryTests`, `SelectionModelTests` |
| P5 | Display hot-plug, lid close, RDP reconnect during a session | Done | `WM_DISPLAYCHANGE` cancels the session, pins are moved back on screen, hotkeys are re-registered. | Self-test `overlay` |
| P6 | Windows Graphics Capture border and unsupported builds | Done | GDI is the default. The WGC backend asks for no border and no cursor, and any failure falls back to GDI. | `GpuBackendTests`; self-test `capture` |
| P7 | HDR colours look washed out | Partial | The default GDI path returns tone-mapped colours, and Desktop Duplication is asked for 8-bit BGRA. **Gap:** HDR is not detected explicitly, and no HDR display was available for testing. | Manual matrix |
| P8 | Secure desktop (UAC prompt, lock screen) | Done | `OpenInputDesktop` failure raises `CaptureUnavailableException`, which becomes a notification. Nothing hangs. | Manual matrix (lock the screen, then press the hotkey from a script) |
| P9 | Elevated windows hide their controls from UI Automation | Partial | Snapping falls back to the window rectangle. **Gap:** there is no "run elevated" option. | `SnapIndexTests` |
| P10 | Exclusive full-screen games | Partial | When Windows reports a Direct3D full-screen app, ShotIt skips the overlay, captures through DXGI or WGC and notifies. **Gap:** not tested against a real exclusive full-screen game. | `GpuBackendTests`; manual matrix |
| P11 | `GetWindowRect` includes invisible borders | Done | `DWMWA_EXTENDED_FRAME_BOUNDS` is used for top-level windows. | `WindowListIntegrationTests` |
| P12 | Cloaked windows look visible | Done | `DWMWA_CLOAKED` windows are filtered out of the snap list. | `WindowListIntegrationTests` |
| P13 | Clipboard locked by another process | Done | Bounded retries; all formats are set in one operation; a failure is reported, never thrown. | Not automated |
| P14 | Dim overlay stuck on screen | Done | Ten-minute watchdog, focus-loss check, a session-scoped Esc hotkey that works without focus, and cancellation on every unhandled exception. | Self-test `overlay` |
| P15 | Several instances | Done | Named mutex plus a current-user-only named pipe that accepts a fixed list of commands. | Not automated |
| P16 | Start-up registration blocked | Done | The real outcome is read back and shown. Task Manager's "disabled" switch and an MSI all-users entry are both recognised. Enterprises use the MSI properties or the ADMX template. | `StartupTests`, `MachineStartupTests`, `PolicyTests` |
| P17 | Antivirus and SmartScreen warnings | Open | No keyboard hook is used (hotkeys only). **Needs a code-signing certificate**; the release script signs when given one. Antivirus pre-submission is not automated. | – |
| P18 | Unicode, long and locked paths | Partial | `longPathAware` manifest, atomic writes through a temporary file, messages that name the folder. **Gap:** no retry for files locked by sync clients. | `FileNameTemplateTests` |
| P19 | `System.Drawing` GDI leaks | Partial | `System.Drawing` is not referenced anywhere; imaging uses WIC and raw buffers, and the GDI capture path is leak-tested. **Gap:** no analyzer bans the namespace. | `GdiCaptureIntegrationTests` (handle count) |
| P20 | Overlay loses to other topmost windows | Partial | The overlay is placed with `HWND_TOPMOST` each time it is shown. **Gap:** it is not re-asserted if another topmost window appears mid-session. | Self-test `overlay` |
| P21 | Theme switch while running | Done | WPF's system theme mode follows Windows; the tray menu allows dark mode. | Manual matrix |
| P22 | Sleep or hibernate during a session | Done | The suspend broadcast cancels the session; hotkeys are re-registered on resume. | Self-test `overlay` |
| P23 | DWM off, Safe Mode, basic display driver | Partial | DWM calls are checked for failure and fall back to `GetWindowRect`; GDI needs no GPU. **Gap:** not tested on a GPU-less machine. | Manual matrix |
| P24 | Callbacks after a window closed | Done | Dispatcher, task-scheduler and AppDomain handlers log, write a crash report and keep the tray app alive. | Self-tests |
| P25 | Duplicate touch, pen and mouse events | Partial | The overlay listens to WPF mouse events only, which already include promoted touch and pen input, so nothing is delivered twice. **Gap:** `WM_POINTER` is not used, and touch and pen were not tested. | Manual matrix |
| P26 | Accessibility | Partial | Settings, History and result windows are keyboard-operable and name their controls for screen readers. **Gap:** Narrator and high-contrast passes have not been done. | Manual matrix |
| P27 | Culture bugs | Partial | Everything stored uses the invariant culture; displayed dates and numbers use the user's culture. **Gap:** right-to-left layouts are untested. | `SettingsStoreTests` |
| P28 | Accidental uploads and metadata leaks | Done | Uploads happen only on an explicit action, with a confirmation that is on by default. Images are re-encoded from pixels, so no metadata travels. Uploaded files get random 128-bit names. Only https destinations are accepted. | `UploadTests`; self-test `windows` |
| P29 | Update tampering | Partial | The update address must be https, and updates can be turned off by policy. **Gap:** packages are unsigned until a certificate exists; there is no pinned key. | `UpdateService` checks in self-test `policy` |
| P30 | Scrolling capture problems | Open | Scrolling capture is planned for version 1.1. | – |
| P31 | Toasts fail for unpackaged apps | Done | Notifications use the tray icon's balloon API, which Windows 10 and 11 show as toasts without any registration. An application user model ID is set. | Manual |
| P32 | Installer runs during a capture | Partial | The MSI asks the running copy to quit before replacing files; Velopack waits for the app to exit before applying an update. **Gap:** the quit request is not delayed while a capture is open. | – |
| P33 | Per-user and per-machine installs both present | Open | Both copies share the single-instance mutex, so only one runs, but neither installer detects the other. | – |
| P34 | Context-menu entries left after uninstall | Partial | `ShellIntegration.RemoveAll` removes every key ShotIt writes, and the Setup.exe uninstall hook calls it. **Gap:** an MSI uninstall cannot clean other users' per-user entries. | `SendToAndUninstallTests`; self-test `windows` |
| P35 | ARM64 native dependencies | Partial | Every native dependency ships an ARM64 build, and ARM64 packages are produced. **Gap:** never run on ARM64 hardware. | Release script |
| P36 | Highlighter blend on transparent areas | Done | Multiply blend is computed against the opaque base image. | `RenderingTests` |
| P37 | Effects on very large images stall the interface | Partial | Effect results are cached per annotation and blur runs on a reduced copy. **Gap:** effects are not rendered on a background thread. | `RenderingTests` |
| P38 | Pen pressure differs between devices | Open | Strokes use a constant width; pressure is not read. | – |
