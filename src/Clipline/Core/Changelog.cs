using System.Text.RegularExpressions;

namespace Clipline;

/// One version's notes: its groups ("新增", "修复"…) of one-line changes.
public sealed record Release(string Version, string? Date, IReadOnlyList<ReleaseGroup> Groups)
{
    public bool IsUnreleased => Version.Equals("Unreleased", StringComparison.OrdinalIgnoreCase);
}

public sealed record ReleaseGroup(string? Title, IReadOnlyList<string> Items);

/// CHANGELOG.md, built into the app, read back as releases. It understands
/// just the shape the file keeps to: "## [version] - date", "### group" and
/// "- item", with longer items wrapped onto indented lines.
public static partial class Changelog
{
    /// Newest first, as the file has them. Empty sections are left out.
    public static IReadOnlyList<Release> Releases { get; } = Parse(Read());

    private static string Read()
    {
        using var stream = typeof(Changelog).Assembly.GetManifestResourceStream("CHANGELOG.md");
        if (stream is null) return "";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static IReadOnlyList<Release> Parse(string markdown)
    {
        var releases = new List<Release>();
        string? version = null, date = null, groupTitle = null;
        var groups = new List<ReleaseGroup>();
        var items = new List<string>();

        void EndGroup()
        {
            if (items.Count > 0) groups.Add(new ReleaseGroup(groupTitle, items.ToList()));
            items.Clear();
        }
        void EndRelease()
        {
            EndGroup();
            if (version is not null && groups.Count > 0) releases.Add(new Release(version, date, groups.ToList()));
            groups.Clear();
        }

        foreach (string raw in markdown.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("## "))
            {
                EndRelease();
                var heading = ReleaseHeading().Match(line[3..].Trim());
                version = heading.Groups["version"].Value;
                date = heading.Groups["date"].Success ? heading.Groups["date"].Value : null;
                groupTitle = null;
            }
            else if (version is null)
            {
                // The introduction above the first version.
            }
            else if (line.StartsWith("### "))
            {
                EndGroup();
                groupTitle = Plain(line[4..]);
            }
            else if (line.TrimStart() is ['-' or '*', ' ', ..] item && line.Length - line.TrimStart().Length < 2)
            {
                items.Add(Plain(item[2..]));
            }
            else if (line.Length > 0 && char.IsWhiteSpace(line[0]) && items.Count > 0 && line.Trim().Length > 0)
            {
                // A long item wrapped onto the next line, or a nested point.
                items[^1] += " " + Plain(line.Trim().TrimStart('-', '*', ' '));
            }
            else if (line.Trim().Length > 0)
            {
                items.Add(Plain(line.Trim()));
            }
        }
        EndRelease();
        return releases;
    }

    /// Markdown's marks taken off: links keep their text, code and emphasis
    /// their words.
    private static string Plain(string text)
    {
        text = Link().Replace(text, "$1");
        return text.Replace("**", "").Replace("__", "").Replace("`", "").Trim();
    }

    [GeneratedRegex(@"^\[?(?<version>[^\]\s]+)\]?(?:\s*-\s*(?<date>\S+))?")]
    private static partial Regex ReleaseHeading();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex Link();
}
