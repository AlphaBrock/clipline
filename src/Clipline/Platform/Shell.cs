using Windows.System;
using static Clipline.Native;

namespace Clipline;

/// The tray icon, its menu and the global shortcut. All three talk to the
/// app through window messages, so they hook into the line's window.
public sealed partial class Shell : IDisposable
{
    private const int HotKeyId = 1;
    private const uint TrayMessage = WM_APP + 1;
    /// Sent by a second copy of Clipline to the one already running.
    private const uint OpenSettingsMessage = WM_APP + 2;
    private const int MenuToggle = 1, MenuOpenFolder = 2, MenuSettings = 3, MenuQuit = 4;

    public event Action? ToggleRequested;
    public event Action? OpenFolderRequested;
    public event Action? SettingsRequested;
    public event Action? QuitRequested;
    /// Something new was put on the clipboard, by any app.
    public event Action? ClipboardChanged;

    /// The shortcut from the settings is registered and working.
    public bool HasHotKey { get; private set; }

    private readonly IntPtr hwnd;
    // Kept in a field so the garbage collector leaves the callback alone.
    private readonly SubclassProc proc;
    /// Sent to every window when Explorer restarts and the tray is empty again.
    private readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private IntPtr icon;

    public Shell(IntPtr hwnd)
    {
        this.hwnd = hwnd;
        proc = WindowProc;
        SetWindowSubclass(hwnd, proc, UIntPtr.Zero, UIntPtr.Zero);
    }

    public void Install()
    {
        int size = GetSystemMetrics(SM_CXSMICON);
        icon = LoadImage(IntPtr.Zero, Path.Combine(AppContext.BaseDirectory, "Assets", "Clipline.ico"),
                         IMAGE_ICON, size, size, LR_LOADFROMFILE);
        AddTrayIcon();
        ApplyHotKey();
        AddClipboardFormatListener(hwnd);
    }

    // MARK: Shortcut

    /// Registers the shortcut from the settings, or none if it is turned off.
    /// False when another app already owns that combination.
    public bool ApplyHotKey()
    {
        UnregisterHotKey(hwnd, HotKeyId);
        var settings = Settings.Current;
        HasHotKey = settings.HotKeyEnabled
            && RegisterHotKey(hwnd, HotKeyId, settings.HotKeyModifiers | MOD_NOREPEAT, settings.HotKeyKey);
        UpdateTip();
        return HasHotKey;
    }

    /// Lets go of the shortcut for a moment, while a new one is being chosen.
    public void SuspendHotKey()
    {
        UnregisterHotKey(hwnd, HotKeyId);
        HasHotKey = false;
    }

    /// "Ctrl + Alt + T", in the order Windows itself writes them.
    public static string Describe(uint modifiers, uint key)
    {
        var parts = new List<string>();
        if ((modifiers & MOD_WIN) != 0) parts.Add("Win");
        if ((modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & MOD_SHIFT) != 0) parts.Add("Shift");
        if (key != 0) parts.Add(KeyName((VirtualKey)key));
        return string.Join(" + ", parts);
    }

    private static string KeyName(VirtualKey key) => key switch
    {
        >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((char)key).ToString(),
        >= VirtualKey.A and <= VirtualKey.Z => ((char)key).ToString(),
        VirtualKey.Space => "Space",
        (VirtualKey)0xC0 => "`",
        (VirtualKey)0xBD => "-",
        (VirtualKey)0xBB => "=",
        (VirtualKey)0xDB => "[",
        (VirtualKey)0xDD => "]",
        (VirtualKey)0xBA => ";",
        (VirtualKey)0xDE => "'",
        (VirtualKey)0xBC => ",",
        (VirtualKey)0xBE => ".",
        (VirtualKey)0xBF => "/",
        (VirtualKey)0xDC => "\\",
        _ => key.ToString(),
    };

    // MARK: Tray

    private void AddTrayIcon()
    {
        var data = TrayData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = (int)TrayMessage;
        data.hIcon = icon;
        data.szTip = Tip();
        Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private void UpdateTip()
    {
        var data = TrayData();
        data.uFlags = NIF_TIP;
        data.szTip = Tip();
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private string Tip() => HasHotKey
        ? $"Clipline · {Describe(Settings.Current.HotKeyModifiers, Settings.Current.HotKeyKey)}"
        : "Clipline";

    private NOTIFYICONDATA TrayData() => new()
    {
        cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = hwnd,
        uID = 1,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotKeyId)
        {
            ToggleRequested?.Invoke();
            return IntPtr.Zero;
        }
        if (msg == TrayMessage)
        {
            switch ((uint)(lParam.ToInt64() & 0xFFFF))
            {
                case WM_LBUTTONUP: ToggleRequested?.Invoke(); break;
                case WM_RBUTTONUP: ShowMenu(); break;
            }
            return IntPtr.Zero;
        }
        if (msg == WM_CLIPBOARDUPDATE)
        {
            ClipboardChanged?.Invoke();
            return IntPtr.Zero;
        }
        if (msg == OpenSettingsMessage)
        {
            SettingsRequested?.Invoke();
            return IntPtr.Zero;
        }
        if (msg == taskbarCreated) AddTrayIcon();
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    /// Finds the running copy's line window, titled "Clipline", and asks it
    /// to open its settings. It may open them in front of this app's
    /// windows, since it is the one the user just launched.
    public static void AskRunningCopyToOpenSettings()
    {
        IntPtr other = FindWindow("WinUIDesktopWin32WindowClass", "Clipline");
        if (other == IntPtr.Zero) return;
        GetWindowThreadProcessId(other, out uint pid);
        AllowSetForegroundWindow(pid);
        PostMessage(other, OpenSettingsMessage, IntPtr.Zero, IntPtr.Zero);
    }

    private void ShowMenu()
    {
        string toggle = HasHotKey
            ? $"显示或隐藏晾衣绳\t{Describe(Settings.Current.HotKeyModifiers, Settings.Current.HotKeyKey)}"
            : "显示或隐藏晾衣绳";
        IntPtr menu = CreatePopupMenu();
        AppendMenu(menu, MF_STRING, MenuToggle, toggle);
        AppendMenu(menu, MF_STRING, MenuOpenFolder, "打开截图文件夹");
        AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
        AppendMenu(menu, MF_STRING, MenuSettings, "设置…");
        AppendMenu(menu, MF_STRING, MenuQuit, "退出");

        var p = CursorPosition();
        // Without this the menu stays open when you click elsewhere.
        SetForegroundWindow(hwnd);
        int choice = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, p.X, p.Y, hwnd, IntPtr.Zero);
        PostMessage(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);

        switch (choice)
        {
            case MenuToggle: ToggleRequested?.Invoke(); break;
            case MenuOpenFolder: OpenFolderRequested?.Invoke(); break;
            case MenuSettings: SettingsRequested?.Invoke(); break;
            case MenuQuit: QuitRequested?.Invoke(); break;
        }
    }

    public void Dispose()
    {
        var data = TrayData();
        Shell_NotifyIcon(NIM_DELETE, ref data);
        if (icon != IntPtr.Zero) DestroyIcon(icon);
        UnregisterHotKey(hwnd, HotKeyId);
        RemoveClipboardFormatListener(hwnd);
        RemoveWindowSubclass(hwnd, proc, UIntPtr.Zero);
    }
}
