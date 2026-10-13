using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DackelFxp.Ftp.Listing;
using DackelFxp.Ftp.Protocol;
using DackelFxp.Ftp.Transfer;

namespace DackelFxp.Ftp;

/// <summary>
/// High-level FTP/FTPS client. One instance = one control connection.
/// All public operations are serialized; FXP drives two clients via <see cref="FxpTransfer"/>.
/// </summary>
public sealed class FtpClient : IAsyncDisposable
{
    private static readonly Encoding Latin1 = Encoding.Latin1;

    private readonly SemaphoreSlim _opLock = new(1, 1);
    private FtpControlConnection _control;
    private bool? _binary;
    private bool? _protected;
    private bool _sscn;
    private static long _nextId;
    /// <summary>Hosts that need TLS 1.2 (no TLS 1.3 session reuse on data connections). Remembered per process.</summary>
    private static readonly ConcurrentDictionary<string, bool> Tls12Hosts = new(StringComparer.OrdinalIgnoreCase);
    private SslProtocols _negotiatedTls;
    private long _copiedBytes;

    internal long Id { get; } = Interlocked.Increment(ref _nextId);

    public FtpClient(FtpOptions options)
    {
        Options = options;
        _control = CreateControl();
    }

    public FtpOptions Options { get; }
    public event Action<FtpLogEntry>? Log;

    public bool IsConnected => _control.IsConnected;
    public bool IsEncrypted => _control.IsEncrypted;
    public bool IsBusy => _opLock.CurrentCount == 0;
    public X509Certificate2? ServerCertificate => _control.RemoteCertificate;
    public string? WelcomeMessage { get; private set; }
    public string? SystemType { get; private set; }
    public string WorkingDirectory { get; private set; } = "/";
    public IReadOnlySet<string> Features => _features;
    public DateTimeOffset LastActivity { get; private set; } = DateTimeOffset.Now;

    private HashSet<string> _features = new(StringComparer.OrdinalIgnoreCase);

    internal FtpControlConnection Control => _control;
    internal bool DataProtected => _protected == true;
    internal bool UsesTls => Options.TlsMode != FtpTlsMode.None;
    private string HostKey => $"{Options.Host}:{Options.Port}";
    private SslProtocols EffectiveTlsProtocols =>
        Options.TlsProtocols == SslProtocols.None && Tls12Hosts.ContainsKey(HostKey) ? SslProtocols.Tls12 : Options.TlsProtocols;
    /// <summary>TLS version negotiated on the control connection.</summary>
    public SslProtocols NegotiatedTls => _negotiatedTls;

    public bool HasFeature(string feature) => _features.Contains(feature);

    private FtpControlConnection CreateControl()
    {
        var control = new FtpControlConnection(Options.Encoding ?? Encoding.UTF8) { ReadTimeout = Options.ReadTimeout };
        control.Log += e => Log?.Invoke(e);
        return control;
    }

    // ---------------------------------------------------------------- connection

    public Task ConnectAsync(CancellationToken ct = default) => RunAsync(() => ConnectCoreAsync(ct), ct);

    private async Task ConnectCoreAsync(CancellationToken ct)
    {
        if (_control.IsConnected) return;
        await _control.DisposeAsync().ConfigureAwait(false);
        _control = CreateControl();
        _binary = null;
        _protected = null;
        _sscn = false;
        _features = new(StringComparer.OrdinalIgnoreCase);

        await _control.ConnectAsync(Options.Host, Options.Port, Options.ConnectTimeout, ct).ConfigureAwait(false);
        if (Options.TlsMode == FtpTlsMode.Implicit)
            await _control.UpgradeToTlsAsync(Options.Host, ValidateCertificate, ct, EffectiveTlsProtocols).ConfigureAwait(false);

        var welcome = await _control.ReadReplyAsync(ct).ConfigureAwait(false);
        while (welcome.IsPreliminary) welcome = await _control.ReadReplyAsync(ct).ConfigureAwait(false);
        if (!welcome.IsSuccess) throw new FtpCommandException("CONNECT", welcome);
        WelcomeMessage = string.Join(Environment.NewLine, welcome.Lines);

        if (Options.TlsMode == FtpTlsMode.Explicit)
        {
            var auth = await _control.ExecuteAsync("AUTH TLS", ct).ConfigureAwait(false);
            if (!auth.IsSuccess) auth = await _control.ExecuteAsync("AUTH SSL", ct).ConfigureAwait(false);
            if (!auth.IsSuccess) throw new FtpCommandException("AUTH TLS", auth);
            await _control.UpgradeToTlsAsync(Options.Host, ValidateCertificate, ct, EffectiveTlsProtocols).ConfigureAwait(false);
        }

        _negotiatedTls = _control.TlsProtocol;
        await LoginCoreAsync(ct).ConfigureAwait(false);
        await ReadFeaturesCoreAsync(ct).ConfigureAwait(false);

        if (Options.Encoding is null)
        {
            if (_features.Contains("UTF8"))
                await _control.ExecuteAsync("OPTS UTF8 ON", ct).ConfigureAwait(false);
            else
                _control.Encoding = Latin1;
        }

        if (UsesTls)
        {
            await _control.ExecuteAsync("PBSZ 0", ct).ConfigureAwait(false);
            await SetProtectionCoreAsync(Options.ProtectData, ct).ConfigureAwait(false);
            if (Options.ClearControlChannel)
            {
                var ccc = await _control.ExecuteAsync("CCC", ct).ConfigureAwait(false);
                if (ccc.IsSuccess) await _control.DowngradeFromTlsAsync().ConfigureAwait(false);
            }
        }

        var syst = await _control.ExecuteAsync("SYST", ct).ConfigureAwait(false);
        if (syst.IsSuccess) SystemType = syst.Message;
        await RefreshWorkingDirectoryCoreAsync(ct).ConfigureAwait(false);
    }

    private async Task LoginCoreAsync(CancellationToken ct)
    {
        var user = await _control.ExecuteAsync("USER " + Options.User, ct).ConfigureAwait(false);
        if (user.Code == 331)
        {
            var pass = await _control.ExecuteAsync("PASS " + Options.Password, ct).ConfigureAwait(false);
            if (!pass.IsSuccess) throw new FtpCommandException("PASS", pass);
        }
        else if (!user.IsSuccess)
        {
            throw new FtpCommandException("USER", user);
        }
    }

    private async Task ReadFeaturesCoreAsync(CancellationToken ct)
    {
        var feat = await _control.ExecuteAsync("FEAT", ct).ConfigureAwait(false);
        if (!feat.IsSuccess) return;
        foreach (var line in feat.InnerLines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            _features.Add(trimmed);
            var space = trimmed.IndexOf(' ');
            if (space > 0) _features.Add(trimmed[..space]);
        }
    }

    private bool ValidateCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (certificate is null || Options.CertificateValidator is null) return false;
        var cert = certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return Options.CertificateValidator(cert, chain, errors);
    }

    internal RemoteCertificateValidationCallback CertificateCallback => ValidateCertificate;

    public async Task DisconnectAsync()
    {
        await _opLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_control.IsConnected)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await _control.ExecuteAsync("QUIT", cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is FtpException or OperationCanceledException) { }
            }
            await _control.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _opLock.Release();
        }
    }

    // ---------------------------------------------------------------- basic commands

    /// <summary>Executes a raw command and returns the reply without throwing on negative replies.</summary>
    public Task<FtpReply> ExecuteAsync(string command, CancellationToken ct = default) =>
        RunAsync(() => _control.ExecuteAsync(command, ct), ct);

    public Task<FtpReply> NoopAsync(CancellationToken ct = default) => ExecuteAsync("NOOP", ct);

    /// <summary>Sends NOOP only if the connection is idle; returns false if busy.</summary>
    public async Task<bool> TryKeepAliveAsync(CancellationToken ct = default)
    {
        if (!await _opLock.WaitAsync(0, ct).ConfigureAwait(false)) return false;
        try
        {
            if (!_control.IsConnected) return false;
            await _control.ExecuteAsync("NOOP", ct).ConfigureAwait(false);
            LastActivity = DateTimeOffset.Now;
            return true;
        }
        finally
        {
            _opLock.Release();
        }
    }

    public Task<string> GetWorkingDirectoryAsync(CancellationToken ct = default) =>
        RunAsync(async () => { await RefreshWorkingDirectoryCoreAsync(ct).ConfigureAwait(false); return WorkingDirectory; }, ct);

    public Task ChangeDirectoryAsync(string path, CancellationToken ct = default) =>
        RunAsync(() => ChangeDirectoryCoreAsync(path, ct), ct);

    public Task ChangeToParentAsync(CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            await EnsureAsync("CDUP", ct).ConfigureAwait(false);
            await RefreshWorkingDirectoryCoreAsync(ct).ConfigureAwait(false);
        }, ct);

    public Task MakeDirectoryAsync(string path, CancellationToken ct = default) =>
        RunAsync(() => EnsureAsync("MKD " + path, ct), ct);

    public Task RemoveDirectoryAsync(string path, CancellationToken ct = default) =>
        RunAsync(() => EnsureAsync("RMD " + path, ct), ct);

    public Task DeleteFileAsync(string path, CancellationToken ct = default) =>
        RunAsync(() => EnsureAsync("DELE " + path, ct), ct);

    public Task RenameAsync(string from, string to, CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            var rnfr = await _control.ExecuteAsync("RNFR " + from, ct).ConfigureAwait(false);
            if (rnfr.Code != 350) throw new FtpCommandException("RNFR " + from, rnfr);
            await EnsureAsync("RNTO " + to, ct).ConfigureAwait(false);
        }, ct);

    public Task SetPermissionsAsync(string path, string mode, CancellationToken ct = default) =>
        RunAsync(() => EnsureAsync($"SITE CHMOD {mode} {path}", ct), ct);

    /// <summary>Returns the file size or -1 if the server does not tell.</summary>
    public Task<long> GetSizeAsync(string path, CancellationToken ct = default) =>
        RunAsync(() => GetSizeCoreAsync(path, ct), ct);

    public Task<DateTime?> GetModifiedTimeAsync(string path, CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            var reply = await _control.ExecuteAsync("MDTM " + path, ct).ConfigureAwait(false);
            if (reply.Code != 213 || reply.Message.Length < 14) return (DateTime?)null;
            return DateTime.TryParseExact(reply.Message.Trim()[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc)
                ? utc.ToLocalTime() : null;
        }, ct);

    public Task<bool> SetModifiedTimeAsync(string path, DateTime time, CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            if (!HasFeature("MFMT")) return false;
            var stamp = time.ToUniversalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            var reply = await _control.ExecuteAsync($"MFMT {stamp} {path}", ct).ConfigureAwait(false);
            return reply.IsSuccess;
        }, ct);

    /// <summary>Asks the server for a CRC32 (XCRC). Returns null if unsupported.</summary>
    public Task<uint?> GetCrc32Async(string path, CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            if (!HasFeature("XCRC")) return (uint?)null;
            var reply = await _control.ExecuteAsync($"XCRC \"{path}\"", ct).ConfigureAwait(false);
            if (!reply.IsSuccess) return null;
            var token = reply.Message.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(t => t.Length == 8);
            return token is not null && uint.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var crc) ? crc : null;
        }, ct);

    internal async Task<long> GetSizeCoreAsync(string path, CancellationToken ct)
    {
        await SetBinaryCoreAsync(true, ct).ConfigureAwait(false);
        var reply = await _control.ExecuteAsync("SIZE " + path, ct).ConfigureAwait(false);
        return reply.Code == 213 && long.TryParse(reply.Message.Trim(), out var size) ? size : -1;
    }

    internal async Task ChangeDirectoryCoreAsync(string path, CancellationToken ct)
    {
        await EnsureAsync("CWD " + path, ct).ConfigureAwait(false);
        await RefreshWorkingDirectoryCoreAsync(ct).ConfigureAwait(false);
    }

    private async Task RefreshWorkingDirectoryCoreAsync(CancellationToken ct)
    {
        var pwd = await _control.ExecuteAsync("PWD", ct).ConfigureAwait(false);
        if (!pwd.IsSuccess) return;
        var start = pwd.Message.IndexOf('"');
        var end = pwd.Message.LastIndexOf('"');
        WorkingDirectory = start >= 0 && end > start
            ? pwd.Message[(start + 1)..end].Replace("\"\"", "\"")
            : pwd.Message.Split(' ')[0];
    }

    internal async Task<FtpReply> EnsureAsync(string command, CancellationToken ct)
    {
        var reply = await _control.ExecuteAsync(command, ct).ConfigureAwait(false);
        if (!reply.IsSuccess) throw new FtpCommandException(command, reply);
        return reply;
    }

    internal async Task SetBinaryCoreAsync(bool binary, CancellationToken ct)
    {
        if (_binary == binary) return;
        await EnsureAsync(binary ? "TYPE I" : "TYPE A", ct).ConfigureAwait(false);
        _binary = binary;
    }

    internal async Task SetProtectionCoreAsync(bool protect, CancellationToken ct)
    {
        if (!UsesTls || _protected == protect) return;
        await EnsureAsync(protect ? "PROT P" : "PROT C", ct).ConfigureAwait(false);
        _protected = protect;
    }

    /// <summary>SSCN ON makes the server act as TLS client on data connections (secure FXP).</summary>
    internal async Task<bool> TrySetSscnCoreAsync(bool on, CancellationToken ct)
    {
        if (_sscn == on) return true;
        var reply = await _control.ExecuteAsync(on ? "SSCN ON" : "SSCN OFF", ct).ConfigureAwait(false);
        if (!reply.IsSuccess) return false;
        _sscn = on;
        return true;
    }

    internal bool ShouldSendPret => Options.PretMode switch
    {
        FtpPretMode.Always => true,
        FtpPretMode.Never => false,
        _ => _features.Contains("PRET"),
    };

    // ---------------------------------------------------------------- data connections

    private async Task<FtpDataChannel> OpenDataChannelCoreAsync(string transferCommand, CancellationToken ct)
    {
        // Client transfers need us as TLS client, so a leftover SSCN ON from FXP must go,
        // and a PROT C from a clear FXP fallback must not leak into client transfers.
        if (_sscn) await TrySetSscnCoreAsync(false, ct).ConfigureAwait(false);
        await SetProtectionCoreAsync(Options.ProtectData, ct).ConfigureAwait(false);
        if (ShouldSendPret) await EnsureAsync("PRET " + transferCommand, ct).ConfigureAwait(false);

        switch (Options.DataMode)
        {
            case FtpDataMode.Passive:
            case FtpDataMode.ExtendedPassive:
            {
                IPEndPoint endPoint;
                if (Options.DataMode == FtpDataMode.ExtendedPassive)
                {
                    var epsv = await EnsureAsync("EPSV", ct).ConfigureAwait(false);
                    endPoint = new IPEndPoint(_control.RemoteEndPoint!.Address, FtpAddressParser.ParseEpsv(epsv));
                }
                else
                {
                    var pasv = await EnsureAsync("PASV", ct).ConfigureAwait(false);
                    endPoint = FtpAddressParser.ParsePasv(pasv);
                    var controlIp = _control.RemoteEndPoint!.Address;
                    if (Options.PassiveUseControlHost && IsUnroutable(endPoint.Address) && !IsUnroutable(controlIp))
                        endPoint = new IPEndPoint(controlIp, endPoint.Port);
                }
                return await FtpDataChannel.ConnectAsync(endPoint, Options.ConnectTimeout, ct).ConfigureAwait(false);
            }
            default:
            {
                var local = _control.LocalEndPoint!.Address;
                var channel = FtpDataChannel.Listen(local, Options.ActivePortMin, Options.ActivePortMax);
                try
                {
                    var announce = Options.ActiveExternalIp is { Length: > 0 } ext ? IPAddress.Parse(ext) : local;
                    var ep = new IPEndPoint(announce, channel.ListenEndPoint.Port);
                    if (Options.DataMode == FtpDataMode.ExtendedActive)
                        await EnsureAsync("EPRT " + FtpAddressParser.FormatEprt(ep), ct).ConfigureAwait(false);
                    else
                        await EnsureAsync("PORT " + FtpAddressParser.FormatPort(ep), ct).ConfigureAwait(false);
                    return channel;
                }
                catch
                {
                    await channel.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
        }
    }

    internal static bool IsUnroutable(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 127 || b[0] == 0
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>Sends the transfer command and completes the data connection (accept + TLS).</summary>
    private async Task StartDataTransferCoreAsync(FtpDataChannel channel, string command, CancellationToken ct)
    {
        await _control.SendAsync(command, ct).ConfigureAwait(false);
        var reply = await _control.ReadReplyAsync(ct).ConfigureAwait(false);
        if (!reply.IsPreliminary) throw new FtpCommandException(command, reply);
        await channel.AcceptAsync(Options.DataTimeout, ct).ConfigureAwait(false);
        if (UsesTls && _protected == true)
            await channel.SecureAsync(Options.Host, ValidateCertificate, EffectiveTlsProtocols, ct).ConfigureAwait(false);
    }

    private async Task FinishDataTransferCoreAsync(string command, CancellationToken ct)
    {
        var final = await _control.ReadReplyAsync(Options.DataTimeout, ct).ConfigureAwait(false);
        if (!final.IsSuccess) throw new FtpCommandException(command, final);
    }

    /// <summary>On cancellation the control connection state is unknown: abort and drop it.</summary>
    internal async Task AbortAfterCancelAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _control.SendAsync("ABOR", cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FtpException or OperationCanceledException) { }
        await _control.DisposeAsync().ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- listing

    public Task<List<FtpListItem>> ListAsync(string? path = null, CancellationToken ct = default) =>
        RunAsync(async () =>
        {
            var lines = await ListRawCoreAsync(path, ct).ConfigureAwait(false);
            return FtpListParser.Parse(lines, Options.TimeZoneOffset);
        }, ct);

    public Task<List<string>> ListRawAsync(string? path = null, CancellationToken ct = default) =>
        RunAsync(() => ListRawCoreAsync(path, ct), ct);

    private async Task<List<string>> ListRawCoreAsync(string? path, CancellationToken ct)
    {
        // CWD + LIST without argument: works with spaces in names on every server.
        if (!string.IsNullOrEmpty(path) && path != WorkingDirectory)
            await ChangeDirectoryCoreAsync(path, ct).ConfigureAwait(false);

        var method = Options.ListMethod;
        if (method == FtpListMethod.Auto)
            method = _features.Contains("MLSD") || _features.Contains("MLST") ? FtpListMethod.Mlsd : FtpListMethod.ListAl;

        if (method == FtpListMethod.StatL)
        {
            var stat = await _control.ExecuteAsync("STAT -l", ct).ConfigureAwait(false);
            if (stat.IsSuccess) return stat.InnerLines.Select(l => l.TrimStart()).ToList();
            method = FtpListMethod.ListAl;
        }

        var command = method switch
        {
            FtpListMethod.Mlsd => "MLSD",
            FtpListMethod.ListAl => "LIST -al",
            _ => "LIST",
        };
        try
        {
            return await WithTlsFallbackAsync(() => ReadListingCoreAsync(command, ct), ct).ConfigureAwait(false);
        }
        catch (FtpCommandException ex) when (ex.Reply.IsPermanentError && command != "LIST")
        {
            return await WithTlsFallbackAsync(() => ReadListingCoreAsync("LIST", ct), ct).ConfigureAwait(false);
        }
    }

    private async Task<List<string>> ReadListingCoreAsync(string command, CancellationToken ct)
    {
        await SetBinaryCoreAsync(false, ct).ConfigureAwait(false);
        await using var channel = await OpenDataChannelCoreAsync(command, ct).ConfigureAwait(false);
        var lines = new List<string>();
        try
        {
            await StartDataTransferCoreAsync(channel, command, ct).ConfigureAwait(false);
            using (var reader = new StreamReader(channel.Stream, _control.Encoding, false, 16 * 1024, leaveOpen: true))
            {
                while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                    lines.Add(line);
            }
            await channel.CloseAsync().ConfigureAwait(false);
            await FinishDataTransferCoreAsync(command, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await AbortAfterCancelAsync().ConfigureAwait(false);
            throw;
        }
        return lines;
    }

    // ---------------------------------------------------------------- transfers

    /// <summary>Downloads <paramref name="remotePath"/> into <paramref name="target"/>, starting at <paramref name="offset"/> (REST).</summary>
    public Task DownloadAsync(string remotePath, Stream target, long offset = 0, IProgress<long>? progress = null,
        BandwidthLimiter? limiter = null, CancellationToken ct = default) =>
        RunAsync(() => WithTlsFallbackAsync(async () =>
        {
            await SetBinaryCoreAsync(true, ct).ConfigureAwait(false);
            var command = "RETR " + remotePath;
            await using var channel = await OpenDataChannelCoreAsync(command, ct).ConfigureAwait(false);
            try
            {
                if (offset > 0) await RestCoreAsync(offset, ct).ConfigureAwait(false);
                await StartDataTransferCoreAsync(channel, command, ct).ConfigureAwait(false);
                await CopyAsync(channel.Stream, target, offset, progress, limiter, ct).ConfigureAwait(false);
                await channel.CloseAsync().ConfigureAwait(false);
                await FinishDataTransferCoreAsync(command, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await AbortAfterCancelAsync().ConfigureAwait(false);
                throw;
            }
            return true;
        }, ct), ct);

    /// <summary>Uploads <paramref name="source"/> to <paramref name="remotePath"/>. With offset &gt; 0 the upload is resumed.</summary>
    public Task UploadAsync(Stream source, string remotePath, long offset = 0, IProgress<long>? progress = null,
        BandwidthLimiter? limiter = null, CancellationToken ct = default) =>
        RunAsync(() => WithTlsFallbackAsync(async () =>
        {
            await SetBinaryCoreAsync(true, ct).ConfigureAwait(false);
            var command = "STOR " + remotePath;
            await using var channel = await OpenDataChannelCoreAsync(command, ct).ConfigureAwait(false);
            try
            {
                if (offset > 0)
                {
                    await RestCoreAsync(offset, ct).ConfigureAwait(false);
                    if (source.CanSeek) source.Seek(offset, SeekOrigin.Begin);
                }
                await StartDataTransferCoreAsync(channel, command, ct).ConfigureAwait(false);
                await CopyAsync(source, channel.Stream, offset, progress, limiter, ct).ConfigureAwait(false);
                await channel.CloseAsync().ConfigureAwait(false);
                await FinishDataTransferCoreAsync(command, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await AbortAfterCancelAsync().ConfigureAwait(false);
                throw;
            }
            return true;
        }, ct), ct);

    internal async Task RestCoreAsync(long offset, CancellationToken ct)
    {
        var reply = await _control.ExecuteAsync("REST " + offset.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        if (reply.Code != 350) throw new FtpCommandException("REST", reply);
    }

    private async Task CopyAsync(Stream from, Stream to, long startOffset, IProgress<long>? progress,
        BandwidthLimiter? limiter, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var total = startOffset;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (true)
        {
            timeout.CancelAfter(Options.DataTimeout);
            int read;
            try
            {
                read = await from.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (read == 0) break;
                if (limiter is not null) await limiter.WaitAsync(read, ct).ConfigureAwait(false);
                await to.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new FtpException(Lang.T("Zeitüberschreitung auf der Datenverbindung", "Timeout on the data connection"));
            }
            catch (IOException ex)
            {
                throw new FtpException(Lang.T($"Datenverbindung abgebrochen: {ex.Message}", $"Data connection aborted: {ex.Message}"), ex);
            }
            total += read;
            _copiedBytes += read;
            LastActivity = DateTimeOffset.Now;
            progress?.Report(total);
        }
        await to.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// With TLS 1.3, .NET on Linux cannot resume the control session on more than one data connection,
    /// which servers with mandatory session reuse (vsftpd require_ssl_reuse) reject. If a protected data
    /// transfer fails before any byte moved, reconnect with TLS 1.2 (reusable sessions) and retry once.
    /// </summary>
    private async Task<T> WithTlsFallbackAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        _copiedBytes = 0;
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (FtpException) when (UsesTls && _protected == true && _negotiatedTls == SslProtocols.Tls13
                                   && Options.TlsProtocols == SslProtocols.None && _copiedBytes == 0
                                   && Tls12Hosts.TryAdd(HostKey, true))
        {
            _control.WriteLog(FtpLogKind.Tls, Lang.T("Datenverbindung mit TLS 1.3 abgelehnt (Session-Reuse) – verbinde neu mit TLS 1.2", "Data connection with TLS 1.3 rejected (session reuse) – reconnecting with TLS 1.2"));
            var directory = WorkingDirectory;
            await _control.DisposeAsync().ConfigureAwait(false);
            await ConnectCoreAsync(ct).ConfigureAwait(false);
            if (directory != WorkingDirectory)
            {
                try { await ChangeDirectoryCoreAsync(directory, ct).ConfigureAwait(false); }
                catch (FtpCommandException) { }
            }
            return await operation().ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- locking

    /// <summary>Takes exclusive ownership of the connection (used by FXP to drive two clients).</summary>
    internal async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await _opLock.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(this);
    }

    internal void Touch() => LastActivity = DateTimeOffset.Now;

    private sealed class Releaser(FtpClient owner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner._opLock.Release();
        }
    }

    private async Task RunAsync(Func<Task> action, CancellationToken ct)
    {
        await _opLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
            LastActivity = DateTimeOffset.Now;
        }
        finally
        {
            _opLock.Release();
        }
    }

    private async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _opLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await action().ConfigureAwait(false);
            LastActivity = DateTimeOffset.Now;
            return result;
        }
        finally
        {
            _opLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _control.DisposeAsync().ConfigureAwait(false);
        _opLock.Dispose();
    }
}
