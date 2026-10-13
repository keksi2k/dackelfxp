using DackelFxp.Ftp.Tests;

// Plain FTP on 2121, explicit FTPS (with SSCN/CPSV) on 2122. Any user/password works.
await using var plain = new FakeFtpServer(port: 2121);
await using var tls = new FakeFtpServer(tls: true, port: 2122);

foreach (var server in new[] { plain, tls })
{
    server.Directories["/incoming"] = true;
    server.Directories["/incoming/Some.Release-GRP"] = true;
    server.Directories["/incoming/Some.Release-GRP/Sample"] = true;
    server.Files["/incoming/Some.Release-GRP/some.release-grp.sfv"] = "some.release-grp.r00 00000000\n"u8.ToArray();
    server.Files["/incoming/Some.Release-GRP/some.release-grp.nfo"] = "nfo"u8.ToArray();
    server.Files["/incoming/Some.Release-GRP/some.release-grp.r00"] = new byte[5_000_000];
    server.Files["/incoming/Some.Release-GRP/some.release-grp.r01"] = new byte[5_000_000];
    server.Files["/incoming/Some.Release-GRP/Sample/sample.mkv"] = new byte[1_000_000];
    server.Files["/readme.txt"] = "DackelFXP dev server\n"u8.ToArray();
}
tls.Directories["/archive"] = true;

Console.WriteLine("FTP  : 127.0.0.1:2121");
Console.WriteLine("FTPS : 127.0.0.1:2122 (explizit, selbstsigniertes Zertifikat)");
Console.WriteLine("Enter zum Beenden.");
if (args.Contains("--wait")) await Task.Delay(Timeout.Infinite);
Console.ReadLine();
