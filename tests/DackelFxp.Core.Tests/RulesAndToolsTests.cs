using DackelFxp.Core.Infrastructure;
using DackelFxp.Core.Rules;
using DackelFxp.Core.Settings;
using DackelFxp.Core.Tools;

namespace DackelFxp.Core.Tests;

public class RulesAndToolsTests
{
    [Fact]
    public void SecretProtector_RoundTrip_And_Wrong_Master_Fails()
    {
        var salt = SecretProtector.NewSalt();
        var p = SecretProtector.FromMasterPassword("geheim", salt);
        var enc = p.Protect("pässwört");
        Assert.StartsWith("v1:", enc);
        Assert.Equal("pässwört", p.Unprotect(enc));
        Assert.True(p.Verify(p.CreateVerifier()));

        var wrong = SecretProtector.FromMasterPassword("falsch", salt);
        Assert.False(wrong.Verify(p.CreateVerifier()));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => wrong.Unprotect(enc));
    }

    [Fact]
    public void SkipList_Skips_And_Prioritizes()
    {
        var list = SkipList.CreateDefault();
        Assert.True(list.Evaluate("Thumbs.db", false).Skip);
        Assert.False(list.Evaluate("Thumbs.db", true).Skip); // file-only rule
        Assert.Equal(100, list.Evaluate("release.SFV", false).Priority);
        Assert.Equal(-50, list.Evaluate("Sample", true).Priority);
        Assert.True(list.Evaluate("[XYZ] - ( 100% COMPLETE )", true).Skip);
        Assert.Equal(new RuleResult(false, 0), list.Evaluate("file.r00", false));
    }

    [Theory]
    [InlineData(100, 50, ExistsAction.Resume)]
    [InlineData(100, 100, ExistsAction.Skip)]
    [InlineData(100, 150, ExistsAction.Overwrite)]
    [InlineData(-1, 150, ExistsAction.Overwrite)]
    public void FileExistsPolicy_Selects_By_Size(long source, long target, ExistsAction expected) =>
        Assert.Equal(expected, new FileExistsPolicy().Select(source, target));

    [Fact]
    public void FileExistsPolicy_Renames()
    {
        var existing = new HashSet<string> { "a (1).txt" };
        Assert.Equal("a (2).txt", FileExistsPolicy.RenameTarget("a.txt", existing.Contains));
    }

    [Fact]
    public void CustomCommand_Expands_Placeholders()
    {
        var cmd = new CustomCommand { Commands = "SITE NUKE %p 3 %1\nSITE WHO" };
        var lines = cmd.Expand("/incoming", "Rel-GRP", "MySite", "bad.quality").ToList();
        Assert.Equal(["SITE NUKE /incoming/Rel-GRP 3 bad.quality", "SITE WHO"], lines);
    }

    [Fact]
    public void DirectoryComparer_Finds_Differences()
    {
        var (left, right) = DirectoryComparer.Compare(
            [new("same", false, 10, null), new("diff", false, 10, null), new("onlyL", false, 1, null), new("Dir", true, 0, null)],
            [new("SAME", false, 10, null), new("diff", false, 11, null), new("onlyR", false, 1, null), new("dir", true, 0, null)]);
        Assert.Equal(CompareStatus.Same, left["same"]);
        Assert.Equal(CompareStatus.Different, left["diff"]);
        Assert.Equal(CompareStatus.OnlyLeft, left["onlyL"]);
        Assert.Equal(CompareStatus.OnlyRight, right["onlyR"]);
        Assert.Equal(CompareStatus.Same, right["Dir"]);
    }

    [Fact]
    public void Sfv_Parses_And_Verifies()
    {
        var dir = Directory.CreateTempSubdirectory("dackel-sfv").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.bin"), "hello");
            File.WriteAllText(Path.Combine(dir, "b.bin"), "broken");
            // CRC32("hello") = 3610A686
            File.WriteAllLines(Path.Combine(dir, "x.sfv"), ["; comment", "a.bin 3610a686", "b.bin 00000000", "c.bin 12345678"]);
            var results = SfvVerifier.Verify(Path.Combine(dir, "x.sfv")).ToDictionary(r => r.FileName, r => r.Status);
            Assert.Equal(SfvStatus.Ok, results["a.bin"]);
            Assert.Equal(SfvStatus.Bad, results["b.bin"]);
            Assert.Equal(SfvStatus.Missing, results["c.bin"]);
            Assert.True(SfvVerifier.VerifyAgainstSiblingSfv(Path.Combine(dir, "a.bin")));
            Assert.False(SfvVerifier.VerifyAgainstSiblingSfv(Path.Combine(dir, "b.bin")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Scheduler_IsDue_Respects_Days_And_LastRun()
    {
        var entry = new ScheduleEntry { Time = new TimeOnly(3, 0), Days = [DayOfWeek.Tuesday] };
        var tuesday = new DateTime(2026, 10, 13, 3, 1, 0); // a Tuesday
        Assert.True(Scheduler.IsDue(entry, tuesday));
        Assert.False(Scheduler.IsDue(entry, tuesday.AddDays(1)));
        Assert.False(Scheduler.IsDue(entry, tuesday.AddMinutes(-2)));
        entry.LastRun = tuesday;
        Assert.False(Scheduler.IsDue(entry, tuesday.AddMinutes(1)));
        Assert.Equal(new DateTime(2026, 10, 20, 3, 0, 0), Scheduler.NextRun(entry, tuesday));
    }

    [Fact]
    public void Ident_Response()
    {
        Assert.Equal("6193, 23 : USERID : UNIX : dackel", IdentServer.BuildResponse("6193, 23", "dackel"));
        Assert.Contains("ERROR", IdentServer.BuildResponse("garbage", "dackel"));
    }
}
