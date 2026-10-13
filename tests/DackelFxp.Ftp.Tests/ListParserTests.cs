using DackelFxp.Ftp.Listing;

namespace DackelFxp.Ftp.Tests;

public class ListParserTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0);

    [Fact]
    public void Unix_File_With_Year()
    {
        var item = FtpListParser.ParseLine("-rw-r--r--   1 user group  123456 Jan  1  2024 some file.rar", Now)!;
        Assert.Equal("some file.rar", item.Name);
        Assert.Equal(FtpItemType.File, item.Type);
        Assert.Equal(123456, item.Size);
        Assert.Equal(new DateTime(2024, 1, 1), item.Modified);
        Assert.Equal("user", item.Owner);
        Assert.Equal("group", item.Group);
    }

    [Fact]
    public void Unix_Directory_With_Time_In_Future_Is_Last_Year()
    {
        var item = FtpListParser.ParseLine("drwxrwxrwx   2 glftpd   glftpd          0 Dec 24 18:30 Some.Release-GRP", Now)!;
        Assert.True(item.IsDirectory);
        Assert.Equal(new DateTime(2025, 12, 24, 18, 30, 0), item.Modified);
    }

    [Fact]
    public void Unix_Recent_Time_Is_This_Year()
    {
        var item = FtpListParser.ParseLine("-rw-r--r--   1 user group  5 Oct 12 09:15 a.nfo", Now)!;
        Assert.Equal(new DateTime(2026, 10, 12, 9, 15, 0), item.Modified);
    }

    [Fact]
    public void Unix_Symlink()
    {
        var item = FtpListParser.ParseLine("lrwxrwxrwx   1 root root      11 Jan  1 12:34 link -> /target/dir", Now)!;
        Assert.Equal(FtpItemType.Link, item.Type);
        Assert.Equal("link", item.Name);
        Assert.Equal("/target/dir", item.LinkTarget);
    }

    [Fact]
    public void Unix_Without_Group_Column()
    {
        var item = FtpListParser.ParseLine("-rw-r--r--   1 ftp  1234 Jan  1  2024 file.txt", Now)!;
        Assert.Equal("file.txt", item.Name);
        Assert.Equal(1234, item.Size);
    }

    [Fact]
    public void Unix_Without_Link_Count_Drftpd()
    {
        var item = FtpListParser.ParseLine("drwxr-xr-x  user group 0 Oct 13 10:00 Dir With Spaces", Now)!;
        Assert.True(item.IsDirectory);
        Assert.Equal("Dir With Spaces", item.Name);
    }

    [Fact]
    public void Windows_Iis()
    {
        var dir = FtpListParser.ParseLine("10-13-26  12:34PM       <DIR>          My Folder", Now)!;
        Assert.True(dir.IsDirectory);
        Assert.Equal("My Folder", dir.Name);
        Assert.Equal(new DateTime(2026, 10, 13, 12, 34, 0), dir.Modified);

        var file = FtpListParser.ParseLine("01-02-25  09:05AM            123456 file.zip", Now)!;
        Assert.Equal(123456, file.Size);
        Assert.Equal("file.zip", file.Name);
    }

    [Fact]
    public void Mlsd()
    {
        var item = FtpListParser.ParseLine("type=file;size=4096;modify=20261013120000;perm=r; name with ;semicolon.txt", Now)!;
        Assert.Equal("name with ;semicolon.txt", item.Name);
        Assert.Equal(4096, item.Size);
        Assert.Equal(FtpItemType.File, item.Type);
        Assert.Equal(new DateTime(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc).ToLocalTime(), item.Modified);
    }

    [Fact]
    public void Parse_Skips_Total_Dots_And_Cdir()
    {
        var items = FtpListParser.Parse(
        [
            "total 12",
            "drwxr-xr-x   2 u g 0 Jan  1  2024 .",
            "drwxr-xr-x   2 u g 0 Jan  1  2024 ..",
            "type=cdir;modify=20260101000000; /x",
            "-rw-r--r--   1 u g 10 Jan  1  2024 keep",
        ], now: Now);
        Assert.Single(items);
        Assert.Equal("keep", items[0].Name);
    }

    [Fact]
    public void Parse_Applies_Timezone_Offset()
    {
        var items = FtpListParser.Parse(["-rw-r--r--   1 u g 10 Jan  1  2024 f"], TimeSpan.FromHours(2), Now);
        Assert.Equal(new DateTime(2024, 1, 1, 2, 0, 0), items[0].Modified);
    }
}
