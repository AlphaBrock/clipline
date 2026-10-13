using Microsoft.UI.Dispatching;

namespace Clipline;

[Flags]
public enum CaptureKind { Screenshot = 1, Recording = 2, Both = Screenshot | Recording }

public static class Captures
{
    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };
    private static readonly HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".mkv", ".webm" };

    public static bool IsRecording(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    public static bool Matches(string path, CaptureKind kinds)
    {
        // Hidden names are work in progress, ours or another app's.
        if (Path.GetFileName(path).StartsWith('.')) return false;
        string extension = Path.GetExtension(path);
        return (kinds.HasFlag(CaptureKind.Screenshot) && ImageExtensions.Contains(extension))
            || (kinds.HasFlag(CaptureKind.Recording) && VideoExtensions.Contains(extension));
    }
}

/// Watches one folder for new captures and reports them. Clipline never
/// captures anything itself: Win+PrtScn and the Snipping Tool keep working
/// as usual, and the line just picks up what they save.
public sealed partial class CaptureWatcher : IDisposable
{
    public string Folder { get; }
    /// What it picks up there: screenshots, recordings, or both.
    public CaptureKind Kind { get; }

    /// A capture to hang: its path, and whether it was just taken (as opposed
    /// to one already there when watching started).
    public event Action<string, bool>? Added;
    public event Action<string>? Removed;

    private readonly DispatcherQueue ui;
    private readonly HashSet<string> known = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? watcher;
    private bool disposed;

    public CaptureWatcher(string folder, CaptureKind kind, DispatcherQueue ui)
    {
        Folder = folder;
        Kind = kind;
        this.ui = ui;
    }

    /// Hangs the most recent few, and any pinned ones however old, then
    /// watches for more.
    public void Start(int recent, IReadOnlyCollection<string>? pinned = null)
    {
        if (!Directory.Exists(Folder))
        {
            WaitForFolder();
            return;
        }

        var all = new DirectoryInfo(Folder).EnumerateFiles()
            .Where(f => Captures.Matches(f.FullName, Kind))
            .OrderByDescending(f => f.CreationTimeUtc)
            .ToList();
        var latest = all.Take(recent)
            .Concat(pinned is null ? [] : all.Skip(recent).Where(f => pinned.Contains(f.FullName, StringComparer.OrdinalIgnoreCase)))
            .Reverse();
        foreach (var file in latest)
        {
            known.Add(file.FullName);
            Added?.Invoke(file.FullName, false);
        }

        watcher = new FileSystemWatcher(Folder)
        {
            NotifyFilter = NotifyFilters.FileName,
            IncludeSubdirectories = false,
        };
        watcher.Created += (_, e) => Arrived(e.FullPath, fresh: true);
        watcher.Deleted += (_, e) => Gone(e.FullPath);
        watcher.Renamed += (_, e) =>
        {
            // The Snipping Tool writes under a temporary name first. A rename
            // of something already hanging just follows it along.
            bool wasHanging;
            lock (known) wasHanging = known.Contains(e.OldFullPath);
            Gone(e.OldFullPath);
            Arrived(e.FullPath, fresh: !wasHanging);
        };
        watcher.EnableRaisingEvents = true;
    }

    /// The folder does not exist yet: Windows makes it with the first capture
    /// saved there. Rather than make it ourselves, maybe under the wrong name,
    /// wait for it to appear and start then.
    private void WaitForFolder()
    {
        string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Folder));
        if (parent is null || !Directory.Exists(parent))
        {
            Log.Write($"Cannot watch {Folder}: neither it nor its parent exists");
            return;
        }
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Folder));
        watcher = new FileSystemWatcher(parent) { NotifyFilter = NotifyFilters.DirectoryName };
        watcher.Created += (_, e) =>
        {
            if (!e.Name!.Equals(name, StringComparison.OrdinalIgnoreCase)) return;
            ui.TryEnqueue(() =>
            {
                if (disposed) return;
                watcher?.Dispose();
                Start(recent: 0);
            });
        };
        watcher.EnableRaisingEvents = true;
    }

    private async void Arrived(string path, bool fresh)
    {
        if (!Captures.Matches(path, Kind)) return;
        lock (known) if (!known.Add(path)) return;

        // The file appears before it is fully written: a screenshot within a
        // moment, a long recording maybe much later. Wait until nobody is
        // writing to it any more.
        bool recording = Captures.IsRecording(path);
        var patience = recording ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(3);
        var giveUp = DateTime.UtcNow + patience;
        while (DateTime.UtcNow < giveUp && !disposed)
        {
            if (IsFinished(path))
            {
                ui.TryEnqueue(() => Added?.Invoke(path, fresh));
                return;
            }
            await Task.Delay(recording ? 500 : 120);
        }
        lock (known) known.Remove(path);
    }

    private void Gone(string path)
    {
        lock (known) if (!known.Remove(path)) return;
        ui.TryEnqueue(() => Removed?.Invoke(path));
    }

    /// Opening it while refusing to share write access only works once the
    /// app that made it has closed it. Deleting stays allowed, so Explorer is
    /// never kept waiting by this check.
    private static bool IsFinished(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return stream.Length > 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public void Dispose()
    {
        disposed = true;
        watcher?.Dispose();
    }
}
