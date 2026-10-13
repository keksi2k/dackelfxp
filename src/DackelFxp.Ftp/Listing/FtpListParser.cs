using System.Globalization;
using System.Text.RegularExpressions;

namespace DackelFxp.Ftp.Listing;

/// <summary>Parses directory listings in Unix (incl. glftpd/drftpd), Windows/IIS and MLSD format.</summary>
public static partial class FtpListParser
{
    private static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    [GeneratedRegex(@"^(?<type>[\-ldcbsp])(?<perm>[rwxsStTl\-]{9})[+@.]?\s+(?:(?<links>\d+)\s+)?(?<owner>\S+)\s+(?:(?<group>\S+)\s+)?(?<size>\d+)\s+(?<mon>[A-Za-z]{3})\s+(?<day>\d{1,2})\s+(?<timeyear>\d{1,2}:\d{2}|\d{4})\s(?<name>.+)$")]
    private static partial Regex UnixRegex();

    [GeneratedRegex(@"^(?<date>\d{1,2}-\d{1,2}-\d{2,4})\s+(?<time>\d{1,2}:\d{2}\s*(?:AM|PM|am|pm)?)\s+(?<size><DIR>|\d+)\s+(?<name>.+)$")]
    private static partial Regex WindowsRegex();

    public static List<FtpListItem> Parse(IEnumerable<string> lines, TimeSpan timeZoneOffset = default, DateTime? now = null)
    {
        var result = new List<FtpListItem>();
        var reference = now ?? DateTime.Now;
        foreach (var raw in lines)
        {
            var item = ParseLine(raw, reference);
            if (item is null || item.Name is "." or "..") continue;
            if (item.Modified is { } m && timeZoneOffset != TimeSpan.Zero)
                item = item with { Modified = m + timeZoneOffset };
            result.Add(item);
        }
        return result;
    }

    public static FtpListItem? ParseLine(string line, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        if (line.StartsWith("total ", StringComparison.OrdinalIgnoreCase)) return null;
        return ParseUnix(line, now) ?? ParseWindows(line) ?? ParseMlsd(line);
    }

    public static FtpListItem? ParseUnix(string line, DateTime now)
    {
        var m = UnixRegex().Match(line);
        if (!m.Success) return null;

        var type = m.Groups["type"].Value switch
        {
            "d" => FtpItemType.Directory,
            "l" => FtpItemType.Link,
            _ => FtpItemType.File,
        };
        var name = m.Groups["name"].Value;
        string? target = null;
        if (type == FtpItemType.Link)
        {
            var arrow = name.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0)
            {
                target = name[(arrow + 4)..];
                name = name[..arrow];
            }
        }

        return new FtpListItem
        {
            Name = name,
            Type = type,
            Size = long.Parse(m.Groups["size"].Value, CultureInfo.InvariantCulture),
            Modified = ParseUnixDate(m.Groups["mon"].Value, m.Groups["day"].Value, m.Groups["timeyear"].Value, now),
            Permissions = m.Groups["type"].Value + m.Groups["perm"].Value,
            Owner = m.Groups["owner"].Value,
            Group = m.Groups["group"].Success ? m.Groups["group"].Value : null,
            LinkTarget = target,
            RawLine = line,
        };
    }

    private static DateTime? ParseUnixDate(string mon, string day, string timeOrYear, DateTime now)
    {
        var month = Array.IndexOf(Months, mon.ToLowerInvariant()) + 1;
        if (month == 0 || !int.TryParse(day, out var d)) return null;
        try
        {
            if (timeOrYear.Contains(':'))
            {
                var parts = timeOrYear.Split(':');
                var date = new DateTime(now.Year, month, d, int.Parse(parts[0]), int.Parse(parts[1]), 0);
                // Entries with a time are from the last 6 months: a date in the future belongs to last year.
                if (date > now.AddDays(2)) date = date.AddYears(-1);
                return date;
            }
            return new DateTime(int.Parse(timeOrYear), month, d);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static FtpListItem? ParseWindows(string line)
    {
        var m = WindowsRegex().Match(line);
        if (!m.Success) return null;

        var isDir = m.Groups["size"].Value == "<DIR>";
        DateTime? modified = null;
        var stamp = m.Groups["date"].Value + " " + m.Groups["time"].Value.Replace(" ", "");
        string[] formats = ["MM-dd-yy h:mmtt", "MM-dd-yyyy h:mmtt", "MM-dd-yy HH:mm", "MM-dd-yyyy HH:mm", "M-d-yy h:mmtt", "M-d-yyyy h:mmtt"];
        if (DateTime.TryParseExact(stamp, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            modified = dt;

        return new FtpListItem
        {
            Name = m.Groups["name"].Value,
            Type = isDir ? FtpItemType.Directory : FtpItemType.File,
            Size = isDir ? 0 : long.Parse(m.Groups["size"].Value, CultureInfo.InvariantCulture),
            Modified = modified,
            RawLine = line,
        };
    }

    /// <summary>Parses an MLSD/MLST line: "fact=value;fact=value; name".</summary>
    public static FtpListItem? ParseMlsd(string line)
    {
        var space = line.IndexOf(' ');
        if (space <= 0 || !line[..space].Contains('=')) return null;

        var name = line[(space + 1)..];
        var facts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fact in line[..space].Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = fact.IndexOf('=');
            if (eq > 0) facts[fact[..eq]] = fact[(eq + 1)..];
        }

        var typeFact = facts.GetValueOrDefault("type", "file").ToLowerInvariant();
        if (typeFact is "cdir" or "pdir") return null;
        var type = typeFact switch
        {
            "dir" => FtpItemType.Directory,
            _ when typeFact.StartsWith("os.unix=s", StringComparison.Ordinal) => FtpItemType.Link,
            _ => FtpItemType.File,
        };

        DateTime? modified = null;
        if (facts.TryGetValue("modify", out var mod) && mod.Length >= 14
            && DateTime.TryParseExact(mod[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc))
            modified = utc.ToLocalTime();

        long size = 0;
        if (facts.TryGetValue("size", out var s)) long.TryParse(s, out size);
        else if (facts.TryGetValue("sizd", out var sd)) long.TryParse(sd, out size);

        return new FtpListItem
        {
            Name = name,
            Type = type,
            Size = size,
            Modified = modified,
            Permissions = facts.GetValueOrDefault("unix.mode") ?? facts.GetValueOrDefault("perm"),
            Owner = facts.GetValueOrDefault("unix.owner") ?? facts.GetValueOrDefault("unix.uid"),
            Group = facts.GetValueOrDefault("unix.group") ?? facts.GetValueOrDefault("unix.gid"),
            RawLine = line,
        };
    }
}
