using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Clipline;

/// A window background with nothing in it, so the desktop shows through
/// wherever XAML draws nothing.
///
/// A backdrop hands the window a brush from the system compositor
/// (Windows.UI.Composition, not the one XAML draws with), and that compositor
/// needs a system dispatcher queue on the UI thread, which a WinUI 3 app does
/// not create by itself.
public sealed partial class TransparentBackdrop : SystemBackdrop
{
    private static Windows.UI.Composition.Compositor? compositor;
    private static object? queueController;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        target.SystemBackdrop = SystemCompositor().CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    }

    /// Nothing looks different in a light or dark theme. The base class's
    /// answer to the theme changing throws for a backdrop like this one,
    /// which took the whole app down whenever the theme was switched.
    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        target.SystemBackdrop = null;
        base.OnTargetDisconnected(target);
    }

    private static Windows.UI.Composition.Compositor SystemCompositor()
    {
        if (compositor is not null) return compositor;
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is null)
        {
            var options = new DispatcherQueueOptions
            {
                dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
                threadType = DQTYPE_THREAD_CURRENT,
                apartmentType = DQTAT_COM_STA,
            };
            Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out var raw));
            queueController = WinRT.MarshalInspectable<object>.FromAbi(raw);
        }
        return compositor = new Windows.UI.Composition.Compositor();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions { public int dwSize, threadType, apartmentType; }

    private const int DQTYPE_THREAD_CURRENT = 2, DQTAT_COM_STA = 2;

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);
}
