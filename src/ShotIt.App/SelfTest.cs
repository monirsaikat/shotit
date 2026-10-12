using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ShotIt.App.Infrastructure;
using ShotIt.App.Overlay;
using ShotIt.App.Services;
using ShotIt.App.Views;
using ShotIt.Capture;
using ShotIt.Core.Annotations;
using ShotIt.Core.Editing;
using ShotIt.Core.Geometry;
using ShotIt.Core.Imaging;
using ShotIt.Core.Settings;
using ShotIt.Native;
using ShotIt.Rendering;
using ShotIt.Shell;

namespace ShotIt.App;

/// <summary>
/// Scripted end-to-end checks that drive the real application without human input:
/// <c>ShotIt.exe --selftest &lt;scenario&gt; &lt;output folder&gt;</c>. Each scenario writes result.json
/// (and pictures of the windows it opened) and exits with 0 on success. The test runs against its own
/// data folder and shows a synthetic picture instead of the real screen, so nothing personal is read.
/// </summary>
internal static class SelfTest
{
    private sealed class Report
    {
        public string Scenario { get; set; } = string.Empty;
        public bool Passed { get; set; }
        public List<string> Failures { get; } = [];
        public Dictionary<string, object> Values { get; } = [];

        public void Check(bool condition, string message)
        {
            if (!condition) Failures.Add(message);
        }
    }

    private const string ScratchKey = @"Software\ShotIt.SelfTest";

    public static void Run(App app, AppCommandLine command)
    {
        string scenario = command.Argument ?? "overlay";
        string output = Path.GetFullPath(command.Argument2 ?? Path.Combine(Path.GetTempPath(), "shotit-selftest"));
        Directory.CreateDirectory(output);
        var report = new Report { Scenario = scenario };

        AppServices services;
        try
        {
            string data = Path.Combine(output, "data");
            if (Directory.Exists(data)) Directory.Delete(data, recursive: true);
            // Shell integration is exercised against a scratch registry key and folder, never the real ones.
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(ScratchKey, throwOnMissingSubKey: false);
            var scratch = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(ScratchKey, writable: true);
            if (scenario == "policy")
            {
                using var policy = scratch.CreateSubKey(AppPolicies.KeyPath, writable: true);
                policy.SetValue("DisableUpload", 1, Microsoft.Win32.RegistryValueKind.DWord);
                policy.SetValue("DisableHistory", 1, Microsoft.Win32.RegistryValueKind.DWord);
                policy.SetValue("DisableUpdates", 1, Microsoft.Win32.RegistryValueKind.DWord);
            }

            services = app.Initialize(new AppPaths(data), scratch, Path.Combine(output, "sendto"));
            services.Settings.General.ShowNotifications = false;
            services.Settings.Output.SaveFolder = Path.Combine(output, "saved");
        }
        catch (Exception ex)
        {
            report.Failures.Add("Initialisation failed: " + ex);
            Finish(app, report, output);
            return;
        }

        app.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                switch (scenario)
                {
                    case "overlay": await Overlay(services, report, output); break;
                    case "editor": await Editor(services, report, output); break;
                    case "windows": await Windows(services, report, output); break;
                    case "capture": await RealCapture(services, report); break;
                    case "policy": await Policy(services, report, output); break;
                    default: report.Failures.Add("Unknown scenario: " + scenario); break;
                }
            }
            catch (Exception ex)
            {
                report.Failures.Add("Unhandled exception: " + ex);
            }

            Finish(app, report, output);
        }, DispatcherPriority.ApplicationIdle);
    }

    private static void Finish(App app, Report report, string output)
    {
        report.Passed = report.Failures.Count == 0;
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        app.Services?.CloseAllWindows();
        try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(ScratchKey, throwOnMissingSubKey: false); }
        catch (Exception) { }
        app.Shutdown(report.Passed ? 0 : 1);
    }

    private static Task Settle(int ms = 350) => Task.Delay(ms);

    /// <summary>A picture in which neighbouring pixels always differ, so any scaling or offset error is detectable.</summary>
    private static ScreenFrame SyntheticFrame()
    {
        var topology = Monitors.GetTopology();
        var bounds = topology.VirtualBounds;
        var image = new PixelBuffer(bounds.Width, bounds.Height);
        byte[] d = image.Data;
        for (int y = 0; y < bounds.Height; y++)
        {
            int row = y * image.Stride;
            for (int x = 0; x < bounds.Width; x++)
            {
                int i = row + x * 4;
                bool check = ((x ^ y) & 1) == 0;
                d[i] = (byte)((x * 7 + (check ? 90 : 0)) & 0xFF);
                d[i + 1] = (byte)((y * 13 + (check ? 0 : 60)) & 0xFF);
                d[i + 2] = (byte)(((x >> 3) * 31 + (y >> 3) * 17) & 0xFF);
                d[i + 3] = 255;
            }
        }

        return new ScreenFrame(image, topology, "Synthetic", TimeSpan.Zero);
    }

    private static void SavePng(PixelBuffer image, string path) => File.WriteAllBytes(path, ImageCodec.Encode(image, ImageFormat.Png));

    /// <summary>Saves what the window really looks like (frame, theme and all) by asking Windows to render it.</summary>
    private static void SaveWindowPicture(Window window, string path)
    {
        nint hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (hwnd != 0 && GdiScreen.PrintWindow(hwnd) is { } picture)
        {
            SavePng(picture, path);
            return;
        }

        if (window.Content is not FrameworkElement root || root.ActualWidth < 1 || root.ActualHeight < 1) return;
        var dpi = VisualTreeHelper.GetDpi(window);
        var target = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        target.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    // ------------------------------------------------------------------ scenarios

    private static async Task Overlay(AppServices app, Report report, string output)
    {
        var frame = SyntheticFrame();
        var primary = frame.Topology.Primary;
        report.Values["monitors"] = frame.Topology.Monitors.Select(m => $"{m.Bounds} @ {m.Dpi} dpi").ToArray();
        app.Controller.FrameSourceOverride = () => frame;

        TimeSpan shown = TimeSpan.Zero;
        app.Controller.OverlayShown += t => shown = t;

        // A region in the middle of the primary monitor with odd offsets, to catch off-by-one errors.
        var region = new PixelRect(primary.Bounds.X + 201, primary.Bounds.Y + 151, 641, 363).Intersect(primary.Bounds);

        // 1. Cold start, then warm start for timing.
        app.Controller.StartRegion(OverlayMode.Preselected, region);
        await Settle(500);
        report.Values["coldStartMs"] = Math.Round(shown.TotalMilliseconds, 1);
        report.Check(app.Controller.IsSessionActive, "Session did not start");
        app.Controller.CancelSession("self-test");
        await Settle(150);
        report.Check(!app.Controller.IsSessionActive, "Session did not end on cancel");

        var warm = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            app.Controller.StartRegion(OverlayMode.Preselected, region);
            warm.Add(shown.TotalMilliseconds);
            await Settle(120);
            app.Controller.CancelSession("self-test");
            await Settle(80);
        }

        report.Values["warmStartMs"] = warm.Select(w => Math.Round(w, 1)).ToArray();
        report.Check(warm.Order().ElementAt(warm.Count / 2) <= 150, $"Median warm overlay start {warm.Order().ElementAt(warm.Count / 2):0} ms exceeds 150 ms");

        // 2. Pixel exactness: what is on the real screen inside the region must equal the frame.
        app.Controller.StartRegion(OverlayMode.Preselected, region);
        await Settle(600);
        var session = app.Controller.CurrentSession;
        if (session is null)
        {
            report.Failures.Add("No session for the pixel check");
            return;
        }

        report.Check(session.Phase == OverlayPhase.Adjusting, "Preselected session is not in the adjusting phase");
        var inner = region.Inflate(-(int)Math.Ceiling(8 * primary.Scale));
        var actual = GdiScreen.Capture(inner);
        var expected = frame.Crop(inner);
        long mismatches = 0;
        for (int i = 0; i < expected.Width * expected.Height * 4; i += 4)
            if (actual.Data[i] != expected.Data[i] || actual.Data[i + 1] != expected.Data[i + 1] || actual.Data[i + 2] != expected.Data[i + 2]) mismatches++;
        double ratio = (double)mismatches / (expected.Width * expected.Height);
        report.Values["pixelMismatchRatio"] = ratio;
        report.Check(mismatches == 0, $"Overlay is not pixel exact: {mismatches} of {expected.Width * expected.Height} pixels differ");

        // Only keep pictures when the overlay really covers the screen, so no real desktop content is saved.
        bool overlayVisible = ratio < 0.5;
        var view = region.Inflate((int)(130 * primary.Scale)).Intersect(primary.Bounds);
        if (overlayVisible) SavePng(GdiScreen.Capture(view), Path.Combine(output, "overlay-region.png"));

        // 3. Draw with several tools through the same code paths the mouse uses.
        var editor = session.Editor;
        Vec2 P(double x, double y) => new(region.X - frame.VirtualBounds.X + x, region.Y - frame.VirtualBounds.Y + y);
        void Drag(ToolKind tool, Vec2 a, Vec2 b)
        {
            editor.SetTool(tool);
            editor.PointerDown(a, PointerModifiers.None, 5);
            editor.PointerMove(Vec2.Lerp(a, b, 0.5), PointerModifiers.None);
            editor.PointerMove(b, PointerModifiers.None);
            editor.PointerUp(b, PointerModifiers.None);
        }

        Drag(ToolKind.Arrow, P(40, 40), P(220, 120));
        Drag(ToolKind.Rectangle, P(260, 40), P(420, 130));
        Drag(ToolKind.Highlighter, P(40, 180), P(300, 180));
        Drag(ToolKind.Blur, P(330, 160), P(520, 240));
        Drag(ToolKind.Spotlight, P(20, 20), P(600, 330));
        editor.SetTool(ToolKind.Steps);
        editor.PointerDown(P(80, 270), PointerModifiers.None, 5);
        editor.PointerDown(P(130, 270), PointerModifiers.None, 5);
        TextEditRequest? request = null;
        editor.TextEditRequested += (_, r) => request = r;
        editor.SetTool(ToolKind.Text);
        editor.PointerDown(P(200, 255), PointerModifiers.None, 5);
        await Settle(250);
        report.Check(request is not null, "Text tool did not ask for text");
        report.Check(session.IsTextEditing, "Text box did not open on the overlay");
        if (request is not null) editor.CommitText(request, "Self-test");
        foreach (var w in app.OpenWindows) _ = w;
        app.Controller.HandleEscape(); // closes the text box, must not end the session
        report.Check(app.Controller.IsSessionActive, "Esc during text entry ended the whole session");
        editor.SetTool(ToolKind.Select);
        await Settle(400);
        report.Check(editor.State.Items.Count == 8, $"Expected 8 annotations, found {editor.State.Items.Count}");
        if (overlayVisible) SavePng(GdiScreen.Capture(view), Path.Combine(output, "overlay-annotated.png"));

        // Undo/redo through the session keeps the count consistent.
        editor.Undo();
        editor.Undo();
        report.Check(editor.State.Items.Count == 6, "Undo did not remove two annotations");
        editor.Redo();
        editor.Redo();
        report.Check(editor.State.Items.Count == 8, "Redo did not restore two annotations");

        // 4. Keyboard nudging and resizing of the region.
        var before = session.Selection.Current!.Value;
        session.KeyDown(System.Windows.Input.Key.Right, System.Windows.Input.ModifierKeys.None);
        session.KeyDown(System.Windows.Input.Key.Down, System.Windows.Input.ModifierKeys.Shift);
        report.Check(session.Selection.Current == before.Offset(1, 10), "Arrow keys did not nudge the region as expected");
        session.Selection.SetRegion(before);

        // 5. Save: the overlay must disappear and a file of exactly the region's size must appear.
        session.RequestAction(OverlayAction.Save);
        await Settle(400);
        report.Check(!app.Controller.IsSessionActive, "Session still active after Save");
        string saved = app.Settings.Output.SaveFolder;
        var files = Directory.Exists(saved) ? Directory.GetFiles(saved, "*.png") : [];
        report.Check(files.Length == 1, $"Expected one saved file, found {files.Length}");
        if (files.Length == 1)
        {
            var image = ImageCodec.DecodeFile(files[0]);
            report.Check(image.Size == region.Size, $"Saved image is {image.Width}x{image.Height}, expected {region.Width}x{region.Height}");
            File.Copy(files[0], Path.Combine(output, "saved-capture.png"), overwrite: true);
            // The spotlight dims the corner; the unannotated centre-right must still match the frame.
        }

        report.Check(app.History.Count() == 1, $"History has {app.History.Count()} entries, expected 1");
        report.Check(app.Settings.Capture.LastRegion is [var lx, var ly, var lw, var lh] && new PixelRect(lx, ly, lw, lh) == region, "Last region was not remembered");

        // 6. Esc with nothing in progress cancels; display change while open cancels and recovers.
        app.Controller.StartRegion(OverlayMode.Region);
        await Settle(200);
        app.Controller.HandleEscape();
        await Settle(100);
        report.Check(!app.Controller.IsSessionActive, "Esc did not cancel an idle session");

        app.Controller.StartRegion(OverlayMode.Region);
        await Settle(150);
        app.Controller.OnDisplayChanged();
        await Settle(400);
        report.Check(!app.Controller.IsSessionActive, "Display change did not cancel the session");
        app.Controller.StartRegion(OverlayMode.Freeform);
        await Settle(200);
        report.Check(app.Controller.IsSessionActive, "Could not start a session after a display change");
        app.Controller.OnSuspending();
        report.Check(!app.Controller.IsSessionActive, "Suspend did not cancel the session");

        // 7. After everything, the screen must be back to normal (no overlay left behind).
        await Settle(300);
        var after = GdiScreen.Capture(inner);
        long same = 0;
        for (int i = 0; i < expected.Width * expected.Height * 4; i += 4)
            if (after.Data[i] == expected.Data[i] && after.Data[i + 1] == expected.Data[i + 1] && after.Data[i + 2] == expected.Data[i + 2]) same++;
        report.Check(same < expected.Width * expected.Height / 2, "An overlay window is still covering the screen after the session ended");
    }

    private static async Task Editor(AppServices app, Report report, string output)
    {
        var image = new PixelBuffer(900, 560);
        image.Fill(new ColorRgba(0xF4, 0xF5, 0xF7));
        for (int y = 40; y < 520; y += 26)
            for (int x = 40; x < 860; x++)
                if ((x / 9 + y) % 7 != 0) image.SetPixel(x, y, new ColorRgba(0x55, 0x5B, 0x66));

        var doc = DocState.FromImage(image)
            .Add(new ArrowAnnotation { Start = new(120, 120), End = new(360, 220), StrokeWidth = 5 })
            .Add(new ShapeAnnotation { Rect = new(420, 80, 220, 120), Kind = ShapeKind.Rectangle })
            .Add(new HighlighterAnnotation { Points = [new(60, 300), new(420, 300)] })
            .Add(new BlurAnnotation { Rect = new(480, 260, 300, 120) })
            .Add(new StepAnnotation { Center = new(100, 420), Number = 1 })
            .Add(new CalloutAnnotation { Box = new(520, 420, 220, 70), Tail = new(470, 520), Text = "Editor self-test" });

        var window = app.OpenEditor(new CaptureResult(doc));
        await Settle(900);
        SaveWindowPicture(window, Path.Combine(output, "editor.png"));

        var session = window.Session;
        report.Check(session.State.Items.Count == 6, "Editor did not load the annotations");
        session.SetTool(ToolKind.Select);
        session.SelectAll();
        session.NudgeSelected(new Vec2(5, 5));
        session.Undo();
        report.Check(session.State.Items[0].Bounds == doc.Items[0].Bounds, "Undo after nudge did not restore positions");

        // Crop and frame.
        session.ApplyCrop(new PixelRect(20, 20, 800, 500));
        report.Check(session.State.Size == new PixelSize(800, 500), "Crop did not resize the document");
        session.Apply(session.State with { Canvas = new CanvasStyle { Padding = 48, Background = BackgroundKind.Gradient, CornerRadius = 14, Shadow = true } });
        await Settle(500);
        SaveWindowPicture(window, Path.Combine(output, "editor-framed.png"));

        var final = DocumentRenderer.RenderFinal(session.State);
        report.Check(final.Size == new PixelSize(896, 596), $"Framed output is {final.Width}x{final.Height}, expected 896x596");
        SavePng(final, Path.Combine(output, "editor-output.png"));

        // Saving through the output service writes a file and a history entry.
        var result = new CaptureResult(session.State);
        string? path = app.Output.Save(result, out string? error);
        report.Check(path is not null && File.Exists(path), "Editor save failed: " + error);
        report.Check(app.History.Count() == 1, "Saving did not create a history entry");

        session.MarkSaved();
        window.Close();
        await Settle(200);
        report.Check(app.OpenWindows.Count == 0, "Editor window did not close");
    }

    private static async Task Windows(AppServices app, Report report, string output)
    {
        app.Features.Confirm = (_, _, _) => true;

        // --- seed history: a plain capture, an annotated one, and one saved to the user folder ---
        PixelBuffer Page(string[] lines)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 640, 240));
                double y = 24;
                foreach (string line in lines)
                {
                    dc.DrawText(TextLayout.Create(line, "Segoe UI", 22, false, false, Brushes.Black), new Point(24, y));
                    y += 40;
                }
            }

            var target = new RenderTargetBitmap(640, 240, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            return target.ToPixelBuffer();
        }

        var plain = new CaptureResult(DocState.FromImage(Page(["Invoice number 48213", "Thank you for your order"])), "winword", "Invoice March");
        var annotated = new CaptureResult(
            DocState.FromImage(Page(["Annotated capture"])).Add(new ArrowAnnotation { Start = new(40, 150), End = new(300, 200) }).Add(new BlurAnnotation { Rect = new(350, 120, 200, 80) }),
            "chrome", "Release notes");
        var saved = new CaptureResult(DocState.FromImage(Page(["Saved capture"])), "explorer", "Holiday photos");
        report.Check(app.Output.EnsureInLibrary(plain) is not null, "Plain capture was not added to history");
        await Task.Delay(1100);
        report.Check(app.Output.EnsureInLibrary(annotated) is not null, "Annotated capture was not added to history");
        await Task.Delay(1100);
        report.Check(app.Output.Save(saved) is not null, "Saving a capture failed");
        report.Check(app.History.Count() == 3, $"Expected 3 history entries, found {app.History.Count()}");
        report.Check(File.Exists(app.Output.ProjectPath(annotated.HistoryId!.Value)), "No editable project was stored for the annotated capture");
        report.Check(!File.Exists(app.Output.ProjectPath(plain.HistoryId!.Value)), "A project was stored for a capture without annotations");

        // --- settings: every section must build and render ---
        var settings = WindowsHost.OpenSettings(app);
        await Settle(700);
        foreach (string section in SettingsWindow.Sections)
        {
            settings.Select(section);
            await Settle(250);
            report.Check(settings.CurrentSection == section, "Settings did not switch to " + section);
            SaveWindowPicture(settings, Path.Combine(output, "settings-" + section.ToLowerInvariant() + ".png"));
        }

        report.Check(ReferenceEquals(WindowsHost.OpenSettings(app, "Upload"), settings), "A second settings window was opened");
        report.Check(settings.CurrentSection == "Upload", "Opening settings at a section did not select it");

        // The upload test button path, against a local folder.
        string uploads = Path.Combine(output, "uploads");
        app.Settings.Upload.Provider = UploadProvider.LocalFolder;
        app.Settings.Upload.LocalFolder = uploads;
        var (testOk, testMessage) = await settings.TestUploadAsync();
        report.Check(testOk, "Test upload failed: " + testMessage);
        app.Settings.Upload.Provider = UploadProvider.None;
        var (noneOk, noneMessage) = await settings.TestUploadAsync();
        report.Check(!noneOk && noneMessage.Length > 0, "Test upload without a destination did not explain itself");
        settings.Close();

        // --- hotkey recorder rules ---
        report.Check(HotkeyBox.Interpret(Key.A, HotkeyModifiers.None, out _, out _) is null, "A bare letter was accepted as a hotkey");
        report.Check(HotkeyBox.Interpret(Key.A, HotkeyModifiers.Shift, out _, out _) is null, "Shift+letter was accepted as a hotkey");
        report.Check(HotkeyBox.Interpret(Key.A, HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, out _, out _) == new Hotkey(HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, 0x41), "Ctrl+Shift+A was not recorded");
        report.Check(HotkeyBox.Interpret(Key.Snapshot, HotkeyModifiers.None, out _, out _) == new Hotkey(HotkeyModifiers.None, Hotkey.VkPrintScreen), "Print Screen alone was not recorded");
        report.Check(HotkeyBox.Interpret(Key.F9, HotkeyModifiers.None, out _, out _) is not null, "F9 alone was not recorded");
        HotkeyBox.Interpret(Key.Back, HotkeyModifiers.None, out bool clear, out _);
        HotkeyBox.Interpret(Key.Escape, HotkeyModifiers.None, out _, out bool cancel);
        report.Check(clear && cancel, "Backspace/Esc are not treated as clear/cancel");
        report.Check(HotkeyBox.Interpret(Key.LeftCtrl, HotkeyModifiers.Ctrl, out _, out _) is null, "A modifier alone was recorded");

        // --- history window ---
        var history = WindowsHost.OpenHistory(app);
        await Settle(700);
        SaveWindowPicture(history, Path.Combine(output, "history.png"));
        report.Check(history.ShownItems.Count == 3, $"History shows {history.ShownItems.Count} items, expected 3");
        report.Check(history.ShownItems.Count > 0 && history.ShownItems[0].Id == saved.HistoryId, "History is not newest first");
        history.SearchText = "release";
        report.Check(history.ShownItems.Count == 1 && history.ShownItems[0].Id == annotated.HistoryId, "Searching by window title did not find the capture");
        history.SearchText = "no-such-thing-%_";
        report.Check(history.ShownItems.Count == 0, "A search with no matches still shows items");
        history.SearchText = string.Empty;

        // Re-opening the annotated capture gives its annotations back as objects.
        var reopened = app.Output.OpenHistoryItem(app.History.Get(annotated.HistoryId!.Value)!, out string? openError);
        report.Check(reopened is not null && reopened.Document.Items.Count == 2, "Annotations were not restored from history: " + openError);
        report.Check(reopened?.Document.Items[0] is ArrowAnnotation, "The restored annotation has the wrong type");
        report.Check(reopened?.LibraryPath is not null && reopened.SavedPath is null, "A library capture was treated as a user file");
        var reopenedSaved = app.Output.OpenHistoryItem(app.History.Get(saved.HistoryId!.Value)!, out _);
        report.Check(reopenedSaved?.SavedPath == saved.SavedPath, "A saved capture lost its file path");

        // Keep, then clear: the kept one survives and a file the user saved is never deleted.
        app.Output.SetKeep(app.History.Get(plain.HistoryId!.Value)!, true);
        string libraryCopy = annotated.LibraryPath!;
        int cleared = app.Output.ClearHistory();
        await Settle(600);
        report.Check(cleared == 2 && app.History.Count() == 1, $"Clear removed {cleared} entries and left {app.History.Count()}");
        report.Check(!File.Exists(libraryCopy), "The internal copy of a cleared capture was left on disk");
        report.Check(!File.Exists(app.Output.ProjectPath(annotated.HistoryId!.Value)), "The project of a cleared capture was left on disk");
        report.Check(File.Exists(saved.SavedPath!), "Clearing history deleted a file the user saved");
        report.Check(history.ShownItems.Count == 1, "The history window did not refresh after clearing");

        history.ConfirmDelete = _ => true;
        history.SelectAll();
        history.DeleteSelected();
        report.Check(app.History.Count() == 0 && history.ShownItems.Count == 0, "Deleting from the history window did not remove the entry");
        SaveWindowPicture(history, Path.Combine(output, "history-empty.png"));
        history.Close();

        // --- text recognition ---
        var ocrSource = new CaptureResult(DocState.FromImage(Page(["Invoice number 48213", "Thank you for your order"])), "winword", "Invoice March");
        app.Output.EnsureInLibrary(ocrSource);
        app.Notifier.Reset();
        app.Actions.Run(ocrSource, OverlayAction.Ocr);
        TextResultWindow? ocrWindow = null;
        for (int i = 0; i < 60 && ocrWindow is null && app.Notifier.Last is null; i++)
        {
            await Task.Delay(100);
            ocrWindow = app.OpenWindows.OfType<TextResultWindow>().FirstOrDefault();
        }

        if (ocrWindow is not null)
        {
            await Settle(300);
            string flat = new(ocrWindow.CurrentText.Where(char.IsLetterOrDigit).ToArray());
            report.Check(flat.Contains("48213", StringComparison.Ordinal) && flat.Contains("Thankyou", StringComparison.OrdinalIgnoreCase), "Recognised text is wrong: " + ocrWindow.CurrentText);
            report.Check(app.History.Get(ocrSource.HistoryId!.Value)!.OcrText.Contains("48213", StringComparison.Ordinal), "Recognised text was not stored in history");
            report.Check(app.History.Query("48213").Count == 1, "History search does not find recognised text");
            SaveWindowPicture(ocrWindow, Path.Combine(output, "ocr.png"));
            ocrWindow.Close();
            report.Values["ocr"] = "ran";
        }
        else
        {
            // No recognition language installed on this machine: the user must have been told, not left guessing.
            report.Check(app.Notifier.Last?.Title == "Text recognition is unavailable", "OCR produced neither a result nor an explanation: " + app.Notifier.Last);
            report.Values["ocr"] = "unavailable: " + app.Notifier.Last?.Text;
        }

        // --- QR code ---
        var matrix = new ZXing.BarcodeWriterGeneric { Format = ZXing.BarcodeFormat.QR_CODE, Options = new ZXing.Common.EncodingOptions { Width = 260, Height = 260, Margin = 4 } }
            .Encode("https://example.com/shotit-selftest");
        var qr = new PixelBuffer(matrix.Width, matrix.Height);
        for (int y = 0; y < matrix.Height; y++)
            for (int x = 0; x < matrix.Width; x++)
                qr.SetPixel(x, y, matrix[x, y] ? ColorRgba.Black : ColorRgba.White);
        app.Actions.Run(new CaptureResult(DocState.FromImage(qr)), OverlayAction.Qr);
        TextResultWindow? qrWindow = null;
        for (int i = 0; i < 50 && qrWindow is null; i++)
        {
            await Task.Delay(100);
            qrWindow = app.OpenWindows.OfType<TextResultWindow>().FirstOrDefault();
        }

        report.Check(qrWindow?.CurrentText == "https://example.com/shotit-selftest", "QR content was not read: " + qrWindow?.CurrentText);
        if (qrWindow is not null)
        {
            await Settle(300);
            SaveWindowPicture(qrWindow, Path.Combine(output, "qr.png"));
            qrWindow.Close();
        }

        var blank = new PixelBuffer(200, 200);
        blank.Fill(ColorRgba.White);
        app.Notifier.Reset();
        app.Actions.Run(new CaptureResult(DocState.FromImage(blank)), OverlayAction.Qr);
        for (int i = 0; i < 50 && app.Notifier.Last is null; i++) await Task.Delay(100);
        report.Check(app.Notifier.Last?.Title == "No code found", "Scanning an image without a code did not say so");

        // --- upload ---
        app.Notifier.Reset();
        app.Actions.Run(ocrSource, OverlayAction.Upload);
        for (int i = 0; i < 30 && app.Notifier.Last is null; i++) await Task.Delay(100);
        report.Check(app.Notifier.Last?.Title == "Upload is not set up", "Uploading without a destination did not explain what to do");

        app.Settings.Upload.Provider = UploadProvider.LocalFolder;
        app.Settings.Upload.LocalPublicUrlBase = "https://shots.example.com";
        int before = Directory.Exists(uploads) ? Directory.GetFiles(uploads).Length : 0;
        app.Notifier.Reset();
        app.Actions.Run(ocrSource, OverlayAction.Upload);
        for (int i = 0; i < 50 && app.Notifier.Last is null; i++) await Task.Delay(100);
        report.Check(app.Notifier.Last?.Title is "Link copied" or "Uploaded", "Upload did not finish: " + app.Notifier.Last);
        report.Check(Directory.GetFiles(uploads).Length == before + 1, "The uploaded file is not in the destination folder");
        string url = app.History.Get(ocrSource.HistoryId!.Value)!.UploadUrl;
        report.Check(url.StartsWith("https://shots.example.com/", StringComparison.Ordinal), "The link was not recorded in history: " + url);
        report.Check(!url.Contains("Invoice", StringComparison.OrdinalIgnoreCase), "The uploaded name leaks the window title");

        app.Settings.Upload.LocalFolder = "C:\\Windows\\System32\\config\\<bad>|name";
        app.Notifier.Reset();
        app.Actions.Run(ocrSource, OverlayAction.Upload);
        for (int i = 0; i < 50 && app.Notifier.Last is null; i++) await Task.Delay(100);
        report.Check(app.Notifier.Last?.Title == "Upload failed", "A failed upload was not reported: " + app.Notifier.Last);

        // --- printing layout ---
        var printed = FeatureActions.BuildPrintVisual(Page(["Print"]), 816, 1056);
        var printedBounds = VisualTreeHelper.GetDescendantBounds(printed);
        report.Check(printedBounds.Width is > 600 and <= 720 && printedBounds.Left >= 48 && printedBounds.Top >= 48, "The print layout is not inside the page margins: " + printedBounds);

        // --- shell integration through the application (scratch registry) ---
        report.Check(app.Shell.GetStartupState() == StartupState.Off, "Start-up was registered before onboarding");
        report.Check(app.Shell.SetContextMenu(true) && app.Shell.IsContextMenuInstalled(), "Context menu registration failed");
        report.Check(app.Shell.SetSendTo(true) && app.Shell.IsSendToInstalled(), "Send-to registration failed");

        // --- onboarding ---
        app.Settings.General.OnboardingDone = false;
        var onboarding = WindowsHost.OpenOnboarding(app);
        await Settle(600);
        SaveWindowPicture(onboarding, Path.Combine(output, "onboarding.png"));
        onboarding.Finish();
        await Settle(200);
        report.Check(app.Settings.General.OnboardingDone, "Onboarding did not record that it finished");
        report.Check(app.Shell.GetStartupState() == StartupState.On, "Onboarding did not register start-up");
        report.Check(!app.OpenWindows.OfType<OnboardingWindow>().Any(), "Onboarding window did not close");

        // --- reset and uninstall ---
        app.Settings.Capture.DelaySeconds = 9;
        app.ResetSettings();
        report.Check(app.Settings.Capture.DelaySeconds == 3 && app.Settings.Upload.Provider == UploadProvider.None, "Reset did not restore defaults");
        report.Check(app.Settings.General.OnboardingDone, "Reset would show onboarding again");
        app.Shell.RemoveAll();
        report.Check(app.Shell.GetStartupState() == StartupState.Off && !app.Shell.IsContextMenuInstalled() && !app.Shell.IsSendToInstalled(), "Uninstall cleanup left registrations behind");

        string secret = app.Secrets.Protect("s3cret");
        report.Check(secret != "s3cret" && app.Secrets.Unprotect(secret) == "s3cret", "Secrets are not protected");
        report.Values["updates"] = app.Updates.Describe();
        report.Check(!app.Updates.IsAvailable, "A development copy claims it can update itself");
        report.Values["openWindows"] = app.OpenWindows.Select(w => w.GetType().Name).ToArray();
        app.Settings.General.ShowNotifications = false;
    }

    /// <summary>With the administrator policies set, nothing may be uploaded, recorded in history or updated.</summary>
    private static async Task Policy(AppServices app, Report report, string output)
    {
        app.Features.Confirm = (_, _, _) => true;
        report.Check(app.Policies is { DisableUpload: true, DisableHistory: true, DisableUpdates: true }, "Policies were not read: " + app.Policies);

        var image = new PixelBuffer(120, 80);
        image.Fill(new ColorRgba(0x22, 0x66, 0xCC));
        var result = new CaptureResult(DocState.FromImage(image), "notepad", "Policy test");

        // History: copying and saving still work, but leave no record.
        report.Check(app.Output.EnsureInLibrary(result) is null, "A capture entered history despite the policy");
        string? path = app.Output.Save(result, out string? error);
        report.Check(path is not null && File.Exists(path), "Saving stopped working under the history policy: " + error);
        report.Check(app.History.Count() == 0, "A saved capture was recorded in history despite the policy");
        report.Check(!Directory.Exists(app.Paths.LibraryDirectory) || Directory.GetFiles(app.Paths.LibraryDirectory, "*", SearchOption.AllDirectories).Length == 0, "An internal copy was written despite the policy");

        // Upload: blocked even with a working destination configured.
        string uploads = Path.Combine(output, "uploads");
        app.Settings.Upload.Provider = UploadProvider.LocalFolder;
        app.Settings.Upload.LocalFolder = uploads;
        foreach (var action in new[] { OverlayAction.Upload, OverlayAction.SearchImage })
        {
            app.Notifier.Reset();
            app.Actions.Run(result, action);
            for (int i = 0; i < 30 && app.Notifier.Last is null; i++) await Task.Delay(100);
            report.Check(app.Notifier.Last?.Title == "Uploads are turned off", $"{action} was not blocked by policy: {app.Notifier.Last}");
        }

        report.Check(!Directory.Exists(uploads) || Directory.GetFiles(uploads).Length == 0, "A file was uploaded despite the policy");

        var settings = WindowsHost.OpenSettings(app, "Upload");
        await Settle(600);
        SaveWindowPicture(settings, Path.Combine(output, "settings-upload-policy.png"));
        var (ok, message) = await settings.TestUploadAsync();
        report.Check(!ok && message.Contains("organisation", StringComparison.Ordinal), "The test upload button ignores the policy: " + message);
        report.Check(!Directory.Exists(uploads) || Directory.GetFiles(uploads).Length == 0, "The test upload wrote a file despite the policy");
        settings.Close();

        // Updates.
        report.Check(!app.Updates.IsAvailable && !app.Updates.AutoCheckEnabled, "Updates are still enabled under the policy");
        report.Check(app.Updates.Describe().Contains("organisation", StringComparison.Ordinal), "The About page does not explain why updates are off: " + app.Updates.Describe());
        string status = await app.Updates.CheckAndDownloadAsync(userInitiated: true);
        report.Check(status.Contains("organisation", StringComparison.Ordinal), "A manual update check ignores the policy: " + status);
    }

    private static async Task RealCapture(AppServices app, Report report)
    {
        // Timing of the real capture path; the pixels themselves are never written anywhere.
        var times = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            var watch = Stopwatch.StartNew();
            var frame = app.CaptureService.CaptureAll(includeCursor: true);
            watch.Stop();
            times.Add(watch.Elapsed.TotalMilliseconds);
            report.Check(frame.Image.Size == frame.VirtualBounds.Size, "Frame size does not match the virtual screen");
            await Task.Delay(30);
        }

        report.Values["captureMs"] = times.Select(t => Math.Round(t, 1)).ToArray();

        // Each GPU backend must agree with GDI on what the desktop looks like (allowing for a clock tick or a blinking caret).
        foreach (var preference in new[] { CaptureBackendPreference.DesktopDuplication, CaptureBackendPreference.WindowsGraphicsCapture })
        {
            app.CaptureService.Preference = CaptureBackendPreference.Gdi;
            var reference = app.CaptureService.CaptureAll(includeCursor: false);
            app.CaptureService.Preference = preference;
            var watch = Stopwatch.StartNew();
            var frame = app.CaptureService.CaptureAll(includeCursor: false);
            watch.Stop();
            string name = preference.ToString();
            report.Values[name + ".backend"] = frame.Backend;
            report.Values[name + ".ms"] = Math.Round(watch.Elapsed.TotalMilliseconds, 1);
            report.Check(frame.Image.Size == reference.Image.Size, name + " returned a frame of the wrong size");
            if (frame.Image.Size != reference.Image.Size) continue;
            long close = 0, total = 0;
            byte[] a = reference.Image.Data, b = frame.Image.Data;
            for (int i = 0; i + 2 < a.Length; i += 4 * 7)
            {
                total++;
                if (Math.Abs(a[i] - b[i]) <= 10 && Math.Abs(a[i + 1] - b[i + 1]) <= 10 && Math.Abs(a[i + 2] - b[i + 2]) <= 10) close++;
            }

            double ratio = (double)close / total;
            report.Values[name + ".matchesGdi"] = Math.Round(ratio, 4);
            report.Check(ratio > 0.97, $"{name} (via {frame.Backend}) differs from GDI: only {ratio:P1} of pixels match");
        }

        app.CaptureService.Preference = CaptureBackendPreference.Auto;
        report.Values["workingSetMb"] = Math.Round(Process.GetCurrentProcess().WorkingSet64 / 1048576.0, 1);
    }
}
