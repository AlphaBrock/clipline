using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;

namespace Clipline;

/// What hangs on the line: a capture, or a note of copied text. Both share
/// the peg, the glass frame and the width, so the rope treats them alike.
public interface ILineCard
{
    /// From the top of the peg to the bottom of the frame.
    double CardHeight { get; }
    /// Scrolled out of view it cannot be clicked, even while it fades.
    void SetInView(bool visible);
}

/// The picture that goes along with the pointer while a card is dragged.
internal static class DragPicture
{
    /// Shows the card's glass frame as what is being dragged, held where it
    /// was picked up, without the controls that only show on hover. Without
    /// a picture the drag still works, as a bare pointer.
    public static async Task ShowAsync(Microsoft.UI.Xaml.DragStartingEventArgs e, FrameworkElement frame, params UIElement[] leaveOut)
    {
        var deferral = e.GetDeferral();
        try
        {
            var grabbed = e.GetPosition(frame);
            if (await SnapshotAsync(frame, leaveOut) is { } shot)
                e.DragUI.SetContentFromSoftwareBitmap(shot.Picture, new Point(grabbed.X * shot.Scale, grabbed.Y * shot.Scale));
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
        {
            Log.Write("Could not picture the card being dragged", ex);
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// The frame as a bitmap, and how many of its pixels make a DIP.
    private static async Task<(SoftwareBitmap Picture, double Scale)?> SnapshotAsync(FrameworkElement frame, UIElement[] leaveOut)
    {
        var shown = leaveOut.Select(e => e.Visibility).ToArray();
        foreach (var element in leaveOut) element.Visibility = Visibility.Collapsed;
        try
        {
            var target = new RenderTargetBitmap();
            await target.RenderAsync(frame);
            if (target.PixelWidth == 0 || frame.ActualWidth == 0) return null;
            var pixels = await target.GetPixelsAsync();
            var picture = SoftwareBitmap.CreateCopyFromBuffer(pixels, BitmapPixelFormat.Bgra8,
                target.PixelWidth, target.PixelHeight, BitmapAlphaMode.Premultiplied);
            return (picture, target.PixelWidth / frame.ActualWidth);
        }
        finally
        {
            for (int i = 0; i < leaveOut.Length; i++) leaveOut[i].Visibility = shown[i];
        }
    }
}
