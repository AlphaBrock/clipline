using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Media.Core;
using Windows.Media.Editing;
using Windows.Media.Playback;
using Windows.Media.Transcoding;
using Windows.Storage;
using Launcher = Windows.System.Launcher;
using VirtualKey = Windows.System.VirtualKey;

namespace Clipline;

/// Trims a screen recording in place. It is to recordings what Paint is to
/// screenshots: press and hold, pick the part to keep, and the result is
/// written over the original file.
public sealed partial class TrimWindow : Window
{
    private static TrimWindow? current;

    private readonly StorageFile file;
    /// Shown the rendered copy before it is deleted, once it has replaced the original.
    private readonly Func<StorageFile, Task> onSaved;
    private readonly DispatcherQueueTimer clock;
    private MediaPlayer? player;
    private TimeSpan length;
    private bool saving;

    /// Opens the trimming window for a recording, closing any other one.
    public static void Show(StorageFile file, Func<StorageFile, Task> onSaved)
    {
        // Only MP4 can be written back the way it came.
        if (!file.FileType.Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            _ = Launcher.LaunchFileAsync(file);
            return;
        }
        current?.Close();
        current = new TrimWindow(file, onSaved);
        current.Activate();
    }

    private TrimWindow(StorageFile file, Func<StorageFile, Task> onSaved)
    {
        InitializeComponent();
        this.file = file;
        this.onSaved = onSaved;
        Title = $"裁剪 · {file.Name}";
        TitleText.Text = Title;
        WindowChrome.Prepare(this, TitleBar, Root, 780, 600);

        CreatePlayer();

        clock = DispatcherQueue.CreateTimer();
        clock.Interval = TimeSpan.FromMilliseconds(40);
        clock.Tick += (_, _) => KeepInRange();

        PlayButton.Click += (_, _) => TogglePlay();
        Range.ValueChanged += OnRangeChanged;
        CancelButton.Click += (_, _) => Close();
        SaveButton.Click += (_, _) => _ = SaveAsync();
        Root.KeyDown += OnKeyDown;
        Closed += (_, _) =>
        {
            clock.Stop();
            ReleasePlayer();
            if (current == this) current = null;
        };
    }

    // MARK: Playing

    private void CreatePlayer()
    {
        player = new MediaPlayer { Source = MediaSource.CreateFromStorageFile(file), AutoPlay = false };
        player.MediaOpened += (_, _) => DispatcherQueue.TryEnqueue(Opened);
        player.MediaFailed += (_, e) => DispatcherQueue.TryEnqueue(() => Fail($"无法播放这个录屏：{e.ErrorMessage}"));
        player.PlaybackSession.PlaybackStateChanged += (_, _) => DispatcherQueue.TryEnqueue(ShowPlayState);
        Player.SetMediaPlayer(player);
    }

    private void Opened()
    {
        if (player is null) return;
        bool first = length == TimeSpan.Zero;
        length = player.PlaybackSession.NaturalDuration;
        if (first)
        {
            Range.Maximum = length.TotalSeconds;
            Range.RangeStart = 0;
            Range.RangeEnd = length.TotalSeconds;
            Range.StepFrequency = Math.Max(0.01, length.TotalSeconds / 1000);
        }
        // Back from a failed save, the kept range is still what was chosen.
        player.PlaybackSession.Position = Start;
        Range.IsEnabled = PlayButton.IsEnabled = SaveButton.IsEnabled = true;
        ShowRange();
        clock.Start();
    }

    private TimeSpan Start => TimeSpan.FromSeconds(Range.RangeStart);
    private TimeSpan End => TimeSpan.FromSeconds(Range.RangeEnd);

    private void TogglePlay()
    {
        if (player is null) return;
        var session = player.PlaybackSession;
        if (session.PlaybackState == MediaPlaybackState.Playing)
        {
            player.Pause();
            return;
        }
        if (session.Position < Start || session.Position >= End - TimeSpan.FromMilliseconds(50))
            session.Position = Start;
        player.Play();
    }

    /// Plays only the kept part, round and round.
    private void KeepInRange()
    {
        if (player is null) return;
        var session = player.PlaybackSession;
        if (session.PlaybackState == MediaPlaybackState.Playing && session.Position >= End)
            session.Position = Start;
    }

    private void ShowPlayState()
    {
        bool playing = player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
        PlayIcon.Glyph = playing ? "" : "";
    }

    /// Dragging either end shows the frame it is on.
    private void OnRangeChanged(object? sender, RangeChangedEventArgs e)
    {
        if (player is null) return;
        player.Pause();
        player.PlaybackSession.Position = TimeSpan.FromSeconds(e.NewValue);
        ShowRange();
    }

    private void ShowRange()
    {
        StartText.Text = Format(Start);
        EndText.Text = Format(End);
        var kept = End - Start;
        KeptText.Text = kept >= length - TimeSpan.FromMilliseconds(50) ? $"全部 {Format(kept)}" : $"保留 {Format(kept)}";
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.f") : t.ToString(@"m\:ss\.f");

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (saving) return;
        switch (e.Key)
        {
            case VirtualKey.Space: TogglePlay(); e.Handled = true; break;
            case VirtualKey.Escape: Close(); e.Handled = true; break;
            case VirtualKey.Enter when SaveButton.IsEnabled: _ = SaveAsync(); e.Handled = true; break;
        }
    }

    // MARK: Saving

    /// Renders the kept part to a temporary file outside the watched folder,
    /// then copies it over the original. Copying over a file only changes it,
    /// so the line never sees the recording leave or a new one arrive.
    private async Task SaveAsync()
    {
        if (saving) return;
        if (Start <= TimeSpan.Zero && End >= length - TimeSpan.FromMilliseconds(50))
        {
            Close();
            return;
        }

        saving = true;
        Problem.IsOpen = false;
        Saving.Visibility = Visibility.Visible;
        Range.IsEnabled = PlayButton.IsEnabled = SaveButton.IsEnabled = CancelButton.IsEnabled = false;
        // The player keeps the file open; let go of it before writing over it.
        ReleasePlayer();

        StorageFile? temp = null;
        try
        {
            var clip = await MediaClip.CreateFromFileAsync(file);
            clip.TrimTimeFromStart = Start;
            clip.TrimTimeFromEnd = clip.OriginalDuration - End;
            var composition = new MediaComposition();
            composition.Clips.Add(clip);

            var tempFolder = await StorageFolder.GetFolderFromPathAsync(Path.GetTempPath());
            temp = await tempFolder.CreateFileAsync($"clipline-trim-{Guid.NewGuid():N}.mp4");
            var render = composition.RenderToFileAsync(temp, MediaTrimmingPreference.Precise);
            render.Progress = (_, percent) => DispatcherQueue.TryEnqueue(() => SaveProgress.Value = percent);
            var result = await render;
            if (result != TranscodeFailureReason.None) throw new IOException($"渲染失败（{result}）");

            File.Copy(temp.Path, file.Path, overwrite: true);
            Log.Write($"Trimmed {file.Name} to {Format(Start)}–{Format(End)}");
            await onSaved(temp);
            Close();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                       or System.Runtime.InteropServices.COMException)
        {
            Log.Write($"Could not trim {file.Name}", e);
            saving = false;
            Saving.Visibility = Visibility.Collapsed;
            CancelButton.IsEnabled = true;
            Fail($"没能保存：{e.Message}");
            // Playing again lets the user retry or cancel.
            CreatePlayer();
        }
        finally
        {
            if (temp is not null)
            {
                try { File.Delete(temp.Path); }
                catch (IOException) { }
            }
        }
    }

    private void Fail(string message)
    {
        Problem.Message = message;
        Problem.IsOpen = true;
    }

    private void ReleasePlayer()
    {
        if (player is null) return;
        Player.SetMediaPlayer(null);
        player.Pause();
        player.Source = null;
        player.Dispose();
        player = null;
    }
}
