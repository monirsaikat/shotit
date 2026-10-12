using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ShotIt.Core.Settings;

public enum SettingsLoadOutcome
{
    Loaded,
    CreatedDefaults,
    Migrated,
    RecoveredFromCorruption,
}

public sealed record SettingsLoadResult(AppSettings Settings, SettingsLoadOutcome Outcome, string? Detail = null);

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON. Writes are atomic (temp file then replace),
/// the previous three versions are kept as backups, and a corrupt file never loses the user's data:
/// it is set aside and defaults are used.
/// </summary>
public sealed class SettingsStore
{
    private const int BackupCount = 3;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock _gate = new();

    public SettingsStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
    }

    public string FilePath { get; }

    public SettingsLoadResult Load()
    {
        lock (_gate)
        {
            if (!File.Exists(FilePath))
                return new SettingsLoadResult(new AppSettings(), SettingsLoadOutcome.CreatedDefaults);

            try
            {
                string json = File.ReadAllText(FilePath);
                var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                }) as JsonObject ?? throw new JsonException("Settings root is not an object.");

                int version = node["schemaVersion"]?.GetValue<int>() ?? 1;
                bool migrated = Migrate(node, version);
                var settings = node.Deserialize<AppSettings>(JsonOptions) ?? throw new JsonException("Settings deserialized to null.");
                settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
                Normalize(settings);
                return new SettingsLoadResult(settings, migrated ? SettingsLoadOutcome.Migrated : SettingsLoadOutcome.Loaded);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or NotSupportedException)
            {
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                string aside = FilePath + ".corrupt-" + stamp;
                try { File.Copy(FilePath, aside, overwrite: true); }
                catch (IOException) { /* keeping a copy is best effort */ }
                catch (UnauthorizedAccessException) { }
                return new SettingsLoadResult(new AppSettings(), SettingsLoadOutcome.RecoveredFromCorruption, aside);
            }
        }
    }

    /// <summary>Upgrades older settings documents in place. Returns true when anything changed.</summary>
    internal static bool Migrate(JsonObject root, int fromVersion)
    {
        bool changed = false;
        if (fromVersion < 2)
        {
            // v1 kept a single "hotkey" string at the root and "saveFolder" outside the output section.
            if (root["hotkey"] is JsonValue hk)
            {
                var hotkeys = root["hotkeys"] as JsonObject ?? [];
                hotkeys["region"] ??= hk.GetValue<string>();
                root["hotkeys"] = hotkeys;
                root.Remove("hotkey");
            }

            if (root["saveFolder"] is JsonValue sf)
            {
                var output = root["output"] as JsonObject ?? [];
                output["saveFolder"] ??= sf.GetValue<string>();
                root["output"] = output;
                root.Remove("saveFolder");
            }

            changed = true;
        }

        root["schemaVersion"] = AppSettings.CurrentSchemaVersion;
        return changed;
    }

    /// <summary>Repairs out-of-range values so the rest of the app never has to validate them.</summary>
    internal static void Normalize(AppSettings s)
    {
        s.General ??= new();
        s.Hotkeys ??= new();
        s.Capture ??= new();
        s.Output ??= new();
        s.Editor ??= new();
        s.History ??= new();
        s.Upload ??= new();
        s.Updates ??= new();
        s.Privacy ??= new();
        s.Shell ??= new();
        s.Editor.Tool ??= new();
        s.Editor.RecentColors ??= [];
        s.History.ExcludedTitlePatterns ??= [];

        s.Capture.DelaySeconds = Math.Clamp(s.Capture.DelaySeconds, 1, 30);
        s.Capture.FixedWidth = Math.Clamp(s.Capture.FixedWidth, 16, 16384);
        s.Capture.FixedHeight = Math.Clamp(s.Capture.FixedHeight, 16, 16384);
        if (s.Capture.LastRegion is { Length: not 4 }) s.Capture.LastRegion = null;
        s.Output.JpegQuality = Math.Clamp(s.Output.JpegQuality, 10, 100);
        if (string.IsNullOrWhiteSpace(s.Output.FileNameTemplate)) s.Output.FileNameTemplate = new OutputSettings().FileNameTemplate;
        s.History.MaxAgeDays = Math.Clamp(s.History.MaxAgeDays, 0, 3650);
        s.History.MaxSizeMb = Math.Clamp(s.History.MaxSizeMb, 0, 1024 * 1024);
        if (s.Editor.RecentColors.Count > 12) s.Editor.RecentColors.RemoveRange(12, s.Editor.RecentColors.Count - 12);
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(FilePath))!;
            Directory.CreateDirectory(dir);
            settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);

            if (File.Exists(FilePath))
            {
                RotateBackups();
                File.Replace(temp, FilePath, FilePath + ".bak1", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, FilePath);
            }
        }
    }

    private void RotateBackups()
    {
        for (int i = BackupCount - 1; i >= 1; i--)
        {
            string from = FilePath + ".bak" + i.ToString(CultureInfo.InvariantCulture);
            string to = FilePath + ".bak" + (i + 1).ToString(CultureInfo.InvariantCulture);
            if (!File.Exists(from)) continue;
            try { File.Move(from, to, overwrite: true); }
            catch (IOException) { /* backups are best effort */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
