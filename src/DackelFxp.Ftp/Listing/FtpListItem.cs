namespace DackelFxp.Ftp.Listing;

public enum FtpItemType { File, Directory, Link }

public sealed record FtpListItem
{
    public required string Name { get; init; }
    public required FtpItemType Type { get; init; }
    public long Size { get; init; }
    public DateTime? Modified { get; init; }
    public string? Permissions { get; init; }
    public string? Owner { get; init; }
    public string? Group { get; init; }
    public string? LinkTarget { get; init; }
    public string? RawLine { get; init; }

    public bool IsDirectory => Type == FtpItemType.Directory;
}
