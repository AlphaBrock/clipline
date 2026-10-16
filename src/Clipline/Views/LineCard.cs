using System.Numerics;
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

/// The small motions both kinds of card make on their own, apart from how
/// the rope swings them.
internal static class CardMotion
{
    private const float Rest = 24, Lifted = 44;
    private const float LiftScale = 1.035f;
    private const float BadgeHidden = 0.82f;

    /// Readies the frame to rise toward the pointer, and the badge to pop.
    public static void Prepare(FrameworkElement frame, FrameworkElement badge)
    {
        frame.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(180) };
        frame.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(180) };
        // It grows from the top, where it hangs, so the peg keeps its hold.
        frame.SizeChanged += (_, e) => frame.CenterPoint = new Vector3((float)(e.NewSize.Width / 2), 0, 0);

        badge.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(220) };
        badge.Scale = new Vector3(BadgeHidden, BadgeHidden, 1);
        badge.SizeChanged += (_, e) => badge.CenterPoint = new Vector3((float)(e.NewSize.Width / 2), (float)(e.NewSize.Height / 2), 0);
    }

    /// Hovered, the card comes a little toward you, its shadow deepening.
    public static void Lift(FrameworkElement frame, bool up)
    {
        float scale = up && !Motion.Reduced ? LiftScale : 1;
        frame.Scale = new Vector3(scale, scale, 1);
        frame.Translation = new Vector3(0, 0, up ? Lifted : Rest);
    }

    /// A tick for a copy, a pin for pinning; nothing for a warning.
    public static void ShowGlyph(Microsoft.UI.Xaml.Controls.FontIcon icon, string text)
    {
        string? glyph = text switch
        {
            "已复制" => "",
            "已置顶" => "",
            "已取消置顶" => "",
            _ => null,
        };
        icon.Visibility = glyph is null ? Visibility.Collapsed : Visibility.Visible;
        if (glyph is not null) icon.Glyph = glyph;
    }

    /// Shows or hides a badge over the card, popping it in and out.
    public static void Pop(UIElement badge, bool show)
    {
        float scale = show || Motion.Reduced ? 1 : BadgeHidden;
        badge.Scale = new Vector3(scale, scale, 1);
        badge.Opacity = show ? 1 : 0;
    }
}
