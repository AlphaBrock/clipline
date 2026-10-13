using System.Diagnostics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;
using WinRT.Interop;

namespace Clipline;

/// Every setting, each applied the moment it changes: there is no Save
/// button, as in Windows' own Settings.
public sealed partial class SettingsWindow : Window
{
    private static SettingsWindow? current;

    private readonly Shell shell;
    private Settings S => Settings.Current;
    /// Waiting for the user to press a new shortcut.
    private bool recording;
    /// The launch toggle is being set to what Windows says, not by the user.
    private bool showingLaunch;

    /// Brings the settings window forward, opening it if needed.
    public static void Show(Shell shell)
    {
        current ??= new SettingsWindow(shell);
        current.Activate();
    }

    private SettingsWindow(Shell shell)
    {
        InitializeComponent();
        this.shell = shell;
        Title = "Clipline 设置";
        WindowChrome.Prepare(this, TitleBar, Root, 860, 760);
        Closed += (_, _) =>
        {
            if (recording) StopRecording();
            current = null;
        };

        Load();
        Wire();
    }

    // MARK: Showing what is set

    private void Load()
    {
        RevealAtEdge.IsOn = S.RevealAtEdge;
        RevealDelay.Value = S.RevealDelay;
        RevealOnCapture.IsOn = S.RevealOnCapture;
        QuietInFullScreen.IsOn = S.QuietInFullScreen;
        MaxVisible.Value = S.MaxVisible;
        MaxHanging.Value = S.MaxHanging;
        MaxPinnedCaptures.Value = S.MaxPinnedCaptures;
        RecentAtLaunch.Value = S.RecentAtLaunch;
        HangRecordings.IsOn = S.HangRecordings;
        HangClipboardScreenshots.IsOn = S.HangClipboardScreenshots;
        ClipboardScreenshotDays.Value = S.ClipboardScreenshotDays;
        HangGameBarCaptures.IsOn = S.HangGameBarCaptures;
        GameBarFolderText.Text = "Win+Alt+PrtScn 和 Win+Alt+R 存在 " + S.GameBarFolder;
        KeepClipboardText.IsOn = S.KeepClipboardText;
        MaxTextHistory.Value = S.MaxTextHistory;
        TextHistoryDays.Value = S.TextHistoryDays;
        MaxPinnedText.Value = S.MaxPinnedText;
        HotKeyEnabled.IsOn = S.HotKeyEnabled;
        Theme.SelectedIndex = (int)S.Theme;
        ReduceMotion.IsOn = S.ReduceMotion;
        _ = ShowLaunchAtSignInAsync();
        AboutText.Text = $"版本 {AppInfo.Version}" + (AppInfo.Edition is { } edition ? $" · {edition}" : "");
        if (AppInfo.RepositoryUrl is { } repository)
        {
            ProjectLink.NavigateUri = new Uri(repository);
            ProjectLink.Visibility = Visibility.Visible;
            ReleasesLink.NavigateUri = new Uri(repository + "/releases");
            ReleasesLink.Visibility = Visibility.Visible;
        }
        ShowChangelog();
        ShowDependents();
    }

    private async Task ShowLaunchAtSignInAsync()
    {
        bool on = await SignInLaunch.IsEnabledAsync();
        showingLaunch = true;
        LaunchAtSignIn.IsOn = on;
        showingLaunch = false;
    }

    /// Every version in CHANGELOG.md, newest first: its number and date, then
    /// each group's changes as a bulleted list.
    private void ShowChangelog()
    {
        var releases = Changelog.Releases;
        ChangelogCard.Description = releases.FirstOrDefault(r => !r.IsUnreleased) is { } latest
            ? $"最新版本 {latest.Version}" + (latest.Date is null ? "" : $"，发布于 {latest.Date}")
            : "还没有发布过版本";

        var versionStyle = (Style)Root.Resources["ChangelogVersion"];
        var metaStyle = (Style)Root.Resources["ChangelogMeta"];
        var itemStyle = (Style)Root.Resources["ChangelogItem"];
        foreach (var release in releases)
        {
            var block = new StackPanel { Spacing = 6 };
            var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            heading.Children.Add(new TextBlock { Style = versionStyle, Text = release.IsUnreleased ? "开发中" : release.Version });
            string meta = string.Join(" · ", new[]
            {
                release.Date,
                release.Version == AppInfo.Version ? "当前版本" : null,
            }.OfType<string>());
            if (meta.Length > 0)
                heading.Children.Add(new TextBlock { Style = metaStyle, Text = meta, VerticalAlignment = VerticalAlignment.Center });
            block.Children.Add(heading);

            foreach (var group in release.Groups)
            {
                if (group.Title is not null)
                    block.Children.Add(new TextBlock { Style = metaStyle, Text = group.Title, Margin = new Thickness(0, 4, 0, 0) });
                foreach (string item in group.Items)
                {
                    // The bullet in a column of its own, so wrapped lines
                    // line up under the text rather than under the bullet.
                    var row = new Grid { ColumnSpacing = 8 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    row.Children.Add(new TextBlock { Style = itemStyle, Text = "•" });
                    var text = new TextBlock { Style = itemStyle, Text = item };
                    Grid.SetColumn(text, 1);
                    row.Children.Add(text);
                    block.Children.Add(row);
                }
            }
            ChangelogPanel.Children.Add(block);
        }
    }

    /// Texts and enabled states that follow from other settings.
    private void ShowDependents()
    {
        DelayCard.IsEnabled = S.RevealAtEdge;
        RevealDelayText.Text = $"{S.RevealDelay} 毫秒";
        MaxVisibleText.Text = $"{S.MaxVisible} 张";
        ScreenshotFolderText.Text = S.EffectiveScreenshotFolder + (S.ScreenshotFolder is null ? "（Windows 默认）" : "");
        ScreenshotFolderReset.IsEnabled = S.ScreenshotFolder is not null;
        RecordingFolderCard.IsEnabled = S.HangRecordings;
        RecordingFolderText.Text = S.EffectiveRecordingFolder + (S.RecordingFolder is null ? "（Snipping Tool 默认）" : "");
        RecordingFolderReset.IsEnabled = S.RecordingFolder is not null;
        MaxTextHistoryCard.IsEnabled = S.KeepClipboardText;
        TextHistoryDaysCard.IsEnabled = S.KeepClipboardText;
        MaxPinnedTextCard.IsEnabled = S.KeepClipboardText;
        ClipboardScreenshotDaysText.Text = S.ClipboardScreenshotDays > 0
            ? $"存下来超过 {S.ClipboardScreenshotDays} 天的剪贴板截图移到回收站，置顶的除外。只清理 Clipline 自己的 {Settings.ClipboardFolder}，其他文件夹不动。"
            : $"0 天表示不清理，{Settings.ClipboardFolder} 里的截图一直留着。";
        TextHistoryDaysText.Text = S.TextHistoryDays > 0
            ? $"复制超过 {S.TextHistoryDays} 天的文字自动删除，置顶的除外。"
            : "0 天表示不按时间删除，只按上面的条数。";
        ShowTextHistory();
        HotKeyCard.IsEnabled = S.HotKeyEnabled;
        if (!recording) HotKeyText.Text = Shell.Describe(S.HotKeyModifiers, S.HotKeyKey);
        ShowHotKeyProblem();
    }

    private void ShowTextHistory()
    {
        var clips = TextHistory.Items;
        int pinned = clips.Count(c => c.Pinned);
        TextHistoryText.Text = clips.Count == 0
            ? "还没有记录。记录只保存在本机的数据文件夹里。"
            : $"现有 {clips.Count} 条" + (pinned > 0 ? $"，其中 {pinned} 条已置顶，清空时会留下" : "") + "。记录只保存在本机的数据文件夹里。";
        ClearTextHistory.IsEnabled = clips.Count > pinned;
    }

    private void ShowHotKeyProblem()
    {
        bool taken = S.HotKeyEnabled && !shell.HasHotKey && !recording;
        HotKeyProblem.IsOpen = taken;
        if (taken)
            HotKeyProblem.Message = $"{Shell.Describe(S.HotKeyModifiers, S.HotKeyKey)} 已被其他程序占用，换一个组合键试试。";
    }

    // MARK: Changing it

    private void Wire()
    {
        RevealAtEdge.Toggled += (_, _) => Change(nameof(S.RevealAtEdge), s => s.RevealAtEdge = RevealAtEdge.IsOn);
        RevealDelay.ValueChanged += (_, e) => Change(nameof(S.RevealDelay), s => s.RevealDelay = (int)e.NewValue);
        RevealOnCapture.Toggled += (_, _) => Change(nameof(S.RevealOnCapture), s => s.RevealOnCapture = RevealOnCapture.IsOn);
        QuietInFullScreen.Toggled += (_, _) => Change(nameof(S.QuietInFullScreen), s => s.QuietInFullScreen = QuietInFullScreen.IsOn);
        MaxVisible.ValueChanged += (_, e) => Change(nameof(S.MaxVisible), s => s.MaxVisible = (int)e.NewValue);
        MaxHanging.ValueChanged += (_, e) =>
        {
            if (!double.IsNaN(e.NewValue)) Change(nameof(S.MaxHanging), s => s.MaxHanging = (int)e.NewValue);
        };
        MaxPinnedCaptures.ValueChanged += (_, e) =>
        {
            if (!double.IsNaN(e.NewValue)) Change(nameof(S.MaxPinnedCaptures), s => s.MaxPinnedCaptures = (int)e.NewValue);
        };
        ClipboardScreenshotDays.ValueChanged += (_, e) =>
        {
            if (!double.IsNaN(e.NewValue)) Change(nameof(S.ClipboardScreenshotDays), s => s.ClipboardScreenshotDays = (int)e.NewValue);
        };
        TextHistoryDays.ValueChanged += (_, e) =>
        {
            if (!double.IsNaN(e.NewValue)) Change(nameof(S.TextHistoryDays), s => s.TextHistoryDays = (int)e.NewValue);
        };
        MaxPinnedText.ValueChanged += (_, e) =>
        {
            if (!double.IsNaN(e.NewValue)) Change(nameof(S.MaxPinnedText), s => s.MaxPinnedText = (int)e.NewValue);
        };
        RecentAtLaunch.ValueChanged += (_, e) =>
        {
            if (!double.IsNaN(e.NewValue)) Change(nameof(S.RecentAtLaunch), s => s.RecentAtLaunch = (int)e.NewValue);
        };

        ScreenshotFolderPick.Click += async (_, _) =>
        {
            if (await PickFolderAsync() is { } path) Change(nameof(S.ScreenshotFolder), s => s.ScreenshotFolder = path);
        };
        ScreenshotFolderReset.Click += (_, _) => Change(nameof(S.ScreenshotFolder), s => s.ScreenshotFolder = null);
        HangRecordings.Toggled += (_, _) => Change(nameof(S.HangRecordings), s => s.HangRecordings = HangRecordings.IsOn);
        RecordingFolderPick.Click += async (_, _) =>
        {
            if (await PickFolderAsync() is { } path) Change(nameof(S.RecordingFolder), s => s.RecordingFolder = path);
        };
        RecordingFolderReset.Click += (_, _) => Change(nameof(S.RecordingFolder), s => s.RecordingFolder = null);
        HangClipboardScreenshots.Toggled += (_, _) =>
            Change(nameof(S.HangClipboardScreenshots), s => s.HangClipboardScreenshots = HangClipboardScreenshots.IsOn);
        HangGameBarCaptures.Toggled += (_, _) =>
            Change(nameof(S.HangGameBarCaptures), s => s.HangGameBarCaptures = HangGameBarCaptures.IsOn);
        KeepClipboardText.Toggled += (_, _) =>
            Change(nameof(S.KeepClipboardText), s => s.KeepClipboardText = KeepClipboardText.IsOn);
        MaxTextHistory.ValueChanged += (_, e) =>
        {
            if (!double.IsNaN(e.NewValue)) Change(nameof(S.MaxTextHistory), s => s.MaxTextHistory = (int)e.NewValue);
        };
        ClearTextHistory.Click += (_, _) => TextHistory.Clear();
        // The count follows copies made while the window is open.
        TextHistory.Changed += OnTextHistoryChanged;
        Closed += (_, _) => TextHistory.Changed -= OnTextHistoryChanged;

        HotKeyEnabled.Toggled += (_, _) =>
        {
            Change(nameof(S.HotKeyEnabled), s => s.HotKeyEnabled = HotKeyEnabled.IsOn);
            shell.ApplyHotKey();
            ShowDependents();
        };
        HotKeyButton.Click += (_, _) => StartRecording();
        Root.PreviewKeyDown += OnPreviewKeyDown;
        HotKeyButton.LostFocus += (_, _) => { if (recording) StopRecording(); };

        Theme.SelectionChanged += (_, _) => Change(nameof(S.Theme), s => s.Theme = (ThemeChoice)Theme.SelectedIndex);
        ReduceMotion.Toggled += (_, _) => Change(nameof(S.ReduceMotion), s => s.ReduceMotion = ReduceMotion.IsOn);
        LaunchAtSignIn.Toggled += async (_, _) =>
        {
            if (showingLaunch) return;
            bool wanted = LaunchAtSignIn.IsOn;
            bool on = await SignInLaunch.SetAsync(wanted);
            LaunchProblem.IsOpen = wanted && !on;
            if (on != LaunchAtSignIn.IsOn)
            {
                showingLaunch = true;
                LaunchAtSignIn.IsOn = on;
                showingLaunch = false;
            }
        };
        OpenLogs.Click += (_, _) =>
        {
            string folder = Path.GetDirectoryName(Log.FilePath)!;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        };
    }

    private void OnTextHistoryChanged(TextClip? copied) => ShowTextHistory();

    private void Change(string name, Action<Settings> change)
    {
        S.Update(name, change);
        ShowDependents();
    }

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    // MARK: Recording a shortcut

    private void StartRecording()
    {
        recording = true;
        // Otherwise pressing the current shortcut would toggle the line
        // instead of reaching this window.
        shell.SuspendHotKey();
        HotKeyText.Text = "请按下组合键…";
        HotKeyProblem.IsOpen = false;
    }

    private void StopRecording()
    {
        recording = false;
        shell.ApplyHotKey();
        ShowDependents();
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!recording) return;
        e.Handled = true;
        var key = e.Key;
        if (key == VirtualKey.Escape)
        {
            StopRecording();
            return;
        }

        uint modifiers = 0;
        if (IsDown(VirtualKey.Control)) modifiers |= Native.MOD_CONTROL;
        if (IsDown(VirtualKey.Menu)) modifiers |= Native.MOD_ALT;
        if (IsDown(VirtualKey.Shift)) modifiers |= Native.MOD_SHIFT;
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) modifiers |= Native.MOD_WIN;

        // Only modifiers so far: show them and wait for the rest.
        if (key is VirtualKey.Control or VirtualKey.Menu or VirtualKey.Shift or VirtualKey.LeftWindows
                or VirtualKey.RightWindows or VirtualKey.LeftControl or VirtualKey.RightControl
                or VirtualKey.LeftMenu or VirtualKey.RightMenu or VirtualKey.LeftShift or VirtualKey.RightShift)
        {
            HotKeyText.Text = Shell.Describe(modifiers, 0) + " …";
            return;
        }
        if (modifiers == 0 || modifiers == Native.MOD_SHIFT)
        {
            HotKeyText.Text = "要带上 Ctrl、Alt 或 Win";
            return;
        }

        S.Update(nameof(S.HotKeyKey), s =>
        {
            s.HotKeyModifiers = modifiers;
            s.HotKeyKey = (uint)key;
        });
        StopRecording();
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}
