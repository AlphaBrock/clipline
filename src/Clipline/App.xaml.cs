using Microsoft.UI.Xaml;

namespace Clipline;

public partial class App : Application
{
    private LineWindow? line;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => Log.Write("Unhandled", e.Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Log.Write("Unobserved task", e.Exception);
    }

    /// Held for as long as this copy runs, so a second one knows to step aside.
    private static Mutex? running;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Already running: launching again opens its settings instead of
        // putting a second line and a second tray icon on screen.
        running = new Mutex(initiallyOwned: true, "Clipline.SingleInstance", out bool first);
        if (!first)
        {
            Shell.AskRunningCopyToOpenSettings();
            Exit();
            return;
        }

        // On a first run nothing has made it yet, and the clipboard folder's
        // watcher needs at least its parent to be there.
        Directory.CreateDirectory(AppInfo.DataFolder);

        line = new LineWindow();
        line.Start();
        // Clipline.exe --settings opens straight into the settings.
        if (Environment.GetCommandLineArgs().Contains("--settings", StringComparer.OrdinalIgnoreCase))
            line.OpenSettings();
    }
}

/// log.txt in the data folder (see AppInfo), for what goes wrong out of sight.
public static class Log
{
    public static readonly string FilePath = Path.Combine(AppInfo.DataFolder, "log.txt");

    public static void Write(string what, Exception? e = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} {what}{(e is null ? "" : "\n" + e)}\n");
        }
        catch (IOException) { }
    }
}
