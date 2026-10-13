namespace DackelFxp.Core.Infrastructure;

/// <summary>%APPDATA%\DackelFxp on Windows, ~/.config/dackelfxp on Linux.</summary>
public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? DefaultRoot();
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }
    public string SitesFile => Path.Combine(Root, "sites.json");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string QueueFile => Path.Combine(Root, "queue.json");
    public string KeyFile => Path.Combine(Root, "key.bin");
    public string LogDirectory => Path.Combine(Root, "logs");

    private static string DefaultRoot()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, OperatingSystem.IsWindows() ? "DackelFxp" : "dackelfxp");
    }

    /// <summary>Restricts a file to the current user on Unix (secrets, keys).</summary>
    public static void RestrictToUser(string file)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(file))
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
