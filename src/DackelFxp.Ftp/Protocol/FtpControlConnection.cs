using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DackelFxp.Ftp.Protocol;

/// <summary>
/// The FTP control channel: sends commands and reads (multi-line) replies.
/// Commands can be sent and replies read separately, which FXP needs to drive two servers at once.
/// </summary>
public sealed class FtpControlConnection : IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly byte[] _buffer = new byte[16 * 1024];
    private int _bufferStart;
    private int _bufferEnd;
    private readonly MemoryStream _partial = new();
    private Task<int>? _pendingRead;
    private Socket? _socket;
    private Stream? _stream;

    public FtpControlConnection(Encoding encoding)
    {
        Encoding = encoding;
    }

    public event Action<FtpLogEntry>? Log;

    public Encoding Encoding { get; set; }
    public TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public bool IsConnected => _socket is { Connected: true } && _stream is not null;
    public bool IsEncrypted => _stream is SslStream;
    public X509Certificate2? RemoteCertificate { get; private set; }
    public SslProtocols TlsProtocol => (_stream as SslStream)?.SslProtocol ?? SslProtocols.None;
    public System.Net.IPEndPoint? LocalEndPoint => Normalize(_socket?.LocalEndPoint);
    public System.Net.IPEndPoint? RemoteEndPoint => Normalize(_socket?.RemoteEndPoint);

    private static System.Net.IPEndPoint? Normalize(System.Net.EndPoint? ep) =>
        ep is System.Net.IPEndPoint { Address.IsIPv4MappedToIPv6: true } ip
            ? new System.Net.IPEndPoint(ip.Address.MapToIPv4(), ip.Port)
            : ep as System.Net.IPEndPoint;

    public async Task ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        WriteLog(FtpLogKind.Info, Lang.T($"Verbinde mit {host}:{port} ...", $"Connecting to {host}:{port} ..."));
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await socket.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            socket.Dispose();
            throw new FtpConnectionException(Lang.T($"Zeitüberschreitung beim Verbinden mit {host}:{port}", $"Timeout connecting to {host}:{port}"));
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new FtpConnectionException(Lang.T($"Verbindung zu {host}:{port} fehlgeschlagen: {ex.Message}", $"Connection to {host}:{port} failed: {ex.Message}"), ex);
        }
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: true);
        WriteLog(FtpLogKind.Info, Lang.T($"Verbunden mit {RemoteEndPoint}", $"Connected to {RemoteEndPoint}"));
    }

    /// <summary>Wraps the current stream in TLS (AUTH TLS or implicit FTPS).</summary>
    public async Task UpgradeToTlsAsync(string targetHost, RemoteCertificateValidationCallback validation, CancellationToken ct,
        SslProtocols protocols = SslProtocols.None)
    {
        if (_stream is null) throw new FtpConnectionException(Lang.T("Nicht verbunden", "Not connected"));
        if (_bufferEnd != _bufferStart || _partial.Length > 0 || _pendingRead is not null) throw new FtpException(Lang.T("Unerwartete Daten vor dem TLS-Handshake", "Unexpected data before the TLS handshake"));

        var ssl = new SslStream(_stream, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsClientAsync(FtpTls.CreateClientOptions(targetHost, validation, protocols), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            _stream = null;
            throw new FtpConnectionException(Lang.T($"TLS-Handshake fehlgeschlagen: {ex.Message}", $"TLS handshake failed: {ex.Message}"), ex);
        }
        _stream = ssl;
        RemoteCertificate = ssl.RemoteCertificate as X509Certificate2
            ?? (ssl.RemoteCertificate is null ? null : X509CertificateLoader.LoadCertificate(ssl.RemoteCertificate.GetRawCertData()));
        WriteLog(FtpLogKind.Tls, Lang.T($"TLS aktiv: {ssl.SslProtocol}, {ssl.NegotiatedCipherSuite}", $"TLS active: {ssl.SslProtocol}, {ssl.NegotiatedCipherSuite}"));
    }

    /// <summary>Drops TLS on the control channel after CCC.</summary>
    public async Task DowngradeFromTlsAsync()
    {
        if (_stream is not SslStream ssl || _socket is null) return;
        try { await ssl.ShutdownAsync().ConfigureAwait(false); } catch (IOException) { }
        _stream = new NetworkStream(_socket, ownsSocket: true);
        WriteLog(FtpLogKind.Tls, Lang.T("Steuerkanal unverschlüsselt (CCC)", "Control channel unencrypted (CCC)"));
    }

    /// <summary>Sends a command and waits for the final (non-1xx) reply.</summary>
    public async Task<FtpReply> ExecuteAsync(string command, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SendCoreAsync(command, ct).ConfigureAwait(false);
            FtpReply reply;
            do reply = await ReadReplyCoreAsync(ReadTimeout, ct).ConfigureAwait(false);
            while (reply.IsPreliminary);
            return reply;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// For raw/custom commands: sends the command, reads the final reply and then collects further
    /// replies that arrive within <paramref name="quietPeriod"/> (some servers and site scripts answer
    /// with several separate replies). All lines are logged as they arrive.
    /// </summary>
    public async Task<IReadOnlyList<FtpReply>> ExecuteCollectAsync(string command, TimeSpan quietPeriod, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SendCoreAsync(command, ct).ConfigureAwait(false);
            var replies = new List<FtpReply>();
            FtpReply reply;
            do
            {
                reply = await ReadReplyCoreAsync(ReadTimeout, ct).ConfigureAwait(false);
                replies.Add(reply);
            }
            while (reply.IsPreliminary);

            while (await TryReadReplyCoreAsync(quietPeriod, ct).ConfigureAwait(false) is { } extra)
                replies.Add(extra);
            return replies;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Sends a command without reading the reply. Use <see cref="ReadReplyAsync(CancellationToken)"/> afterwards.</summary>
    public async Task SendAsync(string command, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { await SendCoreAsync(command, ct).ConfigureAwait(false); }
        finally { _lock.Release(); }
    }

    /// <summary>Reads exactly one reply (which may be preliminary).</summary>
    public Task<FtpReply> ReadReplyAsync(CancellationToken ct) => ReadReplyAsync(ReadTimeout, ct);

    /// <summary>Reads exactly one reply with a custom timeout (e.g. the end of a long transfer).</summary>
    public async Task<FtpReply> ReadReplyAsync(TimeSpan timeout, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { return await ReadReplyCoreAsync(timeout, ct).ConfigureAwait(false); }
        finally { _lock.Release(); }
    }

    private async Task SendCoreAsync(string command, CancellationToken ct)
    {
        var stream = _stream ?? throw new FtpConnectionException(Lang.T("Nicht verbunden", "Not connected"));
        await DrainStaleRepliesAsync(ct).ConfigureAwait(false);
        WriteLog(FtpLogKind.Command, MaskCommand(command));
        var bytes = Encoding.GetBytes(command + "\r\n");
        try
        {
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            throw new FtpConnectionException(Lang.T($"Senden fehlgeschlagen: {ex.Message}", $"Send failed: {ex.Message}"), ex);
        }
    }

    /// <summary>Data from the server that nobody has asked for yet.</summary>
    private bool HasPendingInput =>
        _bufferEnd > _bufferStart || _partial.Length > 0
        || _pendingRead is { IsCompleted: true }
        || (_socket is { } s && SafeAvailable(s) > 0);

    private static int SafeAvailable(Socket socket)
    {
        try { return socket.Available; }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { return 0; }
    }

    /// <summary>
    /// Reads replies that arrived after the previous command was considered complete. Without this,
    /// every following command would receive the reply of its predecessor (e.g. PASV getting "257 ...").
    /// </summary>
    private async Task DrainStaleRepliesAsync(CancellationToken ct)
    {
        while (HasPendingInput)
        {
            var stale = await TryReadReplyCoreAsync(TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false);
            if (stale is null) break;
        }
    }

    /// <summary>Reads a reply if its first line arrives within <paramref name="wait"/>, otherwise null.</summary>
    private async Task<FtpReply?> TryReadReplyCoreAsync(TimeSpan wait, CancellationToken ct)
    {
        var first = await ReadLineCoreAsync(wait, throwOnTimeout: false, ct).ConfigureAwait(false);
        return first is null ? null : await CompleteReplyAsync(first, ct).ConfigureAwait(false);
    }

    private async Task<FtpReply> ReadReplyCoreAsync(TimeSpan timeout, CancellationToken ct)
    {
        var first = (await ReadLineCoreAsync(timeout, throwOnTimeout: true, ct).ConfigureAwait(false))!;
        return await CompleteReplyAsync(first, ct).ConfigureAwait(false);
    }

    private async Task<FtpReply> CompleteReplyAsync(string first, CancellationToken ct)
    {
        var lines = new List<string> { first };
        if (first.Length < 3 || !int.TryParse(first.AsSpan(0, 3), out var code))
        {
            WriteLog(FtpLogKind.Error, first);
            throw new FtpConnectionException(Lang.T($"Ungültige Antwort vom Server: '{first}'", $"Invalid reply from server: '{first}'"));
        }

        if (first.Length > 3 && first[3] == '-')
        {
            var terminator = first[..3] + " ";
            while (true)
            {
                var line = (await ReadLineCoreAsync(ReadTimeout, throwOnTimeout: true, ct).ConfigureAwait(false))!;
                lines.Add(line);
                if (line.StartsWith(terminator, StringComparison.Ordinal) || line == first[..3]) break;
            }
        }

        var last = lines[^1];
        var message = last.Length > 4 ? last[4..] : string.Empty;
        if (lines.Count > 1 && message.Length == 0) message = first.Length > 4 ? first[4..] : string.Empty;
        var reply = new FtpReply(code, message, lines);
        foreach (var line in lines)
            WriteLog(reply.IsPositive ? FtpLogKind.Reply : FtpLogKind.Error, line);
        return reply;
    }

    /// <summary>
    /// Reads one line. A socket read that outlives the timeout is never cancelled (that could lose data or
    /// break the TLS stream); it stays pending in <see cref="_pendingRead"/> and is picked up next time.
    /// Partial lines are kept in <see cref="_partial"/>. Returns null on timeout if <paramref name="throwOnTimeout"/> is false.
    /// </summary>
    private async Task<string?> ReadLineCoreAsync(TimeSpan timeout, bool throwOnTimeout, CancellationToken ct)
    {
        var stream = _stream ?? throw new FtpConnectionException(Lang.T("Nicht verbunden", "Not connected"));
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var span = _buffer.AsSpan(_bufferStart, _bufferEnd - _bufferStart);
            var nl = span.IndexOf((byte)'\n');
            if (nl >= 0)
            {
                _partial.Write(span[..nl]);
                _bufferStart += nl + 1;
                var bytes = _partial.GetBuffer().AsSpan(0, (int)_partial.Length);
                if (bytes.Length > 0 && bytes[^1] == '\r') bytes = bytes[..^1];
                var text = Encoding.GetString(bytes);
                _partial.SetLength(0);
                return text;
            }
            _partial.Write(span);
            _bufferStart = _bufferEnd = 0;

            _pendingRead ??= stream.ReadAsync(_buffer).AsTask();
            var remaining = deadline - DateTime.UtcNow;
            int read;
            try
            {
                read = await _pendingRead.WaitAsync(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                if (!throwOnTimeout) return null;
                throw new FtpConnectionException(Lang.T("Zeitüberschreitung beim Warten auf die Serverantwort", "Timeout waiting for the server reply"));
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                _pendingRead = null;
                throw new FtpConnectionException(Lang.T($"Verbindung verloren: {ex.Message}", $"Connection lost: {ex.Message}"), ex);
            }
            _pendingRead = null;
            if (read == 0) throw new FtpConnectionException(Lang.T("Verbindung vom Server geschlossen", "Connection closed by server"));
            _bufferEnd = read;
        }
    }

    private static string MaskCommand(string command) =>
        command.StartsWith("PASS ", StringComparison.OrdinalIgnoreCase) ? "PASS ********" : command;

    internal void WriteLog(FtpLogKind kind, string text) => Log?.Invoke(new FtpLogEntry(DateTimeOffset.Now, kind, text));

    public async ValueTask DisposeAsync()
    {
        if (_stream is SslStream ssl && _socket is { Connected: true })
        {
            // Best effort close_notify: some servers log or mishandle a TLS session that just vanishes.
            try { await ssl.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException or InvalidOperationException) { }
        }
        if (_stream is not null)
        {
            try { await _stream.DisposeAsync().ConfigureAwait(false); } catch { /* closing anyway */ }
            _stream = null;
        }
        _socket?.Dispose();
        _socket = null;
        _bufferStart = _bufferEnd = 0;
        _partial.SetLength(0);
        _pendingRead = null;
    }
}
