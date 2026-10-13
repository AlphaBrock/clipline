using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace Clipline;

/// A piece of copied text on the line, written on a note pegged up like a
/// photo. Links, e-mail addresses, paths, colour codes and code each show
/// the way that suits them.
///
/// | Click            | Copy it again.                               |
/// | Double click     | Read all of it, with what can be done to it. |
/// | Press and hold   | Pin it to the front, or unpin it.            |
/// | Drag into an app | Drop the text there.                         |
/// | Click the cross  | Delete it from the history.                  |
public sealed partial class NoteCard : UserControl, ILineCard
{
    /// Paper is lighter than a print: notes pull the rope down less.
    public const double PaperWeight = 0.7;
    /// The most of the text a note ever lays out.
    private const int PreviewLength = 600;
    /// Inside the frame's padding and border and the paper's padding, less
    /// a DIP to spare.
    private const double TextWidth = PhotoCard.CardWidth - 2 * (6 + 1) - 2 * 11 - 1;
    private static readonly TimeSpan HoldDelay = TimeSpan.FromMilliseconds(550);
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Microsoft YaHei UI");

    public TextClip Clipping { get; }
    public double CardHeight { get; private set; }

    /// Asked to be deleted from the history.
    public event Action? LetGo;
    public event Action<bool>? Pressed;
    public event Action<Point>? Poked;
    public event Action<bool>? DraggingChanged;
    /// Double-clicked: to be read in full.
    public event Action? ReadRequested;
    /// Just pinned or unpinned from the note itself.
    public event Action? PinChanged;
    /// Laid out for real, it came out a different height than measured.
    public event Action? Resized;

    private readonly DispatcherQueueTimer holdTimer;
    private readonly DispatcherQueueTimer badgeTimer;
    private bool held;
    private bool hovered;
    /// The note cannot show all of the text, so it says how long it is.
    private bool cut;

    public NoteCard(TextClip clip)
    {
        InitializeComponent();
        Clipping = clip;
        Width = PhotoCard.CardWidth;
        // The text a whole number of DIPs wide. Left to the layout, it is
        // measured a fraction narrower than it is drawn at some screen scales,
        // and a line that only just fits is measured onto two but drawn on one,
        // leaving a blank line under it.
        Heading.Width = Preview.Width = TextWidth;
        CenterPoint = new Vector3((float)(PhotoCard.CardWidth / 2), (float)PhotoCard.RopeGrip, 0);
        Fill();

        holdTimer = DispatcherQueue.CreateTimer();
        holdTimer.Interval = HoldDelay;
        holdTimer.IsRepeating = false;
        holdTimer.Tick += (_, _) => { held = true; Press(false); TogglePin(); };

        badgeTimer = DispatcherQueue.CreateTimer();
        badgeTimer.Interval = TimeSpan.FromMilliseconds(900);
        badgeTimer.IsRepeating = false;
        badgeTimer.Tick += (_, _) => Badge.Opacity = 0;

        PointerEntered += (_, _) => { hovered = true; ShowChips(); };
        PointerExited += (_, _) => { hovered = false; ShowChips(); CancelHold(); };
        PointerPressed += OnPointerPressed;
        PointerReleased += (_, _) => CancelHold();
        PointerCanceled += (_, _) => CancelHold();
        PointerCaptureLost += (_, _) => CancelHold();
        Tapped += OnTapped;
        DoubleTapped += (_, e) => { e.Handled = true; ReadRequested?.Invoke(); };
        DragStarting += OnDragStarting;
        // Should it ever lay out differently than it first measured.
        SizeChanged += (_, e) =>
        {
            double height = Fitted(e.NewSize.Height);
            if (CardHeight <= 0 || Math.Abs(height - CardHeight) < 0.5) return;
            CardHeight = height;
            Resized?.Invoke();
        };
        DropCompleted += (_, _) =>
        {
            Body.Opacity = 1;
            DraggingChanged?.Invoke(false);
        };

        CloseChip.Tapped += (_, e) => { e.Handled = true; LetGo?.Invoke(); };
        CloseChip.DoubleTapped += (_, e) => e.Handled = true;
        PinChip.Tapped += (_, e) => { e.Handled = true; TogglePin(); };
        PinChip.DoubleTapped += (_, e) => e.Handled = true;
        // Pressing a chip is not pressing the note: holding it must not pin.
        PinChip.PointerPressed += (_, e) => e.Handled = true;
        CloseChip.PointerPressed += (_, e) => e.Handled = true;
    }


    // MARK: What it shows

    /// Writes the note, once. It sizes itself to its text: call MeasureHeight
    /// once it is in the tree to know how tall that made it.
    private void Fill()
    {
        var clip = Clipping;
        switch (clip.Kind)
        {
            case ClipKind.Link:
                var (site, rest) = clip.LinkParts();
                Heading.Text = site;
                Heading.MaxLines = 1;
                Heading.Visibility = Visibility.Visible;
                Preview.Text = Breakable(rest.Length > 0 ? rest : clip.Text.Trim());
                Preview.MaxLines = 3;
                Preview.Opacity = 0.75;
                cut = false;
                break;
            case ClipKind.Email:
                Heading.Text = clip.Text.Trim();
                Heading.Visibility = Visibility.Visible;
                Preview.Visibility = Visibility.Collapsed;
                cut = false;
                break;
            case ClipKind.Path:
                var (name, folder) = clip.PathParts();
                Heading.Text = name;
                Heading.Visibility = Visibility.Visible;
                Preview.Text = Breakable(folder);
                Preview.MaxLines = 3;
                Preview.Opacity = 0.75;
                Preview.Visibility = folder.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                cut = false;
                break;
            case ClipKind.Color:
                if (clip.Color() is { } color)
                {
                    Swatch.Background = new SolidColorBrush(color);
                    Swatch.Visibility = Visibility.Visible;
                }
                Heading.Text = clip.Text.Trim();
                Heading.FontFamily = Mono;
                Heading.Visibility = Visibility.Visible;
                Preview.Visibility = Visibility.Collapsed;
                cut = false;
                break;
            case ClipKind.Code:
                string code = clip.Tidied(PreviewLength);
                Preview.Text = code;
                Preview.FontFamily = Mono;
                Preview.FontSize = 11;
                Preview.LineHeight = 16;
                Preview.TextWrapping = TextWrapping.NoWrap;
                Preview.MaxLines = 8;
                cut = clip.Text.Length > PreviewLength || code.Count(c => c == '\n') >= 8;
                break;
            default:
                string text = clip.Tidied(PreviewLength);
                Preview.Text = text;
                cut = text.Length > 120 || text.Count(c => c == '\n') >= 6;
                break;
        }

        KindIcon.Glyph = Glyph(clip.Kind);
        Meta.Text = clip.Describe(withLength: cut);
        PinMark.Visibility = clip.Pinned ? Visibility.Visible : Visibility.Collapsed;
        ShowChips();
    }

    /// Brings what it says about itself up to date: when it was copied, and
    /// whether it is pinned.
    public void Refresh()
    {
        Meta.Text = Clipping.Describe(withLength: cut);
        PinMark.Visibility = Clipping.Pinned ? Visibility.Visible : Visibility.Collapsed;
        ShowChips();
    }

    /// Lays the note out, now that it is in the tree, and notes its height.
    public void MeasureHeight()
    {
        Measure(new Size(PhotoCard.CardWidth, double.PositiveInfinity));
        CardHeight = Fitted(DesiredSize.Height);
    }

    private static double Fitted(double height) => Math.Max(height, PhotoCard.PegRow + 60);

    /// An address or a path with somewhere to wrap after each slash, so it
    /// breaks between its parts rather than in the middle of one.
    private static string Breakable(string text) => text.Replace("/", "/\u200B").Replace("\\", "\\\u200B");

    public static string Glyph(ClipKind kind) => kind switch
    {
        ClipKind.Link => "",
        ClipKind.Email => "",
        ClipKind.Path => "",
        ClipKind.Color => "",
        ClipKind.Code => "",
        _ => "",
    };

    private void ShowChips()
    {
        CloseChip.Opacity = hovered ? 1 : 0;
        PinChip.Opacity = hovered ? 1 : 0;
        PinChipGlyph.Glyph = Clipping.Pinned ? "" : "";
        ToolTipService.SetToolTip(PinChip, Clipping.Pinned ? "取消置顶" : "置顶，排在最前面，清空历史时也留着");
    }

    // MARK: Gestures

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        held = false;
        holdTimer.Start();
        Press(true);
    }

    private void CancelHold()
    {
        holdTimer.Stop();
        Press(false);
    }

    private void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        // The release that ends a long press is not a click.
        if (held) { held = false; return; }
        Copy();
        Poked?.Invoke(e.GetPosition(this));
    }

    public void Copy()
    {
        if (!CopyText(Clipping.Text)) return;
        Flash("已复制");
    }

    /// Puts text on the clipboard, to stay there after Clipline quits.
    public static bool CopyText(string text)
    {
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            return true;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // Another app holding the clipboard open.
            Log.Write("Could not copy the note", e);
            return false;
        }
    }

    private void TogglePin()
    {
        bool pinned = !Clipping.Pinned;
        if (!TextHistory.SetPinned(Clipping, pinned))
        {
            Flash($"最多置顶 {TextHistory.MaxPinned} 条");
            return;
        }
        Flash(pinned ? "已置顶" : "已取消置顶");
        PinChanged?.Invoke();
    }

    public void Flash(string text)
    {
        BadgeText.Text = text;
        Badge.Opacity = 1;
        badgeTimer.Stop();
        badgeTimer.Start();
    }

    private async void OnDragStarting(UIElement sender, DragStartingEventArgs e)
    {
        CancelHold();
        e.Data.SetText(Clipping.Text);
        e.AllowedOperations = DataPackageOperation.Copy;
        DraggingChanged?.Invoke(true);
        badgeTimer.Stop();
        Badge.Opacity = 0;
        await DragPicture.ShowAsync(e, Frame, CloseChip, PinChip, Badge);
        Body.Opacity = 0.45;
    }

    public void SetInView(bool visible) => IsHitTestVisible = visible;

    private void Press(bool down) => Pressed?.Invoke(down);
}
