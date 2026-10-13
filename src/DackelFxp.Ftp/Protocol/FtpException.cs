namespace DackelFxp.Ftp.Protocol;

public class FtpException : Exception
{
    public FtpException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The server answered a command with a negative reply.</summary>
public sealed class FtpCommandException : FtpException
{
    public FtpCommandException(string command, FtpReply reply)
        : base($"'{command}' fehlgeschlagen: {reply.Code} {reply.Message}")
    {
        Command = command;
        Reply = reply;
    }

    public string Command { get; }
    public FtpReply Reply { get; }
    public bool IsTransient => Reply.IsTransientError;
}

/// <summary>The control connection was lost or is unusable.</summary>
public sealed class FtpConnectionException : FtpException
{
    public FtpConnectionException(string message, Exception? inner = null) : base(message, inner) { }
}
