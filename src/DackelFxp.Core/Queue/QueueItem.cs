using System.Text.Json.Serialization;
using DackelFxp.Core.Infrastructure;

namespace DackelFxp.Core.Queue;

public enum TransferKind { Download, Upload, Fxp }

public enum QueueStatus { Pending, Running, Done, Failed, Skipped, Paused }

/// <summary>One end of a transfer: a site path, or a local path when <see cref="SiteId"/> is null.</summary>
public sealed record TransferEndpoint(Guid? SiteId, string Path, string SiteName = "Lokal")
{
    [JsonIgnore] public bool IsLocal => SiteId is null;
}

public sealed class QueueItem : ObservableModel
{
    private QueueStatus _status;
    private long _transferred;
    private double _speed;
    private string? _error;
    private int _attempts;
    private long _size;
    private DateTimeOffset? _notBefore;
    private string? _detail;

    public Guid Id { get; init; } = Guid.NewGuid();
    public required TransferKind Kind { get; init; }
    public required TransferEndpoint Source { get; init; }
    public required TransferEndpoint Target { get; init; }
    public bool IsDirectory { get; init; }
    /// <summary>Directory items expanded from this parent (for display/grouping).</summary>
    public Guid? ParentId { get; init; }
    public int Priority { get; init; }

    public string Name => System.IO.Path.GetFileName(Source.Path.TrimEnd('/', '\\')) is { Length: > 0 } n ? n : Source.Path;

    public DateTime? Modified { get; init; }

    public long Size { get => _size; set { if (Set(ref _size, value)) Raise(nameof(ProgressPercent)); } }
    /// <summary>Live info while running (e.g. FXP duration).</summary>
    [JsonIgnore] public string? Detail { get => _detail; set { if (Set(ref _detail, value)) Raise(nameof(StatusText)); } }
    public QueueStatus Status { get => _status; set { if (Set(ref _status, value)) Raise(nameof(StatusText)); } }
    public long Transferred { get => _transferred; set { if (Set(ref _transferred, value)) Raise(nameof(ProgressPercent)); } }
    [JsonIgnore] public double Speed { get => _speed; set => Set(ref _speed, value); }
    public string? Error { get => _error; set { if (Set(ref _error, value)) Raise(nameof(StatusText)); } }
    public int Attempts { get => _attempts; set => Set(ref _attempts, value); }
    /// <summary>Retry back-off: the item is not picked up before this time.</summary>
    [JsonIgnore] public DateTimeOffset? NotBefore { get => _notBefore; set => Set(ref _notBefore, value); }

    [JsonIgnore] public double ProgressPercent => Size > 0 ? Math.Min(100, Transferred * 100.0 / Size) : 0;

    [JsonIgnore]
    public string StatusText => Status switch
    {
        QueueStatus.Pending when Attempts > 0 => $"Warte (Versuch {Attempts + 1})",
        QueueStatus.Pending => "Wartend",
        QueueStatus.Running => Detail is null ? "Läuft" : "Läuft – " + Detail,
        QueueStatus.Done => "Fertig",
        QueueStatus.Failed => "Fehler: " + Error,
        QueueStatus.Skipped => "Übersprungen" + (Error is null ? "" : ": " + Error),
        QueueStatus.Paused => "Pausiert",
        _ => Status.ToString(),
    };
}
