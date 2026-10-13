using DackelFxp.Ftp;
using DackelFxp.Core.Connections;
using DackelFxp.Core.Sites;
using DackelFxp.Ftp.Listing;
using DackelFxp.Ftp.Protocol;

namespace DackelFxp.Core.Browsing;

public sealed record BrowserEntry(
    string Name,
    bool IsDirectory,
    long Size,
    DateTime? Modified,
    string? Permissions = null,
    string? Owner = null,
    string? Group = null,
    string? LinkTarget = null,
    bool IsLink = false);

/// <summary>One side of the dual-pane browser: local file system or an FTP site.</summary>
public interface IBrowser
{
    string Title { get; }
    Site? Site { get; }
    bool IsRemote => Site is not null;
    /// <summary>Root for "go up" (local Windows: "" = drive list).</summary>
    string Root { get; }

    Task<string> GetHomeAsync(CancellationToken ct);
    Task<(string Path, IReadOnlyList<BrowserEntry> Entries)> ListAsync(string path, CancellationToken ct);
    string Combine(string directory, string name);
    string GetParent(string path);
    Task CreateDirectoryAsync(string path, CancellationToken ct);
    Task DeleteAsync(string path, bool isDirectory, CancellationToken ct);
    Task RenameAsync(string from, string to, CancellationToken ct);
}

public sealed class LocalBrowser : IBrowser
{
    public string Title => "Lokal";
    public Site? Site => null;
    public string Root => OperatingSystem.IsWindows() ? "" : "/";

    public Task<string> GetHomeAsync(CancellationToken ct) =>
        Task.FromResult(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public Task<(string Path, IReadOnlyList<BrowserEntry> Entries)> ListAsync(string path, CancellationToken ct) => Task.Run(() =>
    {
        if (OperatingSystem.IsWindows() && path.Length == 0)
        {
            IReadOnlyList<BrowserEntry> drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => new BrowserEntry(d.Name, true, d.TotalSize - d.TotalFreeSpace, null, Owner: d.VolumeLabel))
                .ToList();
            return ("", drives);
        }

        var dir = new DirectoryInfo(path);
        if (!dir.Exists) throw new DirectoryNotFoundException(Lang.T($"Verzeichnis nicht gefunden: {path}", $"Directory not found: {path}"));
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, RecurseSubdirectories = false };
        IReadOnlyList<BrowserEntry> entries = dir.EnumerateFileSystemInfos("*", options)
            .Select(i => new BrowserEntry(
                i.Name,
                i is DirectoryInfo,
                i is FileInfo f ? f.Length : 0,
                i.LastWriteTime,
                OperatingSystem.IsWindows() ? null : UnixMode(i),
                IsLink: i.LinkTarget is not null,
                LinkTarget: i.LinkTarget))
            .ToList();
        return (dir.FullName, entries);
    }, ct);

    private static string UnixMode(FileSystemInfo info)
    {
        var m = info.UnixFileMode;
        char Bit(UnixFileMode flag, char c) => m.HasFlag(flag) ? c : '-';
        return (info is DirectoryInfo ? "d" : "-")
               + Bit(UnixFileMode.UserRead, 'r') + Bit(UnixFileMode.UserWrite, 'w') + Bit(UnixFileMode.UserExecute, 'x')
               + Bit(UnixFileMode.GroupRead, 'r') + Bit(UnixFileMode.GroupWrite, 'w') + Bit(UnixFileMode.GroupExecute, 'x')
               + Bit(UnixFileMode.OtherRead, 'r') + Bit(UnixFileMode.OtherWrite, 'w') + Bit(UnixFileMode.OtherExecute, 'x');
    }

    public string Combine(string directory, string name) => directory.Length == 0 ? name : Path.Combine(directory, name);

    public string GetParent(string path)
    {
        if (path.Length == 0) return "";
        var parent = Directory.GetParent(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) is { Length: > 0 } p ? p : path);
        if (parent is null) return Root;
        // "C:\" itself: going up shows the drive list.
        if (OperatingSystem.IsWindows() && Path.GetPathRoot(path) == path) return "";
        return parent.FullName;
    }

    public Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        Directory.CreateDirectory(path);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string path, bool isDirectory, CancellationToken ct) => Task.Run(() =>
    {
        if (isDirectory) Directory.Delete(path, recursive: true);
        else File.Delete(path);
    }, ct);

    public Task RenameAsync(string from, string to, CancellationToken ct)
    {
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
        return Task.CompletedTask;
    }
}

public sealed class RemoteBrowser(ConnectionManager connections, Site site) : IBrowser
{
    public string Title => site.DisplayName;
    public Site Site => site;
    Site? IBrowser.Site => site;
    public string Root => "/";

    public async Task<string> GetHomeAsync(CancellationToken ct)
    {
        await using var conn = await connections.RentAsync(site, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(site.RemotePath)) return site.RemotePath;
        return await conn.Client.GetWorkingDirectoryAsync(ct).ConfigureAwait(false);
    }

    public async Task<(string Path, IReadOnlyList<BrowserEntry> Entries)> ListAsync(string path, CancellationToken ct)
    {
        await using var conn = await connections.RentAsync(site, ct).ConfigureAwait(false);
        var listing = await conn.Client.ListAsync(path, ct).ConfigureAwait(false);
        IReadOnlyList<BrowserEntry> entries = listing.Select(ToEntry).ToList();
        return (conn.Client.WorkingDirectory, entries);
    }

    private static BrowserEntry ToEntry(FtpListItem i) => new(
        i.Name, i.Type == FtpItemType.Directory, i.Size, i.Modified, i.Permissions, i.Owner, i.Group, i.LinkTarget,
        i.Type == FtpItemType.Link);

    public string Combine(string directory, string name) => FtpPath.Combine(directory, name);

    public string GetParent(string path) => FtpPath.GetParent(path) is { Length: > 0 } p ? p : "/";

    public async Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        await using var conn = await connections.RentAsync(site, ct).ConfigureAwait(false);
        await conn.Client.MakeDirectoryAsync(path, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string path, bool isDirectory, CancellationToken ct)
    {
        await using var conn = await connections.RentAsync(site, ct).ConfigureAwait(false);
        if (isDirectory) await DeleteTreeAsync(conn.Client, path, ct).ConfigureAwait(false);
        else await conn.Client.DeleteFileAsync(path, ct).ConfigureAwait(false);
    }

    private static async Task DeleteTreeAsync(DackelFxp.Ftp.FtpClient client, string path, CancellationToken ct)
    {
        foreach (var item in await client.ListAsync(path, ct).ConfigureAwait(false))
        {
            var child = FtpPath.Combine(path, item.Name);
            if (item.Type == FtpItemType.Directory) await DeleteTreeAsync(client, child, ct).ConfigureAwait(false);
            else await client.DeleteFileAsync(child, ct).ConfigureAwait(false);
        }
        // Leave the directory before removing it; some servers refuse RMD on the working directory.
        await client.ChangeDirectoryAsync(FtpPath.GetParent(path) is { Length: > 0 } p ? p : "/", ct).ConfigureAwait(false);
        await client.RemoveDirectoryAsync(path, ct).ConfigureAwait(false);
    }

    public async Task RenameAsync(string from, string to, CancellationToken ct)
    {
        await using var conn = await connections.RentAsync(site, ct).ConfigureAwait(false);
        await conn.Client.RenameAsync(from, to, ct).ConfigureAwait(false);
    }

    public async Task SetPermissionsAsync(string path, string mode, CancellationToken ct)
    {
        await using var conn = await connections.RentAsync(site, ct).ConfigureAwait(false);
        await conn.Client.SetPermissionsAsync(path, mode, ct).ConfigureAwait(false);
    }

    /// <summary>Runs raw commands (e.g. SITE ...) and returns all reply lines.</summary>
    public async Task<IReadOnlyList<string>> ExecuteAsync(IEnumerable<string> commands, CancellationToken ct)
    {
        await using var conn = await connections.RentAsync(site, ct).ConfigureAwait(false);
        var output = new List<string>();
        foreach (var command in commands)
        {
            foreach (var reply in await conn.Client.ExecuteRawAsync(command, ct).ConfigureAwait(false))
                output.AddRange(reply.Lines);
        }
        return output;
    }

    public async Task DisconnectAsync() => await connections.DisconnectAsync(site.Id).ConfigureAwait(false);
}
