namespace DackelFxp.Ftp.Protocol;

/// <summary>A complete (possibly multi-line) reply from an FTP server.</summary>
public sealed record FtpReply(int Code, string Message, IReadOnlyList<string> Lines)
{
    public bool IsPreliminary => Code is >= 100 and < 200;
    public bool IsSuccess => Code is >= 200 and < 300;
    public bool IsIntermediate => Code is >= 300 and < 400;
    public bool IsTransientError => Code is >= 400 and < 500;
    public bool IsPermanentError => Code >= 500;
    public bool IsPositive => Code is >= 100 and < 400;

    /// <summary>Lines between the first and the last line of a multi-line reply.</summary>
    public IEnumerable<string> InnerLines => Lines.Count <= 2 ? [] : Lines.Skip(1).Take(Lines.Count - 2);

    public override string ToString() => $"{Code} {Message}";
}
