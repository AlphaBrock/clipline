using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Clipline;

/// What a piece of copied text looks like it is, which decides how its note
/// shows it.
public enum ClipKind { Text, Link, Email, Path, Color, Code }

/// One piece of copied text, as the history keeps it.
public sealed partial class TextClip
{
    public string Text { get; set; } = "";
    /// When it was last copied, in UTC.
    public DateTime CopiedAt { get; set; }
    /// The app it was copied from, as that app names itself.
    public string? Source { get; set; }
    /// Kept through clearing and through the limit on how many are kept.
    public bool Pinned { get; set; }

    [JsonIgnore]
    public ClipKind Kind => kind ??= Classify(Text);
    private ClipKind? kind;

    // MARK: Describing it

    /// "Edge · 3 分钟前", and the length when the note cannot show it all.
    public string Describe(bool withLength)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(Source)) parts.Add(Source);
        parts.Add(Ago(CopiedAt));
        if (withLength) parts.Add($"{Text.Length.ToString("N0", CultureInfo.CurrentCulture)} 字");
        return string.Join(" · ", parts);
    }

    public static string Ago(DateTime utc)
    {
        var then = utc.ToLocalTime();
        var now = DateTime.Now;
        var span = now - then;
        if (span < TimeSpan.FromMinutes(1)) return "刚刚";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} 分钟前";
        if (then.Date == now.Date) return then.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (then.Date == now.Date.AddDays(-1)) return "昨天 " + then.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (then.Year == now.Year) return $"{then.Month}月{then.Day}日";
        return then.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// The text tidied for showing: tabs as spaces, no carriage returns, no
    /// runs of blank lines, and no more of it than `limit` characters.
    public string Tidied(int limit)
    {
        string text = Text.Length > limit ? Text[..limit] : Text;
        var tidy = new StringBuilder(text.Length);
        int blank = 0;
        foreach (string raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            string line = raw.Replace("\t", "    ").TrimEnd();
            if (line.Length == 0)
            {
                // Leading blank lines, and more than one in a row, say nothing.
                if (tidy.Length == 0 || ++blank > 1) continue;
            }
            else blank = 0;
            if (tidy.Length > 0) tidy.Append('\n');
            tidy.Append(line);
        }
        return tidy.ToString().TrimEnd();
    }

    /// For a link: the site, and the rest of the address.
    public (string Site, string Address) LinkParts()
    {
        if (Link() is not { } uri) return (Text.Trim(), "");
        string host = uri.IdnHost.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.IdnHost[4..] : uri.IdnHost;
        string rest = Uri.UnescapeDataString(uri.PathAndQuery + uri.Fragment);
        return (host.Length > 0 ? host : uri.Scheme, rest == "/" ? "" : rest);
    }

    /// For a path: the file or folder's name, and where it is.
    public (string Name, string Folder) PathParts()
    {
        string path = Text.Trim().Trim('"').TrimEnd('\\', '/');
        string name = System.IO.Path.GetFileName(path);
        if (name.Length == 0) return (path, "");
        return (name, System.IO.Path.GetDirectoryName(path) ?? "");
    }

    /// Where a link, an e-mail address or a path leads, to open it.
    public Uri? Target() => Kind switch
    {
        ClipKind.Link => Link(),
        ClipKind.Email => new Uri("mailto:" + Text.Trim()),
        _ => null,
    };

    public Uri? Link()
    {
        string t = Text.Trim();
        if (t.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) t = "https://" + t;
        return Uri.TryCreate(t, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ftp" ? uri : null;
    }

    /// The colour a colour code names.
    public Windows.UI.Color? Color()
    {
        string t = Text.Trim();
        if (t.StartsWith('#'))
        {
            string hex = t[1..];
            if (hex.Length is 3 or 4) hex = string.Concat(hex.Select(c => new string(c, 2)));
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return null;
            return hex.Length == 8
                ? Windows.UI.Color.FromArgb((byte)v, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8))
                : Windows.UI.Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        }
        var numbers = Regex.Matches(t, @"[\d.]+%?").Select(m => m.Value).ToArray();
        if (numbers.Length < 3) return null;
        static double Number(string s) =>
            double.TryParse(s.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
        static byte Byte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
        byte Channel(string s) => Byte(s.EndsWith('%') ? Number(s) * 2.55 : Number(s));
        byte alpha = 255;
        if (numbers.Length > 3)
            alpha = Byte(numbers[3].EndsWith('%') ? Number(numbers[3]) * 2.55 : Number(numbers[3]) * 255);
        return Windows.UI.Color.FromArgb(alpha, Channel(numbers[0]), Channel(numbers[1]), Channel(numbers[2]));
    }

    // MARK: Telling what it is

    private static ClipKind Classify(string text)
    {
        string t = text.Trim();
        if (t.Length == 0) return ClipKind.Text;
        bool oneLine = t.IndexOfAny(['\r', '\n']) < 0;
        if (oneLine && t.Length <= 2048)
        {
            if (ColorCode().IsMatch(t)) return ClipKind.Color;
            if (EmailAddress().IsMatch(t)) return ClipKind.Email;
            if (!t.Contains(' ') && new TextClip { Text = t }.Link() is not null) return ClipKind.Link;
            if (FilePath().IsMatch(t)) return ClipKind.Path;
        }
        return LooksLikeCode(t) ? ClipKind.Code : ClipKind.Text;
    }

    /// Several lines, at least half of them indented or ending the way code
    /// ends its lines, or starting the way it starts them.
    private static bool LooksLikeCode(string t)
    {
        var lines = t.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToArray();
        if (lines.Length < 2) return false;
        int signs = lines.Count(line =>
        {
            string trimmed = line.Trim();
            return line.StartsWith("  ") || line.StartsWith('\t')
                || trimmed[^1] is ';' or '{' or '}'
                || CodeStart().IsMatch(trimmed);
        });
        return signs >= Math.Max(2, lines.Length / 2.0);
    }

    [GeneratedRegex(@"^(#([0-9a-f]{3,4}|[0-9a-f]{6}|[0-9a-f]{8})|rgba?\(\s*[\d.]+%?\s*[,\s]\s*[\d.]+%?\s*[,\s]\s*[\d.]+%?\s*([,/]\s*[\d.]+%?\s*)?\))$", RegexOptions.IgnoreCase)]
    private static partial Regex ColorCode();

    [GeneratedRegex(@"^[^@\s<>()]+@[^@\s<>()]+\.[a-z]{2,}$", RegexOptions.IgnoreCase)]
    private static partial Regex EmailAddress();

    [GeneratedRegex(@"^""?([a-z]:\\|\\\\[^\\/:*?""<>|]+\\)[^/:*?""<>|]*""?$", RegexOptions.IgnoreCase)]
    private static partial Regex FilePath();

    [GeneratedRegex(@"^(//|/\*|\*/|#include|#!|import |from \S+ import|using |namespace |package |def |class |function |fn |func |let |const |var |return\b|public |private |static |if \(|for \(|while \(|<[a-z!/]|SELECT |INSERT |UPDATE |\$ |PS>)", RegexOptions.IgnoreCase)]
    private static partial Regex CodeStart();
}
