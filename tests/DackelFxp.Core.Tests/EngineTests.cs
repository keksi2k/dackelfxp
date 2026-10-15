using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using DackelFxp.Core.Connections;
using DackelFxp.Core.Infrastructure;
using DackelFxp.Core.Queue;
using DackelFxp.Core.Settings;
using DackelFxp.Core.Sites;
using DackelFxp.Ftp;
using DackelFxp.Ftp.Tests;

namespace DackelFxp.Core.Tests;

public sealed class EngineTests : IAsyncLifetime
{
    private readonly string _root = Directory.CreateTempSubdirectory("dackel-engine").FullName;
    private AppPaths _paths = null!;
    private SiteStore _sites = null!;
    private SettingsStore _settings = null!;
    private ConnectionManager _connections = null!;
    private TransferQueue _queue = null!;
    private TransferEngine _engine = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _paths = new AppPaths(Path.Combine(_root, "config"));
        _sites = new SiteStore(_paths);
        _sites.Load();
        _settings = new SettingsStore(_paths);
        _connections = new ConnectionManager(_sites, _settings, new AcceptAll());
        _queue = new TransferQueue();
        _engine = new TransferEngine(_queue, _connections, _sites, _settings);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _engine.DisposeAsync();
        await _connections.DisposeAsync();
        Directory.Delete(_root, true);
    }

    private sealed class AcceptAll : ICertificatePrompt
    {
        public int Calls;
        public Task<CertificateDecision> AskAsync(Site site, X509Certificate2 certificate, SslPolicyErrors errors)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(CertificateDecision.AcceptOnce);
        }
    }

    private Site AddSite(FakeFtpServer server, string name)
    {
        var site = new Site
        {
            Name = name,
            Host = "127.0.0.1",
            Port = server.Port,
            User = "u",
            Password = "p",
            TlsMode = server.Tls ? FtpTlsMode.Explicit : FtpTlsMode.None,
            RetryDelaySeconds = 0,
            RetryCount = 1,
        };
        _sites.AddOrUpdate(site);
        return site;
    }

    private async Task RunQueueAsync()
    {
        var done = new TaskCompletionSource();
        _engine.QueueCompleted += () => done.TrySetResult();
        _engine.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
    }

    private static TransferEndpoint Remote(Site site, string path) => new(site.Id, path, site.Name);
    private static TransferEndpoint Local(string path) => new(null, path);

    [Fact]
    public async Task Downloads_Directory_Recursively_With_Skiplist()
    {
        await using var server = new FakeFtpServer();
        server.Directories["/rel"] = true;
        server.Directories["/rel/Sample"] = true;
        server.Files["/rel/a.r00"] = new byte[5000];
        server.Files["/rel/a.sfv"] = [1, 2];
        server.Files["/rel/Thumbs.db"] = [9];
        server.Files["/rel/Sample/s.mkv"] = new byte[100];
        var site = AddSite(server, "src");
        var target = Path.Combine(_root, "dl", "rel");

        _queue.Add([new QueueItem { Kind = TransferKind.Download, IsDirectory = true, Source = Remote(site, "/rel"), Target = Local(target) }]);
        await RunQueueAsync();

        Assert.Equal(5000, new FileInfo(Path.Combine(target, "a.r00")).Length);
        Assert.True(File.Exists(Path.Combine(target, "a.sfv")));
        Assert.True(File.Exists(Path.Combine(target, "Sample", "s.mkv")));
        Assert.False(File.Exists(Path.Combine(target, "Thumbs.db")));

        var items = _queue.Snapshot();
        Assert.All(items, i => Assert.Equal(QueueStatus.Done, i.Status));
        // .sfv has the highest priority: it comes right after the directory item.
        Assert.Equal("a.sfv", items[1].Name);
    }

    [Fact]
    public async Task Uploads_And_Skips_Equal_Existing()
    {
        await using var server = new FakeFtpServer();
        var site = AddSite(server, "dst");
        var local = Path.Combine(_root, "up");
        Directory.CreateDirectory(local);
        File.WriteAllBytes(Path.Combine(local, "new.bin"), new byte[1234]);
        File.WriteAllBytes(Path.Combine(local, "same.bin"), new byte[10]);
        server.Files["/up/same.bin"] = new byte[10];
        server.Directories["/up"] = true;

        _queue.Add([new QueueItem { Kind = TransferKind.Upload, IsDirectory = true, Source = Local(local), Target = Remote(site, "/up") }]);
        await RunQueueAsync();

        Assert.Equal(1234, server.Files["/up/new.bin"].Length);
        var same = _queue.Snapshot().Single(i => i.Name == "same.bin");
        Assert.Equal(QueueStatus.Skipped, same.Status);
    }

    [Fact]
    public async Task Resumes_Partial_Download()
    {
        await using var server = new FakeFtpServer();
        var payload = Enumerable.Range(0, 50_000).Select(i => (byte)i).ToArray();
        server.Files["/f.bin"] = payload;
        var site = AddSite(server, "src");
        var target = Path.Combine(_root, "f.bin");
        File.WriteAllBytes(target, payload[..20_000]);

        _queue.Add([new QueueItem { Kind = TransferKind.Download, Source = Remote(site, "/f.bin"), Target = Local(target), Size = payload.Length }]);
        await RunQueueAsync();

        Assert.Equal(payload, File.ReadAllBytes(target));
        Assert.Contains("REST 20000", server.ReceivedCommands);
    }

    [Fact]
    public async Task Fxp_Directory_Between_Tls_Sites()
    {
        await using var a = new FakeFtpServer(tls: true) { RequireProtP = true };
        await using var b = new FakeFtpServer(tls: true) { RequireProtP = true };
        a.Directories["/rel"] = true;
        a.Files["/rel/x.rar"] = new byte[70_000];
        a.Files["/rel/x.nfo"] = [1, 2, 3];
        var src = AddSite(a, "A");
        var dst = AddSite(b, "B");

        _queue.Add([new QueueItem { Kind = TransferKind.Fxp, IsDirectory = true, Source = Remote(src, "/rel"), Target = Remote(dst, "/rel") }]);
        await RunQueueAsync();

        Assert.All(_queue.Snapshot(), i => Assert.Equal(QueueStatus.Done, i.Status));
        Assert.Equal(70_000, b.Files["/rel/x.rar"].Length);
        Assert.Equal([1, 2, 3], b.Files["/rel/x.nfo"]);
        Assert.Contains("SSCN ON", a.ReceivedCommands);
    }

    [Fact]
    public async Task Missing_File_Fails_Without_Endless_Retry()
    {
        await using var server = new FakeFtpServer();
        var site = AddSite(server, "src");
        _queue.Add([new QueueItem { Kind = TransferKind.Download, Source = Remote(site, "/nope"), Target = Local(Path.Combine(_root, "nope")) }]);
        await RunQueueAsync();

        var item = Assert.Single(_queue.Snapshot());
        Assert.Equal(QueueStatus.Failed, item.Status);
        Assert.Contains("550", item.Error);
    }

    [Fact]
    public async Task Unreachable_Site_Retries_Then_Fails()
    {
        var site = new Site { Name = "dead", Host = "127.0.0.1", Port = 1, TlsMode = FtpTlsMode.None, RetryCount = 2, RetryDelaySeconds = 0 };
        _sites.AddOrUpdate(site);
        _queue.Add([new QueueItem { Kind = TransferKind.Download, Source = Remote(site, "/x"), Target = Local(Path.Combine(_root, "x")) }]);
        await RunQueueAsync();

        var item = Assert.Single(_queue.Snapshot());
        Assert.Equal(QueueStatus.Failed, item.Status);
        Assert.Equal(3, item.Attempts);
    }

    [Fact]
    public async Task Site_Store_Encrypts_Passwords_On_Disk()
    {
        _sites.AddOrUpdate(new Site { Name = "s", Host = "h", Password = "topsecret" });
        Assert.DoesNotContain("topsecret", await File.ReadAllTextAsync(_paths.SitesFile, Ct));

        var reloaded = new SiteStore(_paths);
        reloaded.Load();
        Assert.Equal("topsecret", reloaded.Sites.Single(s => s.Name == "s").Password);

        reloaded.SetMasterPassword("master");
        var locked = new SiteStore(_paths);
        locked.Load();
        Assert.True(locked.UsesMasterPassword);
        Assert.False(locked.Unlock("wrong"));
        Assert.True(locked.Unlock("master"));
        Assert.Equal("topsecret", locked.Sites.Single(s => s.Name == "s").Password);
    }

    [Fact]
    public void Queue_Save_Load_And_Move()
    {
        var items = Enumerable.Range(0, 3).Select(i => new QueueItem
        {
            Kind = TransferKind.Download,
            Source = new TransferEndpoint(Guid.NewGuid(), "/f" + i, "S"),
            Target = Local("/tmp/f" + i),
        }).ToList();
        _queue.Add(items);
        _queue.Move([items[2]], int.MinValue);
        Assert.Equal("f2", _queue.Snapshot()[0].Name);
        _queue.Move([items[2]], 1);
        Assert.Equal("f2", _queue.Snapshot()[1].Name);

        var file = Path.Combine(_root, "q.json");
        _queue.Save(file);
        var other = new TransferQueue();
        other.Load(file);
        Assert.Equal(_queue.Snapshot().Select(i => i.Source.Path), other.Snapshot().Select(i => i.Source.Path));
    }

    private List<QueueItem> SlowDownloads(FakeFtpServer server, Site site, int count)
    {
        server.RetrDelay = TimeSpan.FromMilliseconds(400);
        var items = new List<QueueItem>();
        for (var i = 0; i < count; i++)
        {
            server.Files[$"/f{i}.bin"] = new byte[100];
            items.Add(new QueueItem { Kind = TransferKind.Download, Source = Remote(site, $"/f{i}.bin"), Target = Local(Path.Combine(_root, "par", $"f{i}.bin")) });
        }
        return items;
    }

    [Fact]
    public async Task Runs_As_Many_Transfers_In_Parallel_As_Configured()
    {
        await using var server = new FakeFtpServer();
        var site = AddSite(server, "src");
        site.MaxConnections = 8;
        _settings.Current.TransferSlots = 4;
        _queue.Add(SlowDownloads(server, site, 8));
        await RunQueueAsync();
        Assert.Equal(4, server.MaxConcurrentRetr);
    }

    [Fact]
    public async Task Site_Connection_Limit_Caps_Parallel_Transfers()
    {
        await using var server = new FakeFtpServer();
        var site = AddSite(server, "src");
        site.MaxConnections = 2;
        _settings.Current.TransferSlots = 5;
        _queue.Add(SlowDownloads(server, site, 6));
        await RunQueueAsync();
        Assert.Equal(2, server.MaxConcurrentRetr);
    }

    [Fact]
    public async Task Raised_Site_Limit_Applies_Without_Reconnect()
    {
        await using var server = new FakeFtpServer();
        var site = AddSite(server, "src");
        site.MaxConnections = 1;
        // The pool is created with the old limit ...
        await using (await _connections.RentAsync(site, Ct)) { }
        // ... then the user raises it in the site manager (the store gets a new Site object).
        var edited = site.Clone();
        edited.MaxConnections = 4;
        _sites.AddOrUpdate(edited);
        _settings.Current.TransferSlots = 4;
        _queue.Add(SlowDownloads(server, edited, 8));
        await RunQueueAsync();
        Assert.Equal(4, server.MaxConcurrentRetr);
    }

    [Fact]
    public async Task Changing_Slots_While_Running_Takes_Effect()
    {
        await using var server = new FakeFtpServer();
        var site = AddSite(server, "src");
        site.MaxConnections = 8;
        _settings.Current.TransferSlots = 1;
        _queue.Add(SlowDownloads(server, site, 10));
        var done = new TaskCompletionSource();
        _engine.QueueCompleted += () => done.TrySetResult();
        _engine.Start();
        await Task.Delay(200, Ct);
        _settings.Current.TransferSlots = 3;
        _engine.ApplySlotCount();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(3, server.MaxConcurrentRetr);
    }

    [Fact]
    public async Task Finished_Items_Are_Removed_When_Enabled()
    {
        await using var server = new FakeFtpServer();
        server.Files["/ok.bin"] = [1];
        server.Directories["/dir"] = true;
        server.Files["/dir/x.bin"] = [2];
        var site = AddSite(server, "src");
        _settings.Current.RemoveFinishedItems = true;
        _queue.Add(
        [
            new QueueItem { Kind = TransferKind.Download, Source = Remote(site, "/ok.bin"), Target = Local(Path.Combine(_root, "rm", "ok.bin")) },
            new QueueItem { Kind = TransferKind.Download, IsDirectory = true, Source = Remote(site, "/dir"), Target = Local(Path.Combine(_root, "rm", "dir")) },
            new QueueItem { Kind = TransferKind.Download, Source = Remote(site, "/missing"), Target = Local(Path.Combine(_root, "rm", "missing")) },
        ]);
        await RunQueueAsync();

        // Only the failed transfer stays visible.
        var left = Assert.Single(_queue.Snapshot());
        Assert.Equal("missing", left.Name);
        Assert.True(File.Exists(Path.Combine(_root, "rm", "dir", "x.bin")));
    }
}
