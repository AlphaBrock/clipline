using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Clipline;

/// The look shared by Clipline's ordinary windows, settings and trimming:
/// Mica behind everything, content drawn up into the title bar, the app's
/// icon, and a size given in DIPs on whichever screen the pointer is on.
public static class WindowChrome
{
    public static void Prepare(Window window, UIElement titleBar, FrameworkElement root, double width, double height)
    {
        window.SystemBackdrop = new MicaBackdrop();
        window.ExtendsContentIntoTitleBar = true;
        window.SetTitleBar(titleBar);
        window.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Clipline.ico"));
        Theming.Follow(window, root);

        var screen = Native.MonitorUnderCursor();
        int w = (int)(width * screen.Scale), h = (int)(height * screen.Scale);
        var bounds = screen.Bounds;
        window.AppWindow.MoveAndResize(new RectInt32(
            bounds.Left + (bounds.Width - w) / 2, bounds.Top + (bounds.Height - h) / 2, w, h));
    }
}
