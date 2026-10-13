using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DackelFxp.App.Converters;
using DackelFxp.App.Services;
using DackelFxp.Core.Queue;
using DackelFxp.Ftp;

namespace DackelFxp.App.ViewModels;

public sealed partial class QueueViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly TransferQueue _queue;

    public QueueViewModel(AppServices services)
    {
        _services = services;
        _queue = services.Queue;
        // Events arrive via CoreDispatcher on the UI thread, in the order the queue changed.
        _queue.Inserted += (index, items) =>
        {
            for (var i = 0; i < items.Count; i++) Items.Insert(Math.Min(index + i, Items.Count), items[i]);
            UpdateSummary();
        };
        _queue.Removed += items =>
        {
            foreach (var item in items) Items.Remove(item);
            UpdateSummary();
        };
        _queue.Reordered += order =>
        {
            Items.Clear();
            foreach (var item in order) Items.Add(item);
        };
        services.Engine.RunningChanged += running => Dispatcher.UIThread.Post(() => IsRunning = running);

        var timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateSummary());
        timer.Start();
    }

    public ObservableCollection<QueueItem> Items { get; } = [];
    public IList<QueueItem> Selection { get; set; } = [];

    [ObservableProperty] public partial bool IsRunning { get; private set; }
    [ObservableProperty] public partial string Summary { get; private set; } = "";
    [ObservableProperty] public partial string TotalSpeed { get; private set; } = "";

    private void UpdateSummary()
    {
        var pending = 0;
        var running = 0;
        var failed = 0;
        long remaining = 0;
        double speed = 0;
        foreach (var item in Items)
        {
            switch (item.Status)
            {
                case QueueStatus.Pending: pending++; remaining += item.Size; break;
                case QueueStatus.Running: running++; speed += item.Speed; remaining += Math.Max(0, item.Size - item.Transferred); break;
                case QueueStatus.Failed: failed++; break;
            }
        }
        TotalSpeed = Formats.Speed(speed);
        var eta = speed > 0 ? TimeSpan.FromSeconds(remaining / speed) : (TimeSpan?)null;
        var left = Formats.Bytes(remaining);
        Summary = Lang.T($"{Items.Count} in Queue, {running} aktiv, {pending} wartend, {failed} Fehler, {left} übrig",
                         $"{Items.Count} in queue, {running} active, {pending} pending, {failed} failed, {left} left")
                  + (eta is { } e ? Lang.T($", ca. {e:hh\\:mm\\:ss}", $", approx. {e:hh\\:mm\\:ss}") : "");
    }

    /// <summary>Re-evaluates texts after a language switch.</summary>
    public void RefreshTexts()
    {
        foreach (var item in Items) item.RefreshText();
        UpdateSummary();
    }

    [RelayCommand]
    public void Start()
    {
        if (_services.Engine.ExistsPrompt is UiExistsPrompt prompt) prompt.Reset();
        _services.Engine.Start();
    }

    [RelayCommand]
    private Task StopAsync() => _services.Engine.StopAsync();

    [RelayCommand]
    private void RemoveSelected() => _queue.Remove(Selection.ToList());

    [RelayCommand]
    private void RemoveFinished() => _queue.RemoveFinished();

    [RelayCommand]
    private async Task ClearAsync()
    {
        if (await _services.Dialogs.ConfirmAsync(Lang.T("Queue leeren", "Clear queue"), Lang.T("Alle nicht laufenden Einträge entfernen?", "Remove all entries that are not running?")))
            _queue.Clear();
    }

    [RelayCommand] private void MoveUp() => _queue.Move(Selection.ToList(), -1);
    [RelayCommand] private void MoveDown() => _queue.Move(Selection.ToList(), 1);
    [RelayCommand] private void MoveTop() => _queue.Move(Selection.ToList(), int.MinValue);
    [RelayCommand] private void MoveBottom() => _queue.Move(Selection.ToList(), int.MaxValue);

    [RelayCommand]
    private void Retry() => _queue.SetStatus(Selection.Where(i => i.Status is QueueStatus.Failed or QueueStatus.Skipped or QueueStatus.Paused).ToList(), QueueStatus.Pending);

    [RelayCommand]
    private void RetryAllFailed() => _queue.SetStatus(Items.Where(i => i.Status == QueueStatus.Failed).ToList(), QueueStatus.Pending);

    [RelayCommand]
    private void Pause()
    {
        foreach (var item in Selection.ToList())
        {
            if (item.Status == QueueStatus.Running) _services.Engine.Abort(item);
            else if (item.Status == QueueStatus.Pending) item.Status = QueueStatus.Paused;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var file = await _services.Dialogs.PickSaveFileAsync(Lang.T("Queue speichern", "Save queue"), "queue.json", "*.json");
        if (file is not null) _queue.Save(file);
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        var file = await _services.Dialogs.PickOpenFileAsync(Lang.T("Queue laden", "Load queue"), "*.json");
        if (file is null) return;
        try { _queue.Load(file); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            await _services.Dialogs.ShowMessageAsync(Lang.T("Queue laden", "Load queue"), Lang.T("Datei konnte nicht gelesen werden: ", "File could not be read: ") + ex.Message);
        }
    }
}
