using DackelFxp.Ftp;
using System.Collections.Concurrent;
using System.Diagnostics;
using DackelFxp.Core.Connections;
using DackelFxp.Core.Rules;
using DackelFxp.Core.Settings;
using DackelFxp.Core.Sites;
using DackelFxp.Core.Tools;
using DackelFxp.Ftp.Listing;
using DackelFxp.Ftp.Protocol;
using DackelFxp.Ftp.Transfer;

namespace DackelFxp.Core.Queue;

/// <summary>Asks the user what to do with an existing target (implemented by the UI). Null = skip.</summary>
public interface IFileExistsPrompt
{
    Task<ExistsDecision?> AskAsync(QueueItem item, long sourceSize, long targetSize);
}

/// <summary>Processes the <see cref="TransferQueue"/> with several parallel slots.</summary>
public sealed class TransferEngine : IAsyncDisposable
{
    private readonly TransferQueue _queue;
    private readonly ConnectionManager _connections;
    private readonly SiteStore _sites;
    private readonly SettingsStore _settings;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<(Guid, Guid), (FxpStrategy Strategy, bool SourcePassive)> _fxpCache = new();
    private readonly SemaphoreSlim _signal = new(0);
    private CancellationTokenSource? _stopCts;
    private readonly List<Task> _workers = [];
    private readonly Lock _workerLock = new();
    private int _workerCount;
    private int _busyWorkers;

    public TransferEngine(TransferQueue queue, ConnectionManager connections, SiteStore sites, SettingsStore settings)
    {
        _queue = queue;
        _connections = connections;
        _sites = sites;
        _settings = settings;
        _queue.WorkAvailable += Wake;
    }

    public IFileExistsPrompt? ExistsPrompt { get; set; }
    public bool IsRunning => _stopCts is not null;

    public event Action<bool>? RunningChanged;
    /// <summary>The queue ran empty (all items done, failed or skipped).</summary>
    public event Action? QueueCompleted;
    public event Action<QueueItem, Exception>? ItemFailed;

    public void Start()
    {
        if (_stopCts is not null) return;
        _stopCts = new CancellationTokenSource();
        ApplySlotCount();
        RunningChanged?.Invoke(true);
    }

    private int DesiredSlots => Math.Max(1, _settings.Current.TransferSlots);

    /// <summary>
    /// Adapts the number of parallel workers to the "parallel transfers" setting, also while the queue runs:
    /// missing workers are started, surplus workers end after their current item.
    /// </summary>
    public void ApplySlotCount()
    {
        if (_stopCts is not { } cts) return;
        lock (_workerLock)
        {
            _workers.RemoveAll(t => t.IsCompleted);
            while (_workerCount < DesiredSlots)
            {
                _workerCount++;
                var token = cts.Token;
                _workers.Add(Task.Run(() => WorkerAsync(token)));
            }
        }
        Wake();
    }

    /// <summary>A worker ends if there are more workers than configured slots.</summary>
    private bool TryRetireWorker()
    {
        lock (_workerLock)
        {
            if (_workerCount <= DesiredSlots) return false;
            _workerCount--;
            return true;
        }
    }

    public async Task StopAsync()
    {
        var cts = _stopCts;
        if (cts is null) return;
        _stopCts = null;
        cts.Cancel();
        foreach (var running in _running.Values) running.Cancel();
        Task[] workers;
        lock (_workerLock) workers = [.. _workers];
        try { await Task.WhenAll(workers).ConfigureAwait(false); } catch (OperationCanceledException) { }
        cts.Dispose();
        lock (_workerLock)
        {
            _workers.Clear();
            _workerCount = 0;
        }
        RunningChanged?.Invoke(false);
    }

    /// <summary>Aborts a running item; it is set to paused.</summary>
    public void Abort(QueueItem item)
    {
        if (_running.TryGetValue(item.Id, out var cts)) cts.Cancel();
    }

    private void Wake()
    {
        if (_signal.CurrentCount < 64) _signal.Release();
    }

    private async Task WorkerAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            if (TryRetireWorker()) return;
            var item = _queue.TakeNext(HasFreeConnections);
            if (item is null)
            {
                if (Volatile.Read(ref _busyWorkers) == 0 && !_queue.HasPendingWork())
                {
                    _ = Task.Run(OnCompletedAsync, CancellationToken.None);
                    return;
                }
                try { await _signal.WaitAsync(TimeSpan.FromSeconds(1), stop).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            Interlocked.Increment(ref _busyWorkers);
            try { await ProcessAsync(item, stop).ConfigureAwait(false); }
            finally
            {
                Interlocked.Decrement(ref _busyWorkers);
                Wake();
            }
        }
    }

    private int _completing;

    private async Task OnCompletedAsync()
    {
        if (Interlocked.Exchange(ref _completing, 1) == 1) return;
        try
        {
            await StopAsync().ConfigureAwait(false);
            if (_settings.Current.OnQueueComplete == CompletionAction.DisconnectAll)
                await _connections.DisconnectAllAsync().ConfigureAwait(false);
            QueueCompleted?.Invoke();
        }
        finally
        {
            Volatile.Write(ref _completing, 0);
        }
    }

    private async Task ProcessAsync(QueueItem item, CancellationToken stop)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        _running[item.Id] = cts;
        item.Error = null;
        try
        {
            var skipped = item.IsDirectory
                ? await ExpandDirectoryAsync(item, cts.Token).ConfigureAwait(false)
                : item.Kind switch
                {
                    TransferKind.Download => await DownloadAsync(item, cts.Token).ConfigureAwait(false),
                    TransferKind.Upload => await UploadAsync(item, cts.Token).ConfigureAwait(false),
                    _ => await FxpAsync(item, cts.Token).ConfigureAwait(false),
                };
            item.Speed = 0;
            item.Status = skipped is null ? QueueStatus.Done : QueueStatus.Skipped;
            if (skipped is not null) item.Error = skipped;
            else if (_settings.Current.RemoveFinishedItems) _queue.Remove([item]);
        }
        catch (OperationCanceledException)
        {
            item.Speed = 0;
            item.Status = stop.IsCancellationRequested ? QueueStatus.Pending : QueueStatus.Paused;
        }
        catch (Exception ex)
        {
            item.Speed = 0;
            HandleFailure(item, ex);
        }
        finally
        {
            _running.TryRemove(item.Id, out _);
        }
    }

    private void HandleFailure(QueueItem item, Exception ex)
    {
        item.Attempts++;
        item.Error = ex.Message;
        var site = ResolveSite(item.Source) ?? ResolveSite(item.Target);
        var maxRetries = site?.RetryCount ?? 3;
        var permanent = ex is FtpCommandException { Reply.IsPermanentError: true } cmd
                        && !cmd.Command.StartsWith("PASS", StringComparison.Ordinal)
                        && !cmd.Command.StartsWith("USER", StringComparison.Ordinal)
                        && !cmd.Command.StartsWith("CONNECT", StringComparison.Ordinal)
                        || ex is UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException
                        || ex is InvalidOperationException;
        if (permanent || item.Attempts > maxRetries)
        {
            item.Status = QueueStatus.Failed;
            ItemFailed?.Invoke(item, ex);
            return;
        }
        item.NotBefore = DateTimeOffset.Now.AddSeconds(site?.RetryDelaySeconds ?? 10);
        item.Status = QueueStatus.Pending;
    }

    /// <summary>
    /// Only start items whose connections are free right now. Otherwise the item would be shown as running
    /// while it just waits for a connection, and items of other sites could not move ahead.
    /// </summary>
    private bool HasFreeConnections(QueueItem item)
    {
        var source = ResolveSite(item.Source);
        var target = ResolveSite(item.Target);
        if (source is not null && target is not null && source.Id == target.Id)
            // With a limit below 2 this can never work: let the item start and fail with a clear message.
            return source.MaxConnections < 2 || _connections.FreeConnections(source) >= 2;
        return (source is null || _connections.FreeConnections(source) >= 1)
               && (target is null || _connections.FreeConnections(target) >= 1);
    }

    private Site? ResolveSite(TransferEndpoint endpoint) => endpoint.SiteId is { } id ? _sites.Find(id) : null;

    private Site RequireSite(TransferEndpoint endpoint) =>
        ResolveSite(endpoint) ?? throw new InvalidOperationException(Lang.T($"Site '{endpoint.SiteName}' existiert nicht mehr", $"Site '{endpoint.SiteName}' no longer exists"));

    // ---------------------------------------------------------------- existing targets

    /// <summary>Returns (offset, targetPath) or null if the item must be skipped.</summary>
    private async Task<(long Offset, string Path)?> ResolveExistingAsync(QueueItem item, long sourceSize, long targetSize,
        string targetPath, Func<Task<Func<string, bool>>> existsFactory, bool canResume)
    {
        if (targetSize < 0) return (0, targetPath);
        var action = _settings.Current.FileExists.Select(sourceSize, targetSize);
        ExistsDecision? decision = action switch
        {
            ExistsAction.Overwrite => ExistsDecision.Overwrite,
            ExistsAction.Resume => ExistsDecision.Resume,
            ExistsAction.Rename => ExistsDecision.Rename,
            ExistsAction.Skip => null,
            _ => ExistsPrompt is null ? null : await ExistsPrompt.AskAsync(item, sourceSize, targetSize).ConfigureAwait(false),
        };
        return decision switch
        {
            null or ExistsDecision.Skip => null,
            ExistsDecision.Resume when canResume && targetSize < sourceSize => (targetSize, targetPath),
            ExistsDecision.Resume when targetSize == sourceSize => null,
            ExistsDecision.Rename => (0, RenamePath(targetPath, await existsFactory().ConfigureAwait(false))),
            _ => (0, targetPath),
        };
    }

    private static string RenamePath(string path, Func<string, bool> exists)
    {
        var remote = path.Contains('/') && !path.Contains('\\');
        var dir = remote ? FtpPath.GetParent(path) : Path.GetDirectoryName(path) ?? "";
        var name = remote ? FtpPath.GetName(path) : Path.GetFileName(path);
        var newName = FileExistsPolicy.RenameTarget(name, exists);
        return remote ? FtpPath.Combine(dir, newName) : Path.Combine(dir, newName);
    }

    // ---------------------------------------------------------------- transfers

    private async Task<string?> DownloadAsync(QueueItem item, CancellationToken ct)
    {
        var site = RequireSite(item.Source);
        await using var conn = await _connections.RentAsync(site, ct).ConfigureAwait(false);
        var client = conn.Client;
        if (item.Size <= 0) item.Size = Math.Max(0, await client.GetSizeAsync(item.Source.Path, ct).ConfigureAwait(false));

        var targetPath = item.Target.Path;
        var existing = File.Exists(targetPath) ? new FileInfo(targetPath).Length : -1;
        var dir = Path.GetDirectoryName(targetPath)!;
        var resolved = await ResolveExistingAsync(item, item.Size, existing, targetPath,
            () => Task.FromResult<Func<string, bool>>(name => File.Exists(Path.Combine(dir, name))), canResume: true).ConfigureAwait(false);
        if (resolved is not { } target) return Lang.T("Ziel existiert", "target exists");

        Directory.CreateDirectory(Path.GetDirectoryName(target.Path)!);
        var progress = new ProgressTracker(item, target.Offset);
        await using (var fs = new FileStream(target.Path, target.Offset > 0 ? FileMode.OpenOrCreate : FileMode.Create,
                         FileAccess.Write, FileShare.Read, 64 * 1024, useAsync: true))
        {
            fs.SetLength(target.Offset);
            fs.Seek(target.Offset, SeekOrigin.Begin);
            await client.DownloadAsync(item.Source.Path, fs, target.Offset, progress, _connections.GetLimiter(site), ct).ConfigureAwait(false);
        }
        progress.Complete();

        if (_settings.Current.PreserveTimestamps && item.Modified is { } modified)
            File.SetLastWriteTime(target.Path, modified);
        if (_settings.Current.VerifySfvAfterDownload)
        {
            var check = SfvVerifier.VerifyAgainstSiblingSfv(target.Path);
            if (check == false) throw new InvalidOperationException(Lang.T("CRC-Prüfung (SFV) fehlgeschlagen", "CRC check (SFV) failed"));
        }
        return null;
    }

    private async Task<string?> UploadAsync(QueueItem item, CancellationToken ct)
    {
        var site = RequireSite(item.Target);
        var local = new FileInfo(item.Source.Path);
        if (!local.Exists) throw new FileNotFoundException(Lang.T("Lokale Datei fehlt", "Local file missing"), local.FullName);
        item.Size = local.Length;

        await using var conn = await _connections.RentAsync(site, ct).ConfigureAwait(false);
        var client = conn.Client;
        var existing = await client.GetSizeAsync(item.Target.Path, ct).ConfigureAwait(false);
        var resolved = await ResolveExistingAsync(item, local.Length, existing, item.Target.Path,
            () => RemoteExistsAsync(client, item.Target.Path, ct), canResume: true).ConfigureAwait(false);
        if (resolved is not { } target) return Lang.T("Ziel existiert", "target exists");

        var progress = new ProgressTracker(item, target.Offset);
        await using (var fs = new FileStream(local.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true))
            await client.UploadAsync(fs, target.Path, target.Offset, progress, _connections.GetLimiter(site), ct).ConfigureAwait(false);
        progress.Complete();

        if (_settings.Current.PreserveTimestamps)
            await client.SetModifiedTimeAsync(target.Path, local.LastWriteTime, ct).ConfigureAwait(false);
        return null;
    }

    private static async Task<Func<string, bool>> RemoteExistsAsync(DackelFxp.Ftp.FtpClient client, string targetPath, CancellationToken ct)
    {
        var listing = await client.ListAsync(FtpPath.GetParent(targetPath), ct).ConfigureAwait(false);
        var names = listing.Select(i => i.Name).ToHashSet();
        return names.Contains;
    }

    private async Task<string?> FxpAsync(QueueItem item, CancellationToken ct)
    {
        var sourceSite = RequireSite(item.Source);
        var targetSite = RequireSite(item.Target);
        if (sourceSite.Id == targetSite.Id && sourceSite.MaxConnections < 2)
            throw new InvalidOperationException(Lang.T("FXP innerhalb einer Site braucht mindestens 2 Verbindungen", "FXP within one site needs at least 2 connections"));

        var (src, dst) = await RentPairAsync(sourceSite, targetSite, ct).ConfigureAwait(false);
        await using var _ = src;
        await using var __ = dst;

        if (item.Size <= 0) item.Size = Math.Max(0, await src.Client.GetSizeAsync(item.Source.Path, ct).ConfigureAwait(false));
        var existing = await dst.Client.GetSizeAsync(item.Target.Path, ct).ConfigureAwait(false);
        var resolved = await ResolveExistingAsync(item, item.Size, existing, item.Target.Path,
            () => RemoteExistsAsync(dst.Client, item.Target.Path, ct), canResume: true).ConfigureAwait(false);
        if (resolved is not { } target) return Lang.T("Ziel existiert", "target exists");

        var key = (sourceSite.Id, targetSite.Id);
        _fxpCache.TryGetValue(key, out var cached);
        var options = new FxpOptions
        {
            SecureMode = CombineSecureMode(sourceSite.FxpSecureMode, targetSite.FxpSecureMode),
            AllowClearFallback = sourceSite.FxpAllowClear && targetSite.FxpAllowClear,
            PassiveSide = sourceSite.FxpPassiveSide != FxpPassiveSide.Auto ? sourceSite.FxpPassiveSide : targetSite.FxpPassiveSide,
            PreferredStrategy = _fxpCache.ContainsKey(key) ? cached.Strategy : null,
            PreferredSourcePassive = _fxpCache.ContainsKey(key) ? cached.SourcePassive : null,
            Offset = target.Offset,
        };

        item.Transferred = target.Offset;
        var sw = Stopwatch.StartNew();
        using var ticker = new FxpTicker(item, sw);
        var result = await FxpTransfer.TransferAsync(src.Client, item.Source.Path, dst.Client, target.Path, options, ct).ConfigureAwait(false);
        _fxpCache[key] = (result.Strategy, result.SourcePassive);
        item.Transferred = item.Size;
        return null;
    }

    private static FxpSecureMode CombineSecureMode(FxpSecureMode a, FxpSecureMode b)
    {
        if (a == FxpSecureMode.Clear || b == FxpSecureMode.Clear) return FxpSecureMode.Clear;
        if (a != FxpSecureMode.Auto) return a;
        return b;
    }

    /// <summary>Rents two connections in a fixed order so opposite FXP transfers cannot deadlock.</summary>
    private async Task<(PooledConnection Source, PooledConnection Target)> RentPairAsync(Site source, Site target, CancellationToken ct)
    {
        var sourceFirst = source.Id.CompareTo(target.Id) <= 0;
        var first = await _connections.RentAsync(sourceFirst ? source : target, ct).ConfigureAwait(false);
        try
        {
            var second = await _connections.RentAsync(sourceFirst ? target : source, ct).ConfigureAwait(false);
            return sourceFirst ? (first, second) : (second, first);
        }
        catch
        {
            await first.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // ---------------------------------------------------------------- directories

    private async Task<string?> ExpandDirectoryAsync(QueueItem item, CancellationToken ct)
    {
        var sourceSite = ResolveSite(item.Source);
        var skipList = _settings.Current.FindSkipList(sourceSite?.SkipListName);

        // Create the target directory.
        if (item.Target.IsLocal)
        {
            Directory.CreateDirectory(item.Target.Path);
        }
        else
        {
            await using var conn = await _connections.RentAsync(RequireSite(item.Target), ct).ConfigureAwait(false);
            var mkd = await conn.Client.ExecuteAsync("MKD " + item.Target.Path, ct).ConfigureAwait(false);
            // 550/553 usually means "exists"; real problems surface on the first file.
            _ = mkd;
        }

        // List the source.
        List<(string Name, bool IsDir, long Size, DateTime? Modified)> entries;
        if (item.Source.IsLocal)
        {
            var dir = new DirectoryInfo(item.Source.Path);
            entries = dir.EnumerateFileSystemInfos()
                .Select(i => (i.Name, i is DirectoryInfo, i is FileInfo f ? f.Length : 0L, (DateTime?)i.LastWriteTime))
                .ToList();
        }
        else
        {
            await using var conn = await _connections.RentAsync(sourceSite!, ct).ConfigureAwait(false);
            var listing = await conn.Client.ListAsync(item.Source.Path, ct).ConfigureAwait(false);
            entries = listing.Select(i => (i.Name, i.Type == FtpItemType.Directory, i.Size, i.Modified)).ToList();
        }

        var children = new List<QueueItem>();
        foreach (var entry in entries)
        {
            var rule = skipList?.Evaluate(entry.Name, entry.IsDir) ?? default;
            if (rule.Skip) continue;
            children.Add(new QueueItem
            {
                Kind = item.Kind,
                Source = item.Source with { Path = Combine(item.Source, entry.Name) },
                Target = item.Target with { Path = Combine(item.Target, entry.Name) },
                IsDirectory = entry.IsDir,
                ParentId = item.Id,
                Priority = rule.Priority,
                Size = entry.Size,
                Modified = entry.Modified,
            });
        }

        var filesFirst = _settings.Current.TransferFilesBeforeFolders;
        var ordered = children
            .OrderByDescending(c => c.Priority)
            .ThenBy(c => filesFirst ? (c.IsDirectory ? 1 : 0) : 0)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _queue.InsertAfter(item, ordered);
        item.Size = ordered.Where(c => !c.IsDirectory).Sum(c => c.Size);
        item.Transferred = item.Size;
        return null;
    }

    private static string Combine(TransferEndpoint endpoint, string name) =>
        endpoint.IsLocal ? Path.Combine(endpoint.Path, name) : FtpPath.Combine(endpoint.Path, name);

    public async ValueTask DisposeAsync()
    {
        _queue.WorkAvailable -= Wake;
        await StopAsync().ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- progress

    /// <summary>Updates bytes and speed at most every 250 ms to keep the UI responsive.</summary>
    private sealed class ProgressTracker(QueueItem item, long start) : IProgress<long>
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _lastReport;
        private readonly long _start = start;
        private long _current;

        public void Report(long value)
        {
            _current = value;
            var now = _clock.ElapsedMilliseconds;
            if (now - _lastReport < 250) return;
            _lastReport = now;
            item.Transferred = value;
            var seconds = _clock.Elapsed.TotalSeconds;
            if (seconds > 0) item.Speed = (value - _start) / seconds;
        }

        public void Complete() => item.Transferred = Math.Max(_current, _start);
    }

    /// <summary>FXP gives no byte progress; show elapsed time as an indeterminate speed estimate.</summary>
    private sealed class FxpTicker : IDisposable
    {
        private readonly Timer _timer;
        private readonly QueueItem _item;

        public FxpTicker(QueueItem item, Stopwatch sw)
        {
            _item = item;
            _timer = new Timer(_ => item.Detail = $"FXP {sw.Elapsed:hh\\:mm\\:ss}", null, 1000, 1000);
        }

        public void Dispose()
        {
            _timer.Dispose();
            _item.Detail = null;
        }
    }
}
