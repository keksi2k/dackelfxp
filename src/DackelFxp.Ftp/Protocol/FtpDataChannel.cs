using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace DackelFxp.Ftp.Protocol;

/// <summary>A single data connection (passive: we connect; active: we accept).</summary>
internal sealed class FtpDataChannel : IAsyncDisposable
{
    private Socket? _socket;
    private TcpListener? _listener;
    private Stream? _stream;

    private FtpDataChannel() { }

    public Stream Stream => _stream ?? throw new FtpException(Lang.T("Datenverbindung nicht geöffnet", "Data connection not open"));

    public static async Task<FtpDataChannel> ConnectAsync(IPEndPoint endPoint, TimeSpan timeout, CancellationToken ct)
    {
        var socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await socket.ConnectAsync(endPoint, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            socket.Dispose();
            throw new FtpException(Lang.T($"Zeitüberschreitung bei der Datenverbindung zu {endPoint}", $"Timeout on data connection to {endPoint}"));
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new FtpException(Lang.T($"Datenverbindung zu {endPoint} fehlgeschlagen: {ex.Message}", $"Data connection to {endPoint} failed: {ex.Message}"), ex);
        }
        return new FtpDataChannel { _socket = socket, _stream = new NetworkStream(socket, ownsSocket: true) };
    }

    public static FtpDataChannel Listen(IPAddress localAddress, int portMin, int portMax)
    {
        if (portMin <= 0 || portMax < portMin)
            return StartListener(localAddress, 0);

        var ports = Enumerable.Range(portMin, portMax - portMin + 1).OrderBy(_ => Random.Shared.Next());
        foreach (var port in ports)
        {
            try { return StartListener(localAddress, port); }
            catch (SocketException) { /* port in use, try next */ }
        }
        throw new FtpException(Lang.T($"Kein freier Port im Bereich {portMin}-{portMax}", $"No free port in range {portMin}-{portMax}"));
    }

    private static FtpDataChannel StartListener(IPAddress localAddress, int port)
    {
        var listener = new TcpListener(localAddress, port);
        listener.Start(1);
        return new FtpDataChannel { _listener = listener };
    }

    public IPEndPoint ListenEndPoint => (IPEndPoint)(_listener ?? throw new InvalidOperationException()).LocalEndpoint;

    public async Task AcceptAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (_listener is null) return;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            _socket = await _listener.AcceptSocketAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new FtpException(Lang.T("Server hat keine aktive Datenverbindung aufgebaut (PORT)", "Server did not open the active data connection (PORT)"));
        }
        finally
        {
            _listener.Stop();
            _listener = null;
        }
        _stream = new NetworkStream(_socket, ownsSocket: true);
    }

    public async Task SecureAsync(string targetHost, RemoteCertificateValidationCallback validation, SslProtocols protocols, CancellationToken ct)
    {
        var ssl = new SslStream(Stream, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsClientAsync(FtpTls.CreateClientOptions(targetHost, validation, protocols), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            _stream = null;
            throw new FtpException(Lang.T($"TLS auf der Datenverbindung fehlgeschlagen: {ex.Message}", $"TLS on the data connection failed: {ex.Message}"), ex);
        }
        _stream = ssl;
    }

    /// <summary>
    /// Closes the channel gracefully: TLS close_notify, FIN, then wait for the server to close.
    /// Closing with unread data (e.g. TLS 1.3 session tickets the server sent during an upload)
    /// would make the OS send a RST, and the server would discard the upload as aborted.
    /// </summary>
    public async Task CloseAsync()
    {
        if (_stream is SslStream ssl)
        {
            try { await ssl.ShutdownAsync().ConfigureAwait(false); } catch (IOException) { } catch (ObjectDisposedException) { }
        }
        if (_socket is { } socket)
        {
            try
            {
                socket.Shutdown(SocketShutdown.Send);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var buffer = new byte[4096];
                while (await socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token).ConfigureAwait(false) > 0) { }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException) { }
        }
        await DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _listener?.Stop();
        _listener = null;
        if (_stream is not null)
        {
            try { await _stream.DisposeAsync().ConfigureAwait(false); } catch { /* closing anyway */ }
            _stream = null;
        }
        _socket?.Dispose();
        _socket = null;
    }
}
