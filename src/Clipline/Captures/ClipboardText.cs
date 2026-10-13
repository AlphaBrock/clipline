using System.Diagnostics;

namespace Clipline;

/// Text copied in any app goes into the text history, along with the name
/// of the app it came from.
///
/// Left out: what Clipline itself copies (copying a note again must not
/// shuffle the line), and whatever an app asks clipboard tools to leave
/// alone, as password managers do.
public sealed class ClipboardText
{
    /// App names by program path: looking them up reads the program's file.
    private readonly Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);
    private int reading;

    /// Called whenever the clipboard changes, on the UI thread.
    public async void OnClipboardChanged()
    {
        if (!Settings.Current.KeepClipboardText || !Native.ClipboardHasText()) return;
        uint owner = Native.ClipboardOwnerProcessId();
        if (owner == Environment.ProcessId) return;
        // One read at a time: an app that puts its text there several times
        // over in a burst has the last of it kept.
        int ticket = ++reading;

        string? text;
        string? source;
        try
        {
            (text, source) = await Task.Run(() => (Native.ReadClipboardText(TextHistory.MaxLength), AppName(owner)));
        }
        catch (Exception e)
        {
            Log.Write("Could not read the text on the clipboard", e);
            return;
        }
        if (ticket != reading || text is null) return;
        TextHistory.Add(text, source);
    }

    /// What the app calls itself, "Microsoft Edge" rather than "msedge".
    private string? AppName(uint pid)
    {
        if (pid == 0 || Native.ProcessPath(pid) is not { } path) return null;
        lock (names)
        {
            if (names.TryGetValue(path, out var known)) return known;
        }
        string name;
        try
        {
            string? description = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
            name = string.IsNullOrEmpty(description) ? Path.GetFileNameWithoutExtension(path) : description;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            name = Path.GetFileNameWithoutExtension(path);
        }
        lock (names) names[path] = name;
        return name;
    }
}
