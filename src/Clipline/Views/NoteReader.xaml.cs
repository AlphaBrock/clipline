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
    private static readonly TimeSpan OpenTime = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan CloseTime = TimeSpan.FromMilliseconds(150);

    public TextClip? Clipping { get; private set; }
    public bool IsOpen { get; private set; }

    /// Put back, and faded out of sight.
    public event Action? Closed;

    private readonly DispatcherQueueTimer hideTimer;
    private readonly DispatcherQueueTimer copiedTimer;
    private readonly DispatcherQueueTimer noticeTimer;

    public NoteReader()
    {
        InitializeComponent();
        Width = SheetWidth;
        Visibility = Visibility.Collapsed;
        // It grows out of the rope, from the middle of its top edge.
        CenterPoint = new Vector3((float)(SheetWidth / 2), 0, 0);

        hideTimer = DispatcherQueue.CreateTimer();
        hideTimer.Interval = CloseTime;
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
        // The wheel scrolls code up and down, as anywhere else; only Shift
        // or a sideways wheel scrolls it along its lines.
        CodeText.PointerWheelChanged += OnCodeWheel;
        Scroller.ViewChanged += (_, e) => { if (!e.IsIntermediate) wheelTarget = null; };
        // Focused while it is read, so Esc puts it back.
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Escape) return;
            e.Handled = true;
            Close();
        };
        Full.SelectionChanged += (_, _) => ShowCopyLabel(copied: false);
        CodeText.SelectionChanged += (_, _) => ShowCopyLabel(copied: false);
        TextHistory.Changed += _ => Follow();
    }

    private double? wheelTarget;

    private void OnCodeWheel(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(CodeText);
        bool shift = (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Shift) != 0;
        if (point.Properties.IsHorizontalMouseWheel || shift) return;
        e.Handled = true;
        double from = wheelTarget ?? Scroller.VerticalOffset;
        double to = Math.Clamp(from - point.Properties.MouseWheelDelta * 0.4, 0, Scroller.ScrollableHeight);
        wheelTarget = to;
        Scroller.ChangeView(null, to, null);
    }

    // MARK: Opening and closing

    /// Shows `clip` in full, no taller than `maxHeight`. Returns how big it is.
    public Size Open(TextClip clip, double maxHeight)
    {
        hideTimer.Stop();
        Clipping = clip;
        Fill();
        Scroller.MaxHeight = Math.Max(80, maxHeight - 48);
        wheelTarget = null;
        Scroller.ChangeView(null, 0, null, disableAnimation: true);
        ShowCopyLabel(copied: false);

        bool wasOpen = IsOpen;
        IsOpen = true;
        Visibility = Visibility.Visible;
        Measure(new Size(SheetWidth, maxHeight));
        var size = new Size(SheetWidth, Math.Min(maxHeight, DesiredSize.Height));

        if (Motion.Reduced || wasOpen)
        {
            Settle();
            return size;
        }
        // Hidden until the first frame, so it never shows at full size first.
        Opacity = 0;
        // Started once it is placed, and grows from where it is told.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => { if (IsOpen) AnimateIn(); });
        return size;
    }

    /// Where on its top edge it grows out of and shrinks back into: over
    /// the peg of the note it shows.
    public void GrowFrom(double x) => CenterPoint = new Vector3((float)Math.Clamp(x, 24, SheetWidth - 24), 0, 0);

    // MARK: Motion

    private static readonly Vector3 Shrunk = new(0.9f, 0.9f, 1);
    private static readonly Vector3 Raised = new(0, -14, 0);

    /// Out of the note, as if lifted off the rope toward you: it grows a
    /// little past its size and settles back, while it fades in quickly.
    private void AnimateIn()
    {
        var c = CompositionTarget.GetCompositorForCurrentThread();
        StopAnimations();

        var grow = c.CreateSpringVector3Animation();
        grow.Target = "Scale";
        grow.InitialValue = Shrunk;
        grow.FinalValue = Vector3.One;
        grow.DampingRatio = 0.72f;
        grow.Period = TimeSpan.FromMilliseconds(48);

        var drop = c.CreateSpringVector3Animation();
        drop.Target = "Translation";
        drop.InitialValue = Raised;
        drop.FinalValue = Vector3.Zero;
        drop.DampingRatio = 0.8f;
        drop.Period = TimeSpan.FromMilliseconds(48);

        var fade = c.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity";
        fade.Duration = OpenTime;
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, c.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1)));

        Opacity = 1;
        Run(grow);
        Run(drop);
        Run(fade);
    }

    /// Back into the note, quickening as it goes, so it is out of the way
    /// sooner than it came.
    private void AnimateOut()
    {
        var c = CompositionTarget.GetCompositorForCurrentThread();
        var accelerate = c.CreateCubicBezierEasingFunction(new Vector2(0.55f, 0), new Vector2(0.85f, 0.4f));

        var shrink = c.CreateVector3KeyFrameAnimation();
        shrink.Target = "Scale";
        shrink.Duration = CloseTime;
        shrink.InsertExpressionKeyFrame(0, "this.StartingValue");
        shrink.InsertKeyFrame(1, new Vector3(0.94f, 0.94f, 1), accelerate);

        var lift = c.CreateVector3KeyFrameAnimation();
        lift.Target = "Translation";
        lift.Duration = CloseTime;
        lift.InsertExpressionKeyFrame(0, "this.StartingValue");
        lift.InsertKeyFrame(1, new Vector3(0, -8, 0), accelerate);

        var fade = c.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity";
        fade.Duration = CloseTime;
        fade.InsertExpressionKeyFrame(0, "this.StartingValue");
        fade.InsertKeyFrame(1, 0, accelerate);

        StopAnimations();
        Run(shrink);
        Run(lift);
        Run(fade);
    }

    /// Where it rests when open, with nothing moving it.
    private void Settle()
    {
        StopAnimations();
        Opacity = 1;
        Scale = Vector3.One;
        Translation = Vector3.Zero;
    }

    private readonly List<Microsoft.UI.Composition.CompositionAnimation> running = new();

    private void Run(Microsoft.UI.Composition.CompositionAnimation animation)
    {
        running.Add(animation);
        StartAnimation(animation);
    }

    private void StopAnimations()
    {
        foreach (var animation in running) StopAnimation(animation);
        running.Clear();
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
            Settle();
            Visibility = Visibility.Collapsed;
            Closed?.Invoke();
            return;
        }
        AnimateOut();
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
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ").TrimEnd();
        bool code = clip.Kind == ClipKind.Code;
        // Code turns the whole sheet dark, buttons and all, whatever the theme.
        RequestedTheme = code ? ElementTheme.Dark : ElementTheme.Default;
        CodeBackdrop.Visibility = code ? Visibility.Visible : Visibility.Collapsed;
        CodeArea.Visibility = code ? Visibility.Visible : Visibility.Collapsed;
        Full.Visibility = code ? Visibility.Collapsed : Visibility.Visible;
        if (code)
        {
            CodeText.FontFamily = LineNumbers.FontFamily = Mono;
            CodeColours.Write(CodeText, text);
            int lines = text.Count(c => c == '\n') + 1;
            LineNumbers.Text = string.Join("\n", Enumerable.Range(1, lines));
            Full.Text = "";
        }
        else
        {
            Full.Text = text;
            CodeText.Inlines.Clear();
            LineNumbers.Text = "";
        }
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
        CopyLabel.Text = copied ? "已复制" : Selected.Length > 0 ? "复制所选" : "复制";
    }

    /// Whatever is selected in the text showing.
    private string Selected => CodeArea.Visibility == Visibility.Visible ? CodeText.SelectedText : Full.SelectedText;

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
        string selected = Selected;
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
