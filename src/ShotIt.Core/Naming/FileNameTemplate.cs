using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ShotIt.Core.Naming;

public sealed record FileNameContext(DateTime LocalTime, string? AppName = null, string? WindowTitle = null, int Width = 0, int Height = 0, int Counter = 0);

/// <summary>
/// Expands templates such as "ShotIt {date:yyyy-MM-dd} {time:HH-mm-ss} {app}" into names that are
/// always valid on NTFS, FAT32 and network shares.
/// </summary>
public static partial class FileNameTemplate
{
    public const int MaxNameLength = 120;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    [GeneratedRegex(@"\{(?<name>[a-zA-Z]+)(:(?<arg>[^}]*))?\}")]
    private static partial Regex TokenRegex();

    /// <summary>Expands the template and sanitises the result. Never returns an empty or reserved name.</summary>
    public static string Expand(string template, FileNameContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(template)) template = "ShotIt {date} {time}";

        string expanded = TokenRegex().Replace(template, m =>
        {
            string arg = m.Groups["arg"].Value;
            return m.Groups["name"].Value.ToLowerInvariant() switch
            {
                "date" => Format(context.LocalTime, arg, "yyyy-MM-dd"),
                "time" => Format(context.LocalTime, arg, "HH-mm-ss"),
                "datetime" => Format(context.LocalTime, arg, "yyyy-MM-dd HH-mm-ss"),
                "app" => context.AppName ?? string.Empty,
                "title" => Truncate(context.WindowTitle ?? string.Empty, 60),
                "width" => context.Width.ToString(CultureInfo.InvariantCulture),
                "height" => context.Height.ToString(CultureInfo.InvariantCulture),
                "size" => string.Create(CultureInfo.InvariantCulture, $"{context.Width}x{context.Height}"),
                "counter" => context.Counter.ToString(int.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out int digits) && digits is > 0 and < 10 ? new string('0', digits) : "0", CultureInfo.InvariantCulture),
                _ => m.Value,
            };
        });

        return Sanitize(expanded);
    }

    private static string Format(DateTime time, string format, string fallback)
    {
        try
        {
            return time.ToString(string.IsNullOrEmpty(format) ? fallback : format, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            return time.ToString(fallback, CultureInfo.InvariantCulture);
        }
    }

    private static string Truncate(string text, int max)
    {
        if (text.Length <= max) return text;
        // Do not cut a surrogate pair in half.
        if (char.IsHighSurrogate(text[max - 1])) max--;
        return text[..max];
    }

    /// <summary>Removes characters Windows forbids, collapses whitespace, and guards reserved device names.</summary>
    public static string Sanitize(string name)
    {
        var sb = new StringBuilder(name?.Length ?? 0);
        bool lastSpace = false;
        foreach (char c in name ?? string.Empty)
        {
            char ch = c;
            if (ch < 32 || ch is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*') ch = ' ';
            if (char.IsWhiteSpace(ch))
            {
                if (!lastSpace && sb.Length > 0) sb.Append(' ');
                lastSpace = true;
            }
            else
            {
                sb.Append(ch);
                lastSpace = false;
            }
        }

        string result = Truncate(sb.ToString(), MaxNameLength).Trim(' ', '.');
        if (result.Length == 0) result = "ShotIt";

        string stem = result.Split('.')[0].Trim();
        if (ReservedNames.Contains(stem)) result = "_" + result;
        return result;
    }

    /// <summary>
    /// Returns a full path in <paramref name="folder"/> that does not exist yet, appending " (2)", " (3)"...
    /// when needed.
    /// </summary>
    public static string MakeUniquePath(string folder, string name, string extension, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        if (!extension.StartsWith('.')) extension = "." + extension;
        string candidate = Path.Combine(folder, name + extension);
        for (int i = 2; exists(candidate) && i < 10000; i++)
            candidate = Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"{name} ({i}){extension}"));
        return candidate;
    }
}

/// <summary>Simple case-insensitive wildcard matching ("*" and "?") for privacy exclusion rules.</summary>
public static class Wildcard
{
    public static bool IsMatch(string? text, string? pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        text ??= string.Empty;
        int t = 0, p = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t])))
            {
                t++; p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    public static bool MatchesAny(string? text, IEnumerable<string>? patterns) =>
        patterns is not null && patterns.Any(p => IsMatch(text, p));
}
