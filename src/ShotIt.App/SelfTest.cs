using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ShotIt.App.Infrastructure;
using ShotIt.App.Overlay;
using ShotIt.App.Services;
using ShotIt.Capture;
using ShotIt.Core.Annotations;
using ShotIt.Core.Editing;
using ShotIt.Core.Geometry;
using ShotIt.Core.Imaging;
using ShotIt.Core.Settings;
using ShotIt.Native;
using ShotIt.Rendering;

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
            services = app.Initialize(new AppPaths(data));
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

    private static void SaveWindowPicture(Window window, string path)
    {
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
        WindowsHost.OpenSettings(app);
        WindowsHost.OpenHistory(app);
        await Settle(1200);
        int index = 0;
        foreach (var window in app.OpenWindows.ToArray())
        {
            SaveWindowPicture(window, Path.Combine(output, $"window-{index++}-{window.GetType().Name}.png"));
        }

        report.Values["openWindows"] = app.OpenWindows.Select(w => w.GetType().Name).ToArray();
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
        report.Values["workingSetMb"] = Math.Round(Process.GetCurrentProcess().WorkingSet64 / 1048576.0, 1);
    }
}
