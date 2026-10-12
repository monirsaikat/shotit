using ShotIt.Core.History;
using ShotIt.Core.Naming;
using ShotIt.Core.Settings;

namespace ShotIt.Core.Tests;

public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shotit-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public class HotkeyTests
{
    [Theory]
    [InlineData("PrintScreen", HotkeyModifiers.None, 0x2C)]
    [InlineData("Ctrl+Shift+PrintScreen", HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, 0x2C)]
    [InlineData("ctrl + alt + s", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 'S')]
    [InlineData("Win+Shift+F12", HotkeyModifiers.Win | HotkeyModifiers.Shift, 0x7B)]
    [InlineData("Alt+Num5", HotkeyModifiers.Alt, 0x65)]
    [InlineData("Ctrl+0x90", HotkeyModifiers.Ctrl, 0x90)]
    [InlineData("PrtScr", HotkeyModifiers.None, 0x2C)]
    public void Parses(string text, HotkeyModifiers mods, int vk)
    {
        Assert.True(Hotkey.TryParse(text, out var h));
        Assert.Equal(new Hotkey(mods, vk), h);
    }

    [Theory]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Shift")]
    [InlineData("A+B")]
    [InlineData("Ctrl+Banana")]
    [InlineData("F25")]
    public void Rejects(string text) => Assert.False(Hotkey.TryParse(text, out _));

    [Fact]
    public void Empty_text_is_the_empty_hotkey()
    {
        Assert.True(Hotkey.TryParse("", out var h));
        Assert.True(h.IsEmpty);
        Assert.Equal(string.Empty, h.ToString());
    }

    [Fact]
    public void Every_key_round_trips_through_text()
    {
        for (int vk = 1; vk < 256; vk++)
        {
            var h = new Hotkey(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, vk);
            Assert.True(Hotkey.TryParse(h.ToString(), out var back), h.ToString());
            Assert.Equal(h, back);
        }
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void Missing_file_gives_defaults()
    {
        using var dir = new TempDir();
        var result = new SettingsStore(dir.File("settings.json")).Load();
        Assert.Equal(SettingsLoadOutcome.CreatedDefaults, result.Outcome);
        Assert.Equal(new Hotkey(HotkeyModifiers.None, Hotkey.VkPrintScreen), result.Settings.Hotkeys.Region);
    }

    [Fact]
    public void Save_then_load_round_trips_everything_we_set()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("nested/deeper/settings.json"));
        var s = new AppSettings();
        s.General.Theme = AppTheme.Dark;
        s.Hotkeys.Region = new Hotkey(HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, 'S');
        s.Hotkeys.Freeform = Hotkey.None;
        s.Output.SaveFolder = @"D:\Skärmbilder\日本語";
        s.Editor.Tool = s.Editor.Tool with { StrokeWidth = 7.5, Color = new(1, 2, 3) };
        s.Capture.LastRegion = [-100, 20, 300, 400];
        s.Upload.Provider = UploadProvider.S3;
        store.Save(s);

        var loaded = store.Load();
        Assert.Equal(SettingsLoadOutcome.Loaded, loaded.Outcome);
        Assert.Equal(AppTheme.Dark, loaded.Settings.General.Theme);
        Assert.Equal(s.Hotkeys.Region, loaded.Settings.Hotkeys.Region);
        Assert.True(loaded.Settings.Hotkeys.Freeform.IsEmpty);
        Assert.Equal(s.Output.SaveFolder, loaded.Settings.Output.SaveFolder);
        Assert.Equal(7.5, loaded.Settings.Editor.Tool.StrokeWidth);
        Assert.Equal(new ShotIt.Core.Imaging.ColorRgba(1, 2, 3), loaded.Settings.Editor.Tool.Color);
        Assert.Equal([-100, 20, 300, 400], loaded.Settings.Capture.LastRegion!);
        Assert.Equal(UploadProvider.S3, loaded.Settings.Upload.Provider);
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"hotkeys\": 5}")]
    [InlineData("{\"capture\": {\"delaySeconds\": \"soon\"}}")]
    public void Corrupt_file_is_set_aside_and_defaults_are_used(string content)
    {
        using var dir = new TempDir();
        string path = dir.File("settings.json");
        File.WriteAllText(path, content);
        var result = new SettingsStore(path).Load();
        Assert.Equal(SettingsLoadOutcome.RecoveredFromCorruption, result.Outcome);
        Assert.NotNull(result.Detail);
        Assert.True(File.Exists(result.Detail));
        Assert.Equal(content, File.ReadAllText(result.Detail!));
        Assert.NotNull(result.Settings.Hotkeys);
    }

    [Fact]
    public void Version_1_file_is_migrated()
    {
        using var dir = new TempDir();
        string path = dir.File("settings.json");
        File.WriteAllText(path, """{ "hotkey": "Ctrl+Shift+A", "saveFolder": "C:\\Shots", "general": { "theme": "Light" } }""");
        var result = new SettingsStore(path).Load();
        Assert.Equal(SettingsLoadOutcome.Migrated, result.Outcome);
        Assert.Equal(new Hotkey(HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, 'A'), result.Settings.Hotkeys.Region);
        Assert.Equal(@"C:\Shots", result.Settings.Output.SaveFolder);
        Assert.Equal(AppTheme.Light, result.Settings.General.Theme);
        Assert.Equal(AppSettings.CurrentSchemaVersion, result.Settings.SchemaVersion);
    }

    [Fact]
    public void Out_of_range_values_are_normalized_and_nulls_repaired()
    {
        using var dir = new TempDir();
        string path = dir.File("settings.json");
        File.WriteAllText(path, """
            { "schemaVersion": 2, "capture": { "delaySeconds": 9999, "lastRegion": [1,2] },
              "output": { "jpegQuality": -5, "fileNameTemplate": "  " }, "editor": null, "history": { "excludedTitlePatterns": null } }
            """);
        var s = new SettingsStore(path).Load().Settings;
        Assert.Equal(30, s.Capture.DelaySeconds);
        Assert.Null(s.Capture.LastRegion);
        Assert.Equal(10, s.Output.JpegQuality);
        Assert.False(string.IsNullOrWhiteSpace(s.Output.FileNameTemplate));
        Assert.NotNull(s.Editor);
        Assert.NotNull(s.Editor.Tool);
        Assert.NotNull(s.History.ExcludedTitlePatterns);
    }

    [Fact]
    public void Unknown_properties_are_ignored_so_downgrades_do_not_break()
    {
        using var dir = new TempDir();
        string path = dir.File("settings.json");
        File.WriteAllText(path, """{ "schemaVersion": 99, "futureFeature": { "x": 1 }, "general": { "theme": "Dark", "newFlag": true } }""");
        var result = new SettingsStore(path).Load();
        Assert.Equal(AppTheme.Dark, result.Settings.General.Theme);
    }

    [Fact]
    public void Saving_keeps_rotating_backups_and_leaves_no_temp_file()
    {
        using var dir = new TempDir();
        string path = dir.File("settings.json");
        var store = new SettingsStore(path);
        for (int i = 1; i <= 6; i++)
        {
            var s = new AppSettings();
            s.Capture.DelaySeconds = i;
            store.Save(s);
        }

        Assert.False(File.Exists(path + ".tmp"));
        Assert.True(File.Exists(path + ".bak1"));
        Assert.True(File.Exists(path + ".bak2"));
        Assert.True(File.Exists(path + ".bak3"));
        Assert.False(File.Exists(path + ".bak4"));
        Assert.Equal(6, store.Load().Settings.Capture.DelaySeconds);
        Assert.Equal(5, new SettingsStore(path + ".bak1").Load().Settings.Capture.DelaySeconds);
    }
}

public class FileNameTemplateTests
{
    private static readonly FileNameContext Ctx = new(new DateTime(2026, 3, 9, 14, 5, 7), "chrome", "Inbox (3) - mail", 1920, 1080, 42);

    [Fact]
    public void Expands_all_tokens()
    {
        Assert.Equal("ShotIt 2026-03-09 14-05-07 chrome 1920x1080 0042 Inbox (3) - mail",
            FileNameTemplate.Expand("ShotIt {date} {time} {app} {size} {counter:4} {title}", Ctx));
        Assert.Equal("09.03.2026 1405", FileNameTemplate.Expand("{date:dd.MM.yyyy} {time:HHmm}", Ctx));
    }

    [Fact]
    public void Unknown_tokens_are_left_alone()
    {
        Assert.Equal("{nope} 1920", FileNameTemplate.Expand("{nope} {width}", Ctx));
    }

    [Theory]
    [InlineData("a<b>c:d\"e/f\\g|h?i*j", "a b c d e f g h i j")]
    [InlineData("  lots   of\t\tspace  ", "lots of space")]
    [InlineData("trailing dots...", "trailing dots")]
    [InlineData("", "ShotIt")]
    [InlineData("???", "ShotIt")]
    [InlineData("CON", "_CON")]
    [InlineData("lpt1.backup", "_lpt1.backup")]
    [InlineData("Console", "Console")]
    [InlineData("emoji 😀 ok", "emoji 😀 ok")]
    public void Sanitizes(string input, string expected) => Assert.Equal(expected, FileNameTemplate.Sanitize(input));

    [Fact]
    public void Hostile_window_titles_cannot_escape_the_folder_or_break_the_name()
    {
        var ctx = Ctx with { WindowTitle = @"..\..\Windows\System32\evil: <script>|" + new string('x', 500) };
        string name = FileNameTemplate.Expand("{title}", ctx);
        Assert.DoesNotContain('\\', name);
        Assert.DoesNotContain(':', name);
        Assert.True(name.Length <= FileNameTemplate.MaxNameLength);
        Assert.Equal(-1, name.IndexOfAny(Path.GetInvalidFileNameChars()));
    }

    [Fact]
    public void Long_names_are_cut_without_splitting_surrogate_pairs()
    {
        string name = FileNameTemplate.Sanitize(string.Concat(Enumerable.Repeat("😀", 200)));
        Assert.True(name.Length <= FileNameTemplate.MaxNameLength);
        Assert.False(char.IsHighSurrogate(name[^1]));
    }

    [Fact]
    public void Bad_date_format_falls_back_instead_of_throwing()
    {
        Assert.Equal("2026-03-09", FileNameTemplate.Expand("{date:%}", Ctx));
    }

    [Fact]
    public void Unique_path_appends_counter()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\x\shot.png", @"C:\x\shot (2).png" };
        Assert.Equal(@"C:\x\shot (3).png", FileNameTemplate.MakeUniquePath(@"C:\x", "shot", "png", taken.Contains));
        Assert.Equal(@"C:\x\other.png", FileNameTemplate.MakeUniquePath(@"C:\x", "other", ".png", taken.Contains));
    }

    [Theory]
    [InlineData("KeePass - db.kdbx", "*KeePass*", true)]
    [InlineData("my keepass", "*KEEPASS*", true)]
    [InlineData("Notepad", "*KeePass*", false)]
    [InlineData("abc", "a?c", true)]
    [InlineData("abbc", "a?c", false)]
    [InlineData("abc", "", false)]
    [InlineData("", "*", true)]
    [InlineData("a.b.c", "*.c", true)]
    [InlineData("aaa", "a*a*a*a", false)]
    public void Wildcards(string text, string pattern, bool expected) => Assert.Equal(expected, Wildcard.IsMatch(text, pattern));
}

public class HistoryRepositoryTests
{
    private static HistoryItem Item(string path, DateTime created, long size = 100, bool keep = false, string title = "", string ocr = "") =>
        new() { FilePath = path, CreatedUtc = created, FileSize = size, Keep = keep, SourceTitle = title, OcrText = ocr, Width = 10, Height = 20, Thumbnail = [1, 2, 3] };

    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Add_get_update_delete()
    {
        using var repo = new HistoryRepository(":memory:");
        long id = repo.Add(Item(@"C:\a.png", Now, title: "Report – Q2"));
        var item = repo.Get(id)!;
        Assert.Equal(@"C:\a.png", item.FilePath);
        Assert.Equal(Now, item.CreatedUtc);
        Assert.Equal(DateTimeKind.Utc, item.CreatedUtc.Kind);
        Assert.Equal([1, 2, 3], item.Thumbnail!);

        repo.Update(item with { UploadUrl = "https://x/y", Keep = true, OcrText = "hello" });
        item = repo.Get(id)!;
        Assert.Equal("https://x/y", item.UploadUrl);
        Assert.True(item.Keep);

        repo.Delete([id]);
        Assert.Null(repo.Get(id));
        Assert.Equal(0, repo.Count());
    }

    [Fact]
    public void Query_is_newest_first_and_paged()
    {
        using var repo = new HistoryRepository(":memory:");
        for (int i = 0; i < 10; i++) repo.Add(Item($"f{i}.png", Now.AddMinutes(i)));
        var page = repo.Query(limit: 3, offset: 2);
        Assert.Equal(["f7.png", "f6.png", "f5.png"], page.Select(p => p.FilePath));
    }

    [Fact]
    public void Search_matches_substrings_in_any_field_and_requires_all_words()
    {
        using var repo = new HistoryRepository(":memory:");
        repo.Add(Item(@"C:\shots\invoice.png", Now, title: "Chrome - Billing"));
        repo.Add(Item(@"C:\shots\cat.png", Now, ocr: "total amount 100% due"));
        repo.Add(Item(@"C:\shots\under_score.png", Now));

        Assert.Single(repo.Query("billing"));
        Assert.Single(repo.Query("voice"));
        Assert.Single(repo.Query("amount due"));
        Assert.Empty(repo.Query("amount billing"));
        Assert.Equal(3, repo.Query("shots").Count);
        // LIKE wildcards typed by the user are literals.
        Assert.Single(repo.Query("100%"));
        Assert.Single(repo.Query("under_score"));
        Assert.Empty(repo.Query("under.score"));
        Assert.Equal(3, repo.Query("   ").Count);
    }

    [Fact]
    public void Search_is_safe_against_sql_injection()
    {
        using var repo = new HistoryRepository(":memory:");
        repo.Add(Item("a.png", Now));
        Assert.Empty(repo.Query("'; DROP TABLE items; --"));
        Assert.Equal(1, repo.Count());
    }

    [Fact]
    public void Retention_by_age_spares_kept_items()
    {
        using var repo = new HistoryRepository(":memory:");
        repo.Add(Item("old.png", Now.AddDays(-100)));
        repo.Add(Item("old-kept.png", Now.AddDays(-100), keep: true));
        repo.Add(Item("new.png", Now.AddDays(-1)));
        var expired = repo.FindExpired(maxAgeDays: 90, maxTotalBytes: 0, Now);
        Assert.Equal(["old.png"], expired.Select(e => e.FilePath));
    }

    [Fact]
    public void Retention_by_size_removes_oldest_first_until_it_fits()
    {
        using var repo = new HistoryRepository(":memory:");
        repo.Add(Item("1.png", Now.AddDays(-5), size: 400));
        repo.Add(Item("2-kept.png", Now.AddDays(-4), size: 400, keep: true));
        repo.Add(Item("3.png", Now.AddDays(-3), size: 400));
        repo.Add(Item("4.png", Now.AddDays(-2), size: 400));
        var expired = repo.FindExpired(maxAgeDays: 0, maxTotalBytes: 900, Now);
        Assert.Equal(["1.png", "3.png"], expired.Select(e => e.FilePath));
    }

    [Fact]
    public void Retention_with_no_limits_removes_nothing()
    {
        using var repo = new HistoryRepository(":memory:");
        repo.Add(Item("1.png", Now.AddYears(-10), size: long.MaxValue / 4));
        Assert.Empty(repo.FindExpired(0, 0, Now));
    }

    [Fact]
    public void Clear_respects_kept_flag()
    {
        using var repo = new HistoryRepository(":memory:");
        repo.Add(Item("1.png", Now));
        repo.Add(Item("2.png", Now, keep: true));
        repo.Clear();
        Assert.Equal(1, repo.Count());
        repo.Clear(includeKept: true);
        Assert.Equal(0, repo.Count());
    }

    [Fact]
    public void Database_persists_on_disk_and_reopens()
    {
        using var dir = new TempDir();
        string path = dir.File("sub/history.db");
        using (var repo = new HistoryRepository(path)) repo.Add(Item("x.png", Now));
        using (var repo = new HistoryRepository(path)) Assert.Equal(1, repo.Count());
    }

    [Fact]
    public void Concurrent_writers_do_not_lose_rows()
    {
        using var repo = new HistoryRepository(":memory:");
        Parallel.For(0, 200, i => repo.Add(Item($"{i}.png", Now.AddSeconds(i))));
        Assert.Equal(200, repo.Count());
    }
}
