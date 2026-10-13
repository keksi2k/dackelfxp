namespace DackelFxp.Ftp.Protocol;

/// <summary>Helpers for remote (always '/'-separated) paths.</summary>
public static class FtpPath
{
    public static string Combine(string directory, string name)
    {
        if (name.StartsWith('/')) return name;
        if (string.IsNullOrEmpty(directory)) return name;
        return directory.EndsWith('/') ? directory + name : directory + "/" + name;
    }

    public static string GetName(string path)
    {
        var trimmed = path.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        return idx < 0 ? trimmed : trimmed[(idx + 1)..];
    }

    public static string GetParent(string path)
    {
        var trimmed = path.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        return idx switch
        {
            < 0 => "",
            0 => "/",
            _ => trimmed[..idx],
        };
    }
}
