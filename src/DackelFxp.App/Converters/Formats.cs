using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using DackelFxp.Core.Queue;
using DackelFxp.Ftp.Protocol;

namespace DackelFxp.App.Converters;

public static class Formats
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(long bytes)
    {
        if (bytes < 0) return "?";
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : value.ToString(value < 10 ? "0.00" : value < 100 ? "0.0" : "0", CultureInfo.CurrentCulture) + " " + Units[unit];
    }

    public static string Speed(double bytesPerSecond) => bytesPerSecond <= 0 ? "" : Bytes((long)bytesPerSecond) + "/s";

    public static readonly IValueConverter BytesConverter = new FuncValueConverter<long, string>(Bytes);
    public static readonly IValueConverter SpeedConverter = new FuncValueConverter<double, string>(Speed);

    public static readonly IValueConverter KindConverter = new FuncValueConverter<TransferKind, string>(k => k switch
    {
        TransferKind.Download => "⬇",
        TransferKind.Upload => "⬆",
        _ => "⇄",
    });

    public static readonly IValueConverter StatusBrushConverter = new FuncValueConverter<QueueStatus, IBrush>(s => s switch
    {
        QueueStatus.Running => Brushes.DeepSkyBlue,
        QueueStatus.Done => Brushes.MediumSeaGreen,
        QueueStatus.Failed => Brushes.IndianRed,
        QueueStatus.Skipped => Brushes.Goldenrod,
        QueueStatus.Paused => Brushes.Gray,
        _ => Brushes.Gray,
    });

    public static readonly IValueConverter LogBrushConverter = new FuncValueConverter<FtpLogKind, IBrush>(k => k switch
    {
        FtpLogKind.Command => Brushes.SkyBlue,
        FtpLogKind.Error => Brushes.IndianRed,
        FtpLogKind.Tls => Brushes.MediumSeaGreen,
        FtpLogKind.Info => Brushes.Goldenrod,
        _ => Brushes.Silver,
    });
}
