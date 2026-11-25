using DackelFxp.Ftp;

namespace DackelFxp.Core.Browsing;

/// <summary>
/// Validates names that come from a directory listing (often from an untrusted server)
/// before they are used as a single path segment below a target folder.
/// A name like <c>..\..\evil.exe</c> or <c>C:\Windows\x</c> must never escape the folder it is written into.
/// </summary>
public static class EntryName
{
    private static readonly HashSet<string> WindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
        "CONIN$", "CONOUT$",
    };

    /// <summary>True if <paramref name="name"/> can safely be created as a file or folder on the local file system.</summary>
    public static bool IsSafeLocal(string? name) => IsSafeLocal(name, OperatingSystem.IsWindows());

    /// <summary>Platform-explicit variant of <see cref="IsSafeLocal(string?)"/> (Windows rules or Unix rules).</summary>
    public static bool IsSafeLocal(string? name, bool windows)
    {
        if (!IsSafeCommon(name)) return false;
        if (name!.Contains('/')) return false;
        if (!windows) return true;

        // Windows: backslash and colon are separators / drive or stream markers,
        // and these characters are not allowed in file names at all.
        if (name.IndexOfAny(['\\', ':', '*', '?', '"', '<', '>', '|']) >= 0) return false;
        // Trailing dots and spaces are silently stripped by Windows ("x." == "x", "..." == "").
        if (name[^1] is '.' or ' ') return false;
        var stem = name.Split('.')[0].TrimEnd(' ');
        return !WindowsDeviceNames.Contains(stem);
    }

    /// <summary>True if <paramref name="name"/> is a single path segment for an FTP server (no directory change, no command injection).</summary>
    public static bool IsSafeRemote(string? name) => IsSafeCommon(name) && !name!.Contains('/');

    private static bool IsSafeCommon(string? name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or "..") return false;
        foreach (var c in name)
            if (char.IsControl(c)) return false;
        return true;
    }

    /// <summary>
    /// Combines a local folder with a listing name and checks that the result stays inside that folder.
    /// Throws <see cref="UnsafeEntryNameException"/> otherwise.
    /// </summary>
    public static string CombineLocal(string directory, string name)
    {
        if (!IsSafeLocal(name)) throw new UnsafeEntryNameException(name);
        var combined = Path.Combine(directory, name);
        if (directory.Length > 0 && !IsInside(directory, combined)) throw new UnsafeEntryNameException(name);
        return combined;
    }

    /// <summary>True if <paramref name="path"/> resolves to a location below <paramref name="directory"/>.</summary>
    public static bool IsInside(string directory, string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return full.StartsWith(root, comparison) && full.Length > root.Length;
    }

    /// <summary>True if a local path contains no "." or ".." segments that could move it elsewhere.</summary>
    public static bool HasNoTraversal(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).All(s => s is not ("." or ".."));
}

public sealed class UnsafeEntryNameException(string name)
    : InvalidOperationException(Lang.T($"Unsicherer Dateiname vom Server abgelehnt: \"{Printable(name)}\"",
        $"Unsafe file name from server rejected: \"{Printable(name)}\""))
{
    public string EntryName { get; } = name;

    public static string Printable(string name) =>
        new(name.Select(c => char.IsControl(c) ? '?' : c).ToArray());
}
