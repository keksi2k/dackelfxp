namespace DackelFxp.Core.Infrastructure;

/// <summary>Where the configuration lives, by priority.</summary>
public enum ConfigLocationKind
{
    /// <summary>--config &lt;dir&gt; or DACKELFXP_CONFIG.</summary>
    CommandLine,
    /// <summary>Folder "data" next to the program.</summary>
    Portable,
    /// <summary>Folder chosen by the user; the standard folder only holds a pointer to it.</summary>
    Custom,
    /// <summary>%APPDATA%\DackelFxp on Windows, ~/.config/dackelfxp on Linux, ~/Library/Application Support/dackelfxp on macOS.</summary>
    Default,
}

public sealed class AppPaths
{
    public const string PortableFolderName = "data";
    private const string PointerFileName = "location.txt";

    /// <param name="root">Fixed folder (tests, --config); null = resolve as described in <see cref="ConfigLocationKind"/>.</param>
    public AppPaths(string? root = null, string? programDirectory = null)
    {
        ProgramDirectory = programDirectory ?? DefaultProgramDirectory();
        (Kind, Root) = root is not null ? (ConfigLocationKind.CommandLine, Path.GetFullPath(root)) : Resolve(ProgramDirectory);
        Directory.CreateDirectory(Root);
    }

    public ConfigLocationKind Kind { get; }
    public string Root { get; }
    /// <summary>Folder that holds the program (on macOS the folder containing the .app bundle).</summary>
    public string ProgramDirectory { get; }
    public string PortableRoot => Path.Combine(ProgramDirectory, PortableFolderName);
    public static string DefaultRoot { get; set; } = StandardRoot();

    public string SitesFile => Path.Combine(Root, "sites.json");
    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string QueueFile => Path.Combine(Root, "queue.json");
    public string KeyFile => Path.Combine(Root, "key.bin");
    public string LogDirectory => Path.Combine(Root, "logs");

    private static (ConfigLocationKind, string) Resolve(string programDirectory)
    {
        var portable = Path.Combine(programDirectory, PortableFolderName);
        if (Directory.Exists(portable)) return (ConfigLocationKind.Portable, portable);
        if (ReadPointer() is { } custom) return (ConfigLocationKind.Custom, custom);
        return (ConfigLocationKind.Default, DefaultRoot);
    }

    private static string? ReadPointer()
    {
        try
        {
            var text = File.ReadAllText(Path.Combine(DefaultRoot, PointerFileName)).Trim();
            return Path.IsPathFullyQualified(text) ? text : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string StandardRoot()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, OperatingSystem.IsWindows() ? "DackelFxp" : "dackelfxp");
    }

    private static string DefaultProgramDirectory()
    {
        var dir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        // macOS: DackelFXP.app/Contents/MacOS -> the folder next to the bundle, not inside it.
        var macOs = Path.Combine(".app", "Contents", "MacOS");
        if (dir.EndsWith(macOs, StringComparison.Ordinal))
            return Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(dir)))!;
        return dir;
    }

    /// <summary>Folder for the given location; <paramref name="customRoot"/> only for <see cref="ConfigLocationKind.Custom"/>.</summary>
    public string TargetRoot(ConfigLocationKind kind, string? customRoot) => kind switch
    {
        ConfigLocationKind.Portable => PortableRoot,
        ConfigLocationKind.Custom => Path.GetFullPath(customRoot ?? throw new ArgumentNullException(nameof(customRoot))),
        ConfigLocationKind.Default => DefaultRoot,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>True if the folder already holds a configuration (it is then used as it is).</summary>
    public static bool HasConfiguration(string root) =>
        File.Exists(Path.Combine(root, "settings.json")) || File.Exists(Path.Combine(root, "sites.json"));

    /// <summary>Throws if the folder cannot be created or written (e.g. a program folder without write access).</summary>
    public static void EnsureWritable(string root)
    {
        Directory.CreateDirectory(root);
        var probe = Path.Combine(root, $".write-test-{Guid.NewGuid():N}");
        File.WriteAllText(probe, "");
        File.Delete(probe);
    }

    /// <summary>
    /// Makes the configuration live in another folder from the next start on: copies the current files there
    /// (unless the target already holds a configuration) and sets the pointer / the portable folder accordingly.
    /// A portable folder that would take precedence is renamed, never deleted.
    /// </summary>
    public void SwitchTo(ConfigLocationKind kind, string? customRoot = null)
    {
        var target = TargetRoot(kind, customRoot);
        if (!SamePath(target, Root) && !HasConfiguration(target)) CopyDirectory(Root, target);
        Directory.CreateDirectory(target);

        var pointer = Path.Combine(DefaultRoot, PointerFileName);
        if (kind == ConfigLocationKind.Custom)
        {
            Directory.CreateDirectory(DefaultRoot);
            File.WriteAllText(pointer, target);
        }
        else if (kind == ConfigLocationKind.Default && File.Exists(pointer))
        {
            File.Delete(pointer);
        }

        if (kind != ConfigLocationKind.Portable && Directory.Exists(PortableRoot))
            Directory.Move(PortableRoot, $"{PortableRoot}-{DateTime.Now:yyyyMMdd-HHmmss}");
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            if (Path.GetFileName(file) == PointerFileName) continue;
            var dest = Path.Combine(to, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: false);
            if (Path.GetFileName(file) is "key.bin" or "sites.json") RestrictToUser(dest);
        }
        foreach (var dir in Directory.EnumerateDirectories(from))
        {
            // Never copy a folder into itself (portable folder below the current one).
            if (SamePath(dir, to)) continue;
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
        }
    }

    /// <summary>Restricts a file to the current user on Unix (secrets, keys).</summary>
    public static void RestrictToUser(string file)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(file))
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
