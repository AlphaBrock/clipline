using Microsoft.Win32;
using Windows.ApplicationModel;

namespace Clipline;

/// Launching when the user signs in to Windows. The exe lists itself under
/// the user's Run key; the MSIX package cannot write there, so it turns on
/// the startup task its manifest declares. Either way Windows keeps the
/// state, not settings.json, and Task Manager's Startup apps can change it.
public static class SignInLaunch
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Clipline";
    /// The TaskId in Package.appxmanifest.
    private const string TaskId = "CliplineStartup";

    public static async Task<bool> IsEnabledAsync()
    {
        if (!AppInfo.IsPackaged)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        var task = await StartupTask.GetAsync(TaskId);
        return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
    }

    /// Turns it on or off, and says whether it is on afterwards: Windows
    /// refuses to turn a package's task back on once the user has turned it
    /// off in Task Manager.
    public static async Task<bool> SetAsync(bool on)
    {
        if (!AppInfo.IsPackaged)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
            return on;
        }
        var task = await StartupTask.GetAsync(TaskId);
        if (on)
        {
            var state = await task.RequestEnableAsync();
            return state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }
        task.Disable();
        return false;
    }
}
