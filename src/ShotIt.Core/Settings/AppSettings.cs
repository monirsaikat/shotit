using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ShotIt.Core.Editing;

namespace ShotIt.Core.Settings;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>A global hotkey: modifier flags plus a Windows virtual-key code.</summary>
[JsonConverter(typeof(HotkeyJsonConverter))]
public readonly record struct Hotkey(HotkeyModifiers Modifiers, int VirtualKey)
{
    public static readonly Hotkey None;

    public const int VkPrintScreen = 0x2C;

    public bool IsEmpty => VirtualKey == 0;

    private static readonly Dictionary<int, string> KeyNames = new()
    {
        [0x2C] = "PrintScreen", [0x20] = "Space", [0x0D] = "Enter", [0x09] = "Tab", [0x1B] = "Esc",
        [0x2D] = "Insert", [0x2E] = "Delete", [0x24] = "Home", [0x23] = "End", [0x21] = "PageUp", [0x22] = "PageDown",
        [0x25] = "Left", [0x26] = "Up", [0x27] = "Right", [0x28] = "Down", [0x13] = "Pause", [0x91] = "ScrollLock",
        [0xC0] = "`", [0xBD] = "-", [0xBB] = "=", [0xDB] = "[", [0xDD] = "]", [0xDC] = "\\", [0xBA] = ";",
        [0xDE] = "'", [0xBC] = ",", [0xBE] = ".", [0xBF] = "/",
    };

    public static string KeyName(int vk)
    {
        if (KeyNames.TryGetValue(vk, out var name)) return name;
        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F).ToString(CultureInfo.InvariantCulture);
        if (vk is >= 0x60 and <= 0x69) return "Num" + (vk - 0x60).ToString(CultureInfo.InvariantCulture);
        return "0x" + vk.ToString("X2", CultureInfo.InvariantCulture);
    }

    public override string ToString()
    {
        if (IsEmpty) return string.Empty;
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(KeyName(VirtualKey));
        return string.Join('+', parts);
    }

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = None;
        if (string.IsNullOrWhiteSpace(text)) return true;
        var mods = HotkeyModifiers.None;
        int vk = 0;
        // "Ctrl++" style input is not supported; the "+" key is written as "=".
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL": mods |= HotkeyModifiers.Ctrl; continue;
                case "ALT": mods |= HotkeyModifiers.Alt; continue;
                case "SHIFT": mods |= HotkeyModifiers.Shift; continue;
                case "WIN" or "WINDOWS": mods |= HotkeyModifiers.Win; continue;
            }

            if (vk != 0) return false;
            vk = ParseKey(raw);
            if (vk == 0) return false;
        }

        if (vk == 0) return false;
        hotkey = new Hotkey(mods, vk);
        return true;
    }

    private static int ParseKey(string name)
    {
        foreach (var (code, text) in KeyNames)
            if (string.Equals(text, name, StringComparison.OrdinalIgnoreCase)) return code;
        if (string.Equals(name, "PrtScr", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "PrtSc", StringComparison.OrdinalIgnoreCase)) return VkPrintScreen;
        if (name.Length == 1)
        {
            char c = char.ToUpperInvariant(name[0]);
            if (c is >= '0' and <= '9' or >= 'A' and <= 'Z') return c;
        }
        if (name.Length is 2 or 3 && (name[0] is 'F' or 'f') &&
            int.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int f) && f is >= 1 and <= 24)
            return 0x6F + f;
        if (name.StartsWith("Num", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(name.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n is >= 0 and <= 9)
            return 0x60 + n;
        if (name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(name.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int raw) && raw is > 0 and < 256)
            return raw;
        return 0;
    }
}

public sealed class HotkeyJsonConverter : JsonConverter<Hotkey>
{
    public override Hotkey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Hotkey.TryParse(reader.GetString(), out var h) ? h : Hotkey.None;

    public override void Write(Utf8JsonWriter writer, Hotkey value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public enum AfterCaptureAction
{
    ShowToolbar,
    Copy,
    Save,
    OpenEditor,
    Upload,
    Pin,
}

public enum ImageFormat
{
    Png,
    Jpeg,
    Bmp,
    Pdf,
}

public enum HistoryMode
{
    All,
    SavedOnly,
    Off,
}

public enum UploadProvider
{
    None,
    Imgur,
    S3,
    Ftp,
    Sftp,
    CustomHttp,
    LocalFolder,
}

public enum CaptureBackendPreference
{
    Auto,
    Gdi,
    WindowsGraphicsCapture,
    DesktopDuplication,
}

public sealed class GeneralSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;
    public bool StartWithWindows { get; set; }
    public bool PlaySound { get; set; }
    public bool ShowNotifications { get; set; } = true;
    public bool OnboardingDone { get; set; }

    /// <summary>Bring pinned images back where they were when ShotIt starts again.</summary>
    public bool RestorePins { get; set; }

    /// <summary>Language tag for text recognition; empty means "use my Windows languages".</summary>
    public string OcrLanguage { get; set; } = string.Empty;
}

public sealed class HotkeySettings
{
    public Hotkey Region { get; set; } = new(HotkeyModifiers.None, Hotkey.VkPrintScreen);
    public Hotkey FullScreen { get; set; } = new(HotkeyModifiers.Ctrl, Hotkey.VkPrintScreen);
    public Hotkey ActiveWindow { get; set; } = new(HotkeyModifiers.Alt, Hotkey.VkPrintScreen);
    public Hotkey Window { get; set; } = new(HotkeyModifiers.Shift, Hotkey.VkPrintScreen);
    public Hotkey LastRegion { get; set; } = new(HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, Hotkey.VkPrintScreen);
    public Hotkey Freeform { get; set; } = Hotkey.None;
    public Hotkey DelayedRegion { get; set; } = Hotkey.None;
    public Hotkey TogglePins { get; set; } = new(HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, 'P');
    public Hotkey OpenHistory { get; set; } = Hotkey.None;
    public Hotkey PinClipboard { get; set; } = Hotkey.None;
}

public sealed class CaptureSettings
{
    public bool IncludeCursor { get; set; }
    public int DelaySeconds { get; set; } = 3;
    public bool SnapToWindows { get; set; } = true;
    public bool ShowMagnifier { get; set; } = true;
    public bool WindowShadowPadding { get; set; }
    public CaptureBackendPreference Backend { get; set; } = CaptureBackendPreference.Auto;
    public AfterCaptureAction AfterRegion { get; set; } = AfterCaptureAction.ShowToolbar;
    public AfterCaptureAction AfterFullScreen { get; set; } = AfterCaptureAction.Copy;
    public AfterCaptureAction EnterAction { get; set; } = AfterCaptureAction.Copy;
    public int FixedWidth { get; set; } = 1280;
    public int FixedHeight { get; set; } = 720;
    public int[]? LastRegion { get; set; }
}

public sealed class OutputSettings
{
    public string SaveFolder { get; set; } = string.Empty;
    public string FileNameTemplate { get; set; } = "ShotIt {date:yyyy-MM-dd} {time:HH-mm-ss}";
    public ImageFormat Format { get; set; } = ImageFormat.Png;
    public int JpegQuality { get; set; } = 90;
    public bool AutoSaveEveryCapture { get; set; }
    public bool CopyFileAlongsideImage { get; set; }
    public bool AskWhereToSave { get; set; }
    public string LastSaveAsFolder { get; set; } = string.Empty;
}

public sealed class EditorSettings
{
    public ToolOptions Tool { get; set; } = new();
    public List<string> RecentColors { get; set; } = [];
    public string WatermarkText { get; set; } = string.Empty;
    public bool SmartGuides { get; set; } = true;
}

public sealed class HistorySettings
{
    public HistoryMode Mode { get; set; } = HistoryMode.All;
    public int MaxAgeDays { get; set; } = 90;
    public int MaxSizeMb { get; set; } = 2048;
    public List<string> ExcludedTitlePatterns { get; set; } = ["*KeePass*", "*1Password*", "*Bitwarden*", "*LastPass*"];
}

public sealed class UploadSettings
{
    public UploadProvider Provider { get; set; } = UploadProvider.None;
    public bool ConfirmBeforeUpload { get; set; } = true;
    public bool CopyUrlAfterUpload { get; set; } = true;
    public bool OpenUrlAfterUpload { get; set; }

    // Secrets below are stored encrypted (DPAPI) as base64; see ISecretProtector.
    public string ImgurClientId { get; set; } = string.Empty;

    public string S3Endpoint { get; set; } = string.Empty;
    public string S3Region { get; set; } = "us-east-1";
    public string S3Bucket { get; set; } = string.Empty;
    public string S3AccessKey { get; set; } = string.Empty;
    public string S3SecretKeyProtected { get; set; } = string.Empty;
    public string S3PathPrefix { get; set; } = string.Empty;
    public string S3PublicUrlBase { get; set; } = string.Empty;
    public bool S3PathStyle { get; set; } = true;

    public string FtpHost { get; set; } = string.Empty;
    public int FtpPort { get; set; }
    public string FtpUser { get; set; } = string.Empty;
    public string FtpPasswordProtected { get; set; } = string.Empty;
    public string FtpRemoteFolder { get; set; } = "/";
    public string FtpPublicUrlBase { get; set; } = string.Empty;

    public string CustomUrl { get; set; } = string.Empty;
    public string CustomFileField { get; set; } = "file";
    public string CustomHeaders { get; set; } = string.Empty;
    public string CustomResponseUrlPath { get; set; } = "url";
    public string CustomResponseDeletePath { get; set; } = string.Empty;

    public string LocalFolder { get; set; } = string.Empty;
    public string LocalPublicUrlBase { get; set; } = string.Empty;
}

public sealed class UpdateSettings
{
    public bool AutoCheck { get; set; } = true;
    public string Channel { get; set; } = "stable";
    public string FeedUrl { get; set; } = string.Empty;
}

public sealed class PrivacySettings
{
    public bool SendCrashReports { get; set; }
}

public sealed class ShellSettings
{
    public bool ContextMenu { get; set; }
    public bool SendTo { get; set; }
}

/// <summary>Everything the user can configure. Persisted as versioned JSON by <see cref="SettingsStore"/>.</summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public GeneralSettings General { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public CaptureSettings Capture { get; set; } = new();
    public OutputSettings Output { get; set; } = new();
    public EditorSettings Editor { get; set; } = new();
    public HistorySettings History { get; set; } = new();
    public UploadSettings Upload { get; set; } = new();
    public UpdateSettings Updates { get; set; } = new();
    public PrivacySettings Privacy { get; set; } = new();
    public ShellSettings Shell { get; set; } = new();
}
