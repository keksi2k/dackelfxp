using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DackelFxp.Ftp.Tests;

/// <summary>
/// Minimal in-memory FTP server for tests: plain/explicit/implicit TLS, PASV/PORT, PROT P,
/// SSCN, CPSV and PRET, so FXP between two instances can be tested end-to-end.
/// </summary>
public sealed class FakeFtpServer : IAsyncDisposable
{
    private static readonly Lazy<X509Certificate2> SharedCertificate = new(CreateCertificate);

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    public FakeFtpServer(bool tls = false, bool implicitTls = false, int port = 0)
    {
        Tls = tls || implicitTls;
        ImplicitTls = implicitTls;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public bool Tls { get; }
    public bool ImplicitTls { get; }
    public bool SupportsSscn { get; set; } = true;
    public bool SupportsCpsv { get; set; } = true;
    public bool SupportsMlsd { get; set; } = true;
    public bool RequirePret { get; set; }
    /// <summary>Refuse PROT C (forces encrypted data).</summary>
    public bool RequireProtP { get; set; }
    /// <summary>Refuse PORT (e.g. FXP disabled for this direction).</summary>
    public bool RefusePort { get; set; }
    /// <summary>Delay per RETR to make transfers overlap (concurrency tests).</summary>
    public TimeSpan RetrDelay { get; set; }
    private int _activeRetr;
    private int _maxActiveRetr;
    /// <summary>Highest number of RETR transfers that ran at the same time.</summary>
    public int MaxConcurrentRetr => Volatile.Read(ref _maxActiveRetr);

    internal void EnterRetr()
    {
        var now = Interlocked.Increment(ref _activeRetr);
        int max;
        while (now > (max = Volatile.Read(ref _maxActiveRetr)) && Interlocked.CompareExchange(ref _maxActiveRetr, now, max) != max) { }
    }

    internal void LeaveRetr() => Interlocked.Decrement(ref _activeRetr);

    public ConcurrentDictionary<string, byte[]> Files { get; } = new();
    public ConcurrentDictionary<string, bool> Directories { get; } = new(new Dictionary<string, bool> { ["/"] = true });
    /// <summary>Symbolic links: path -> target (listed as links, CWD follows them).</summary>
    public ConcurrentDictionary<string, string> Links { get; } = new();
    public ConcurrentQueue<string> ReceivedCommands { get; } = new();
    public ConcurrentQueue<Exception> Errors { get; } = new();

    public static X509Certificate2 Certificate => SharedCertificate.Value;

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(1));
        // Round-trip through PFX so the private key is usable by SChannel on Windows.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var socket = await _listener.AcceptSocketAsync(_cts.Token);
                _ = new Session(this, socket).RunAsync(_cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _acceptLoop; } catch { }
    }

    private sealed class Session(FakeFtpServer server, Socket socket)
    {
        private Stream _stream = new NetworkStream(socket, ownsSocket: true);
        private string _cwd = "/";
        private bool _protP;
        private bool _sscn;
        private bool _loggedIn;
        private string? _user;
        private long _rest;
        private string? _renameFrom;
        private bool _pretSeen;
        private TcpListener? _pasvListener;
        private bool _cpsv;
        private IPEndPoint? _portTarget;

        public async Task RunAsync(CancellationToken ct)
        {
            try
            {
                if (server.ImplicitTls) await UpgradeAsync(ct);
                await ReplyAsync("220 FakeFtpServer ready", ct);
                while (!ct.IsCancellationRequested)
                {
                    var line = await ReadCommandLineAsync(ct);
                    if (line is null) break;
                    server.ReceivedCommands.Enqueue(line);
                    var space = line.IndexOf(' ');
                    var verb = (space < 0 ? line : line[..space]).ToUpperInvariant();
                    var arg = space < 0 ? "" : line[(space + 1)..];
                    if (!await HandleAsync(verb, arg, ct)) break;
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException) { }
            catch (Exception ex) { server.Errors.Enqueue(ex); }
            finally
            {
                _pasvListener?.Stop();
                await _stream.DisposeAsync();
            }
        }

        // Read byte-wise so no TLS handshake bytes are swallowed by a reader buffer after AUTH TLS.
        private async Task<string?> ReadCommandLineAsync(CancellationToken ct)
        {
            var bytes = new List<byte>();
            var one = new byte[1];
            while (true)
            {
                var n = await _stream.ReadAsync(one, ct);
                if (n == 0) return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
                if (one[0] == '\n') return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
                bytes.Add(one[0]);
            }
        }

        private async Task ReplyAsync(string text, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(text + "\r\n");
            await _stream.WriteAsync(bytes, ct);
            await _stream.FlushAsync(ct);
        }

        private async Task UpgradeAsync(CancellationToken ct)
        {
            var ssl = new SslStream(_stream, false);
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = Certificate }, ct);
            _stream = ssl;
        }

        private string Resolve(string path)
        {
            if (string.IsNullOrEmpty(path)) return _cwd;
            var full = path.StartsWith('/') ? path : (_cwd.TrimEnd('/') + "/" + path);
            var parts = new List<string>();
            foreach (var p in full.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (p == ".") continue;
                if (p == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
                parts.Add(p);
            }
            return "/" + string.Join('/', parts);
        }

        private async Task<bool> HandleAsync(string verb, string arg, CancellationToken ct)
        {
            if (!_loggedIn && verb is not ("USER" or "PASS" or "AUTH" or "QUIT" or "FEAT" or "PBSZ" or "PROT"))
            {
                await ReplyAsync("530 Please login", ct);
                return true;
            }

            switch (verb)
            {
                case "AUTH":
                    if (!server.Tls || server.ImplicitTls) { await ReplyAsync("502 No TLS", ct); break; }
                    await ReplyAsync("234 AUTH TLS ok", ct);
                    await UpgradeAsync(ct);
                    break;
                case "USER":
                    _user = arg;
                    await ReplyAsync("331 Password required", ct);
                    break;
                case "PASS":
                    if (_user == "bad") { await ReplyAsync("530 Login incorrect", ct); break; }
                    _loggedIn = true;
                    await ReplyAsync("230 Logged in", ct);
                    break;
                case "FEAT":
                    var feats = new List<string> { "211-Features:", " UTF8", " SIZE", " MDTM", " REST STREAM", " PRET" };
                    if (server.SupportsMlsd) feats.Add(" MLSD");
                    if (server.Tls) feats.AddRange([" AUTH TLS", " PBSZ", " PROT"]);
                    if (server.SupportsSscn) feats.Add(" SSCN");
                    if (server.SupportsCpsv) feats.Add(" CPSV");
                    feats.Add("211 End");
                    await ReplyAsync(string.Join("\r\n", feats), ct);
                    break;
                case "OPTS": await ReplyAsync("200 OK", ct); break;
                case "PBSZ": await ReplyAsync("200 PBSZ=0", ct); break;
                case "PROT":
                    if (arg == "C" && server.RequireProtP) { await ReplyAsync("536 PROT P required", ct); break; }
                    _protP = arg == "P";
                    await ReplyAsync("200 PROT " + arg, ct);
                    break;
                case "SSCN":
                    if (!server.SupportsSscn) { await ReplyAsync("500 Unknown command", ct); break; }
                    if (arg == "ON") _sscn = true;
                    else if (arg == "OFF") _sscn = false;
                    await ReplyAsync($"200 SSCN:{(_sscn ? "CLIENT" : "SERVER")} METHOD", ct);
                    break;
                case "SYST": await ReplyAsync("215 UNIX Type: L8", ct); break;
                case "NOOP": await ReplyAsync("200 NOOP ok", ct); break;
                case "TYPE": await ReplyAsync("200 Type set", ct); break;
                case "PWD": await ReplyAsync($"257 \"{_cwd}\" is current directory", ct); break;
                case "CWD":
                {
                    var target = Resolve(arg);
                    if (server.Links.TryGetValue(target, out var linkTarget)) target = linkTarget;
                    if (!server.Directories.ContainsKey(target)) { await ReplyAsync("550 No such directory", ct); break; }
                    _cwd = target;
                    await ReplyAsync("250 CWD ok", ct);
                    break;
                }
                case "CDUP":
                    _cwd = Resolve("..");
                    await ReplyAsync("250 CDUP ok", ct);
                    break;
                case "MKD":
                {
                    var target = Resolve(arg);
                    if (!server.Directories.TryAdd(target, true)) { await ReplyAsync("550 Exists", ct); break; }
                    await ReplyAsync($"257 \"{target}\" created", ct);
                    break;
                }
                case "RMD":
                    if (!server.Directories.TryRemove(Resolve(arg), out _)) { await ReplyAsync("550 No such directory", ct); break; }
                    await ReplyAsync("250 RMD ok", ct);
                    break;
                case "DELE":
                    if (!server.Files.TryRemove(Resolve(arg), out _)) { await ReplyAsync("550 No such file", ct); break; }
                    await ReplyAsync("250 DELE ok", ct);
                    break;
                case "SIZE":
                    if (server.Files.TryGetValue(Resolve(arg), out var sz)) await ReplyAsync($"213 {sz.Length}", ct);
                    else await ReplyAsync("550 No such file", ct);
                    break;
                case "RNFR":
                    _renameFrom = Resolve(arg);
                    await ReplyAsync(server.Files.ContainsKey(_renameFrom) ? "350 Ready" : "550 No such file", ct);
                    break;
                case "RNTO":
                    if (_renameFrom is null || !server.Files.TryRemove(_renameFrom, out var moved)) { await ReplyAsync("503 RNFR first", ct); break; }
                    server.Files[Resolve(arg)] = moved;
                    await ReplyAsync("250 Renamed", ct);
                    break;
                case "REST":
                    _rest = long.Parse(arg);
                    await ReplyAsync($"350 Restarting at {_rest}", ct);
                    break;
                case "PRET":
                    _pretSeen = true;
                    await ReplyAsync("200 OK, will use master for upcoming transfer", ct);
                    break;
                case "PASV":
                case "CPSV":
                {
                    if (verb == "CPSV" && !server.SupportsCpsv) { await ReplyAsync("500 Unknown command", ct); break; }
                    if (server.RequirePret && !_pretSeen) { await ReplyAsync("503 PRET required", ct); break; }
                    _pasvListener?.Stop();
                    _pasvListener = new TcpListener(IPAddress.Loopback, 0);
                    _pasvListener.Start(1);
                    _portTarget = null;
                    _cpsv = verb == "CPSV";
                    var port = ((IPEndPoint)_pasvListener.LocalEndpoint).Port;
                    await ReplyAsync($"227 Entering Passive Mode (127,0,0,1,{port / 256},{port % 256})", ct);
                    break;
                }
                case "PORT":
                {
                    if (server.RefusePort) { await ReplyAsync("500 PORT not allowed", ct); break; }
                    var n = arg.Split(',').Select(int.Parse).ToArray();
                    _portTarget = new IPEndPoint(new IPAddress([(byte)n[0], (byte)n[1], (byte)n[2], (byte)n[3]]), n[4] * 256 + n[5]);
                    _pasvListener?.Stop();
                    _pasvListener = null;
                    _cpsv = false;
                    await ReplyAsync("200 PORT ok", ct);
                    break;
                }
                case "LIST":
                case "MLSD":
                case "RETR":
                case "STOR":
                    await TransferAsync(verb, arg, ct);
                    break;
                case "QUIT":
                    await ReplyAsync("221 Bye", ct);
                    return false;
                case "SITE" when arg.StartsWith("MULTI", StringComparison.OrdinalIgnoreCase):
                    // Like some site scripts: several complete replies for one command, the last one late.
                    await ReplyAsync("200-Line one\r\n200 First reply", ct);
                    await ReplyAsync("200 Second reply", ct);
                    await Task.Delay(150, ct);
                    await ReplyAsync("200 Third reply", ct);
                    break;
                case "ABOR":
                    await ReplyAsync("226 ABOR ok", ct);
                    break;
                default:
                    await ReplyAsync("500 Unknown command", ct);
                    break;
            }
            return true;
        }

        private async Task TransferAsync(string verb, string arg, CancellationToken ct)
        {
            if (_pasvListener is null && _portTarget is null) { await ReplyAsync("425 Use PASV or PORT first", ct); return; }
            var path = Resolve(verb is "LIST" or "MLSD" && arg.StartsWith('-') ? "" : arg);
            if (verb == "RETR" && !server.Files.ContainsKey(path)) { await ReplyAsync("550 No such file", ct); ResetData(); return; }

            Socket dataSocket;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                if (_pasvListener is not null)
                {
                    dataSocket = await _pasvListener.AcceptSocketAsync(timeout.Token);
                }
                else
                {
                    dataSocket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    await dataSocket.ConnectAsync(_portTarget!, timeout.Token);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException)
            {
                await ReplyAsync("425 Can't open data connection", ct);
                ResetData();
                return;
            }

            // Like vsftpd: 150 only after the data connection is established.
            await ReplyAsync("150 Opening data connection", ct);
            Stream data = new NetworkStream(dataSocket, ownsSocket: true);
            try
            {
                if (_protP)
                {
                    var ssl = new SslStream(data, false);
                    if (_sscn || _cpsv)
                        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost", RemoteCertificateValidationCallback = (_, _, _, _) => true }, ct);
                    else
                        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = Certificate }, ct);
                    data = ssl;
                }

                switch (verb)
                {
                    case "LIST":
                    case "MLSD":
                        var sb = new StringBuilder();
                        foreach (var dir in server.Directories.Keys.Where(d => d != path && ParentOf(d) == path))
                            sb.Append(verb == "MLSD" ? $"type=dir;modify=20260101120000; {Name(dir)}\r\n" : $"drwxr-xr-x   2 user group 0 Jan  1  2026 {Name(dir)}\r\n");
                        foreach (var (link, linkTarget) in server.Links.Where(l => ParentOf(l.Key) == path))
                            sb.Append(verb == "MLSD" ? $"type=OS.unix=symlink;modify=20260101120000; {Name(link)}\r\n" : $"lrwxrwxrwx   1 user group 10 Jan  1  2026 {Name(link)} -> {linkTarget}\r\n");
                        foreach (var (file, content) in server.Files.Where(f => ParentOf(f.Key) == path))
                            sb.Append(verb == "MLSD" ? $"type=file;size={content.Length};modify=20260101120000; {Name(file)}\r\n" : $"-rw-r--r--   1 user group {content.Length} Jan  1  2026 {Name(file)}\r\n");
                        await data.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString()), ct);
                        break;
                    case "RETR":
                        var bytes = server.Files[path];
                        server.EnterRetr();
                        try
                        {
                            if (server.RetrDelay > TimeSpan.Zero) await Task.Delay(server.RetrDelay, ct);
                            await data.WriteAsync(bytes.AsMemory((int)Math.Min(_rest, bytes.Length)), ct);
                        }
                        finally
                        {
                            server.LeaveRetr();
                        }
                        break;
                    case "STOR":
                        using (var ms = new MemoryStream())
                        {
                            if (_rest > 0 && server.Files.TryGetValue(path, out var existing)) ms.Write(existing, 0, (int)Math.Min(_rest, existing.Length));
                            await data.CopyToAsync(ms, ct);
                            server.Files[path] = ms.ToArray();
                        }
                        break;
                }
                if (data is SslStream s) { try { await s.ShutdownAsync(); } catch (IOException) { } }
                await DrainAsync(dataSocket);
            }
            catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException)
            {
                await data.DisposeAsync();
                await ReplyAsync("426 Transfer aborted: " + ex.Message, ct);
                ResetData();
                return;
            }
            await data.DisposeAsync();
            await ReplyAsync("226 Transfer complete", ct);
            ResetData();
        }

        /// <summary>FIN, then read until the peer closes, so unread TLS records do not turn the close into a RST.</summary>
        private static async Task DrainAsync(Socket socket)
        {
            try
            {
                socket.Shutdown(SocketShutdown.Send);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var buffer = new byte[4096];
                while (await socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token) > 0) { }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException) { }
        }

        private void ResetData()
        {
            _pasvListener?.Stop();
            _pasvListener = null;
            _portTarget = null;
            _cpsv = false;
            _rest = 0;
            _pretSeen = false;
        }

        private static string ParentOf(string path)
        {
            var idx = path.LastIndexOf('/');
            return idx <= 0 ? "/" : path[..idx];
        }

        private static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];
    }
}
