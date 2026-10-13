using System.Net;
using System.Text.RegularExpressions;

namespace DackelFxp.Ftp.Protocol;

public static partial class FtpAddressParser
{
    [GeneratedRegex(@"(\d{1,3}),\s*(\d{1,3}),\s*(\d{1,3}),\s*(\d{1,3}),\s*(\d{1,3}),\s*(\d{1,3})")]
    private static partial Regex PasvRegex();

    [GeneratedRegex(@"\((.)\1\1(\d+)\1\)")]
    private static partial Regex EpsvRegex();

    /// <summary>Parses the host/port from a 227 PASV (or CPSV) reply.</summary>
    public static IPEndPoint ParsePasv(FtpReply reply)
    {
        var match = PasvRegex().Match(string.Join(" ", reply.Lines));
        if (!match.Success) throw new FtpException(Lang.T($"PASV-Antwort nicht lesbar: {reply.Message}", $"Cannot parse PASV reply: {reply.Message}"));
        var bytes = new byte[4];
        for (var i = 0; i < 4; i++) bytes[i] = byte.Parse(match.Groups[i + 1].Value);
        var port = int.Parse(match.Groups[5].Value) * 256 + int.Parse(match.Groups[6].Value);
        return new IPEndPoint(new IPAddress(bytes), port);
    }

    /// <summary>Returns the "h1,h2,h3,h4,p1,p2" argument from a PASV reply, as needed for PORT in FXP.</summary>
    public static string ExtractPasvArgument(FtpReply reply)
    {
        var ep = ParsePasv(reply);
        return FormatPort(ep);
    }

    /// <summary>Parses the port from a 229 EPSV reply.</summary>
    public static int ParseEpsv(FtpReply reply)
    {
        var match = EpsvRegex().Match(reply.Message);
        if (!match.Success) throw new FtpException(Lang.T($"EPSV-Antwort nicht lesbar: {reply.Message}", $"Cannot parse EPSV reply: {reply.Message}"));
        return int.Parse(match.Groups[2].Value);
    }

    public static string FormatPort(IPEndPoint ep)
    {
        var ip = ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;
        var b = ip.GetAddressBytes();
        if (b.Length != 4) throw new FtpException(Lang.T("PORT unterstützt nur IPv4, EPRT verwenden", "PORT supports IPv4 only, use EPRT"));
        return $"{b[0]},{b[1]},{b[2]},{b[3]},{ep.Port / 256},{ep.Port % 256}";
    }

    public static string FormatEprt(IPEndPoint ep)
    {
        var ip = ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;
        var proto = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 2 : 1;
        return $"|{proto}|{ip}|{ep.Port}|";
    }
}
