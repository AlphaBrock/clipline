using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace Clipline;

/// Light, dark or following Windows, as chosen in the settings, for every
/// window Clipline opens. Follows along when the choice changes.
public static class Theming
{
    public static void Follow(Window window, FrameworkElement root)
    {
        void Apply()
        {
            root.RequestedTheme = Settings.Current.Theme switch
            {
                ThemeChoice.Light => ElementTheme.Light,
                ThemeChoice.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
            TintCaptionButtons(window.AppWindow, root.ActualTheme);
        }

        void OnChanged(string name)
        {
            if (name == nameof(Settings.Theme)) Apply();
        }

        Apply();
        // Windows switching between light and dark on its own.
        root.ActualThemeChanged += (_, _) => TintCaptionButtons(window.AppWindow, root.ActualTheme);
        Settings.Changed += OnChanged;
        window.Closed += (_, _) => Settings.Changed -= OnChanged;
    }

    /// With the content drawn under the title bar, the minimise and close
    /// buttons still need telling which theme they sit on.
    private static void TintCaptionButtons(AppWindow window, ElementTheme theme)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        var bar = window.TitleBar;
        bool dark = theme == ElementTheme.Dark;
        var ink = dark ? Colors.White : Colors.Black;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = ink;
        bar.ButtonHoverForegroundColor = ink;
        bar.ButtonHoverBackgroundColor = dark ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x10, 0, 0, 0);
        bar.ButtonPressedBackgroundColor = dark ? Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0A, 0, 0, 0);
        bar.ButtonInactiveForegroundColor = dark ? Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x80, 0, 0, 0);
    }
}
