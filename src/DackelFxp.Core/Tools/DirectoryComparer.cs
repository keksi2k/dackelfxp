namespace DackelFxp.Core.Tools;

public enum CompareStatus { Same, OnlyLeft, OnlyRight, Different, Newer, Older }

[Flags]
public enum CompareBy { Name = 0, Size = 1, Date = 2 }

public sealed record CompareEntry(string Name, bool IsDirectory, long Size, DateTime? Modified);

/// <summary>Compares two directory listings (FlashFXP "Compare Folders").</summary>
public static class DirectoryComparer
{
    /// <returns>Status for each name in left and right, keyed by side.</returns>
    public static (Dictionary<string, CompareStatus> Left, Dictionary<string, CompareStatus> Right) Compare(
        IEnumerable<CompareEntry> left, IEnumerable<CompareEntry> right, CompareBy by = CompareBy.Size,
        bool ignoreCase = true, TimeSpan? dateTolerance = null)
    {
        var comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var l = left.GroupBy(e => e.Name, comparer).ToDictionary(g => g.Key, g => g.First(), comparer);
        var r = right.GroupBy(e => e.Name, comparer).ToDictionary(g => g.Key, g => g.First(), comparer);
        var tolerance = dateTolerance ?? TimeSpan.FromMinutes(1);
        var resultLeft = new Dictionary<string, CompareStatus>(comparer);
        var resultRight = new Dictionary<string, CompareStatus>(comparer);

        foreach (var (name, a) in l)
        {
            if (!r.TryGetValue(name, out var b))
            {
                resultLeft[name] = CompareStatus.OnlyLeft;
                continue;
            }
            var (sa, sb) = CompareOne(a, b, by, tolerance);
            resultLeft[name] = sa;
            resultRight[b.Name] = sb;
        }
        foreach (var (name, _) in r)
            if (!l.ContainsKey(name)) resultRight[name] = CompareStatus.OnlyRight;
        return (resultLeft, resultRight);
    }

    private static (CompareStatus, CompareStatus) CompareOne(CompareEntry a, CompareEntry b, CompareBy by, TimeSpan tolerance)
    {
        if (a.IsDirectory != b.IsDirectory) return (CompareStatus.Different, CompareStatus.Different);
        if (a.IsDirectory) return (CompareStatus.Same, CompareStatus.Same);
        if (by.HasFlag(CompareBy.Size) && a.Size != b.Size) return (CompareStatus.Different, CompareStatus.Different);
        if (by.HasFlag(CompareBy.Date) && a.Modified is { } ma && b.Modified is { } mb && (ma - mb).Duration() > tolerance)
            return ma > mb ? (CompareStatus.Newer, CompareStatus.Older) : (CompareStatus.Older, CompareStatus.Newer);
        return (CompareStatus.Same, CompareStatus.Same);
    }
}
