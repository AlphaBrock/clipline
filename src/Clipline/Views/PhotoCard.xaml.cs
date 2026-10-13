using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Clipline;

/// One capture on the line: glass frame, peg, and every gesture you can
/// make on it. How it moves is up to the rope it hangs on. A screen recording shows its first frame with
/// a play button and its length.
///
/// | Click            | Copy the image, or the recording as a file.  |
/// | Double click     | Open it in the default viewer or player.     |
/// | Press and hold   | Edit the image, or trim the recording.       |
/// | Drag into an app | Send a copy. It stays on the line.           |
/// | Drag to a folder | Move it there. It leaves the line.           |
/// | Click the cross  | Take it off the line.                        |
/// | Click the pin    | Pin it to the front of the line, or unpin it. |
public sealed partial class PhotoCard : UserControl, ILineCard
{
    public const double CardWidth = 200;
    /// From the top of the card down to where the rope passes through the peg.
    public const double RopeGrip = 8;

    /// The first row of the card's grid, where the peg sits above the frame.
    public const double PegRow = 16;
    /// The frame's padding plus its border.
    private const double FramePadding = 6 + 1;
    private static readonly TimeSpan HoldDelay = TimeSpan.FromMilliseconds(550);

    public string FilePath { get; }
    public bool IsRecording { get; }
    /// When the capture was saved, which orders the line.
    public DateTime TakenAt { get; }
    public double CardHeight { get; private set; }

    /// Pinned to the front of the line, and hung again at every launch.
    public bool Pinned
    {
        get => pinned;
        set { pinned = value; ShowChips(); }
    }
    private bool pinned;

    public event Action? LetGo;
    /// The pin was clicked: to be pinned, or unpinned.
    public event Action? PinToggled;
    /// Pressed down with the pointer, or let go.
    public event Action<bool>? Pressed;
    /// Clicked at this point in the card.
    public event Action<Windows.Foundation.Point>? Poked;
    /// True when a drag starts, false when it ends.
    public event Action<bool>? DraggingChanged;

    private StorageFile? file;

    private readonly DispatcherQueueTimer holdTimer;
    private readonly DispatcherQueueTimer badgeTimer;
    private bool held;
    private bool hovered;

    public PhotoCard(string path)
    {
        InitializeComponent();
        FilePath = path;
        IsRecording = Captures.IsRecording(path);
        TakenAt = File.GetCreationTimeUtc(path);
        Width = CardWidth;

        // It turns, and gives when pressed, around where the rope runs
        // through the peg.
        CenterPoint = new Vector3((float)(CardWidth / 2), (float)RopeGrip, 0);

        holdTimer = DispatcherQueue.CreateTimer();
        holdTimer.Interval = HoldDelay;
        holdTimer.IsRepeating = false;
        holdTimer.Tick += (_, _) => { held = true; Press(false); Edit(); };

        badgeTimer = DispatcherQueue.CreateTimer();
        badgeTimer.Interval = TimeSpan.FromMilliseconds(900);
        badgeTimer.IsRepeating = false;
        badgeTimer.Tick += (_, _) => CopiedBadge.Opacity = 0;

        PointerEntered += (_, _) => { hovered = true; ShowChips(); };
        PointerExited += (_, _) => { hovered = false; ShowChips(); CancelHold(); };
        PointerPressed += OnPointerPressed;
        PointerReleased += (_, _) => CancelHold();
        PointerCanceled += (_, _) => CancelHold();
        PointerCaptureLost += (_, _) => CancelHold();
        Tapped += OnTapped;
        DoubleTapped += (_, e) => { e.Handled = true; Open(); };
        DragStarting += OnDragStarting;
        DropCompleted += (_, _) =>
        {
            Body.Opacity = 1;
            DraggingChanged?.Invoke(false);
        };

        CloseChip.Tapped += (_, e) => { e.Handled = true; LetGo?.Invoke(); };
        CloseChip.DoubleTapped += (_, e) => e.Handled = true;
        PinChip.Tapped += (_, e) => { e.Handled = true; PinToggled?.Invoke(); };
        PinChip.DoubleTapped += (_, e) => e.Handled = true;
        // Pressing the pin is not pressing the photo.
        PinChip.PointerPressed += (_, e) => e.Handled = true;
    }

    private void ShowChips()
    {
        CloseChip.Opacity = hovered ? 1 : 0;
        PinChip.Opacity = hovered || pinned ? 1 : 0;
        PinChipGlyph.Glyph = pinned ? (hovered ? "" : "") : "";
        ToolTipService.SetToolTip(PinChip, pinned ? "取消置顶" : "置顶，排在最前面，启动时也会挂上");
    }

    /// Reads the capture and sizes the frame to it. False if it cannot be read.
    ///
    /// A recording can be shown from `lookAt`, an identical copy elsewhere.
    /// After trimming, that is the freshly rendered file: Windows' media
    /// editing caches what it read from a path for as long as the app runs,
    /// so the recording itself would still show its old length.
    public async Task<bool> LoadAsync(StorageFile? lookAt = null)
    {
        try
        {
            file = await StorageFile.GetFileFromPathAsync(FilePath);
            var bitmap = new BitmapImage { DecodePixelWidth = (int)(CardWidth * 2) };
            if (IsRecording)
            {
                var (frame, length) = await Thumbnails.FirstFrameAsync(lookAt ?? file, (int)(CardWidth * 2));
                await bitmap.SetSourceAsync(frame);
                Duration.Text = length.TotalHours >= 1 ? length.ToString(@"h\:mm\:ss") : length.ToString(@"m\:ss");
                PlayBadge.Visibility = Visibility.Visible;
                DurationBadge.Visibility = Visibility.Visible;
            }
            else
            {
                // Left open: the bitmap may decode lazily from it.
                await bitmap.SetSourceAsync(await Thumbnails.ImageAsync(FilePath, (int)(CardWidth * 2)));
            }

            double width = CardWidth - 2 * FramePadding;
            double aspect = bitmap.PixelWidth > 0 ? (double)bitmap.PixelHeight / bitmap.PixelWidth : 0.6;
            Picture.Width = width;
            Picture.Height = Math.Clamp(width * aspect, 72, 150);
            Picture.Source = bitmap;

            CardHeight = PegRow + Picture.Height + 2 * FramePadding;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                       or System.Runtime.InteropServices.COMException)
        {
            Log.Write($"Could not load {Path.GetFileName(FilePath)}", e);
            return false;
        }
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

    private void Copy()
    {
        if (file is null) return;
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        // A recording goes as the file, which chat apps and Explorer paste as the video.
        if (IsRecording) package.SetStorageItems(new[] { file });
        else package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        Clipboard.SetContent(package);
        // Keep it on the clipboard after Clipline quits.
        Clipboard.Flush();
        Flash("已复制");
    }

    /// Says something over the photo for a moment.
    public void Flash(string text)
    {
        BadgeText.Text = text;
        CopiedBadge.Opacity = 1;
        badgeTimer.Stop();
        badgeTimer.Start();
    }

    /// Whatever it was saying is done: it has been picked up.
    private void HideBadge()
    {
        badgeTimer.Stop();
        CopiedBadge.Opacity = 0;
    }

    private void Open() => Launch(FilePath, verb: null);

    /// A recording opens for trimming. An image opens in Paint for most
    /// people, or whatever the user picked to edit images with.
    private void Edit()
    {
        if (IsRecording)
        {
            if (file is not null) TrimWindow.Show(file, onSaved: trimmed => LoadAsync(lookAt: trimmed));
            return;
        }
        if (!Launch(FilePath, verb: "edit")) Open();
    }

    private static bool Launch(string path, string? verb)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = verb ?? "" });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private async void OnDragStarting(UIElement sender, DragStartingEventArgs e)
    {
        CancelHold();
        if (file is null) { e.Cancel = true; return; }
        e.Data.SetStorageItems(new[] { file });
        if (!IsRecording) e.Data.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        // Apps take a copy. Explorer moves it into the folder, or the Recycle
        // Bin, and the watcher then takes it off the line.
        e.AllowedOperations = DataPackageOperation.Copy | DataPackageOperation.Move;
        DraggingChanged?.Invoke(true);
        HideBadge();

        // The card itself goes with the pointer, while the one on the line
        // fades to show it is in hand.
        await DragPicture.ShowAsync(e, Frame, CloseChip, PinChip, CopiedBadge);
        Body.Opacity = 0.45;
    }

    // MARK: Motion

    public void SetInView(bool visible) => IsHitTestVisible = visible;

    private void Press(bool down) => Pressed?.Invoke(down);
}
