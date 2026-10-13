using System.Net.Security;
using System.Security.Authentication;

namespace DackelFxp.Ftp.Protocol;

internal static class FtpTls
{
    /// <summary>
    /// Options for control and data connections. The same target host is used for both, so the
    /// TLS session can be resumed on data connections (required by vsftpd/glftpd with session reuse).
    /// </summary>
    public static SslClientAuthenticationOptions CreateClientOptions(string targetHost, RemoteCertificateValidationCallback validation,
        SslProtocols protocols = SslProtocols.None) => new()
    {
        TargetHost = targetHost,
        EnabledSslProtocols = protocols, // None = OS default: TLS 1.2 / 1.3
        RemoteCertificateValidationCallback = validation,
        AllowTlsResume = true,
    };
}
