using DackelFxp.Ftp.Protocol;

namespace DackelFxp.Ftp.Transfer;

/// <summary>How the data channel between two servers is secured.</summary>
public enum FxpSecureMode
{
    /// <summary>Try SSCN on either side, then CPSV, then (if allowed) unencrypted.</summary>
    Auto,
    Sscn,
    Cpsv,
    /// <summary>PROT C: data channel unencrypted.</summary>
    Clear,
}

public enum FxpPassiveSide { Auto, Source, Destination }

/// <summary>A concrete way to set up the FXP data channel.</summary>
public enum FxpStrategy { Clear, SscnOnPassive, SscnOnActive, Cpsv }

public sealed class FxpOptions
{
    public FxpSecureMode SecureMode { get; init; } = FxpSecureMode.Auto;
    /// <summary>In Auto mode, fall back to an unencrypted data channel if no secure variant works.</summary>
    public bool AllowClearFallback { get; init; }
    public FxpPassiveSide PassiveSide { get; init; } = FxpPassiveSide.Auto;
    /// <summary>Strategy that worked last time for this site pair; tried first.</summary>
    public FxpStrategy? PreferredStrategy { get; init; }
    public bool? PreferredSourcePassive { get; init; }
    /// <summary>Resume position (REST on both sides).</summary>
    public long Offset { get; init; }
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromHours(12);
}

public sealed record FxpResult(TimeSpan Elapsed, FxpStrategy Strategy, bool SourcePassive);

/// <summary>Server-to-server transfer (FXP) including secure FXP via SSCN/CPSV and PRET.</summary>
public static class FxpTransfer
{
    public static async Task<FxpResult> TransferAsync(FtpClient source, string sourcePath, FtpClient destination,
        string destinationPath, FxpOptions options, CancellationToken ct = default)
    {
        if (ReferenceEquals(source, destination))
            throw new ArgumentException("FXP braucht zwei verschiedene Verbindungen");

        // Always lock in the same order to avoid deadlocks with opposite-direction transfers.
        var (first, second) = source.Id < destination.Id ? (source, destination) : (destination, source);
        using var lock1 = await first.AcquireAsync(ct).ConfigureAwait(false);
        using var lock2 = await second.AcquireAsync(ct).ConfigureAwait(false);

        var start = DateTimeOffset.Now;
        await source.SetBinaryCoreAsync(true, ct).ConfigureAwait(false);
        await destination.SetBinaryCoreAsync(true, ct).ConfigureAwait(false);

        var errors = new List<string>();
        foreach (var sourcePassive in PassiveOrder(options))
        {
            foreach (var strategy in StrategyOrder(source, destination, options))
            {
                var passive = sourcePassive ? source : destination;
                var active = sourcePassive ? destination : source;
                var setup = await TrySetupAsync(source, sourcePath, destination, destinationPath, passive, active, strategy, options, ct)
                    .ConfigureAwait(false);
                if (setup is not null)
                {
                    errors.Add($"{(sourcePassive ? "Quelle" : "Ziel")} PASV/{strategy}: {setup}");
                    continue;
                }

                await RunTransferAsync(source, sourcePath, destination, destinationPath, passive, options, ct).ConfigureAwait(false);
                source.Touch();
                destination.Touch();
                return new FxpResult(DateTimeOffset.Now - start, strategy, sourcePassive);
            }
        }
        throw new FtpException("FXP nicht möglich: " + string.Join("; ", errors));
    }

    private static IEnumerable<bool> PassiveOrder(FxpOptions options) => options.PassiveSide switch
    {
        FxpPassiveSide.Source => [true],
        FxpPassiveSide.Destination => [false],
        _ when options.PreferredSourcePassive == false => [false, true],
        _ => [true, false],
    };

    internal static List<FxpStrategy> StrategyOrder(FtpClient source, FtpClient destination, FxpOptions options)
    {
        // Encryption only works if both ends speak TLS; a plain-FTP site forces a clear data channel.
        if (!source.UsesTls || !destination.UsesTls || options.SecureMode == FxpSecureMode.Clear)
            return [FxpStrategy.Clear];

        List<FxpStrategy> list = options.SecureMode switch
        {
            FxpSecureMode.Sscn => [FxpStrategy.SscnOnPassive, FxpStrategy.SscnOnActive],
            FxpSecureMode.Cpsv => [FxpStrategy.Cpsv],
            _ => [FxpStrategy.SscnOnPassive, FxpStrategy.SscnOnActive, FxpStrategy.Cpsv],
        };
        if (options.SecureMode == FxpSecureMode.Auto && options.AllowClearFallback) list.Add(FxpStrategy.Clear);
        if (options.PreferredStrategy is { } preferred && list.Remove(preferred)) list.Insert(0, preferred);
        return list;
    }

    /// <summary>Prepares both sides (PROT, SSCN, PRET, PASV/PORT, REST). Returns an error text or null on success.</summary>
    private static async Task<string?> TrySetupAsync(FtpClient source, string sourcePath, FtpClient destination, string destinationPath,
        FtpClient passive, FtpClient active, FxpStrategy strategy, FxpOptions options, CancellationToken ct)
    {
        try
        {
            var secure = strategy != FxpStrategy.Clear;
            await passive.SetProtectionCoreAsync(secure, ct).ConfigureAwait(false);
            await active.SetProtectionCoreAsync(secure, ct).ConfigureAwait(false);

            // Exactly one side may act as TLS client; reset any SSCN left over from earlier transfers.
            var passiveSscn = strategy == FxpStrategy.SscnOnPassive;
            var activeSscn = strategy == FxpStrategy.SscnOnActive;
            if (!await passive.TrySetSscnCoreAsync(passiveSscn, ct).ConfigureAwait(false) && passiveSscn)
                return "SSCN wird nicht unterstützt";
            if (!await active.TrySetSscnCoreAsync(activeSscn, ct).ConfigureAwait(false) && activeSscn)
                return "SSCN wird nicht unterstützt";

            var passiveCommand = TransferCommand(passive, source, sourcePath, destinationPath);
            var activeCommand = TransferCommand(active, source, sourcePath, destinationPath);
            if (passive.ShouldSendPret) await passive.EnsureAsync("PRET " + passiveCommand, ct).ConfigureAwait(false);
            if (active.ShouldSendPret) await active.EnsureAsync("PRET " + activeCommand, ct).ConfigureAwait(false);

            var pasvCommand = strategy == FxpStrategy.Cpsv ? "CPSV" : "PASV";
            var pasv = await passive.Control.ExecuteAsync(pasvCommand, ct).ConfigureAwait(false);
            if (pasv.Code != 227) return $"{pasvCommand}: {pasv}";

            var port = await active.Control.ExecuteAsync("PORT " + FtpAddressParser.ExtractPasvArgument(pasv), ct).ConfigureAwait(false);
            if (!port.IsSuccess) return $"PORT: {port}";

            if (options.Offset > 0)
            {
                await source.RestCoreAsync(options.Offset, ct).ConfigureAwait(false);
                await destination.RestCoreAsync(options.Offset, ct).ConfigureAwait(false);
            }
            return null;
        }
        catch (FtpCommandException ex)
        {
            return ex.Message;
        }
    }

    private static string TransferCommand(FtpClient client, FtpClient source, string sourcePath, string destinationPath) =>
        ReferenceEquals(client, source) ? "RETR " + sourcePath : "STOR " + destinationPath;

    private static async Task RunTransferAsync(FtpClient source, string sourcePath, FtpClient destination, string destinationPath,
        FtpClient passive, FxpOptions options, CancellationToken ct)
    {
        var active = ReferenceEquals(passive, source) ? destination : source;
        var passiveCommand = TransferCommand(passive, source, sourcePath, destinationPath);
        var activeCommand = TransferCommand(active, source, sourcePath, destinationPath);

        // The passive side must have its command before the active side connects.
        // Replies are read concurrently: some servers only answer 150 once the connection is up.
        await passive.Control.SendAsync(passiveCommand, ct).ConfigureAwait(false);
        await active.Control.SendAsync(activeCommand, ct).ConfigureAwait(false);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var passiveTask = ReadFinalAsync(passive, options.MaxDuration, cts.Token);
        var activeTask = ReadFinalAsync(active, options.MaxDuration, cts.Token);

        try
        {
            var firstDone = await Task.WhenAny(passiveTask, activeTask).ConfigureAwait(false);
            var firstReply = await firstDone.ConfigureAwait(false);
            if (!firstReply.IsSuccess)
            {
                // One side failed (e.g. file exists, no access): the other side would wait forever.
                var other = firstDone == passiveTask ? active : passive;
                cts.Cancel();
                await IgnoreAsync(firstDone == passiveTask ? activeTask : passiveTask).ConfigureAwait(false);
                await AbortAsync(other).ConfigureAwait(false);
                var cmd = firstDone == passiveTask ? passiveCommand : activeCommand;
                throw new FtpCommandException(cmd, firstReply);
            }

            var secondReply = await (firstDone == passiveTask ? activeTask : passiveTask).ConfigureAwait(false);
            if (!secondReply.IsSuccess)
                throw new FtpCommandException(firstDone == passiveTask ? activeCommand : passiveCommand, secondReply);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await IgnoreAsync(passiveTask).ConfigureAwait(false);
            await IgnoreAsync(activeTask).ConfigureAwait(false);
            await AbortAsync(source).ConfigureAwait(false);
            await AbortAsync(destination).ConfigureAwait(false);
            throw;
        }
        catch (FtpConnectionException)
        {
            // One control connection broke: the other side's state is unknown too.
            cts.Cancel();
            await IgnoreAsync(passiveTask).ConfigureAwait(false);
            await IgnoreAsync(activeTask).ConfigureAwait(false);
            await AbortAsync(source).ConfigureAwait(false);
            await AbortAsync(destination).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<FtpReply> ReadFinalAsync(FtpClient client, TimeSpan timeout, CancellationToken ct)
    {
        while (true)
        {
            var reply = await client.Control.ReadReplyAsync(timeout, ct).ConfigureAwait(false);
            if (!reply.IsPreliminary) return reply;
        }
    }

    private static async Task IgnoreAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception ex) when (ex is FtpException or OperationCanceledException) { }
    }

    /// <summary>The protocol state of an interrupted FXP side is unknown, so the connection is dropped.</summary>
    private static Task AbortAsync(FtpClient client) => client.AbortAfterCancelAsync();
}
