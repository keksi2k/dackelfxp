using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DackelFxp.App.Converters;

/// <summary>Resolves an icon key (e.g. "FolderIcon") to the geometry defined in Styles.axaml.</summary>
public sealed class IconConverter : IValueConverter
{
    public static readonly IconConverter Instance = new();
    private static readonly Dictionary<string, Geometry?> Cache = [];

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key) return null;
        if (Cache.TryGetValue(key, out var cached)) return cached;
        Geometry? geometry = null;
        if (Application.Current?.TryFindResource(key, out var res) == true) geometry = res as Geometry;
        Cache[key] = geometry;
        return geometry;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
