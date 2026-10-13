using System.Runtime.InteropServices;

namespace Clipline;

/// A display, in physical pixels.
public readonly record struct Monitor(IntPtr Handle, Native.RECT Bounds, double Scale);

/// The Win32 calls WinUI does not cover: the cursor, monitors, window styles,
/// the click-through region, the tray, the hotkey, the known folders and
/// the working set.
public static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public readonly bool Contains(POINT p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;
    }

    // MARK: Cursor and monitors

    public static POINT CursorPosition()
    {
        GetCursorPos(out var p);
        return p;
    }

    public static Monitor MonitorAt(POINT p)
    {
        var handle = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(handle, ref info);
        double scale = GetDpiForMonitor(handle, MDT_EFFECTIVE_DPI, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
        return new Monitor(handle, info.rcMonitor, scale);
    }

    public static Monitor MonitorUnderCursor() => MonitorAt(CursorPosition());

    public static bool IsLeftButtonDown() => (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;

    /// True while a full screen game, video or presentation owns the screen.
    public static bool IsSomethingFullScreen()
    {
        if (SHQueryUserNotificationState(out var state) != 0) return false;
        return state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;
    }

    // MARK: The line's window

    /// Keeps the window off the taskbar and Alt+Tab, and stops clicks on it
    /// from stealing focus from the app you were using.
    public static void MakeOverlay(IntPtr hwnd)
    {
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        ex &= ~WS_EX_APPWINDOW;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));


        // Windows 11 draws a rounded border around every window. Not this one.
        int noRounding = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref noRounding, sizeof(int));
        int noBorder = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));

        // Under XAML's transparent content the window still has an opaque
        // surface of its own. Blur-behind over an empty, off-screen region
        // turns on per-pixel alpha without blurring anything, and erasing
        // the background to black makes that surface see-through.
        IntPtr nowhere = CreateRectRgn(-2, -2, -1, -1);
        var blur = new DWM_BLURBEHIND { dwFlags = DWM_BB_ENABLE | DWM_BB_BLURREGION, fEnable = true, hRgnBlur = nowhere };
        DwmEnableBlurBehindWindow(hwnd, ref blur);
        DeleteObject(nowhere);
        SetWindowSubclass(hwnd, overlayProc, new UIntPtr(0xE5E), UIntPtr.Zero);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    private static readonly SubclassProc overlayProc = (hwnd, msg, wParam, lParam, id, data) =>
    {
        switch (msg)
        {
            // Even without a title bar WinUI keeps a thin frame around the
            // window, which Windows 11 draws as a pale line along the top.
            // Claim the whole window as client area and there is no frame.
            case WM_NCCALCSIZE when wParam != IntPtr.Zero:
                return IntPtr.Zero;
            case WM_ERASEBKGND:
                GetClientRect(hwnd, out var client);
                FillRect(wParam, ref client, GetStockObject(BLACK_BRUSH));
                return new IntPtr(1);
            default:
                return DefSubclassProc(hwnd, msg, wParam, lParam);
        }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_BLURBEHIND
    {
        public int dwFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fEnable;
        public IntPtr hRgnBlur;
        [MarshalAs(UnmanagedType.Bool)] public bool fTransitionOnMaximized;
    }

    private const int DWM_BB_ENABLE = 0x1, DWM_BB_BLURREGION = 0x2;
    private const uint WM_ERASEBKGND = 0x0014;
    private const int BLACK_BRUSH = 4;

    [DllImport("dwmapi.dll")] private static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DWM_BLURBEHIND blur);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RECT rect, IntPtr brush);
    [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);

    public static void BringToTop(IntPtr hwnd) =>
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    /// Lets the overlay take the keyboard, for typing into it, and makes it
    /// the active window; or goes back to never taking focus.
    public static void SetActivatable(IntPtr hwnd, bool activatable)
    {
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        ex = activatable ? ex & ~WS_EX_NOACTIVATE : ex | WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
        if (activatable) SetForegroundWindow(hwnd);
    }

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);

    /// Limits the window, drawing and clicks alike, to a polygon and a
    /// rectangle. The transparent space outside them lets clicks fall through
    /// to whatever is underneath.
    public static void SetRegion(IntPtr hwnd, POINT[] polygon, RECT rect)
    {
        IntPtr region = CreatePolygonRgn(polygon, polygon.Length, WINDING);
        IntPtr part = CreateRectRgn(rect.Left, rect.Top, rect.Right, rect.Bottom);
        CombineRgn(region, region, part, RGN_OR);
        DeleteObject(part);
        // The system owns the region from here on.
        SetWindowRgn(hwnd, region, true);
    }

    // MARK: Folders

    private static readonly Guid FolderScreenshots = new("b7bede81-df94-4682-a7d8-57a52620b86f");

    /// Where Win+PrtScn and the Snipping Tool save screenshots. Usually
    /// Pictures\Screenshots, but the user can move it.
    public static string ScreenshotsFolder()
    {
        if (SHGetKnownFolderPath(FolderScreenshots, 0, IntPtr.Zero, out var raw) == 0)
        {
            try { return Marshal.PtrToStringUni(raw)!; }
            finally { Marshal.FreeCoTaskMem(raw); }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
    }

    /// Where the Snipping Tool saves screen recordings: a folder in Videos
    /// named in the language Windows was set up in, "Screen Recordings" in
    /// English and "屏幕录制" in Simplified Chinese. There is no known folder
    /// for it, so look for those names, then for a folder of files named the
    /// way the Snipping Tool names them: the folder's name, a date and a time.
    public static string RecordingsFolder()
    {
        string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        string[] names = { "Screen Recordings", "屏幕录制", "螢幕錄製" };
        foreach (var name in names)
        {
            string candidate = Path.Combine(videos, name);
            if (Directory.Exists(candidate)) return candidate;
        }

        try
        {
            foreach (var folder in Directory.EnumerateDirectories(videos))
            {
                string name = Path.GetFileName(folder);
                var pattern = new System.Text.RegularExpressions.Regex(
                    "^" + System.Text.RegularExpressions.Regex.Escape(name) + @" \d{4}-\d{2}-\d{2} \d{6}\.mp4$");
                if (Directory.EnumerateFiles(folder, "*.mp4").Any(f => pattern.IsMatch(Path.GetFileName(f))))
                    return folder;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        // Not recorded anything yet: the name it will most likely get.
        bool chinese = System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("zh-CN", StringComparison.OrdinalIgnoreCase)
                    || System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase);
        return Path.Combine(videos, chinese ? "屏幕录制" : "Screen Recordings");
    }

    private static readonly Guid FolderAppCaptures = new("edc0fe71-98d8-4f4a-b920-c8dc133cb165");

    /// Where the Xbox Game Bar saves its recordings (Win+Alt+R) and
    /// screenshots (Win+Alt+PrtScn): Videos\Captures, unless moved.
    public static string GameBarFolder()
    {
        if (SHGetKnownFolderPath(FolderAppCaptures, 0, IntPtr.Zero, out var raw) == 0)
        {
            try { return Marshal.PtrToStringUni(raw)!; }
            finally { Marshal.FreeCoTaskMem(raw); }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Captures");
    }

    /// Sends a file to the Recycle Bin, where it can still be restored.
    public static bool Recycle(string path) => Recycle([path]);

    /// Sends files to the Recycle Bin in one go.
    public static bool Recycle(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0) return true;
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            // A list of paths, each ending in a null, the list in another.
            pFrom = string.Concat(paths.Select(p => p + "\0")) + "\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }

    // MARK: Clipboard

    public const uint WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll")] public static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    private const uint CF_BITMAP = 2, CF_DIB = 8, CF_DIBV5 = 17;

    /// Whether there is an image on the clipboard, without opening it.
    public static bool ClipboardHasImage() =>
        IsClipboardFormatAvailable(CF_DIB) || IsClipboardFormatAvailable(CF_BITMAP) || IsClipboardFormatAvailable(CF_DIBV5);

    /// The name of the program that last put something on the clipboard, if
    /// it said who it was.
    public static string? ClipboardOwnerProcess()
    {
        IntPtr owner = GetClipboardOwner();
        if (owner == IntPtr.Zero) return null;
        // A window already destroyed gives no process, not "Idle".
        if (GetWindowThreadProcessId(owner, out uint pid) == 0 || pid == 0) return null;
        try { return System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
        catch (ArgumentException) { return null; }
    }

    /// The window class of the clipboard's owner, if it still has one.
    public static string? ClipboardOwnerClass()
    {
        IntPtr owner = GetClipboardOwner();
        return owner == IntPtr.Zero ? null : WindowClass(owner);
    }

    /// Whether the clipboard holds a format registered under this name.
    public static bool ClipboardHasFormat(string name)
    {
        uint format = RegisterClipboardFormat(name);
        return format != 0 && IsClipboardFormatAvailable(format);
    }

    public static string? WindowClass(IntPtr hwnd)
    {
        var name = new char[256];
        int length = GetClassName(hwnd, name, name.Length);
        return length > 0 ? new string(name, 0, length) : null;
    }

    /// The program the window belongs to, by name, and whether the window
    /// covers the whole of its screen, as a screenshot tool's does while
    /// you pick what to capture.
    public static (string? Process, bool CoversScreen) WindowOwner(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid == 0) return (null, false);
        string? name;
        try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
        catch (ArgumentException) { return (null, false); }
        if (!GetWindowRect(hwnd, out var rect)) return (name, false);
        var screen = MonitorAt(new POINT { X = (rect.Left + rect.Right) / 2, Y = (rect.Top + rect.Bottom) / 2 }).Bounds;
        bool covers = rect.Left <= screen.Left && rect.Top <= screen.Top && rect.Right >= screen.Right && rect.Bottom >= screen.Bottom;
        return (name, covers);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, char[] name, int length);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    /// The process that last put something on the clipboard, or 0.
    public static uint ClipboardOwnerProcessId()
    {
        IntPtr owner = GetClipboardOwner();
        if (owner == IntPtr.Zero) return 0;
        return GetWindowThreadProcessId(owner, out uint pid) == 0 ? 0 : pid;
    }

    /// Where a process's program is, or null if Windows will not say.
    public static string? ProcessPath(uint pid)
    {
        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            var path = new char[1024];
            int length = path.Length;
            return QueryFullProcessImageName(process, 0, path, ref length) ? new string(path, 0, length) : null;
        }
        finally { CloseHandle(process); }
    }

    public static bool ClipboardHasText() => IsClipboardFormatAvailable(CF_UNICODETEXT);

    /// The text on the clipboard, if there is any, it is no longer than
    /// `maxLength`, and the app that put it there did not ask for it to be
    /// left out of clipboard history and clipboard tools, as password
    /// managers do for passwords. Opens the clipboard, so call it away from
    /// the UI thread: an app rendering its text late can take a while.
    public static string? ReadClipboardText(int maxLength)
    {
        if (IsClipboardFormatAvailable(ExcludeFromMonitoring) || IsClipboardFormatAvailable(ViewerIgnore)) return null;
        for (int attempt = 0; !OpenClipboard(IntPtr.Zero); attempt++)
        {
            // Its owner may still hold it open for a moment.
            if (attempt == 8) return null;
            Thread.Sleep(25);
        }
        try
        {
            if (Flag(IncludeInHistory) == 0) return null;
            IntPtr data = GetClipboardData(CF_UNICODETEXT);
            if (data == IntPtr.Zero) return null;
            long chars = (long)GlobalSize(data) / 2;
            if (chars == 0 || chars > maxLength + 1) return null;
            IntPtr text = GlobalLock(data);
            if (text == IntPtr.Zero) return null;
            try
            {
                string s = Marshal.PtrToStringUni(text, (int)chars);
                int end = s.IndexOf('\0');
                return end < 0 ? s : s[..end];
            }
            finally { GlobalUnlock(data); }
        }
        finally { CloseClipboard(); }
    }

    /// A DWORD format's value on the open clipboard, or null if it has none.
    private static uint? Flag(uint format)
    {
        if (format == 0 || !IsClipboardFormatAvailable(format)) return null;
        IntPtr data = GetClipboardData(format);
        if (data == IntPtr.Zero || (long)GlobalSize(data) < 4) return null;
        IntPtr value = GlobalLock(data);
        if (value == IntPtr.Zero) return null;
        try { return (uint)Marshal.ReadInt32(value); }
        finally { GlobalUnlock(data); }
    }

    private const uint CF_UNICODETEXT = 13;
    // How apps ask clipboard history and clipboard tools to leave something
    // out: https://learn.microsoft.com/windows/win32/dataxchg/clipboard-formats#cloud-clipboard-and-clipboard-history-formats
    private static readonly uint ExcludeFromMonitoring = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint IncludeInHistory = RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private static readonly uint ViewerIgnore = RegisterClipboardFormat("Clipboard Viewer Ignore");
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, char[] path, ref int length);

    // MARK: Window messages, tray and hotkey

    public delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    public const uint WM_NULL = 0x0000;
    public const uint WM_HOTKEY = 0x0312;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_APP = 0x8000;

    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    public const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    public const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4;

    public const uint MF_STRING = 0x0, MF_SEPARATOR = 0x800;
    public const uint TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100;

    [DllImport("comctl32.dll")] public static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] public static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id);
    [DllImport("comctl32.dll")] public static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string name);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    public const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10;
    public const int SM_CXSMICON = 49;

    [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] public static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string title);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(uint pid);

    // MARK: Memory

    /// Hands the pages this process is not using right now back to Windows.
    /// They stay on the standby list and come back without touching the disk
    /// the next time they are needed, unless something else needed the room.
    public static void TrimWorkingSet() => SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);

    // MARK: Package

    /// Running from an MSIX package, with a package identity.
    public static bool HasPackageIdentity()
    {
        uint length = 0;
        return GetCurrentPackageFullName(ref length, null) != APPMODEL_ERROR_NO_PACKAGE;
    }

    private const int APPMODEL_ERROR_NO_PACKAGE = 15700;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint length, char[]? name);

    // MARK: Private declarations

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;
    private const int VK_LBUTTON = 0x01;
    private const int QUNS_BUSY = 2, QUNS_RUNNING_D3D_FULL_SCREEN = 3, QUNS_PRESENTATION_MODE = 4;
    private const int GWL_EXSTYLE = -20;
    private const uint SWP_NOZORDER = 0x4, SWP_FRAMECHANGED = 0x20;
    private const uint WM_NCCALCSIZE = 0x0083;
    private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x8000000;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_DONOTROUND = 1;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    private const int RGN_OR = 2;

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern IntPtr CreatePolygonRgn(POINT[] points, int count, int fillMode);
    private const int WINDING = 2;
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr dest, IntPtr a, IntPtr b, int mode);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);
}
