using ShotIt.Core.Geometry;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ShotIt.Native;

public sealed record MenuEntry(int Id, string Text)
{
    public static readonly MenuEntry Separator = new(0, string.Empty) { IsSeparator = true };

    public bool IsSeparator { get; init; }
    public bool Checked { get; init; }
    public bool Enabled { get; init; } = true;
    public bool Default { get; init; }
    public IReadOnlyList<MenuEntry>? Children { get; init; }
}

/// <summary>A native popup menu. Native menus are instant, keyboard accessible and behave correctly from a tray icon.</summary>
public static unsafe class PopupMenu
{
    /// <summary>Shows the menu and blocks until it closes. Returns the chosen id, or 0 when dismissed.</summary>
    public static int Show(nint ownerWindow, IReadOnlyList<MenuEntry> entries, PixelPoint at)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var owner = (HWND)ownerWindow;
        HMENU menu = Build(entries);
        if (menu.IsNull) return 0;
        try
        {
            // Required so the menu closes when the user clicks elsewhere (documented tray-menu quirk).
            PInvoke.SetForegroundWindow(owner);
            const uint ReturnCommand = 0x0100, RightButton = 0x0002, NoNotify = 0x0080;
            int chosen = PInvoke.TrackPopupMenuEx(menu, ReturnCommand | RightButton | NoNotify, at.X, at.Y, owner, (TPMPARAMS*)null);
            PInvoke.PostMessage(owner, PInvoke.WM_NULL, default, default);
            return chosen;
        }
        finally
        {
            PInvoke.DestroyMenu(menu); // destroys submenus too
        }
    }

    private static HMENU Build(IReadOnlyList<MenuEntry> entries)
    {
        HMENU menu = PInvoke.CreatePopupMenu();
        if (menu.IsNull) return menu;
        foreach (var e in entries)
        {
            if (e.IsSeparator)
            {
                PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, (string?)null);
                continue;
            }

            var flags = MENU_ITEM_FLAGS.MF_STRING;
            if (e.Checked) flags |= MENU_ITEM_FLAGS.MF_CHECKED;
            if (!e.Enabled) flags |= MENU_ITEM_FLAGS.MF_GRAYED;
            if (e.Default) flags |= MENU_ITEM_FLAGS.MF_DEFAULT;

            if (e.Children is { Count: > 0 })
            {
                HMENU sub = Build(e.Children);
                PInvoke.AppendMenu(menu, flags | MENU_ITEM_FLAGS.MF_POPUP, (nuint)(nint)sub.Value, e.Text);
            }
            else
            {
                PInvoke.AppendMenu(menu, flags, (nuint)e.Id, e.Text);
            }
        }

        return menu;
    }
}

public enum TrayEvent
{
    None,
    LeftClick,
    DoubleClick,
    ContextMenu,
    BalloonClick,
}

/// <summary>
/// A notification-area icon. The owner window forwards messages to <see cref="TranslateMessage"/>
/// and calls <see cref="Recreate"/> when Explorer restarts.
/// </summary>
public sealed unsafe class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private readonly nint _window;
    private readonly int _callbackMessage;
    private HICON _icon;
    private string _tooltip;
    private bool _added;
    private bool _disposed;

    /// <param name="window">Window that receives the callback message.</param>
    /// <param name="callbackMessage">A private message number (WM_APP + n).</param>
    /// <param name="iconPng">A square PNG; Windows scales it to the tray size.</param>
    /// <param name="tooltip">Hover text, at most 127 characters.</param>
    public TrayIcon(nint window, int callbackMessage, ReadOnlySpan<byte> iconPng, string tooltip)
    {
        _window = window;
        _callbackMessage = callbackMessage;
        _tooltip = tooltip ?? string.Empty;
        _icon = CreateIcon(iconPng);
        Add();
    }

    public static nint CreateIconHandle(ReadOnlySpan<byte> png) => (nint)CreateIcon(png).Value;

    private static HICON CreateIcon(ReadOnlySpan<byte> png)
    {
        if (png.IsEmpty) return default;
        const uint IconVersion = 0x00030000;
        return PInvoke.CreateIconFromResourceEx(png, true, IconVersion, 0, 0, IMAGE_FLAGS.LR_DEFAULTCOLOR);
    }

    private NOTIFYICONDATAW Data(NOTIFY_ICON_DATA_FLAGS flags)
    {
        NOTIFYICONDATAW data = default;
        data.cbSize = (uint)sizeof(NOTIFYICONDATAW);
        data.hWnd = (HWND)_window;
        data.uID = IconId;
        data.uFlags = flags;
        data.uCallbackMessage = (uint)_callbackMessage;
        data.hIcon = _icon;
        data.szTip = Truncate(_tooltip, 127);
        return data;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    private void Add()
    {
        var data = Data(NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP);
        _added = PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, in data);
        if (!_added)
        {
            // A stale icon with our id may exist after a crash; replace it.
            PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, in data);
            _added = PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, in data);
        }

        const uint Version4 = 4;
        data.uVersion = Version4;
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_SETVERSION, in data);
    }

    /// <summary>Adds the icon again after Explorer restarted (TaskbarCreated).</summary>
    public void Recreate()
    {
        if (_disposed) return;
        Add();
    }

    public bool IsVisible => _added && !_disposed;

    public void SetTooltip(string tooltip)
    {
        if (_disposed) return;
        _tooltip = tooltip ?? string.Empty;
        var data = Data(NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP);
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, in data);
    }

    /// <summary>Shows a notification balloon (rendered as a toast on Windows 10 and 11).</summary>
    public void ShowBalloon(string title, string text, bool warning = false)
    {
        if (_disposed) return;
        var data = Data(NOTIFY_ICON_DATA_FLAGS.NIF_INFO);
        data.szInfoTitle = Truncate(title ?? string.Empty, 63);
        data.szInfo = Truncate(text ?? string.Empty, 255);
        data.dwInfoFlags = warning ? NOTIFY_ICON_INFOTIP_FLAGS.NIIF_WARNING : NOTIFY_ICON_INFOTIP_FLAGS.NIIF_INFO;
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, in data);
    }

    /// <summary>Decodes the tray callback message. <paramref name="anchor"/> is where a menu should open.</summary>
    public static TrayEvent TranslateMessage(nint wParam, nint lParam, out PixelPoint anchor)
    {
        // With NOTIFYICON_VERSION_4 the event is in the low word of lParam and the position in wParam.
        anchor = new PixelPoint((short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF));
        uint code = (uint)(lParam & 0xFFFF);
        return code switch
        {
            PInvoke.WM_CONTEXTMENU => TrayEvent.ContextMenu,
            PInvoke.NIN_SELECT => TrayEvent.LeftClick,
            PInvoke.WM_LBUTTONDBLCLK => TrayEvent.DoubleClick,
            PInvoke.NIN_BALLOONUSERCLICK => TrayEvent.BalloonClick,
            _ => TrayEvent.None,
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var data = Data(0);
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, in data);
        if (!_icon.IsNull)
        {
            PInvoke.DestroyIcon(_icon);
            _icon = default;
        }

        GC.SuppressFinalize(this);
    }

    ~TrayIcon()
    {
        // Leaving a dead icon in the tray until the user hovers over it looks broken; remove it.
        if (_disposed) return;
        var data = Data(0);
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, in data);
    }
}
