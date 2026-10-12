using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ShotIt.Core.History;

public sealed record HistoryItem
{
    public long Id { get; init; }
    public DateTime CreatedUtc { get; init; }
    public string FilePath { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public string SourceApp { get; init; } = string.Empty;
    public string SourceTitle { get; init; } = string.Empty;
    public string UploadUrl { get; init; } = string.Empty;
    public string DeleteUrl { get; init; } = string.Empty;
    public string OcrText { get; init; } = string.Empty;
    public string Tags { get; init; } = string.Empty;
    public bool Keep { get; init; }
    public long FileSize { get; init; }
    public byte[]? Thumbnail { get; init; }
}

/// <summary>
/// The capture library index (SQLite, WAL). One connection guarded by a lock: write volume is tiny
/// and this keeps every operation trivially serialised.
/// </summary>
public sealed class HistoryRepository : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly Lock _gate = new();
    private bool _disposed;

    public HistoryRepository(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (databasePath != ":memory:")
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);

        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        _db.Open();
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=NORMAL;");
        Execute("""
            CREATE TABLE IF NOT EXISTS items (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                created_utc TEXT NOT NULL,
                file_path TEXT NOT NULL DEFAULT '',
                width INTEGER NOT NULL DEFAULT 0,
                height INTEGER NOT NULL DEFAULT 0,
                source_app TEXT NOT NULL DEFAULT '',
                source_title TEXT NOT NULL DEFAULT '',
                upload_url TEXT NOT NULL DEFAULT '',
                delete_url TEXT NOT NULL DEFAULT '',
                ocr_text TEXT NOT NULL DEFAULT '',
                tags TEXT NOT NULL DEFAULT '',
                keep INTEGER NOT NULL DEFAULT 0,
                file_size INTEGER NOT NULL DEFAULT 0,
                thumbnail BLOB
            );
            CREATE INDEX IF NOT EXISTS ix_items_created ON items(created_utc DESC);
            """);
    }

    private void Execute(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Stamp(DateTime utc) => utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    public long Add(HistoryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO items (created_utc, file_path, width, height, source_app, source_title, upload_url, delete_url, ocr_text, tags, keep, file_size, thumbnail)
                VALUES ($created, $path, $w, $h, $app, $title, $url, $del, $ocr, $tags, $keep, $size, $thumb);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$created", Stamp(item.CreatedUtc == default ? DateTime.UtcNow : item.CreatedUtc));
            cmd.Parameters.AddWithValue("$path", item.FilePath);
            cmd.Parameters.AddWithValue("$w", item.Width);
            cmd.Parameters.AddWithValue("$h", item.Height);
            cmd.Parameters.AddWithValue("$app", item.SourceApp);
            cmd.Parameters.AddWithValue("$title", item.SourceTitle);
            cmd.Parameters.AddWithValue("$url", item.UploadUrl);
            cmd.Parameters.AddWithValue("$del", item.DeleteUrl);
            cmd.Parameters.AddWithValue("$ocr", item.OcrText);
            cmd.Parameters.AddWithValue("$tags", item.Tags);
            cmd.Parameters.AddWithValue("$keep", item.Keep ? 1 : 0);
            cmd.Parameters.AddWithValue("$size", item.FileSize);
            cmd.Parameters.AddWithValue("$thumb", (object?)item.Thumbnail ?? DBNull.Value);
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public HistoryItem? Get(long id)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT * FROM items WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadItem(reader, withThumbnail: true) : null;
        }
    }

    /// <summary>
    /// Newest first. <paramref name="search"/> matches any part of the file name, window title, app,
    /// OCR text or tags; every whitespace-separated word must match somewhere.
    /// </summary>
    public IReadOnlyList<HistoryItem> Query(string? search = null, int limit = 500, int offset = 0, bool withThumbnails = true)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            var where = new List<string>();
            var words = (search ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < words.Length; i++)
            {
                string p = "$s" + i.ToString(CultureInfo.InvariantCulture);
                where.Add($"(file_path LIKE {p} ESCAPE '\\' OR source_title LIKE {p} ESCAPE '\\' OR source_app LIKE {p} ESCAPE '\\' OR ocr_text LIKE {p} ESCAPE '\\' OR tags LIKE {p} ESCAPE '\\' OR upload_url LIKE {p} ESCAPE '\\')");
                string escaped = words[i].Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
                cmd.Parameters.AddWithValue(p, "%" + escaped + "%");
            }

            cmd.CommandText = "SELECT * FROM items"
                + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : string.Empty)
                + " ORDER BY created_utc DESC, id DESC LIMIT $limit OFFSET $offset";
            cmd.Parameters.AddWithValue("$limit", limit);
            cmd.Parameters.AddWithValue("$offset", offset);

            var result = new List<HistoryItem>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) result.Add(ReadItem(reader, withThumbnails));
            return result;
        }
    }

    public int Count()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM items";
            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    public void Update(HistoryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                UPDATE items SET file_path=$path, upload_url=$url, delete_url=$del, ocr_text=$ocr, tags=$tags, keep=$keep, file_size=$size,
                    width=$w, height=$h, thumbnail=COALESCE($thumb, thumbnail)
                WHERE id=$id
                """;
            cmd.Parameters.AddWithValue("$id", item.Id);
            cmd.Parameters.AddWithValue("$path", item.FilePath);
            cmd.Parameters.AddWithValue("$url", item.UploadUrl);
            cmd.Parameters.AddWithValue("$del", item.DeleteUrl);
            cmd.Parameters.AddWithValue("$ocr", item.OcrText);
            cmd.Parameters.AddWithValue("$tags", item.Tags);
            cmd.Parameters.AddWithValue("$keep", item.Keep ? 1 : 0);
            cmd.Parameters.AddWithValue("$size", item.FileSize);
            cmd.Parameters.AddWithValue("$w", item.Width);
            cmd.Parameters.AddWithValue("$h", item.Height);
            // A null thumbnail (items read without thumbnails) leaves the stored one untouched.
            cmd.Parameters.AddWithValue("$thumb", (object?)item.Thumbnail ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public void Delete(IEnumerable<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            foreach (long id in ids)
            {
                using var cmd = _db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM items WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public void Clear(bool includeKept = false)
    {
        lock (_gate) Execute(includeKept ? "DELETE FROM items" : "DELETE FROM items WHERE keep = 0");
    }

    /// <summary>
    /// Works out which items the retention policy removes: everything older than <paramref name="maxAgeDays"/>,
    /// then the oldest items until the total size fits <paramref name="maxTotalBytes"/>. Kept items are never
    /// returned. Zero disables a limit. The caller deletes the files and then calls <see cref="Delete"/>.
    /// </summary>
    public IReadOnlyList<HistoryItem> FindExpired(int maxAgeDays, long maxTotalBytes, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var all = Query(limit: int.MaxValue, withThumbnails: false);
        var expired = new List<HistoryItem>();
        var remaining = new List<HistoryItem>();

        foreach (var item in all)
        {
            if (!item.Keep && maxAgeDays > 0 && (now - item.CreatedUtc).TotalDays > maxAgeDays) expired.Add(item);
            else remaining.Add(item);
        }

        if (maxTotalBytes > 0)
        {
            long total = remaining.Sum(i => i.FileSize);
            // "remaining" is newest first; walk from the oldest end.
            for (int i = remaining.Count - 1; i >= 0 && total > maxTotalBytes; i--)
            {
                if (remaining[i].Keep) continue;
                total -= remaining[i].FileSize;
                expired.Add(remaining[i]);
            }
        }

        return expired;
    }

    private static HistoryItem ReadItem(SqliteDataReader r, bool withThumbnail)
    {
        int thumb = r.GetOrdinal("thumbnail");
        return new HistoryItem
        {
            Id = r.GetInt64(r.GetOrdinal("id")),
            CreatedUtc = DateTime.Parse(r.GetString(r.GetOrdinal("created_utc")), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            FilePath = r.GetString(r.GetOrdinal("file_path")),
            Width = r.GetInt32(r.GetOrdinal("width")),
            Height = r.GetInt32(r.GetOrdinal("height")),
            SourceApp = r.GetString(r.GetOrdinal("source_app")),
            SourceTitle = r.GetString(r.GetOrdinal("source_title")),
            UploadUrl = r.GetString(r.GetOrdinal("upload_url")),
            DeleteUrl = r.GetString(r.GetOrdinal("delete_url")),
            OcrText = r.GetString(r.GetOrdinal("ocr_text")),
            Tags = r.GetString(r.GetOrdinal("tags")),
            Keep = r.GetInt64(r.GetOrdinal("keep")) != 0,
            FileSize = r.GetInt64(r.GetOrdinal("file_size")),
            Thumbnail = withThumbnail && !r.IsDBNull(thumb) ? (byte[])r[thumb] : null,
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) _db.Dispose();
    }
}
