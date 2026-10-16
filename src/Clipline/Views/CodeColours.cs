using Windows.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Clipline;

/// Code written out in colour, on the dark sheet code is always shown on,
/// so one palette serves both themes. After One Dark, which most people
/// have seen in an editor somewhere.
internal static class CodeColours
{
    private static readonly Dictionary<CodeToken, SolidColorBrush> Brushes = new()
    {
        [CodeToken.Plain] = Brush(0xE3, 0xE5, 0xEA),
        [CodeToken.Keyword] = Brush(0xC6, 0x78, 0xDD),
        [CodeToken.String] = Brush(0x98, 0xC3, 0x79),
        [CodeToken.Number] = Brush(0xD1, 0x9A, 0x66),
        [CodeToken.Comment] = Brush(0x7F, 0x84, 0x8E),
        [CodeToken.Function] = Brush(0x61, 0xAF, 0xEF),
        [CodeToken.Type] = Brush(0xE5, 0xC0, 0x7B),
        [CodeToken.Tag] = Brush(0xE0, 0x6C, 0x75),
        [CodeToken.Attribute] = Brush(0xD1, 0x9A, 0x66),
        [CodeToken.Punctuation] = Brush(0xAB, 0xB2, 0xBF),
    };

    public static SolidColorBrush Plain => Brushes[CodeToken.Plain];

    /// Replaces what `block` says with `code`, coloured.
    public static void Write(TextBlock block, string code)
    {
        block.Text = "";
        block.Inlines.Clear();
        block.Foreground = Plain;
        foreach (var (text, kind) in CodeHighlighter.Tokens(code))
        {
            var run = new Run { Text = text };
            if (kind != CodeToken.Plain) run.Foreground = Brushes[kind];
            if (kind == CodeToken.Comment) run.FontStyle = FontStyle.Italic;
            block.Inlines.Add(run);
        }
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromArgb(0xFF, r, g, b));
}
