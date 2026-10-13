using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DackelFxp.Ftp;

public enum FtpTlsMode { None, Explicit, Implicit }

public enum FtpDataMode { Passive, ExtendedPassive, Active, ExtendedActive }

public enum FtpListMethod { Auto, List, ListAl, Mlsd, StatL }

public enum FtpPretMode { Auto, Always, Never }

/// <summary>Called when a server certificate does not validate against the system trust store.</summary>
public delegate bool FtpCertificateValidator(X509Certificate2 certificate, X509Chain? chain, SslPolicyErrors errors);

public sealed class FtpOptions
{
    public required string Host { get; init; }
    public int Port { get; init; } = 21;
    public string User { get; init; } = "anonymous";
    public string Password { get; init; } = "dackelfxp@";

    public FtpTlsMode TlsMode { get; init; } = FtpTlsMode.None;
    /// <summary>Encrypt data connections (PROT P). Only relevant with TLS.</summary>
    public bool ProtectData { get; init; } = true;
    /// <summary>Clear the control channel (CCC) after login.</summary>
    public bool ClearControlChannel { get; init; }
    public FtpCertificateValidator? CertificateValidator { get; init; }

    public FtpDataMode DataMode { get; init; } = FtpDataMode.Passive;
    /// <summary>Ignore the IP returned by PASV and use the control connection host instead.</summary>
    public bool PassiveUseControlHost { get; init; } = true;
    /// <summary>External IP announced in PORT/EPRT (NAT). Null = local address of the control socket.</summary>
    public string? ActiveExternalIp { get; init; }
    public int ActivePortMin { get; init; }
    public int ActivePortMax { get; init; }

    public FtpListMethod ListMethod { get; init; } = FtpListMethod.Auto;
    public FtpPretMode PretMode { get; init; } = FtpPretMode.Auto;
    /// <summary>Null = UTF-8 if the server announces it, otherwise Latin-1.</summary>
    public Encoding? Encoding { get; init; }
    /// <summary>Offset added to timestamps from the server listing.</summary>
    public TimeSpan TimeZoneOffset { get; init; }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan DataTimeout { get; init; } = TimeSpan.FromSeconds(60);
}
