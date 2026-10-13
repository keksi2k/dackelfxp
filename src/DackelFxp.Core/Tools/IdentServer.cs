using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DackelFxp.Core.Tools;

/// <summary>
/// RFC 1413 ident responder, as some FTP servers require it.
/// Port 113 needs admin rights on Linux (or setcap cap_net_bind_service / port forwarding).
/// </summary>
public sealed class IdentServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public IdentServer(int port, string user)
    {
        User = user;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _loop = AcceptLoopAsync();
    }

    public string User { get; set; }
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public event Action<string>? Log;

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                _ = HandleAsync(client);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                var request = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
                if (request is null) return;
                var response = BuildResponse(request, User);
                var bytes = Encoding.ASCII.GetBytes(response + "\r\n");
                await stream.WriteAsync(bytes, cts.Token).ConfigureAwait(false);
                Log?.Invoke($"Ident von {client.Client.RemoteEndPoint}: {request.Trim()} -> {response}");
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { }
        }
    }

    public static string BuildResponse(string request, string user)
    {
        var parts = request.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var a) || !int.TryParse(parts[1], out var b))
            return $"{request.Trim()} : ERROR : INVALID-PORT";
        return $"{a}, {b} : USERID : UNIX : {user}";
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        await _loop.ConfigureAwait(false);
    }
}
