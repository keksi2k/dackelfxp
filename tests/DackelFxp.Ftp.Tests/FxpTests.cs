using DackelFxp.Ftp.Protocol;
using DackelFxp.Ftp.Transfer;

namespace DackelFxp.Ftp.Tests;

public class FxpTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Payload() => Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7)).ToArray();

    private static async Task<(FtpClient, FtpClient)> ConnectBothAsync(FakeFtpServer a, FakeFtpServer b)
    {
        var ca = ClientTests.CreateClient(a);
        var cb = ClientTests.CreateClient(b);
        await ca.ConnectAsync(Ct);
        await cb.ConnectAsync(Ct);
        return (ca, cb);
    }

    [Fact]
    public async Task Plain_Fxp()
    {
        await using var src = new FakeFtpServer();
        await using var dst = new FakeFtpServer();
        var payload = Payload();
        src.Files["/rel/file.rar"] = payload;
        var (cs, cd) = await ConnectBothAsync(src, dst);
        await using var _ = cs;
        await using var __ = cd;

        var result = await FxpTransfer.TransferAsync(cs, "/rel/file.rar", cd, "/file.rar", new FxpOptions(), Ct);
        Assert.Equal(payload, dst.Files["/file.rar"]);
        Assert.Equal(FxpStrategy.Clear, result.Strategy);
        Assert.True(result.SourcePassive);
    }

    [Theory]
    [InlineData(true, true, FxpStrategy.SscnOnPassive)]
    [InlineData(false, true, FxpStrategy.Cpsv)]
    public async Task Secure_Fxp_Auto(bool sscn, bool cpsv, FxpStrategy expected)
    {
        await using var src = new FakeFtpServer(tls: true) { SupportsSscn = sscn, SupportsCpsv = cpsv, RequireProtP = true };
        await using var dst = new FakeFtpServer(tls: true) { SupportsSscn = sscn, SupportsCpsv = cpsv, RequireProtP = true };
        var payload = Payload();
        src.Files["/f"] = payload;
        var (cs, cd) = await ConnectBothAsync(src, dst);
        await using var _ = cs;
        await using var __ = cd;

        FxpResult result;
        try { result = await FxpTransfer.TransferAsync(cs, "/f", cd, "/f", new FxpOptions(), Ct); }
        catch { Assert.Fail(string.Join(Environment.NewLine, src.Errors.Concat(dst.Errors))); throw; }
        Assert.Equal(expected, result.Strategy);
        Assert.Equal(payload, dst.Files["/f"]);
    }

    [Fact]
    public async Task Sscn_On_Active_Side_When_Passive_Side_Lacks_It()
    {
        await using var src = new FakeFtpServer(tls: true) { SupportsSscn = false, SupportsCpsv = false };
        await using var dst = new FakeFtpServer(tls: true);
        var payload = Payload();
        src.Files["/f"] = payload;
        var (cs, cd) = await ConnectBothAsync(src, dst);
        await using var _ = cs;
        await using var __ = cd;

        var result = await FxpTransfer.TransferAsync(cs, "/f", cd, "/f", new FxpOptions(), Ct);
        Assert.Equal(FxpStrategy.SscnOnActive, result.Strategy);
        Assert.Equal(payload, dst.Files["/f"]);
    }

    [Fact]
    public async Task No_Secure_Variant_Fails_Without_Clear_Fallback()
    {
        await using var src = new FakeFtpServer(tls: true) { SupportsSscn = false, SupportsCpsv = false };
        await using var dst = new FakeFtpServer(tls: true) { SupportsSscn = false, SupportsCpsv = false };
        src.Files["/f"] = [1, 2, 3];
        var (cs, cd) = await ConnectBothAsync(src, dst);
        await using var _ = cs;
        await using var __ = cd;

        await Assert.ThrowsAsync<FtpException>(() => FxpTransfer.TransferAsync(cs, "/f", cd, "/f", new FxpOptions(), Ct));

        var result = await FxpTransfer.TransferAsync(cs, "/f", cd, "/f", new FxpOptions { AllowClearFallback = true }, Ct);
        Assert.Equal(FxpStrategy.Clear, result.Strategy);
        Assert.Equal([1, 2, 3], dst.Files["/f"]);

        // A following client transfer must be encrypted again.
        var local = new MemoryStream();
        await cd.DownloadAsync("/f", local, ct: Ct);
        Assert.Equal("PROT P", dst.ReceivedCommands.Last(c => c.StartsWith("PROT", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Falls_Back_To_Destination_Passive_When_Port_Refused()
    {
        await using var src = new FakeFtpServer();
        await using var dst = new FakeFtpServer { RefusePort = true };
        src.Files["/f"] = Payload();
        var (cs, cd) = await ConnectBothAsync(src, dst);
        await using var _ = cs;
        await using var __ = cd;

        var result = await FxpTransfer.TransferAsync(cs, "/f", cd, "/f", new FxpOptions(), Ct);
        Assert.False(result.SourcePassive);
        Assert.Equal(src.Files["/f"], dst.Files["/f"]);
    }

    [Fact]
    public async Task Missing_Source_Aborts_Both_Sides()
    {
        await using var src = new FakeFtpServer();
        await using var dst = new FakeFtpServer();
        var (cs, cd) = await ConnectBothAsync(src, dst);
        await using var _ = cs;
        await using var __ = cd;

        var ex = await Assert.ThrowsAsync<FtpCommandException>(() =>
            FxpTransfer.TransferAsync(cs, "/missing", cd, "/f", new FxpOptions(), Ct));
        Assert.Equal(550, ex.Reply.Code);
        Assert.True(cs.IsConnected);
    }

    [Fact]
    public async Task Resumes_With_Rest_On_Both_Sides()
    {
        await using var src = new FakeFtpServer();
        await using var dst = new FakeFtpServer();
        var payload = Payload();
        src.Files["/f"] = payload;
        dst.Files["/f"] = payload[..100_000];
        var (cs, cd) = await ConnectBothAsync(src, dst);
        await using var _ = cs;
        await using var __ = cd;

        await FxpTransfer.TransferAsync(cs, "/f", cd, "/f", new FxpOptions { Offset = 100_000 }, Ct);
        Assert.Equal(payload, dst.Files["/f"]);
    }

    [Fact]
    public async Task Client_Transfer_After_Sscn_Resets_It()
    {
        await using var src = new FakeFtpServer(tls: true);
        await using var dst = new FakeFtpServer(tls: true);
        src.Files["/f"] = Payload();
        var (cs, cd) = await ConnectBothAsync(src, dst);
        await using var _ = cs;
        await using var __ = cd;

        await FxpTransfer.TransferAsync(cs, "/f", cd, "/f", new FxpOptions(), Ct);
        var local = new MemoryStream();
        await cs.DownloadAsync("/f", local, ct: Ct);
        Assert.Equal(src.Files["/f"], local.ToArray());
        Assert.Contains("SSCN OFF", src.ReceivedCommands);
    }
}
