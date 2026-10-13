using System.Text;
using DackelFxp.Ftp.Protocol;
using DackelFxp.Ftp.Transfer;

namespace DackelFxp.Ftp.Tests;

public class ClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static FtpClient CreateClient(FakeFtpServer server, FtpDataMode mode = FtpDataMode.Passive, bool protect = true) => new(new FtpOptions
    {
        Host = "127.0.0.1",
        Port = server.Port,
        User = "test",
        Password = "secret",
        TlsMode = server.ImplicitTls ? FtpTlsMode.Implicit : server.Tls ? FtpTlsMode.Explicit : FtpTlsMode.None,
        ProtectData = protect,
        DataMode = mode,
        CertificateValidator = (_, _, _) => true,
        ReadTimeout = TimeSpan.FromSeconds(10),
        DataTimeout = TimeSpan.FromSeconds(10),
    });

    [Fact]
    public async Task Connects_And_Reads_Features()
    {
        await using var server = new FakeFtpServer();
        await using var client = CreateClient(server);
        await client.ConnectAsync(Ct);

        Assert.True(client.IsConnected);
        Assert.True(client.HasFeature("MLSD"));
        Assert.Equal("/", client.WorkingDirectory);
        Assert.Contains("PASS secret", server.ReceivedCommands);
    }

    [Fact]
    public async Task Login_Failure_Throws()
    {
        await using var server = new FakeFtpServer();
        await using var client = new FtpClient(new FtpOptions { Host = "127.0.0.1", Port = server.Port, User = "bad", Password = "x" });
        var ex = await Assert.ThrowsAsync<FtpCommandException>(() => client.ConnectAsync(Ct));
        Assert.Equal(530, ex.Reply.Code);
    }

    [Fact]
    public async Task Password_Is_Masked_In_Log()
    {
        await using var server = new FakeFtpServer();
        await using var client = CreateClient(server);
        var log = new List<FtpLogEntry>();
        client.Log += log.Add;
        await client.ConnectAsync(Ct);
        Assert.DoesNotContain(log, e => e.Text.Contains("secret"));
        Assert.Contains(log, e => e.Text == "PASS ********");
    }

    [Theory]
    [InlineData(false, false, FtpDataMode.Passive)]
    [InlineData(false, false, FtpDataMode.Active)]
    [InlineData(true, false, FtpDataMode.Passive)]
    [InlineData(true, false, FtpDataMode.Active)]
    [InlineData(false, true, FtpDataMode.Passive)]
    public async Task Upload_Download_List_RoundTrip(bool tls, bool implicitTls, FtpDataMode mode)
    {
        await using var server = new FakeFtpServer(tls, implicitTls);
        server.Directories["/dir"] = true;
        await using var client = CreateClient(server, mode);
        await client.ConnectAsync(Ct);
        Assert.Equal(tls || implicitTls, client.IsEncrypted);

        var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("dackel ", 50_000)));
        await client.UploadAsync(new MemoryStream(payload), "/dir/file.bin", ct: Ct);
        Assert.Equal(payload, server.Files["/dir/file.bin"]);

        var items = await client.ListAsync("/dir", Ct);
        var item = Assert.Single(items);
        Assert.Equal("file.bin", item.Name);
        Assert.Equal(payload.Length, item.Size);

        long lastProgress = 0;
        var target = new MemoryStream();
        await client.DownloadAsync("/dir/file.bin", target, progress: new SyncProgress(p => lastProgress = p), ct: Ct);
        Assert.Equal(payload, target.ToArray());
        Assert.Equal(payload.Length, lastProgress);
        Assert.Equal(payload.Length, await client.GetSizeAsync("/dir/file.bin", Ct));
    }

    [Fact]
    public async Task Resumes_Download_And_Upload()
    {
        await using var server = new FakeFtpServer();
        var payload = Enumerable.Range(0, 10_000).Select(i => (byte)i).ToArray();
        server.Files["/f"] = payload;
        await using var client = CreateClient(server);
        await client.ConnectAsync(Ct);

        var target = new MemoryStream();
        target.Write(payload, 0, 4000);
        await client.DownloadAsync("/f", target, offset: 4000, ct: Ct);
        Assert.Equal(payload, target.ToArray());

        server.Files["/g"] = payload[..3000];
        await client.UploadAsync(new MemoryStream(payload), "/g", offset: 3000, ct: Ct);
        Assert.Equal(payload, server.Files["/g"]);
    }

    [Fact]
    public async Task File_Operations()
    {
        await using var server = new FakeFtpServer();
        server.Files["/a.txt"] = [1, 2, 3];
        await using var client = CreateClient(server);
        await client.ConnectAsync(Ct);

        await client.MakeDirectoryAsync("/new", Ct);
        await client.ChangeDirectoryAsync("/new", Ct);
        Assert.Equal("/new", client.WorkingDirectory);
        await client.ChangeToParentAsync(Ct);
        Assert.Equal("/", client.WorkingDirectory);

        await client.RenameAsync("/a.txt", "/b.txt", Ct);
        Assert.True(server.Files.ContainsKey("/b.txt"));
        await client.DeleteFileAsync("/b.txt", Ct);
        Assert.Empty(server.Files);
        await client.RemoveDirectoryAsync("/new", Ct);

        await Assert.ThrowsAsync<FtpCommandException>(() => client.DeleteFileAsync("/missing", Ct));
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task Sends_Pret_When_Announced()
    {
        await using var server = new FakeFtpServer { RequirePret = true };
        server.Files["/x"] = [1];
        await using var client = CreateClient(server);
        await client.ConnectAsync(Ct);
        await client.DownloadAsync("/x", new MemoryStream(), ct: Ct);
        Assert.Contains("PRET RETR /x", server.ReceivedCommands);
    }

    [Fact]
    public async Task Bandwidth_Limiter_Slows_Down()
    {
        await using var server = new FakeFtpServer();
        server.Files["/f"] = new byte[200_000];
        await using var client = CreateClient(server);
        await client.ConnectAsync(Ct);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await client.DownloadAsync("/f", new MemoryStream(), limiter: new BandwidthLimiter(100_000), ct: Ct);
        // 200 KB at 100 KB/s with a 100 KB initial bucket: at least ~1 s.
        Assert.True(sw.Elapsed > TimeSpan.FromMilliseconds(800), $"zu schnell: {sw.Elapsed}");
    }

    [Fact]
    public async Task Raw_Command_Collects_All_Replies_And_Connection_Stays_In_Sync()
    {
        await using var server = new FakeFtpServer();
        server.Directories["/dir"] = true;
        server.Files["/dir/a.txt"] = [1];
        await using var client = CreateClient(server);
        await client.ConnectAsync(Ct);

        var replies = await client.ExecuteRawAsync("SITE MULTI", Ct);
        Assert.Equal(["First reply", "Second reply", "Third reply"], replies.Select(r => r.Message));

        await client.ChangeDirectoryAsync("/dir", Ct);
        Assert.Equal("/dir", client.WorkingDirectory);
        Assert.Single(await client.ListAsync("/dir", Ct));
    }

    [Fact]
    public async Task Late_Replies_Are_Drained_Before_The_Next_Command()
    {
        await using var server = new FakeFtpServer();
        server.Directories["/dir"] = true;
        server.Files["/dir/a.txt"] = [1];
        await using var client = CreateClient(server);
        await client.ConnectAsync(Ct);

        // Plain execute only reads the first reply; the others arrive afterwards.
        await client.ExecuteAsync("SITE MULTI", Ct);
        await Task.Delay(300, Ct);

        await client.ChangeDirectoryAsync("/dir", Ct);
        Assert.Equal("/dir", client.WorkingDirectory);
        var items = await client.ListAsync("/dir", Ct);
        Assert.Equal("a.txt", Assert.Single(items).Name);
    }

    /// <summary>Progress&lt;T&gt; posts asynchronously; tests need synchronous reports.</summary>
    internal sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }
}
