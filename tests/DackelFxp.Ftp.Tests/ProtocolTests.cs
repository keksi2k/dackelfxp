using System.Net;
using DackelFxp.Ftp.Protocol;

namespace DackelFxp.Ftp.Tests;

public class ProtocolTests
{
    private static FtpReply Reply(int code, string message) => new(code, message, [$"{code} {message}"]);

    [Fact]
    public void Parses_Pasv()
    {
        var ep = FtpAddressParser.ParsePasv(Reply(227, "Entering Passive Mode (192,168,1,10,195,80)."));
        Assert.Equal(IPAddress.Parse("192.168.1.10"), ep.Address);
        Assert.Equal(195 * 256 + 80, ep.Port);
    }

    [Fact]
    public void Parses_Pasv_Without_Parentheses()
    {
        var ep = FtpAddressParser.ParsePasv(Reply(227, "Entering Passive Mode 10,0,0,1,4,1"));
        Assert.Equal(1025, ep.Port);
    }

    [Fact]
    public void Parses_Epsv()
    {
        Assert.Equal(50123, FtpAddressParser.ParseEpsv(Reply(229, "Entering Extended Passive Mode (|||50123|)")));
    }

    [Fact]
    public void Formats_Port_And_Eprt()
    {
        var ep = new IPEndPoint(IPAddress.Parse("1.2.3.4"), 1025);
        Assert.Equal("1,2,3,4,4,1", FtpAddressParser.FormatPort(ep));
        Assert.Equal("|1|1.2.3.4|1025|", FtpAddressParser.FormatEprt(ep));
    }

    [Theory]
    [InlineData("/a/b", "c", "/a/b/c")]
    [InlineData("/a/b/", "c", "/a/b/c")]
    [InlineData("/a", "/abs", "/abs")]
    public void Combines_Paths(string dir, string name, string expected) => Assert.Equal(expected, FtpPath.Combine(dir, name));

    [Theory]
    [InlineData("/a/b/c", "/a/b", "c")]
    [InlineData("/a", "/", "a")]
    [InlineData("/a/b/", "/a", "b")]
    public void Splits_Paths(string path, string parent, string name)
    {
        Assert.Equal(parent, FtpPath.GetParent(path));
        Assert.Equal(name, FtpPath.GetName(path));
    }

    [Theory]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.1", true)]
    [InlineData("192.168.0.1", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("172.32.0.1", false)]
    public void Detects_Unroutable(string ip, bool expected) => Assert.Equal(expected, FtpClient.IsUnroutable(IPAddress.Parse(ip)));
}
