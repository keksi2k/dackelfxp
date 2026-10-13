using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DackelFxp.Core.Settings;
using DackelFxp.Core.Sites;
using DackelFxp.Ftp;
using DackelFxp.Ftp.Protocol;
using DackelFxp.Ftp.Transfer;

namespace DackelFxp.Core.Connections;

public enum CertificateDecision { Reject, AcceptOnce, AcceptAlways }

/// <summary>Asks the user about an untrusted server certificate (implemented by the UI).</summary>
public interface ICertificatePrompt
{
    Task<CertificateDecision> AskAsync(Site site, X509Certificate2 certificate, SslPolicyErrors errors);
}

public sealed record SiteLogEntry(Site Site, int Connection, FtpLogEntry Entry);

/// <summary>A connection borrowed from the pool. Dispose returns it.</summary>
public sealed class PooledConnection : IAsyncDisposable
{
    private readonly ConnectionManager.SitePool _pool;
    private int _returned;

    internal PooledConnection(ConnectionManager.SitePool pool, FtpClient client, int number)
    {
        _pool = pool;
        Client = client;
        Number = number;
    }

    public FtpClient Client { get; }
    public int Number { get; }
    public Site Site => _pool.Site;

    public ValueTask DisposeAsync() =>
        Interlocked.Exchange(ref _returned, 1) == 0 ? _pool.ReturnAsync(this) : ValueTask.CompletedTask;
}

/// <summary>Connection pools per site, keep-alive, idle disconnect, certificate trust and speed limits.</summary>
public sealed class ConnectionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, SitePool> _pools = new();
    private readonly ConcurrentDictionary<string, bool> _sessionTrusted = new();
    private readonly SemaphoreSlim _promptLock = new(1, 1);
    private readonly SiteStore _sites;
    private readonly SettingsStore _settings;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _maintenance;

    public ConnectionManager(SiteStore sites, SettingsStore settings, ICertificatePrompt? prompt = null)
    {
        _sites = sites;
        _settings = settings;
        CertificatePrompt = prompt;
        GlobalLimiter = new BandwidthLimiter(settings.Current.GlobalSpeedLimitKb * 1024L);
        _maintenance = MaintenanceLoopAsync(_cts.Token);
    }

    public ICertificatePrompt? CertificatePrompt { get; set; }
    public BandwidthLimiter GlobalLimiter { get; }
    public event Action<SiteLogEntry>? Log;
    /// <summary>Raised when connections of a site are opened or closed.</summary>
    public event Action<Site>? ConnectionsChanged;

    public Task<PooledConnection> RentAsync(Site site, CancellationToken ct = default) =>
        _pools.GetOrAdd(site.Id, _ => new SitePool(this, site)).RentAsync(site, ct);

    public int ConnectionCount(Guid siteId) => _pools.TryGetValue(siteId, out var p) ? p.OpenCount : 0;

    public BandwidthLimiter GetLimiter(Site site) =>
        _pools.GetOrAdd(site.Id, _ => new SitePool(this, site)).Limiter;

    public void ApplySpeedLimits()
    {
        GlobalLimiter.BytesPerSecond = _settings.Current.GlobalSpeedLimitKb * 1024L;
        foreach (var pool in _pools.Values) pool.Limiter.BytesPerSecond = pool.Site.SpeedLimitKb * 1024L;
    }

    public async Task DisconnectAsync(Guid siteId)
    {
        if (_pools.TryRemove(siteId, out var pool)) await pool.CloseAllAsync().ConfigureAwait(false);
    }

    public async Task DisconnectAllAsync()
    {
        foreach (var id in _pools.Keys.ToList()) await DisconnectAsync(id).ConfigureAwait(false);
    }

    internal FtpOptions CreateOptions(Site site) => new()
    {
        Host = site.Host,
        Port = site.Port,
        User = site.User,
        Password = site.Password,
        TlsMode = site.TlsMode,
        ProtectData = site.ProtectData,
        TlsProtocols = site.TlsVersion switch
        {
            TlsVersion.Tls12 => System.Security.Authentication.SslProtocols.Tls12,
            TlsVersion.Tls13 => System.Security.Authentication.SslProtocols.Tls13,
            _ => System.Security.Authentication.SslProtocols.None,
        },
        ClearControlChannel = site.ClearControlChannel,
        CertificateValidator = (cert, chain, errors) => ValidateCertificate(site, cert, errors),
        DataMode = site.DataMode,
        PassiveUseControlHost = site.PassiveUseControlHost,
        ListMethod = site.ListMethod,
        PretMode = site.PretMode,
        Encoding = site.Encoding is "" or "auto" ? null : Encoding.GetEncoding(site.Encoding),
        TimeZoneOffset = TimeSpan.FromHours(site.TimeZoneOffsetHours),
    };

    private bool ValidateCertificate(Site site, X509Certificate2 cert, SslPolicyErrors errors)
    {
        var fingerprint = Fingerprint(cert);
        if (site.TrustedCertificate == fingerprint || _sessionTrusted.ContainsKey(fingerprint)) return true;
        if (CertificatePrompt is null) return false;

        // Called from the TLS handshake on a pool thread; prompts are serialized so parallel connections ask once.
        _promptLock.Wait();
        try
        {
            if (_sessionTrusted.ContainsKey(fingerprint)) return true;
            var decision = CertificatePrompt.AskAsync(site, cert, errors).GetAwaiter().GetResult();
            if (decision == CertificateDecision.Reject) return false;
            _sessionTrusted[fingerprint] = true;
            if (decision == CertificateDecision.AcceptAlways)
            {
                site.TrustedCertificate = fingerprint;
                if (!site.IsTemporary && _sites.Find(site.Id) is not null) _sites.Save();
            }
            return true;
        }
        finally
        {
            _promptLock.Release();
        }
    }

    public static string Fingerprint(X509Certificate2 cert) =>
        Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256));

    private async Task MaintenanceLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                foreach (var pool in _pools.Values)
                {
                    try { await pool.MaintainAsync(_settings.Current.IdleDisconnectMinutes, ct).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is FtpException or OperationCanceledException) { }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _maintenance.ConfigureAwait(false); } catch (OperationCanceledException) { }
        await DisconnectAllAsync().ConfigureAwait(false);
    }

    internal sealed class SitePool
    {
        private readonly ConnectionManager _owner;
        private readonly SemaphoreSlim _slots;
        private readonly ConcurrentBag<(FtpClient Client, int Number)> _idle = [];
        private readonly ConcurrentDictionary<FtpClient, int> _all = new();
        private int _counter;

        public SitePool(ConnectionManager owner, Site site)
        {
            _owner = owner;
            Site = site;
            _slots = new SemaphoreSlim(Math.Max(1, site.MaxConnections));
            Limiter = new BandwidthLimiter(site.SpeedLimitKb * 1024L, owner.GlobalLimiter);
        }

        public Site Site { get; private set; }
        public BandwidthLimiter Limiter { get; }
        public int OpenCount => _all.Count;

        public async Task<PooledConnection> RentAsync(Site site, CancellationToken ct)
        {
            Site = site;
            await _slots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                while (_idle.TryTake(out var idle))
                {
                    if (idle.Client.IsConnected) return new PooledConnection(this, idle.Client, idle.Number);
                    await DropAsync(idle.Client).ConfigureAwait(false);
                }

                var number = Interlocked.Increment(ref _counter);
                var client = new FtpClient(_owner.CreateOptions(site));
                client.Log += e => _owner.Log?.Invoke(new SiteLogEntry(Site, number, e));
                try
                {
                    await client.ConnectAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
                _all[client] = number;
                site.LastConnected = DateTimeOffset.Now;
                _owner.ConnectionsChanged?.Invoke(Site);
                return new PooledConnection(this, client, number);
            }
            catch
            {
                _slots.Release();
                throw;
            }
        }

        public async ValueTask ReturnAsync(PooledConnection connection)
        {
            try
            {
                if (connection.Client.IsConnected && _all.ContainsKey(connection.Client))
                    _idle.Add((connection.Client, connection.Number));
                else
                    await DropAsync(connection.Client).ConfigureAwait(false);
            }
            finally
            {
                _slots.Release();
            }
        }

        public async Task MaintainAsync(int idleDisconnectMinutes, CancellationToken ct)
        {
            var keepAlive = TimeSpan.FromSeconds(Math.Max(5, Site.KeepAliveSeconds));
            var idleLimit = idleDisconnectMinutes > 0 ? TimeSpan.FromMinutes(idleDisconnectMinutes) : TimeSpan.MaxValue;
            var keep = new List<(FtpClient, int)>();
            while (_idle.TryTake(out var entry))
            {
                var idleFor = DateTimeOffset.Now - entry.Client.LastActivity;
                if (!entry.Client.IsConnected || idleFor > idleLimit)
                {
                    await DisconnectAsync(entry.Client).ConfigureAwait(false);
                    continue;
                }
                if (Site.KeepAliveSeconds > 0 && idleFor > keepAlive)
                {
                    try { await entry.Client.TryKeepAliveAsync(ct).ConfigureAwait(false); }
                    catch (FtpException) { await DisconnectAsync(entry.Client).ConfigureAwait(false); continue; }
                }
                keep.Add(entry);
            }
            foreach (var entry in keep) _idle.Add(entry);
        }

        public async Task CloseAllAsync()
        {
            foreach (var client in _all.Keys.ToList()) await DisconnectAsync(client).ConfigureAwait(false);
        }

        private async Task DisconnectAsync(FtpClient client)
        {
            try { await client.DisconnectAsync().ConfigureAwait(false); } catch (FtpException) { }
            await DropAsync(client).ConfigureAwait(false);
        }

        private async Task DropAsync(FtpClient client)
        {
            if (_all.TryRemove(client, out _)) _owner.ConnectionsChanged?.Invoke(Site);
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }
}
