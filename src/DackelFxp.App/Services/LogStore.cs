using System.Collections.ObjectModel;
using Avalonia.Threading;
using DackelFxp.Core.Connections;
using DackelFxp.Ftp.Protocol;

namespace DackelFxp.App.Services;

public sealed record LogLine(DateTimeOffset Time, string Source, FtpLogKind Kind, string Text)
{
    public string TimeText => Time.ToString("HH:mm:ss");
}

/// <summary>Collects protocol logs of all connections (bounded) for the log panel.</summary>
public sealed class LogStore
{
    private const int MaxLines = 5000;
    private readonly List<LogLine> _pending = [];
    private readonly Lock _lock = new();
    private readonly DispatcherTimer _flush;

    public LogStore(ConnectionManager connections)
    {
        connections.Log += e => Add(new LogLine(e.Entry.Time, $"{e.Site.DisplayName} #{e.Connection}", e.Entry.Kind, e.Entry.Text));
        // Batch log lines: a fast transfer can produce many lines per second.
        _flush = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => Flush());
        _flush.Start();
    }

    public ObservableCollection<LogLine> Lines { get; } = [];
    /// <summary>Source filter entries; <see cref="AllSources"/> is shown as "Alle"/"All".</summary>
    public ObservableCollection<string> Sources { get; } = [AllSources];
    public const string AllSources = "*";

    public void AddInfo(string source, string text) => Add(new LogLine(DateTimeOffset.Now, source, FtpLogKind.Info, text));
    public void AddError(string source, string text) => Add(new LogLine(DateTimeOffset.Now, source, FtpLogKind.Error, text));

    public void Add(LogLine line)
    {
        lock (_lock) _pending.Add(line);
    }

    private void Flush()
    {
        List<LogLine> batch;
        lock (_lock)
        {
            if (_pending.Count == 0) return;
            batch = [.. _pending];
            _pending.Clear();
        }
        foreach (var line in batch)
        {
            Lines.Add(line);
            var site = line.Source.Split(" #")[0];
            if (!Sources.Contains(site)) Sources.Add(site);
        }
        while (Lines.Count > MaxLines) Lines.RemoveAt(0);
        LinesAdded?.Invoke();
    }

    public event Action? LinesAdded;

    public void Clear() => Lines.Clear();
}
