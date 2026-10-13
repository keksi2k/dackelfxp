using System.Globalization;
using System.IO.Hashing;

namespace DackelFxp.Core.Tools;

public enum SfvStatus { Ok, Bad, Missing }

public sealed record SfvResult(string FileName, uint Expected, uint? Actual, SfvStatus Status);

/// <summary>Reads .sfv files and checks local files via CRC32.</summary>
public static class SfvVerifier
{
    public static Dictionary<string, uint> Parse(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';')) continue;
            var space = line.LastIndexOf(' ');
            if (space <= 0) continue;
            var hex = line[(space + 1)..];
            if (hex.Length == 8 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var crc))
                result[line[..space].Trim()] = crc;
        }
        return result;
    }

    public static uint ComputeCrc32(string file)
    {
        var crc = new Crc32();
        using var fs = File.OpenRead(file);
        crc.Append(fs);
        return crc.GetCurrentHashAsUInt32();
    }

    public static List<SfvResult> Verify(string sfvFile)
    {
        var dir = Path.GetDirectoryName(sfvFile)!;
        return Parse(File.ReadLines(sfvFile)).Select(entry =>
        {
            var path = Path.Combine(dir, entry.Key);
            if (!File.Exists(path)) return new SfvResult(entry.Key, entry.Value, null, SfvStatus.Missing);
            var actual = ComputeCrc32(path);
            return new SfvResult(entry.Key, entry.Value, actual, actual == entry.Value ? SfvStatus.Ok : SfvStatus.Bad);
        }).ToList();
    }

    /// <summary>Checks a downloaded file against an .sfv in the same folder. Null = no SFV entry found.</summary>
    public static bool? VerifyAgainstSiblingSfv(string file)
    {
        var dir = Path.GetDirectoryName(file);
        if (dir is null || !Directory.Exists(dir)) return null;
        var name = Path.GetFileName(file);
        if (name.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var sfv in Directory.EnumerateFiles(dir, "*.sfv"))
        {
            if (Parse(File.ReadLines(sfv)).TryGetValue(name, out var expected))
                return ComputeCrc32(file) == expected;
        }
        return null;
    }
}
