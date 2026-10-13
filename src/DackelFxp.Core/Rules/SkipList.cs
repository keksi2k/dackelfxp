using System.Text.RegularExpressions;

namespace DackelFxp.Core.Rules;

public enum RuleTarget { Both, Files, Directories }

public enum RuleAction { Skip, Priority }

/// <summary>One skiplist/priority rule. Patterns are wildcards (* ?) unless <see cref="IsRegex"/> is set.</summary>
public sealed class SkipRule
{
    public string Pattern { get; set; } = "";
    public bool IsRegex { get; set; }
    public RuleTarget Target { get; set; } = RuleTarget.Both;
    public RuleAction Action { get; set; } = RuleAction.Skip;
    /// <summary>For <see cref="RuleAction.Priority"/>: higher is transferred earlier, negative later.</summary>
    public int Priority { get; set; }
    public bool Enabled { get; set; } = true;

    private Regex? _regex;
    private string? _compiledFrom;

    public bool Matches(string name, bool isDirectory)
    {
        if (!Enabled || Pattern.Length == 0) return false;
        if (Target == RuleTarget.Files && isDirectory) return false;
        if (Target == RuleTarget.Directories && !isDirectory) return false;
        return GetRegex().IsMatch(name);
    }

    private Regex GetRegex()
    {
        var source = (IsRegex ? "R:" : "W:") + Pattern;
        if (_regex is null || _compiledFrom != source)
        {
            var pattern = IsRegex ? Pattern : WildcardToRegex(Pattern);
            _regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            _compiledFrom = source;
        }
        return _regex;
    }

    public static string WildcardToRegex(string wildcard) =>
        "^" + Regex.Escape(wildcard).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
}

public readonly record struct RuleResult(bool Skip, int Priority);

/// <summary>A named list of skip and priority rules. First matching skip rule wins; priorities add up.</summary>
public sealed class SkipList
{
    public string Name { get; set; } = "Standard";
    public List<SkipRule> Rules { get; set; } = [];

    public RuleResult Evaluate(string name, bool isDirectory)
    {
        var priority = 0;
        foreach (var rule in Rules)
        {
            if (!rule.Matches(name, isDirectory)) continue;
            if (rule.Action == RuleAction.Skip) return new RuleResult(true, 0);
            priority += rule.Priority;
        }
        return new RuleResult(false, priority);
    }

    public static SkipList CreateDefault() => new()
    {
        Name = "Standard",
        Rules =
        [
            new() { Pattern = "*.sfv", Target = RuleTarget.Files, Action = RuleAction.Priority, Priority = 100 },
            new() { Pattern = "*.nfo", Target = RuleTarget.Files, Action = RuleAction.Priority, Priority = 90 },
            new() { Pattern = "Sample", Target = RuleTarget.Directories, Action = RuleAction.Priority, Priority = -50 },
            new() { Pattern = "Thumbs.db", Target = RuleTarget.Files },
            new() { Pattern = ".DS_Store", Target = RuleTarget.Files },
            new() { Pattern = "desktop.ini", Target = RuleTarget.Files },
            new() { Pattern = "*-missing", Target = RuleTarget.Files },
            new() { Pattern = "*.bad", Target = RuleTarget.Files },
            new() { Pattern = @"^\[.*\]\s*-\s*.*(COMPLETE|INCOMPLETE).*$", IsRegex = true },
        ],
    };
}
