using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Dispatching;

namespace Clipline;

/// Text copied in any app, pinned first and then newest first, kept in
/// clipboard-text.json in the data folder so it outlasts a restart. Copying
/// something already kept brings it back to the front rather than keeping
/// it twice.
///
/// Only touched from the UI thread.
public static class TextHistory
{
    /// Longer than this, and it is not kept: a whole document or a log file
    /// copied in one go is not something to pick out of a list again.
    public const int MaxLength = 200_000;

    // Static fields are set up in the order they are written: the path
    // comes before the clips that are loaded from it.
    private static readonly string FilePath = Path.Combine(AppInfo.DataFolder, "clipboard-text.json");
    private static readonly List<TextClip> clips = Load();
    private static DispatcherQueueTimer? saveSoon;

    public static IReadOnlyList<TextClip> Items => clips;

    /// Raised after any change, with the clip that was just copied, if that
    /// is what changed.
    public static event Action<TextClip?>? Changed;

    public static void Add(string text, string? source)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength) return;
        var clip = clips.Find(c => c.Text == text);
        if (clip is null)
        {
            clip = new TextClip { Text = text };
        }
        else
        {
            clips.Remove(clip);
            // Copied again from somewhere that said who it was.
            if (source is not null) clip.Source = source;
        }
        clip.CopiedAt = DateTime.UtcNow;
        clip.Source ??= source;
        // The front of its own kind: the pinned ones stay ahead of the rest.
        clips.Insert(clip.Pinned ? 0 : PinnedCount, clip);
        Trim(announce: false);
        Saved(clip);
    }

    public static void Remove(TextClip clip)
    {
        if (clips.Remove(clip)) Saved(null);
    }

    public static int PinnedCount => clips.Count(c => c.Pinned);

    /// How many can be pinned at once.
    public static int MaxPinned => Math.Max(1, Settings.Current.MaxPinnedText);

    /// Pinned, it moves up among the pinned ones at the front; unpinned, back
    /// among the rest by when it was copied. False if that would pin more
    /// than the settings allow.
    public static bool SetPinned(TextClip clip, bool pinned)
    {
        if (clip.Pinned == pinned || !clips.Contains(clip)) return true;
        if (pinned && PinnedCount >= MaxPinned) return false;
        clip.Pinned = pinned;
        Order(clips);
        Saved(null);
        return true;
    }

    /// Everything but what is pinned.
    public static void Clear()
    {
        if (clips.RemoveAll(c => !c.Pinned) > 0) Saved(null);
    }

    /// Lets go of the oldest beyond the limit in the settings. Pinned clips
    /// stay and do not count.
    public static void Trim(bool announce = true)
    {
        int limit = Math.Max(1, Settings.Current.MaxTextHistory);
        int kept = 0;
        int removed = clips.RemoveAll(c => !c.Pinned && ++kept > limit);
        if (removed > 0 && announce) Saved(null);
    }

    /// Deletes what was copied longer ago than the settings keep it.
    /// Pinned clips stay.
    public static void Expire()
    {
        int days = Settings.Current.TextHistoryDays;
        if (days <= 0) return;
        var before = DateTime.UtcNow - TimeSpan.FromDays(days);
        int removed = clips.RemoveAll(c => !c.Pinned && c.CopiedAt < before);
        if (removed > 0)
        {
            Log.Write($"Deleted {removed} piece(s) of copied text older than {days} days");
            Saved(null);
        }
    }

    /// Pinned first, then the most recently copied.
    private static void Order(List<TextClip> list)
    {
        var ordered = list.OrderByDescending(c => c.Pinned).ThenByDescending(c => c.CopiedAt).ToList();
        list.Clear();
        list.AddRange(ordered);
    }

    // MARK: Saving

    private static void Saved(TextClip? copied)
    {
        // A burst of copies is written once.
        if (saveSoon is null && DispatcherQueue.GetForCurrentThread() is { } queue)
        {
            saveSoon = queue.CreateTimer();
            saveSoon.Interval = TimeSpan.FromSeconds(1);
            saveSoon.IsRepeating = false;
            saveSoon.Tick += (_, _) => SaveNow();
        }
        saveSoon?.Stop();
        saveSoon?.Start();
        Changed?.Invoke(copied);
    }

    /// Writes what is pending at once, as Clipline quits.
    public static void SaveNow()
    {
        saveSoon?.Stop();
        string json = JsonSerializer.Serialize(clips, TextHistoryJson.Default.ListTextClip);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // Written beside it first, so a crash halfway leaves the old one.
            string temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, json);
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write("The clipboard text history could not be saved", e);
        }
    }

    private static List<TextClip> Load()
    {
        try
        {
            if (File.Exists(FilePath)
                && JsonSerializer.Deserialize(File.ReadAllText(FilePath), TextHistoryJson.Default.ListTextClip) is { } loaded)
            {
                loaded.RemoveAll(c => string.IsNullOrEmpty(c.Text));
                // Kept before pinned ones went first.
                Order(loaded);
                return loaded;
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Write("The clipboard text history could not be read; starting afresh", e);
        }
        return new();
    }
}

/// The history's JSON shape, worked out at compile time so trimming the
/// published app cannot break it.
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(List<TextClip>))]
internal sealed partial class TextHistoryJson : JsonSerializerContext;
