using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Clipline;

/// A note held up to read in full, in front of the line: all of its text,
/// selectable, with buttons to copy it, open what it points to, pin it or
/// delete it. A click anywhere else on the line puts it back.
public sealed partial class NoteReader : UserControl
{
    public const double SheetWidth = 460;
    /// The most of a long text it lays out; copying still copies it all.
    private const int ShownLength = 20_000;
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Microsoft YaHei UI");
    private static readonly TimeSpan FadeTime = TimeSpan.FromMilliseconds(170);

    public TextClip? Clipping { get; private set; }
    public bool IsOpen { get; private set; }

    /// Put back, and faded out of sight.
    public event Action? Closed;

    private readonly DispatcherQueueTimer hideTimer;
    private readonly DispatcherQueueTimer copiedTimer;
    private readonly DispatcherQueueTimer noticeTimer;
    private readonly ScalarTransition fade = new() { Duration = FadeTime };
    private readonly Vector3Transition grow = new() { Duration = TimeSpan.FromMilliseconds(240) };

    public NoteReader()
    {
        InitializeComponent();
        Width = SheetWidth;
        Visibility = Visibility.Collapsed;
        // It grows out of the rope, from the middle of its top edge.
        CenterPoint = new Vector3((float)(SheetWidth / 2), 0, 0);

        hideTimer = DispatcherQueue.CreateTimer();
        hideTimer.Interval = FadeTime;
        hideTimer.IsRepeating = false;
        hideTimer.Tick += (_, _) =>
        {
            Visibility = Visibility.Collapsed;
            Closed?.Invoke();
        };

        copiedTimer = DispatcherQueue.CreateTimer();
        copiedTimer.Interval = TimeSpan.FromMilliseconds(1200);
        copiedTimer.IsRepeating = false;
        copiedTimer.Tick += (_, _) => ShowCopyLabel(copied: false);

        noticeTimer = DispatcherQueue.CreateTimer();
        noticeTimer.Interval = TimeSpan.FromSeconds(2.5);
        noticeTimer.IsRepeating = false;
        noticeTimer.Tick += (_, _) => ShowMeta();

        Button(CopyButton, Copy);
        Button(OpenButton, OpenTarget);
        Button(PinButton, TogglePin);
        Button(DeleteButton, () => { if (Clipping is { } c) TextHistory.Remove(c); });
        Button(CloseButton, () => Close());

        // Clicks, double clicks and the wheel on the sheet stay on the sheet:
        // they must not put it back, or page the line behind it.
        Sheet.Tapped += (_, e) => e.Handled = true;
        Sheet.DoubleTapped += (_, e) => e.Handled = true;
        Sheet.PointerWheelChanged += (_, e) => e.Handled = true;
        Full.SelectionChanged += (_, _) => ShowCopyLabel(copied: false);
        TextHistory.Changed += _ => Follow();
    }

    // MARK: Opening and closing

    /// Shows `clip` in full, no taller than `maxHeight`. Returns how big it is.
    public Size Open(TextClip clip, double maxHeight)
    {
        hideTimer.Stop();
        Clipping = clip;
        Fill();
        Scroller.MaxHeight = Math.Max(80, maxHeight - 48);
        Scroller.ChangeView(null, 0, null, disableAnimation: true);
        ShowCopyLabel(copied: false);

        bool wasOpen = IsOpen;
        IsOpen = true;
        Visibility = Visibility.Visible;
        Measure(new Size(SheetWidth, maxHeight));
        var size = new Size(SheetWidth, Math.Min(maxHeight, DesiredSize.Height));

        if (Motion.Reduced || wasOpen)
        {
            OpacityTransition = null;
            ScaleTransition = null;
            Opacity = 1;
            Scale = Vector3.One;
            return size;
        }
        // From a little smaller and see-through to where it belongs. Set the
        // start without transitions, then let them carry it the rest of the way.
        OpacityTransition = null;
        ScaleTransition = null;
        Opacity = 0;
        Scale = new Vector3(0.94f, 0.94f, 1);
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!IsOpen) return;
            OpacityTransition = fade;
            ScaleTransition = grow;
            Opacity = 1;
            Scale = Vector3.One;
        });
        return size;
    }

    /// Puts it back, fading unless `now`.
    public void Close(bool now = false)
    {
        if (!IsOpen) return;
        IsOpen = false;
        Clipping = null;
        if (now || Motion.Reduced)
        {
            hideTimer.Stop();
            Visibility = Visibility.Collapsed;
            Closed?.Invoke();
            return;
        }
        OpacityTransition = fade;
        ScaleTransition = grow;
        Opacity = 0;
        Scale = new Vector3(0.97f, 0.97f, 1);
        hideTimer.Start();
    }

    /// The history changed: deleted, it goes; pinned or unpinned, it says so.
    private void Follow()
    {
        if (!IsOpen || Clipping is not { } clip) return;
        if (!TextHistory.Items.Contains(clip)) Close();
        else ShowPinned();
    }

    // MARK: What it shows

    private void Fill()
    {
        if (Clipping is not { } clip) return;
        KindIcon.Glyph = NoteCard.Glyph(clip.Kind);
        noticeTimer.Stop();
        ShowMeta();

        string text = clip.Text.Length > ShownLength ? clip.Text[..ShownLength] : clip.Text;
        Full.Text = text.Replace("\r\n", "\n").Replace("\t", "    ").TrimEnd();
        bool code = clip.Kind == ClipKind.Code;
        Full.FontFamily = code ? Mono : FontFamily;
        Full.FontSize = code ? 12.5 : 13.5;
        Full.LineHeight = code ? 19 : 21;
        More.Text = $"还有 {clip.Text.Length - ShownLength:N0} 字没有显示，复制可以得到全文。";
        More.Visibility = clip.Text.Length > ShownLength ? Visibility.Visible : Visibility.Collapsed;

        if (clip.Kind == ClipKind.Color && clip.Color() is { } color)
        {
            Swatch.Background = new SolidColorBrush(color);
            Swatch.Visibility = Visibility.Visible;
        }
        else Swatch.Visibility = Visibility.Collapsed;

        string? openTip = clip.Kind switch
        {
            ClipKind.Link => "在浏览器中打开",
            ClipKind.Email => "写邮件",
            ClipKind.Path when Exists(clip) => "在资源管理器中显示",
            _ => null,
        };
        OpenButton.Visibility = openTip is null ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(OpenButton, openTip);
        OpenGlyph.Glyph = clip.Kind == ClipKind.Path ? "" : "";
        ShowPinned();
    }

    private void ShowMeta()
    {
        if (Clipping is { } clip) Meta.Text = KindName(clip.Kind) + " · " + clip.Describe(withLength: true);
    }

    private void ShowPinned()
    {
        bool pinned = Clipping?.Pinned == true;
        PinGlyph.Glyph = pinned ? "" : "";
        // Brushes from elements that hold them, which follow the theme.
        PinGlyph.Foreground = pinned ? PinColour.Background : HoverColour.BorderBrush;
        ToolTipService.SetToolTip(PinButton, pinned ? "取消置顶" : "置顶，排在最前面，清空历史时也留着");
    }

    private void ShowCopyLabel(bool copied)
    {
        CopyGlyph.Glyph = copied ? "" : "";
        CopyLabel.Text = copied ? "已复制" : Full.SelectedText.Length > 0 ? "复制所选" : "复制";
    }

    private static string KindName(ClipKind kind) => kind switch
    {
        ClipKind.Link => "链接",
        ClipKind.Email => "邮箱",
        ClipKind.Path => "路径",
        ClipKind.Color => "颜色",
        ClipKind.Code => "代码",
        _ => "文字",
    };

    private static bool Exists(TextClip clip)
    {
        string path = clip.Text.Trim().Trim('"');
        return File.Exists(path) || Directory.Exists(path);
    }

    // MARK: What it does

    private void Copy()
    {
        if (Clipping is not { } clip) return;
        string selected = Full.SelectedText;
        if (!NoteCard.CopyText(selected.Length > 0 ? selected : clip.Text)) return;
        ShowCopyLabel(copied: true);
        copiedTimer.Stop();
        copiedTimer.Start();
    }

    private void TogglePin()
    {
        if (Clipping is not { } clip || TextHistory.SetPinned(clip, !clip.Pinned)) return;
        // Full: it says so where it says what the note is, for a moment.
        Meta.Text = $"最多置顶 {TextHistory.MaxPinned} 条，先取消一条再置顶";
        noticeTimer.Stop();
        noticeTimer.Start();
    }

    private void OpenTarget()
    {
        if (Clipping is not { } clip) return;
        try
        {
            if (clip.Kind == ClipKind.Path)
                Process.Start("explorer.exe", $"/select,\"{clip.Text.Trim().Trim('"')}\"");
            else if (clip.Target() is { } target)
                Process.Start(new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            Log.Write("Could not open what the note points to", e);
        }
    }

    /// A border that acts as a button: a click does `action`, and the
    /// pointer over it shows a soft highlight, or dims it if it is filled.
    private void Button(Border button, Action action)
    {
        bool filled = button.Background is SolidColorBrush { Color.A: > 0 };
        var rest = button.Background;
        button.PointerEntered += (_, _) =>
        {
            if (filled) button.Opacity = 0.85;
            else button.Background = HoverColour.Background;
        };
        button.PointerExited += (_, _) =>
        {
            button.Background = rest;
            button.Opacity = 1;
        };
        button.Tapped += (_, e) => { e.Handled = true; action(); };
        button.DoubleTapped += (_, e) => e.Handled = true;
    }
}
