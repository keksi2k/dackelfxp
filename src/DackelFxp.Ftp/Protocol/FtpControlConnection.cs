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
        WriteLog(FtpLogKind.Info, $"Verbinde mit {host}:{port} ...");
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
            throw new FtpConnectionException($"Zeitüberschreitung beim Verbinden mit {host}:{port}");
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new FtpConnectionException($"Verbindung zu {host}:{port} fehlgeschlagen: {ex.Message}", ex);
        }
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: true);
        WriteLog(FtpLogKind.Info, $"Verbunden mit {RemoteEndPoint}");
    }

    /// <summary>Wraps the current stream in TLS (AUTH TLS or implicit FTPS).</summary>
    public async Task UpgradeToTlsAsync(string targetHost, RemoteCertificateValidationCallback validation, CancellationToken ct,
        SslProtocols protocols = SslProtocols.None)
    {
        if (_stream is null) throw new FtpConnectionException("Nicht verbunden");
        if (_bufferEnd != _bufferStart) throw new FtpException("Unerwartete Daten vor dem TLS-Handshake");

        var ssl = new SslStream(_stream, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsClientAsync(FtpTls.CreateClientOptions(targetHost, validation, protocols), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            _stream = null;
            throw new FtpConnectionException($"TLS-Handshake fehlgeschlagen: {ex.Message}", ex);
        }
        _stream = ssl;
        RemoteCertificate = ssl.RemoteCertificate as X509Certificate2
            ?? (ssl.RemoteCertificate is null ? null : X509CertificateLoader.LoadCertificate(ssl.RemoteCertificate.GetRawCertData()));
        WriteLog(FtpLogKind.Tls, $"TLS aktiv: {ssl.SslProtocol}, {ssl.NegotiatedCipherSuite}");
    }

    /// <summary>Drops TLS on the control channel after CCC.</summary>
    public async Task DowngradeFromTlsAsync()
    {
        if (_stream is not SslStream ssl || _socket is null) return;
        try { await ssl.ShutdownAsync().ConfigureAwait(false); } catch (IOException) { }
        _stream = new NetworkStream(_socket, ownsSocket: true);
        WriteLog(FtpLogKind.Tls, "Steuerkanal unverschlüsselt (CCC)");
    }

    /// <summary>Sends a command and waits for the final (non-1xx) reply.</summary>
    public async Task<FtpReply> ExecuteAsync(string command, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SendCoreAsync(command, ct).ConfigureAwait(false);
            FtpReply reply;
            do reply = await ReadReplyCoreAsync(ct).ConfigureAwait(false);
            while (reply.IsPreliminary);
            return reply;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Sends a command without reading the reply. Use <see cref="ReadReplyAsync"/> afterwards.</summary>
    public async Task SendAsync(string command, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { await SendCoreAsync(command, ct).ConfigureAwait(false); }
        finally { _lock.Release(); }
    }

    /// <summary>Reads exactly one reply (which may be preliminary).</summary>
    public async Task<FtpReply> ReadReplyAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { return await ReadReplyCoreAsync(ct).ConfigureAwait(false); }
        finally { _lock.Release(); }
    }

    /// <summary>Reads exactly one reply with a custom timeout (e.g. the end of a long transfer).</summary>
    public async Task<FtpReply> ReadReplyAsync(TimeSpan timeout, CancellationToken ct)
    {
        var old = ReadTimeout;
        ReadTimeout = timeout;
        try { return await ReadReplyAsync(ct).ConfigureAwait(false); }
        finally { ReadTimeout = old; }
    }

    private async Task SendCoreAsync(string command, CancellationToken ct)
    {
        var stream = _stream ?? throw new FtpConnectionException("Nicht verbunden");
        WriteLog(FtpLogKind.Command, MaskCommand(command));
        var bytes = Encoding.GetBytes(command + "\r\n");
        try
        {
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            throw new FtpConnectionException($"Senden fehlgeschlagen: {ex.Message}", ex);
        }
    }

    private async Task<FtpReply> ReadReplyCoreAsync(CancellationToken ct)
    {
        var lines = new List<string>();
        var first = await ReadLineAsync(ct).ConfigureAwait(false);
        lines.Add(first);
        if (first.Length < 3 || !int.TryParse(first.AsSpan(0, 3), out var code))
            throw new FtpConnectionException($"Ungültige Antwort vom Server: '{first}'");

        if (first.Length > 3 && first[3] == '-')
        {
            var terminator = first[..3] + " ";
            while (true)
            {
                var line = await ReadLineAsync(ct).ConfigureAwait(false);
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

    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        var stream = _stream ?? throw new FtpConnectionException("Nicht verbunden");
        using var line = new MemoryStream();
        while (true)
        {
            var span = _buffer.AsSpan(_bufferStart, _bufferEnd - _bufferStart);
            var nl = span.IndexOf((byte)'\n');
            if (nl >= 0)
            {
                line.Write(span[..nl]);
                _bufferStart += nl + 1;
                var bytes = line.GetBuffer().AsSpan(0, (int)line.Length);
                if (bytes.Length > 0 && bytes[^1] == '\r') bytes = bytes[..^1];
                return Encoding.GetString(bytes);
            }
            line.Write(span);
            _bufferStart = _bufferEnd = 0;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ReadTimeout);
            int read;
            try
            {
                read = await stream.ReadAsync(_buffer, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new FtpConnectionException("Zeitüberschreitung beim Warten auf die Serverantwort");
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                throw new FtpConnectionException($"Verbindung verloren: {ex.Message}", ex);
            }
            if (read == 0) throw new FtpConnectionException("Verbindung vom Server geschlossen");
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
    }
}
