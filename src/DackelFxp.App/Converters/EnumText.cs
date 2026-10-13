using System.Globalization;
using Avalonia.Data.Converters;
using DackelFxp.App.Services;
using DackelFxp.Core.Rules;
using DackelFxp.Core.Settings;
using DackelFxp.Core.Sites;
using DackelFxp.Ftp;
using DackelFxp.Ftp.Transfer;

namespace DackelFxp.App.Converters;

/// <summary>Display text for enum values (and a few string keys) in combo boxes, in the current language.</summary>
public sealed class EnumText : IValueConverter
{
    public static readonly EnumText Instance = new();

    public static string Of(object? value) => value switch
    {
        FtpTlsMode.None => Lang.T("Keine (FTP)", "None (FTP)"),
        FtpTlsMode.Explicit => Lang.T("Explizit (AUTH TLS)", "Explicit (AUTH TLS)"),
        FtpTlsMode.Implicit => Lang.T("Implizit (Port 990)", "Implicit (port 990)"),
        TlsVersion.Auto => Lang.T("Automatisch", "Automatic"),
        TlsVersion.Tls12 => "TLS 1.2",
        TlsVersion.Tls13 => "TLS 1.3",
        FtpDataMode.Passive => Lang.T("Passiv (PASV)", "Passive (PASV)"),
        FtpDataMode.ExtendedPassive => Lang.T("Erweitert passiv (EPSV)", "Extended passive (EPSV)"),
        FtpDataMode.Active => Lang.T("Aktiv (PORT)", "Active (PORT)"),
        FtpDataMode.ExtendedActive => Lang.T("Erweitert aktiv (EPRT)", "Extended active (EPRT)"),
        FtpListMethod.Auto => Lang.T("Automatisch", "Automatic"),
        FtpListMethod.List => "LIST",
        FtpListMethod.ListAl => "LIST -al",
        FtpListMethod.Mlsd => "MLSD",
        FtpListMethod.StatL => "STAT -l",
        FtpPretMode.Auto => Lang.T("Automatisch (wenn angekündigt)", "Automatic (if announced)"),
        FtpPretMode.Always => Lang.T("Immer", "Always"),
        FtpPretMode.Never => Lang.T("Nie", "Never"),
        FxpSecureMode.Auto => Lang.T("Automatisch (SSCN, dann CPSV)", "Automatic (SSCN, then CPSV)"),
        FxpSecureMode.Sscn => "SSCN",
        FxpSecureMode.Cpsv => "CPSV",
        FxpSecureMode.Clear => Lang.T("Unverschlüsselt (PROT C)", "Unencrypted (PROT C)"),
        FxpPassiveSide.Auto => Lang.T("Automatisch", "Automatic"),
        FxpPassiveSide.Source => Lang.T("Quelle", "Source"),
        FxpPassiveSide.Destination => Lang.T("Ziel", "Destination"),
        ExistsAction.Overwrite => Lang.T("Überschreiben", "Overwrite"),
        ExistsAction.Resume => Lang.T("Fortsetzen", "Resume"),
        ExistsAction.Skip => Lang.T("Überspringen", "Skip"),
        ExistsAction.Rename => Lang.T("Umbenennen", "Rename"),
        ExistsAction.Ask => Lang.T("Nachfragen", "Ask"),
        CompletionAction.None => Lang.T("Nichts", "Nothing"),
        CompletionAction.DisconnectAll => Lang.T("Alle Verbindungen trennen", "Disconnect all"),
        CompletionAction.Exit => Lang.T("Programm beenden", "Exit program"),
        RuleTarget.Both => Lang.T("Dateien & Ordner", "Files & folders"),
        RuleTarget.Files => Lang.T("Dateien", "Files"),
        RuleTarget.Directories => Lang.T("Ordner", "Folders"),
        RuleAction.Skip => Lang.T("Überspringen", "Skip"),
        RuleAction.Priority => Lang.T("Priorität", "Priority"),
        ScheduleAction.StartQueue => Lang.T("Queue starten", "Start queue"),
        ScheduleAction.StopQueue => Lang.T("Queue stoppen", "Stop queue"),
        "Dark" => Lang.T("Dunkel", "Dark"),
        "Light" => Lang.T("Hell", "Light"),
        "System" => "System",
        "de" => "Deutsch",
        "en" => "English",
        LogStore.AllSources => Lang.T("Alle", "All"),
        "auto" => Lang.T("Automatisch", "Automatic"),
        null => "",
        _ => value.ToString() ?? "",
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Of(value);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
