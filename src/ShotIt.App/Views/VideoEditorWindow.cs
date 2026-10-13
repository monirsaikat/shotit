using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ShotIt.App.Infrastructure;
using ShotIt.App.Services;
using ShotIt.Capture;
using ShotIt.Core.Geometry;
using ShotIt.Core.Settings;
using ShotIt.Core.Video;
using ShotIt.Rendering;

namespace ShotIt.App.Views;

/// <summary>
/// A small editor for one video: cut the start and end off, crop the picture, set what it sounds like,
/// and save the result as a new MP4 or GIF. The file that was opened is never changed.
/// </summary>
public sealed class VideoEditorWindow : Window
{
    private const string AudioFilter = "Audio|*.mp3;*.wav;*.m4a;*.aac;*.wma;*.flac|All files|*.*";

    private readonly AppServices _app;
    private readonly string _path;
    private readonly VideoInfo _info;
    private readonly MediaElement _video;
    private readonly MediaPlayer _music = new();
    private readonly CropOverlay _crop;
    private readonly Slider _position;
    private readonly TrimBar _trimBar;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly TextBlock _time, _trimText, _cropText, _audioText, _status;
    private readonly Button _play, _removeAudio, _reveal, _cancel;
    private readonly ProgressBar _progress;
    private readonly FrameworkElement _side, _exportButtons;
    private readonly Slider _audioVolumeSlider;
    private readonly CheckBox _cropBox;
    private TimeSpan _start, _end;
    private string? _audioPath;
    private double _volume = 1, _audioVolume = 1;
    private bool _playing, _updating, _cropOn;
    private CancellationTokenSource? _export;
    private string? _lastSaved;

    public VideoEditorWindow(AppServices app, string path, VideoInfo info)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _path = Path.GetFullPath(path);
        _info = info ?? throw new ArgumentNullException(nameof(info));
        _end = info.Duration;

        Title = Path.GetFileName(_path) + " – ShotIt video editor";
        Width = 1040;
        Height = 660;
        MinWidth = 760;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = AppIcon.Render(64);

        // ---- bottom: saving
        _status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 12, 0) };
        _progress = new ProgressBar { Width = 180, Height = 6, Maximum = 1, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        _reveal = Ui.Button("Show in folder", () => { if (_lastSaved is not null) CaptureActions.RevealInExplorer(_lastSaved); });
        _reveal.Visibility = Visibility.Collapsed;
        _cancel = Ui.Button("Cancel", () => _export?.Cancel());
        _cancel.Visibility = Visibility.Collapsed;
        var exportButtons = Ui.Horizontal(
            Ui.Button("Save as GIF…", () => _ = SaveAsync(RecordingFormat.Gif)),
            Ui.Button("Save as MP4…", () => _ = SaveAsync(RecordingFormat.Video), accent: true));
        _exportButtons = exportButtons;
        var bottom = new DockPanel { Margin = new Thickness(16, 8, 8, 8) };
        var right = Ui.Horizontal(_progress, _cancel, _reveal, exportButtons);
        DockPanel.SetDock(right, Dock.Right);
        bottom.Children.Add(right);
        bottom.Children.Add(_status);

        // ---- right: the edits
        var side = new StackPanel { Width = 270, Margin = new Thickness(16, 12, 16, 0) };
        _side = side;

        side.Children.Add(Ui.SubHeading("Trim"));
        _trimText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        side.Children.Add(_trimText);
        side.Children.Add(Ui.Horizontal(
            Ui.Button("Start here", () => SetTrim(_video!.Position, _end)),
            Ui.Button("End here", () => SetTrim(_start, _video!.Position))));
        side.Children.Add(Ui.Button("Keep everything", () => SetTrim(TimeSpan.Zero, _info.Duration)));
        side.Children.Add(Ui.Hint("Move to a moment in the video, then choose whether it is where the video should start or end."));

        side.Children.Add(Ui.SubHeading("Crop"));
        _cropBox = Ui.Check("Keep only part of the picture", false, on =>
        {
            if (!_updating) SetCrop(on ? _crop!.Selection : null);
        });
        side.Children.Add(_cropBox);
        _cropText = Ui.Hint(string.Empty);
        side.Children.Add(_cropText);

        side.Children.Add(Ui.SubHeading("Sound"));
        if (info.HasAudio)
        {
            side.Children.Add(new TextBlock { Text = "Sound of the video", Margin = new Thickness(0, 0, 0, 2) });
            side.Children.Add(VolumeSlider(1, v =>
            {
                _volume = v;
                _video!.Volume = v;
            }));
        }
        else
        {
            side.Children.Add(Ui.Hint("This video has no sound of its own."));
        }

        _audioText = new TextBlock { Text = "No audio added", TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 2) };
        side.Children.Add(_audioText);
        _removeAudio = Ui.Button("Remove", () => SetAudio(null));
        _removeAudio.Visibility = Visibility.Collapsed;
        side.Children.Add(Ui.Horizontal(Ui.Button("Add audio…", () => _ = BrowseAudioAsync()), _removeAudio));
        _audioVolumeSlider = VolumeSlider(1, v =>
        {
            _audioVolume = v;
            _music.Volume = v;
        });
        _audioVolumeSlider.Visibility = Visibility.Collapsed;
        side.Children.Add(_audioVolumeSlider);

        // ---- centre: the preview
        _video = new MediaElement
        {
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Close,
            ScrubbingEnabled = true,
            Stretch = Stretch.Fill,
            Width = info.Size.Width,
            Height = info.Size.Height,
            Volume = 0,
            Source = new Uri(_path),
        };
        // A player under manual control opens its file only when first told to play, so it is started
        // silently and stopped on the first picture.
        Loaded += (_, _) => _video.Play();
        _video.MediaOpened += (_, _) =>
        {
            if (_playing) return;
            _video.Pause();
            _video.Position = TimeSpan.Zero;
            _video.Volume = _volume;
        };
        _video.MediaFailed += (_, e) => _status.Text = "Windows cannot play this video here, but it can still be saved. " + e.ErrorException?.Message;
        _crop = new CropOverlay(info.Size);
        _crop.Changed += Refresh;
        var picture = new Grid { Width = info.Size.Width, Height = info.Size.Height };
        picture.Children.Add(_video);
        picture.Children.Add(_crop);
        var viewbox = new Viewbox { Stretch = Stretch.Uniform, Child = picture, Margin = new Thickness(12) };
        viewbox.SizeChanged += (_, _) => _crop.Scale = picture.ActualWidth > 0 ? viewbox.ActualWidth / info.Size.Width : 1;

        _play = Ui.Button("Play", TogglePlay);
        _play.MinWidth = 72;
        _play.Margin = new Thickness(0, 0, 10, 0);
        _time = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 110, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(10, 0, 0, 0) };
        _position = new Slider { Minimum = 0, Maximum = Math.Max(0.1, info.Duration.TotalSeconds), IsMoveToPointEnabled = true, SmallChange = 1.0 / 30, LargeChange = 1, VerticalAlignment = VerticalAlignment.Center };
        _position.ValueChanged += (_, _) =>
        {
            if (!_updating) Seek(TimeSpan.FromSeconds(_position.Value));
        };
        _trimBar = new TrimBar { Height = 5, Margin = new Thickness(5, 0, 5, 0) };
        var seek = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        seek.Children.Add(_position);
        seek.Children.Add(_trimBar);
        var transport = new DockPanel { Margin = new Thickness(16, 0, 16, 10) };
        DockPanel.SetDock(_play, Dock.Left);
        DockPanel.SetDock(_time, Dock.Right);
        transport.Children.Add(_play);
        transport.Children.Add(_time);
        transport.Children.Add(seek);

        var centre = new DockPanel();
        DockPanel.SetDock(transport, Dock.Bottom);
        centre.Children.Add(transport);
        centre.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(0x14, 0x15, 0x18)), Child = viewbox });

        var sideScroll = new ScrollViewer { Content = side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var root = new DockPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        DockPanel.SetDock(sideScroll, Dock.Right);
        root.Children.Add(bottom);
        root.Children.Add(sideScroll);
        root.Children.Add(centre);
        Content = root;

        _clock.Tick += (_, _) => OnClock();
        _music.MediaFailed += (_, _) => _status.Text = "The added audio cannot be played here, but it will be tried again when saving.";
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Space && Keyboard.FocusedElement is not TextBox && _export is null)
            {
                TogglePlay();
                e.Handled = true;
            }
        };
        Closed += (_, _) =>
        {
            _export?.Cancel();
            _clock.Stop();
            _music.Close();
            _video.Close();
        };
        Refresh();
    }

    /// <summary>The changes as they would be applied if the video were saved now.</summary>
    internal VideoEdit CurrentEdit => new VideoEdit
    {
        Start = _start,
        End = _end >= _info.Duration ? null : _end,
        Crop = _cropOn ? _crop.Selection : null,
        Volume = _volume,
        AudioPath = _audioPath,
        AudioVolume = _audioVolume,
    }.Normalize(_info);

    private static Slider VolumeSlider(double value, Action<double> changed)
    {
        var slider = new Slider { Minimum = 0, Maximum = 1, Value = value, LargeChange = 0.1, SmallChange = 0.05, IsMoveToPointEnabled = true, Margin = new Thickness(0, 2, 0, 6) };
        slider.ValueChanged += (_, _) => changed(slider.Value);
        return slider;
    }

    private static string Format(TimeSpan time) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds / 100}");

    // ------------------------------------------------------------------ the edits

    internal void SetTrim(TimeSpan start, TimeSpan end)
    {
        var edit = new VideoEdit { Start = start, End = end }.Normalize(_info);
        _start = edit.Start;
        _end = edit.End ?? _info.Duration;
        Refresh();
    }

    /// <summary>Turns cropping on with the given part of the picture, or off with null.</summary>
    internal void SetCrop(PixelRect? area)
    {
        _cropOn = area is not null;
        _updating = true;
        _cropBox.IsChecked = _cropOn;
        _updating = false;
        if (area is { } wanted) _crop.Select(wanted);
        _crop.Active = _cropOn;
        Refresh();
    }

    internal void SetAudio(string? path, TimeSpan? length = null)
    {
        _audioPath = path;
        _music.Stop();
        if (path is null)
        {
            _music.Close();
            _audioText.Text = "No audio added";
        }
        else
        {
            _music.Open(new Uri(Path.GetFullPath(path)));
            _music.Volume = _audioVolume;
            _audioText.Text = Path.GetFileName(path) + (length is { } l ? $"  ({Format(l)})" : string.Empty);
            if (_playing) SyncMusic();
        }

        _removeAudio.Visibility = _audioVolumeSlider.Visibility = path is null ? Visibility.Collapsed : Visibility.Visible;
        Refresh();
    }

    private async Task BrowseAudioAsync()
    {
        var dialog = new OpenFileDialog { Title = "Add audio", Filter = AudioFilter };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            SetAudio(dialog.FileName, await VideoEditor.AudioLengthAsync(dialog.FileName));
        }
        catch (VideoEditException ex)
        {
            _status.Text = ex.Message;
        }
    }

    private void Refresh()
    {
        var edit = CurrentEdit;
        var length = edit.OutputDuration(_info);
        _trimText.Text = _start == TimeSpan.Zero && _end >= _info.Duration
            ? $"The whole video is kept ({Format(_info.Duration)})."
            : $"From {Format(_start)} to {Format(_end)}: {Format(length)} is kept.";
        _trimBar.Set(_start / _info.Duration, _end / _info.Duration);
        var size = edit.OutputSize(_info);
        _cropText.Text = _cropOn
            ? string.Create(CultureInfo.InvariantCulture, $"Drag the frame on the picture. The video will be {size.Width} × {size.Height}.")
            : string.Create(CultureInfo.InvariantCulture, $"The whole picture is kept ({_info.Size.Width} × {_info.Size.Height}).");
        UpdateTime();
    }

    // ------------------------------------------------------------------ playing

    private void UpdateTime()
    {
        var at = _video.Position;
        _time.Text = Format(at) + " / " + Format(_info.Duration);
        _updating = true;
        _position.Value = at.TotalSeconds;
        _updating = false;
    }

    private void Seek(TimeSpan time)
    {
        _video.Position = time < TimeSpan.Zero ? TimeSpan.Zero : time > _info.Duration ? _info.Duration : time;
        if (_playing) SyncMusic();
        _time.Text = Format(_video.Position) + " / " + Format(_info.Duration);
    }

    /// <summary>The added audio starts where the kept part of the video starts.</summary>
    private void SyncMusic()
    {
        if (_audioPath is null) return;
        var at = _video.Position - _start;
        if (at < TimeSpan.Zero)
        {
            _music.Pause();
            return;
        }

        _music.Position = at;
        _music.Play();
    }

    private void TogglePlay()
    {
        if (_playing)
        {
            Pause();
            return;
        }

        // Playing always shows what will be kept.
        if (_video.Position < _start || _video.Position >= _end - TimeSpan.FromMilliseconds(50)) _video.Position = _start;
        _video.Volume = _volume;
        _video.Play();
        _playing = true;
        SyncMusic();
        _clock.Start();
        _play.Content = "Pause";
    }

    private void Pause()
    {
        _playing = false;
        _clock.Stop();
        _video.Pause();
        _music.Pause();
        _play.Content = "Play";
        UpdateTime();
    }

    private void OnClock()
    {
        if (_video.Position >= _end)
        {
            Pause();
            _video.Position = _start;
        }

        UpdateTime();
    }

    // ------------------------------------------------------------------ saving

    private async Task SaveAsync(RecordingFormat format)
    {
        if (_export is not null) return;
        string extension = format == RecordingFormat.Gif ? ".gif" : ".mp4";
        string folder = Path.GetDirectoryName(_path) is { Length: > 0 } beside && Directory.Exists(beside) ? beside : _app.Output.SaveFolder;
        var dialog = new SaveFileDialog
        {
            Title = format == RecordingFormat.Gif ? "Save as GIF" : "Save as MP4",
            Filter = format == RecordingFormat.Gif ? "Animated GIF|*.gif" : "MP4 video|*.mp4",
            InitialDirectory = folder,
            FileName = Path.GetFileNameWithoutExtension(_path) + (format == RecordingFormat.Gif && !_path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ? string.Empty : " (edited)") + extension,
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        await ExportAsync(dialog.FileName, format);
    }

    /// <summary>Writes the edited video. Returns true when the file was saved.</summary>
    internal async Task<bool> ExportAsync(string destination, RecordingFormat format)
    {
        if (_export is not null) return false;
        if (_playing) Pause();
        var edit = CurrentEdit;
        using var cancel = new CancellationTokenSource();
        _export = cancel;
        _side.IsEnabled = _exportButtons.IsEnabled = _play.IsEnabled = _position.IsEnabled = false;
        _crop.IsHitTestVisible = false;
        _reveal.Visibility = Visibility.Collapsed;
        _cancel.Visibility = _progress.Visibility = Visibility.Visible;
        _progress.Value = 0;
        _status.Text = "Saving…";
        var progress = new Progress<double>(value => _progress.Value = value);
        try
        {
            if (format == RecordingFormat.Gif)
                await VideoEditor.ExportGifAsync(_path, edit, destination, _app.Settings.Recording.GifFramesPerSecond, progress, cancel.Token);
            else
                await VideoEditor.ExportMp4Async(_path, edit, destination, progress, cancel.Token);

            _lastSaved = destination;
            bool copied = ClipboardService.SetFile(destination);
            _status.Text = "Saved " + Path.GetFileName(destination) + (copied ? ". The file is on the clipboard." : ".");
            _reveal.Visibility = Visibility.Visible;
            _app.Log.Information("Video saved: {Format}, {Size}, {Seconds:0.0} s", format, edit.OutputSize(_info), edit.OutputDuration(_info).TotalSeconds);
            return true;
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Saving was cancelled.";
            return false;
        }
        catch (VideoEditException ex)
        {
            _app.Log.Warning(ex, "Saving the edited video failed");
            _status.Text = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            _app.Log.Error(ex, "Saving the edited video failed");
            _status.Text = "The video could not be saved. Details are in the log file.";
            return false;
        }
        finally
        {
            _export = null;
            _side.IsEnabled = _exportButtons.IsEnabled = _play.IsEnabled = _position.IsEnabled = true;
            _crop.IsHitTestVisible = true;
            _cancel.Visibility = _progress.Visibility = Visibility.Collapsed;
        }
    }
}

/// <summary>A thin bar under the seek slider showing which part of the video is kept.</summary>
internal sealed class TrimBar : FrameworkElement
{
    private static readonly Brush Track = Frozen(Color.FromArgb(0x50, 0x80, 0x80, 0x80));
    private double _from, _to = 1;

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public void Set(double from, double to)
    {
        _from = Math.Clamp(from, 0, 1);
        _to = Math.Clamp(to, _from, 1);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        double width = ActualWidth, height = ActualHeight;
        drawingContext.DrawRoundedRectangle(Track, null, new Rect(0, 0, width, height), 2, 2);
        var kept = Application.Current.TryFindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue;
        drawingContext.DrawRoundedRectangle(kept, null, new Rect(_from * width, 0, Math.Max(2, (_to - _from) * width), height), 2, 2);
    }
}

/// <summary>
/// The crop frame drawn over the video. It works in the video's own pixels, so what is selected is
/// exactly what is kept, whatever size the preview is shown at.
/// </summary>
internal sealed class CropOverlay : FrameworkElement
{
    [Flags]
    private enum Edge
    {
        None = 0,
        Left = 1,
        Top = 2,
        Right = 4,
        Bottom = 8,
        Move = 16,
        New = 32,
    }

    private static readonly Brush Shade = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0));
    private readonly PixelSize _frame;
    private bool _active;
    private double _scale = 1;
    private Edge _drag;
    private Point _dragFrom;
    private Rect _dragRect;

    public CropOverlay(PixelSize frame)
    {
        _frame = frame;
        // Start with a frame inset a little, so it is obvious that it can be dragged.
        int inset = Math.Min(frame.Width, frame.Height) / 10;
        Selection = new PixelRect(inset, inset, frame.Width - inset * 2, frame.Height - inset * 2);
        IsHitTestVisible = false;
    }

    public PixelRect Selection { get; private set; }

    public event Action? Changed;

    public bool Active
    {
        get => _active;
        set
        {
            _active = value;
            IsHitTestVisible = value;
            InvalidateVisual();
        }
    }

    /// <summary>How many screen units one video pixel takes, so the handles stay the same size on screen.</summary>
    public double Scale
    {
        get => _scale;
        set
        {
            _scale = value > 0 ? value : 1;
            InvalidateVisual();
        }
    }

    public void Select(PixelRect area)
    {
        var picture = new PixelRect(0, 0, _frame.Width, _frame.Height);
        var kept = area.Intersect(picture);
        Selection = kept.Width >= VideoEdit.MinSide && kept.Height >= VideoEdit.MinSide ? kept : Selection;
        InvalidateVisual();
    }

    private double Handle => 9 / _scale;

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (!_active) return;
        var all = new Rect(0, 0, _frame.Width, _frame.Height);
        var area = new Rect(Selection.X, Selection.Y, Selection.Width, Selection.Height);
        // A transparent fill makes the whole picture respond to the mouse, not just the painted parts.
        drawingContext.DrawRectangle(Brushes.Transparent, null, all);
        drawingContext.DrawGeometry(Shade, null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(all), new RectangleGeometry(area)));
        drawingContext.DrawRectangle(null, new Pen(Brushes.White, 1.5 / _scale), area);

        double h = Handle;
        var outline = new Pen(Brushes.Black, 1 / _scale);
        foreach (double x in new[] { area.Left, area.Left + area.Width / 2, area.Right })
        {
            foreach (double y in new[] { area.Top, area.Top + area.Height / 2, area.Bottom })
            {
                if (x == area.Left + area.Width / 2 && y == area.Top + area.Height / 2) continue;
                drawingContext.DrawRectangle(Brushes.White, outline, new Rect(x - h / 2, y - h / 2, h, h));
            }
        }
    }

    private Edge Hit(Point p)
    {
        double reach = Handle;
        var s = Selection;
        bool nearX = p.X >= s.Left - reach && p.X <= s.Right + reach, nearY = p.Y >= s.Top - reach && p.Y <= s.Bottom + reach;
        var edge = Edge.None;
        if (nearY && Math.Abs(p.X - s.Left) <= reach) edge |= Edge.Left;
        else if (nearY && Math.Abs(p.X - s.Right) <= reach) edge |= Edge.Right;
        if (nearX && Math.Abs(p.Y - s.Top) <= reach) edge |= Edge.Top;
        else if (nearX && Math.Abs(p.Y - s.Bottom) <= reach) edge |= Edge.Bottom;
        if (edge != Edge.None) return edge;
        return p.X > s.Left && p.X < s.Right && p.Y > s.Top && p.Y < s.Bottom ? Edge.Move : Edge.New;
    }

    private static Cursor CursorFor(Edge edge) => edge switch
    {
        Edge.Left or Edge.Right => Cursors.SizeWE,
        Edge.Top or Edge.Bottom => Cursors.SizeNS,
        Edge.Left | Edge.Top or Edge.Right | Edge.Bottom => Cursors.SizeNWSE,
        Edge.Right | Edge.Top or Edge.Left | Edge.Bottom => Cursors.SizeNESW,
        Edge.Move => Cursors.SizeAll,
        _ => Cursors.Cross,
    };

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        _dragFrom = e.GetPosition(this);
        _drag = Hit(_dragFrom);
        _dragRect = new Rect(Selection.X, Selection.Y, Selection.Width, Selection.Height);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var p = e.GetPosition(this);
        if (_drag == Edge.None)
        {
            Cursor = CursorFor(Hit(p));
            return;
        }

        double left = _dragRect.Left, top = _dragRect.Top, right = _dragRect.Right, bottom = _dragRect.Bottom;
        double dx = p.X - _dragFrom.X, dy = p.Y - _dragFrom.Y;
        const double Min = VideoEdit.MinSide;
        if (_drag == Edge.New)
        {
            left = Math.Min(_dragFrom.X, p.X);
            right = Math.Max(_dragFrom.X, p.X);
            top = Math.Min(_dragFrom.Y, p.Y);
            bottom = Math.Max(_dragFrom.Y, p.Y);
            if (right - left < Min || bottom - top < Min) return;
        }
        else if (_drag == Edge.Move)
        {
            dx = Math.Clamp(dx, -left, _frame.Width - right);
            dy = Math.Clamp(dy, -top, _frame.Height - bottom);
            left += dx;
            right += dx;
            top += dy;
            bottom += dy;
        }
        else
        {
            if (_drag.HasFlag(Edge.Left)) left = Math.Min(left + dx, right - Min);
            if (_drag.HasFlag(Edge.Right)) right = Math.Max(right + dx, left + Min);
            if (_drag.HasFlag(Edge.Top)) top = Math.Min(top + dy, bottom - Min);
            if (_drag.HasFlag(Edge.Bottom)) bottom = Math.Max(bottom + dy, top + Min);
        }

        left = Math.Clamp(left, 0, _frame.Width - Min);
        top = Math.Clamp(top, 0, _frame.Height - Min);
        right = Math.Clamp(right, left + Min, _frame.Width);
        bottom = Math.Clamp(bottom, top + Min, _frame.Height);
        Selection = PixelRect.FromLTRB((int)Math.Round(left), (int)Math.Round(top), (int)Math.Round(right), (int)Math.Round(bottom));
        InvalidateVisual();
        Changed?.Invoke();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        _drag = Edge.None;
        ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _drag = Edge.None;
        base.OnLostMouseCapture(e);
    }
}
