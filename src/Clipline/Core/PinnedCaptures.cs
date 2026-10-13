using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clipline;

/// The captures pinned to the front of the line, by path, kept in
/// pinned-captures.json in the data folder so they are hung again at launch
/// however old they are. A pinned screenshot from the clipboard is also
/// never cleared away for its age.
///
/// Only touched from the UI thread, but read by the clearing-away on
/// another, so reads take a copy under the lock.
public static class PinnedCaptures
{
    // Static fields are set up in the order they are written: the path
    // comes before the pins that are loaded from it.
    private static readonly string FilePath = Path.Combine(AppInfo.DataFolder, "pinned-captures.json");
    private static readonly HashSet<string> paths = Load();

    public static int Count
    {
        get { lock (paths) return paths.Count; }
    }

    public static string[] Paths
    {
        get { lock (paths) return paths.ToArray(); }
    }

    public static bool Contains(string path)
    {
        lock (paths) return paths.Contains(path);
    }

    /// Pins or unpins it. False if that would pin more than the settings allow.
    public static bool Set(string path, bool pinned)
    {
        lock (paths)
        {
            if (paths.Contains(path) == pinned) return true;
            if (pinned && paths.Count >= Math.Max(1, Settings.Current.MaxPinnedCaptures)) return false;
            if (pinned) paths.Add(path);
            else paths.Remove(path);
        }
        Save();
        return true;
    }

    private static void Save()
    {
        string json;
        lock (paths) json = JsonSerializer.Serialize(paths.ToList(), PinnedCapturesJson.Default.ListString);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write("The pinned captures could not be saved", e);
        }
    }

    /// Pins of files that have since gone are forgotten.
    private static HashSet<string> Load()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(FilePath)
                && JsonSerializer.Deserialize(File.ReadAllText(FilePath), PinnedCapturesJson.Default.ListString) is { } list)
                set.UnionWith(list.Where(File.Exists));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Write("The pinned captures could not be read; none are pinned", e);
        }
        return set;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class PinnedCapturesJson : JsonSerializerContext;
