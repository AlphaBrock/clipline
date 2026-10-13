using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clipline;

public enum ThemeChoice { System, Light, Dark }

/// Everything the settings window can change, kept in settings.json in the
/// data folder (see AppInfo). Each change is saved at once and announced, so
/// the line follows along while the window is still open. Launching at
/// sign-in is the exception: Windows keeps that (see SignInLaunch).
public sealed class Settings
{
    // Static fields are set up in the order they are written, so these
    // come before Current, which needs them to load.
    private static readonly string FilePath = Path.Combine(AppInfo.DataFolder, "settings.json");

    public static Settings Current { get; } = Load();

    /// Raised after any change, with the name of what changed.
    public static event Action<string>? Changed;

    // MARK: The line

    /// How many photos show at once, at most. The screen may fit fewer.
    public int MaxVisible { get; set; } = 6;
    /// How many stay on the line, shown or scrolled out of view.
    public int MaxHanging { get; set; } = 40;
    /// How many recent captures are hung when Clipline starts.
    public int RecentAtLaunch { get; set; } = 4;
    /// Resting the pointer on the top edge brings the line down.
    public bool RevealAtEdge { get; set; } = true;
    /// How long the pointer rests there first, in milliseconds.
    public int RevealDelay { get; set; } = 160;
    /// A new capture brings the line down for a moment.
    public bool RevealOnCapture { get; set; } = true;
    /// The top edge does nothing while a game, video or slideshow is full screen.
    public bool QuietInFullScreen { get; set; } = true;

    // MARK: Captures

    /// Null follows wherever Windows saves screenshots.
    public string? ScreenshotFolder { get; set; }
    public bool HangRecordings { get; set; } = true;
    /// Null follows wherever the Snipping Tool saves screen recordings.
    public string? RecordingFolder { get; set; }
    /// Screenshots a screenshot tool only put on the clipboard are saved and hung too.
    public bool HangClipboardScreenshots { get; set; } = true;
    /// What the Xbox Game Bar records and captures hangs too.
    public bool HangGameBarCaptures { get; set; } = true;
    /// Screenshots kept from the clipboard go to the Recycle Bin once they
    /// are this many days old, unless pinned. 0 keeps them for good.
    public int ClipboardScreenshotDays { get; set; } = 30;
    /// How many captures can be pinned to the front of the line.
    public int MaxPinnedCaptures { get; set; } = 3;

    // MARK: Copied text

    /// Text copied in any app is kept, to find on the line and copy again.
    public bool KeepClipboardText { get; set; } = true;
    /// How many pieces of copied text are kept. Pinned ones do not count.
    public int MaxTextHistory { get; set; } = 50;
    /// Copied text is deleted once it is this many days old, unless pinned.
    /// 0 keeps it until the limit above pushes it out.
    public int TextHistoryDays { get; set; }
    /// How many pieces of copied text can be pinned to the front of the line.
    public int MaxPinnedText { get; set; } = 5;

    // MARK: Shortcut

    public bool HotKeyEnabled { get; set; } = true;
    public uint HotKeyModifiers { get; set; } = Native.MOD_CONTROL | Native.MOD_ALT;
    public uint HotKeyKey { get; set; } = 'T';

    // MARK: Look and feel

    public ThemeChoice Theme { get; set; } = ThemeChoice.System;
    /// No breeze, no bounce: photos still move where they need to, plainly.
    public bool ReduceMotion { get; set; }

    // MARK: Folders as they actually are

    public string EffectiveScreenshotFolder => ScreenshotFolder ?? Native.ScreenshotsFolder();
    public string EffectiveRecordingFolder => RecordingFolder ?? Native.RecordingsFolder();
    public string GameBarFolder => Native.GameBarFolder();

    /// Where screenshots from the clipboard are kept. Clipline owns this
    /// folder, so what is let go from here goes to the Recycle Bin.
    public static string ClipboardFolder { get; } = Path.Combine(AppInfo.DataFolder, "Clipboard");

    // MARK: Saving

    /// Changes one setting, saves, and tells whoever is listening.
    public void Update(string name, Action<Settings> change)
    {
        change(this);
        Save();
        Changed?.Invoke(name);
    }

    private static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJson.Default.Settings) ?? new Settings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Write("Settings could not be read; starting from the defaults", e);
        }
        return new Settings();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SettingsJson.Default.Settings));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write("Settings could not be saved", e);
        }
    }
}

/// The settings' JSON shape, worked out at compile time rather than by
/// reflection, so trimming the published app cannot break it.
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(Settings))]
internal sealed partial class SettingsJson : JsonSerializerContext;
