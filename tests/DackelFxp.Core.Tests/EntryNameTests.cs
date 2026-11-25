using DackelFxp.Core.Browsing;
using DackelFxp.Core.Tools;

namespace DackelFxp.Core.Tests;

public class EntryNameTests
{
    [Theory]
    [InlineData("file.txt")]
    [InlineData("Release.Name-GRP")]
    [InlineData(".hidden")]
    [InlineData("a..b")]
    [InlineData("Ünïcödé 名前.txt")]
    public void Accepts_Normal_Names(string name)
    {
        Assert.True(EntryName.IsSafeLocal(name, windows: true));
        Assert.True(EntryName.IsSafeLocal(name, windows: false));
        Assert.True(EntryName.IsSafeRemote(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../evil")]
    [InlineData("/etc/passwd")]
    [InlineData("a\0b")]
    [InlineData("a\r\nDELE x")]
    public void Rejects_Everywhere(string name)
    {
        Assert.False(EntryName.IsSafeLocal(name, windows: true));
        Assert.False(EntryName.IsSafeLocal(name, windows: false));
        Assert.False(EntryName.IsSafeRemote(name));
    }

    [Theory]
    [InlineData(@"..\..\evil.exe")]
    [InlineData(@"C:\Windows\evil.dll")]
    [InlineData(@"\server\share\x")]
    [InlineData("file.txt:stream")]
    [InlineData("C:evil")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("COM1.log")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("...")]
    [InlineData("a|b")]
    public void Rejects_Windows_Specific(string name)
    {
        Assert.False(EntryName.IsSafeLocal(name, windows: true));
        // Legal (if odd) file names on Unix, and harmless there.
        Assert.True(EntryName.IsSafeLocal(name, windows: false));
    }

    [Fact]
    public void CombineLocal_Stays_Inside_Folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dackel-target");
        Assert.Equal(Path.Combine(dir, "a.txt"), EntryName.CombineLocal(dir, "a.txt"));
        Assert.Throws<UnsafeEntryNameException>(() => EntryName.CombineLocal(dir, ".."));
        Assert.Throws<UnsafeEntryNameException>(() => EntryName.CombineLocal(dir, "../x"));
        Assert.Throws<UnsafeEntryNameException>(() => EntryName.CombineLocal(dir, Path.Combine(Path.GetTempPath(), "x")));
    }

    [Fact]
    public void Sfv_Does_Not_Read_Outside_Its_Folder()
    {
        var root = Directory.CreateTempSubdirectory("dackel-sfv").FullName;
        try
        {
            var dir = Directory.CreateDirectory(Path.Combine(root, "rel")).FullName;
            File.WriteAllBytes(Path.Combine(root, "secret.txt"), [1, 2, 3]);
            File.WriteAllLines(Path.Combine(dir, "x.sfv"), ["../secret.txt 00000000"]);
            var result = Assert.Single(SfvVerifier.Verify(Path.Combine(dir, "x.sfv")));
            Assert.Equal(SfvStatus.Missing, result.Status);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
