namespace DackelFxp.Ftp;

/// <summary>
/// Minimal UI language switch (German/English) shared by all layers.
/// Texts are written inline as pairs: <c>Lang.T("Deutsch", "English")</c>.
/// </summary>
public static class Lang
{
    private static bool _german = true;

    public static bool German
    {
        get => _german;
        set
        {
            if (_german == value) return;
            _german = value;
            Changed?.Invoke();
        }
    }

    /// <summary>"de" or "en".</summary>
    public static string Code
    {
        get => German ? "de" : "en";
        set => German = !string.Equals(value, "en", StringComparison.OrdinalIgnoreCase);
    }

    public static event Action? Changed;

    public static string T(string german, string english) => German ? german : english;
}
