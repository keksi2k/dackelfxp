namespace DackelFxp.Ftp.Protocol;

public enum FtpLogKind { Info, Command, Reply, Error, Tls }

public sealed record FtpLogEntry(DateTimeOffset Time, FtpLogKind Kind, string Text);
