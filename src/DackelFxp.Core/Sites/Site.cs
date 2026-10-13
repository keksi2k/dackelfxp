using System.Text.Json.Serialization;
using DackelFxp.Ftp;
using DackelFxp.Ftp.Transfer;

namespace DackelFxp.Core.Sites;

public enum TlsVersion { Auto, Tls12, Tls13 }

public sealed class Bookmark
{
    public string Name { get; set; } = "";
    public string RemotePath { get; set; } = "/";
    public string? LocalPath { get; set; }
}

/// <summary>A saved server entry in the site manager.</summary>
public sealed class Site
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Neue Site";
    /// <summary>Folder path in the site tree, e.g. "Scene/EU". Empty = root.</summary>
    public string Group { get; set; } = "";

    public string Host { get; set; } = "";
    public int Port { get; set; } = 21;
    public string User { get; set; } = "anonymous";
    /// <summary>Encrypted with <see cref="Infrastructure.SecretProtector"/>; plain text only in memory.</summary>
    public string ProtectedPassword { get; set; } = "";
    [JsonIgnore] public string Password { get; set; } = "";

    public FtpTlsMode TlsMode { get; set; } = FtpTlsMode.Explicit;
    public bool ProtectData { get; set; } = true;
    public TlsVersion TlsVersion { get; set; } = TlsVersion.Auto;
    public bool ClearControlChannel { get; set; }
    /// <summary>SHA-256 fingerprint of a certificate the user accepted permanently.</summary>
    public string? TrustedCertificate { get; set; }

    public FtpDataMode DataMode { get; set; } = FtpDataMode.Passive;
    public bool PassiveUseControlHost { get; set; } = true;
    public FtpListMethod ListMethod { get; set; } = FtpListMethod.Auto;
    public FtpPretMode PretMode { get; set; } = FtpPretMode.Auto;
    /// <summary>"auto", "utf-8", "iso-8859-1", ...</summary>
    public string Encoding { get; set; } = "auto";
    public double TimeZoneOffsetHours { get; set; }

    public FxpSecureMode FxpSecureMode { get; set; } = FxpSecureMode.Auto;
    public bool FxpAllowClear { get; set; }
    public FxpPassiveSide FxpPassiveSide { get; set; } = FxpPassiveSide.Auto;

    public int MaxConnections { get; set; } = 2;
    public int KeepAliveSeconds { get; set; } = 60;
    public int RetryCount { get; set; } = 3;
    public int RetryDelaySeconds { get; set; } = 10;
    /// <summary>KB/s, 0 = unlimited.</summary>
    public int SpeedLimitKb { get; set; }

    public string RemotePath { get; set; } = "";
    public string LocalPath { get; set; } = "";
    public string? SkipListName { get; set; }
    public List<Bookmark> Bookmarks { get; set; } = [];
    public string Notes { get; set; } = "";

    public DateTimeOffset? LastConnected { get; set; }

    /// <summary>Site that only lives for this session (quick connect).</summary>
    [JsonIgnore] public bool IsTemporary { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"{Host}:{Port}" : Name;

    public Site Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this, Infrastructure.Json.Options);
        var copy = System.Text.Json.JsonSerializer.Deserialize<Site>(json, Infrastructure.Json.Options)!;
        copy.Password = Password;
        copy.IsTemporary = IsTemporary;
        return copy;
    }
}
