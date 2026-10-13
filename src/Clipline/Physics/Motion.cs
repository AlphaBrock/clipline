using Windows.UI.ViewManagement;

namespace Clipline;

public static class Motion
{
    private static readonly UISettings System = new();

    /// The user asked for less motion, here or in Windows (Settings,
    /// Accessibility, Visual effects, Animation effects). Photos still move
    /// where they need to, but nothing swings, sways or bounces.
    public static bool Reduced => Settings.Current.ReduceMotion || !System.AnimationsEnabled;
}
