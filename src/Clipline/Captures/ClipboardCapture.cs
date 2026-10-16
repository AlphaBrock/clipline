using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Clipline;

/// Screenshots that only ever reach the clipboard: Snipaste and most other
/// screenshot tools by default, and the Snipping Tool when told not to save.
/// Each one is written as a PNG to Clipline's own folder, where the watcher
/// picks it up like any other capture.
///
/// Only images put there by a known screenshot tool count. An image copied
/// in a browser or a chat is not a screenshot and stays off the line.
///
/// QQ, WeChat and WeCom take screenshots too, but copying a picture out of a
/// chat in them puts an image on the clipboard as well, so their name alone
/// says nothing. Their screenshots are told apart instead:
///
/// | WeChat | adds a format of its own, WeChatScreenshotFormat.             |
/// | WeCom  | copies from its snipping window, of class SnapshotWnd.       |
/// | QQ     | leaves no owner, just after a full-screen QQ window was in   |
/// |        | front: the one you pick the area in.                         |
///
/// Packaged screenshot tools, such as the Win+Shift+S overlay, go through
/// the clipboard broker, a window of class CLIPBRDWNDCLASS in svchost, so
/// the broker is the owner. Theirs are the images the broker puts there
/// while, or just after, the tool is in front.
public sealed class ClipboardCapture
{
    /// Process names, without ".exe", of the tools whose clipboard images
    /// are screenshots.
    private static readonly HashSet<string> ScreenshotTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "SnippingTool",       // Snipping Tool, Windows 11
        "ScreenClippingHost", // Win+Shift+S, Windows 10 and early 11
        "ScreenSketch",       // Snip & Sketch
        "Snipaste",
        "PixPin",
        "ShareX",
        "Greenshot",
        "Lightshot",
        "flameshot",
        "FSCapture",          // FastStone Capture
    };

    /// Chat apps whose screenshot tool covers the screen while you pick an
    /// area, by process name.
    private static readonly HashSet<string> ChatApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "QQ", "Weixin", "WeChat", "WXWork",
    };

    /// How soon after a chat app's full-screen window was last in front its
    /// screenshot reaches the clipboard: it copies as the window closes.
    private static readonly TimeSpan SnipGrace = TimeSpan.FromSeconds(3);

    /// The Snipping Tool usually saves the file as well, a moment later.
    /// Wait that long before deciding the clipboard copy is the only one.
    private static readonly TimeSpan SaveGrace = TimeSpan.FromSeconds(1.5);

    private DateTime lastTaken;

    private IntPtr lastForeground;
    /// The chat app whose full-screen window is in front, if one is.
    private string? snipping;
    /// The last chat app seen picking an area to capture, and when.
    private string? snipApp;
    private DateTime snipSeen;
    /// The screenshot tool whose window is in front, if one is.
    private string? frontTool;
    /// The last screenshot tool seen in front, and when.
    private string? toolApp;
    private DateTime toolSeen;

    /// Called often, on the UI thread: notes a chat app's screenshot window
    /// while it is in front. Only looks the window up when it changes.
    public void NoticeForeground()
    {
        var window = Native.GetForegroundWindow();
        if (window != lastForeground)
        {
            lastForeground = window;
            var (process, coversScreen) = Native.WindowOwner(window);
            snipping = coversScreen && process is not null && ChatApps.Contains(process) ? process : null;
            frontTool = process is not null && ScreenshotTools.Contains(process) ? process : null;
        }
        if (frontTool is not null)
        {
            toolApp = frontTool;
            toolSeen = DateTime.UtcNow;
        }
        if (snipping is null) return;
        snipApp = snipping;
        snipSeen = DateTime.UtcNow;
    }

    /// The screenshot tool that put the image on the clipboard, or null if
    /// it is not a screenshot.
    private string? ScreenshotSource(string? owner)
    {
        if (owner is not null && ScreenshotTools.Contains(owner)) return owner;
        if (owner is "svchost" && Native.ClipboardOwnerClass() == "CLIPBRDWNDCLASS")
        {
            NoticeForeground();
            if (toolApp is { } tool && DateTime.UtcNow - toolSeen < SnipGrace) return tool;
        }
        if (Native.ClipboardHasFormat("WeChatScreenshotFormat")) return "WeChat";
        if (owner is "WXWork" && Native.ClipboardOwnerClass() == "SnapshotWnd") return "WeCom";
        if (snipApp is { } app && DateTime.UtcNow - snipSeen < SnipGrace
            && (owner is null || owner.Equals(app, StringComparison.OrdinalIgnoreCase)))
        {
            // One screenshot per time the window was up.
            snipApp = null;
            return app;
        }
        return null;
    }

    /// Called whenever the clipboard changes, on the UI thread.
    public async void OnClipboardChanged()
    {
        if (!Settings.Current.HangClipboardScreenshots || !Native.ClipboardHasImage()) return;
        string? from = Native.ClipboardOwnerProcess();
        if (ScreenshotSource(from) is not { } owner)
        {
            Log.Write($"An image from {from ?? "an unknown app"} is on the clipboard; not a screenshot tool, so it stays off the line");
            return;
        }

        // The tool may still hold the clipboard open for a moment.
        DataPackageView? content = null;
        for (int attempt = 0; content is null; attempt++)
        {
            try
            {
                content = Clipboard.GetContent();
            }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                if (attempt == 5)
                {
                    Log.Write($"Could not read what {owner} put on the clipboard", e);
                    return;
                }
                await Task.Delay(100);
            }
        }
        if (!content.Contains(StandardDataFormats.Bitmap))
        {
            Log.Write($"{owner} put no image on the clipboard: {string.Join(", ", content.AvailableFormats)}");
            return;
        }

        // Some tools put the same image on the clipboard more than once.
        var taken = DateTime.Now;
        if (taken - lastTaken < TimeSpan.FromMilliseconds(500))
        {
            Log.Write($"{owner} put the same screenshot on the clipboard again");
            return;
        }
        lastTaken = taken;

        try
        {
            var reference = await content.GetBitmapAsync();
            using var source = await reference.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(source);
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            await Task.Delay(SaveGrace);
            if (SavedAsFileSince(taken))
            {
                Log.Write($"{owner} also saved this screenshot as a file; the clipboard copy is not needed");
                return;
            }

            Directory.CreateDirectory(Settings.ClipboardFolder);
            var folder = await StorageFolder.GetFolderFromPathAsync(Settings.ClipboardFolder);
            var file = await folder.CreateFileAsync($"截图 {taken:yyyy-MM-dd HHmmss}.png", CreationCollisionOption.GenerateUniqueName);
            using (var output = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
                encoder.SetSoftwareBitmap(bitmap);
                await encoder.FlushAsync();
            }
            Log.Write($"Saved a screenshot from {owner} on the clipboard as {file.Name}");
        }
        catch (Exception e)
        {
            // Whatever the clipboard or the tool throws at us, the line keeps going.
            Log.Write($"Could not keep the screenshot {owner} put on the clipboard", e);
        }
    }

    /// Sends the screenshots kept from the clipboard to the Recycle Bin once
    /// they are older than the settings keep them, going by when each was
    /// last written. Pinned ones stay. The watcher then takes any that hang
    /// off the line. Touches only the disk: run it off the UI thread.
    public static void ExpireOld()
    {
        int days = Settings.Current.ClipboardScreenshotDays;
        if (days <= 0 || !Directory.Exists(Settings.ClipboardFolder)) return;
        var before = DateTime.UtcNow - TimeSpan.FromDays(days);
        try
        {
            var old = new DirectoryInfo(Settings.ClipboardFolder).EnumerateFiles()
                .Where(f => Captures.Matches(f.FullName, CaptureKind.Screenshot) && f.LastWriteTimeUtc < before
                            && !PinnedCaptures.Contains(f.FullName))
                .Select(f => f.FullName)
                .ToList();
            if (old.Count == 0) return;
            bool recycled = Native.Recycle(old);
            Log.Write($"{(recycled ? "Recycled" : "Could not recycle all of")} {old.Count} clipboard screenshot(s) older than {days} days");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write("Could not clear away old clipboard screenshots", e);
        }
    }

    /// Whether an image landed in the screenshot or Game Bar folder since
    /// the screenshot was taken.
    private static bool SavedAsFileSince(DateTime taken)
    {
        var since = taken - TimeSpan.FromSeconds(2);
        var settings = Settings.Current;
        foreach (var folder in new[] { settings.EffectiveScreenshotFolder, settings.GameBarFolder })
        {
            try
            {
                if (Directory.Exists(folder) && new DirectoryInfo(folder).EnumerateFiles()
                        .Any(f => Captures.Matches(f.FullName, CaptureKind.Screenshot) && f.CreationTime >= since))
                    return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return false;
    }
}
