using System.Security.Cryptography;
using DackelFxp.Ftp;
using DackelFxp.Ftp.Transfer;

namespace DackelFxp.IntegrationTests;

/// <summary>
/// Tests against two real vsftpd servers (see docker/docker-compose.yml).
/// Skipped unless DACKELFXP_IT_A / DACKELFXP_IT_B point to the servers.
/// </summary>
public class VsftpdTests
{
    private static readonly string? HostA = Environment.GetEnvironmentVariable("DACKELFXP_IT_A");
    private static readonly string? HostB = Environment.GetEnvironmentVariable("DACKELFXP_IT_B");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>FXP needs both servers; client tests only server A ("host[:port]").</summary>
    private static void RequireServers(bool both = true) =>
        Assert.SkipWhen(HostA is null || (both && HostB is null), "DACKELFXP_IT_A/B nicht gesetzt (docker compose nötig)");

    private static FtpClient Client(string hostAndPort, FtpTlsMode tls, FtpDataMode mode = FtpDataMode.Passive) => new(new FtpOptions
    {
        Host = hostAndPort.Split(':')[0],
        Port = hostAndPort.Contains(':') ? int.Parse(hostAndPort.Split(':')[1]) : 21,
        User = "dackel",
        Password = "dackel",
        TlsMode = tls,
        DataMode = mode,
        // vsftpd uses a self-signed certificate.
        CertificateValidator = (_, _, _) => true,
    });

    private static byte[] RandomPayload(int size) => RandomNumberGenerator.GetBytes(size);

    private static string Unique(string prefix) => $"/{prefix}-{Guid.NewGuid():N}.bin";

    [Theory]
    [InlineData(FtpTlsMode.None, FtpDataMode.Passive)]
    [InlineData(FtpTlsMode.None, FtpDataMode.Active)]
    [InlineData(FtpTlsMode.Explicit, FtpDataMode.Passive)]
    [InlineData(FtpTlsMode.Explicit, FtpDataMode.ExtendedPassive)]
    [InlineData(FtpTlsMode.Explicit, FtpDataMode.Active)]
    public async Task Upload_List_Download_Resume(FtpTlsMode tls, FtpDataMode mode)
    {
        RequireServers(both: false);
        Assert.SkipWhen(mode == FtpDataMode.Active && Environment.GetEnvironmentVariable("DACKELFXP_IT_NO_ACTIVE") is not null,
            "Aktiver Modus vom Host in den Container nicht möglich");
        await using var client = Client(HostA!, tls, mode);
        await client.ConnectAsync(Ct);
        Assert.Equal(tls != FtpTlsMode.None, client.IsEncrypted);

        var payload = RandomPayload(3_000_000);
        var path = Unique("rt");
        // TLS: every data connection must resume the control session (require_ssl_reuse=YES).
        await client.UploadAsync(new MemoryStream(payload), path, ct: Ct);
        Assert.Equal(payload.Length, await client.GetSizeAsync(path, Ct));

        var listing = await client.ListAsync("/", Ct);
        Assert.Contains(listing, i => "/" + i.Name == path && i.Size == payload.Length);

        var partial = new MemoryStream();
        partial.Write(payload, 0, 1_000_000);
        await client.DownloadAsync(path, partial, offset: 1_000_000, ct: Ct);
        Assert.Equal(payload, partial.ToArray());

        await client.DeleteFileAsync(path, Ct);
    }

    [Fact]
    public async Task Plain_Fxp_Between_Two_Servers()
    {
        RequireServers();
        await using var a = Client(HostA!, FtpTlsMode.None);
        await using var b = Client(HostB!, FtpTlsMode.None);
        await a.ConnectAsync(Ct);
        await b.ConnectAsync(Ct);

        var payload = RandomPayload(4_000_000);
        var path = Unique("fxp");
        await a.UploadAsync(new MemoryStream(payload), path, ct: Ct);

        var result = await FxpTransfer.TransferAsync(a, path, b, path, new FxpOptions(), Ct);
        Assert.Equal(FxpStrategy.Clear, result.Strategy);

        var copy = new MemoryStream();
        await b.DownloadAsync(path, copy, ct: Ct);
        Assert.Equal(payload, copy.ToArray());
    }

    [Fact]
    public async Task Tls_Fxp_Without_Sscn_Support_Uses_Clear_Fallback_Only_When_Allowed()
    {
        RequireServers();
        await using var a = Client(HostA!, FtpTlsMode.Explicit);
        await using var b = Client(HostB!, FtpTlsMode.Explicit);
        await a.ConnectAsync(Ct);
        await b.ConnectAsync(Ct);

        var payload = RandomPayload(500_000);
        var path = Unique("fxptls");
        await a.UploadAsync(new MemoryStream(payload), path, ct: Ct);

        // vsftpd knows neither SSCN nor CPSV: secure FXP is impossible.
        await Assert.ThrowsAsync<DackelFxp.Ftp.Protocol.FtpException>(() =>
            FxpTransfer.TransferAsync(a, path, b, path, new FxpOptions(), Ct));

        var result = await FxpTransfer.TransferAsync(a, path, b, path, new FxpOptions { AllowClearFallback = true }, Ct);
        Assert.Equal(FxpStrategy.Clear, result.Strategy);

        // Afterwards encrypted client transfers must still work (PROT P restored).
        var copy = new MemoryStream();
        await b.DownloadAsync(path, copy, ct: Ct);
        Assert.Equal(payload, copy.ToArray());
    }

    [Fact]
    public async Task Fxp_Resume_With_Rest()
    {
        RequireServers();
        await using var a = Client(HostA!, FtpTlsMode.None);
        await using var b = Client(HostB!, FtpTlsMode.None);
        await a.ConnectAsync(Ct);
        await b.ConnectAsync(Ct);

        var payload = RandomPayload(2_000_000);
        var path = Unique("fxpres");
        await a.UploadAsync(new MemoryStream(payload), path, ct: Ct);
        await b.UploadAsync(new MemoryStream(payload[..700_000]), path, ct: Ct);

        await FxpTransfer.TransferAsync(a, path, b, path, new FxpOptions { Offset = 700_000 }, Ct);

        var copy = new MemoryStream();
        await b.DownloadAsync(path, copy, ct: Ct);
        Assert.Equal(payload, copy.ToArray());
    }
}
