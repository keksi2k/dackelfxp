namespace DackelFxp.Core.Rules;

public enum ExistsAction { Overwrite, Resume, Skip, Rename, Ask }

public enum ExistsDecision { Overwrite, Resume, Skip, Rename }

/// <summary>What to do when the target exists, depending on how its size compares to the source (like FlashFXP).</summary>
public sealed class FileExistsPolicy
{
    public ExistsAction WhenSmaller { get; set; } = ExistsAction.Resume;
    public ExistsAction WhenEqual { get; set; } = ExistsAction.Skip;
    public ExistsAction WhenLarger { get; set; } = ExistsAction.Overwrite;

    /// <summary>Unknown sizes (-1) cannot be compared: then the "larger" rule applies.</summary>
    public ExistsAction Select(long sourceSize, long targetSize)
    {
        if (sourceSize < 0 || targetSize < 0) return WhenLarger == ExistsAction.Resume ? ExistsAction.Overwrite : WhenLarger;
        if (targetSize < sourceSize) return WhenSmaller;
        if (targetSize == sourceSize) return WhenEqual;
        return WhenLarger == ExistsAction.Resume ? ExistsAction.Overwrite : WhenLarger;
    }

    public static string RenameTarget(string name, Func<string, bool> exists)
    {
        var ext = Path.GetExtension(name);
        var stem = name[..^ext.Length];
        for (var i = 1; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!exists(candidate)) return candidate;
        }
    }
}
