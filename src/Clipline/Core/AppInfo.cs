using System.Reflection;

namespace Clipline;

/// Which copy of Clipline this is, and where it keeps its things.
///
/// The same build ships three ways: an MSIX package, an installed exe, and a
/// portable folder. The package keeps its data in its own app data folder;
/// the portable copy keeps it in a "data" folder beside Clipline.exe, so it
/// can travel on a USB stick; the installed copy uses %LOCALAPPDATA%\Clipline.
public static class AppInfo
{
    /// A file the portable zip carries next to Clipline.exe. Deleting it turns
    /// that copy into an ordinary one.
    public const string PortableMarker = "portable.txt";

    public static bool IsPackaged { get; } = Native.HasPackageIdentity();

    public static bool IsPortable { get; } =
        !IsPackaged && File.Exists(Path.Combine(ExeFolder(), PortableMarker));

    /// "0.1.0", without the commit the SDK appends.
    public static string Version { get; } = ReadVersion();

    /// What the about section says this copy is, or null for a plain build.
    public static string? Edition => IsPackaged ? "MSIX" : IsPortable ? "便携版" : null;

    /// The GitHub repository, when the build was told it.
    public static string? RepositoryUrl { get; } = typeof(AppInfo).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "RepositoryUrl")?.Value?.TrimEnd('/') is { Length: > 0 } url ? url : null;

    /// Settings, the log and screenshots from the clipboard live here.
    /// A packaged app sees %LOCALAPPDATA% redirected into its package, where
    /// Explorer and other apps cannot follow, so it uses the real path of
    /// its own folder instead.
    public static string DataFolder { get; } = IsPackaged
        ? Windows.Storage.ApplicationData.Current.LocalFolder.Path
        : IsPortable
            ? Path.Combine(ExeFolder(), "data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clipline");

    private static string ExeFolder() =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    private static string ReadVersion()
    {
        var assembly = typeof(AppInfo).Assembly;
        string? version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(version)) return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        int plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }
}
