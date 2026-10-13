using DackelFxp.Core.Infrastructure;
using DackelFxp.Core.Rules;

namespace DackelFxp.Core.Settings;

public enum ScheduleAction { StartQueue, StopQueue }

public enum CompletionAction { None, DisconnectAll, Exit }

public sealed class ScheduleEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    public ScheduleAction Action { get; set; } = ScheduleAction.StartQueue;
    /// <summary>Time of day (local).</summary>
    public TimeOnly Time { get; set; } = new(3, 0);
    /// <summary>Empty = only once on <see cref="Date"/>.</summary>
    public List<DayOfWeek> Days { get; set; } = [];
    public DateOnly? Date { get; set; }
    /// <summary>Optional queue file to load before starting.</summary>
    public string? QueueFile { get; set; }
    public DateTimeOffset? LastRun { get; set; }
}

/// <summary>A user-defined command sequence. Placeholders: %f file, %d directory, %p full path, %s site name.</summary>
public sealed class CustomCommand
{
    public string Name { get; set; } = "";
    /// <summary>One raw command per line, e.g. "SITE NUKE %p 3 bad".</summary>
    public string Commands { get; set; } = "";
    /// <summary>Null = all sites.</summary>
    public Guid? SiteId { get; set; }
    public bool AskForParameter { get; set; }

    public IEnumerable<string> Expand(string? directory, string? file, string siteName, string? parameter = null)
    {
        var path = directory is null ? file : (file is null ? directory : DackelFxp.Ftp.Protocol.FtpPath.Combine(directory, file));
        foreach (var line in Commands.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return line
                .Replace("%f", file ?? "")
                .Replace("%d", directory ?? "")
                .Replace("%p", path ?? "")
                .Replace("%s", siteName)
                .Replace("%1", parameter ?? "");
        }
    }
}

public sealed class AppSettings
{
    public int TransferSlots { get; set; } = 2;
    /// <summary>KB/s, 0 = unlimited.</summary>
    public int GlobalSpeedLimitKb { get; set; }
    public FileExistsPolicy FileExists { get; set; } = new();
    public bool TransferFilesBeforeFolders { get; set; } = true;
    public bool PreserveTimestamps { get; set; } = true;
    public bool VerifySfvAfterDownload { get; set; }
    public int IdleDisconnectMinutes { get; set; } = 10;
    public CompletionAction OnQueueComplete { get; set; } = CompletionAction.None;

    public string DefaultSkipList { get; set; } = "Standard";
    public List<SkipList> SkipLists { get; set; } = [SkipList.CreateDefault()];
    public List<CustomCommand> CustomCommands { get; set; } =
    [
        new() { Name = "Site Help", Commands = "SITE HELP" },
        new() { Name = "Who", Commands = "SITE WHO" },
    ];
    public List<ScheduleEntry> Schedules { get; set; } = [];

    public bool IdentEnabled { get; set; }
    public int IdentPort { get; set; } = 113;
    public string IdentUser { get; set; } = "dackel";

    public string Theme { get; set; } = "Dark";
    public string LastLocalPath { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public double WindowWidth { get; set; } = 1400;
    public double WindowHeight { get; set; } = 900;
    public bool WindowMaximized { get; set; }
    public List<string> QuickConnectHistory { get; set; } = [];

    public SkipList? FindSkipList(string? name) =>
        SkipLists.FirstOrDefault(s => s.Name == (name ?? DefaultSkipList)) ?? SkipLists.FirstOrDefault();
}

public sealed class SettingsStore(AppPaths paths)
{
    public AppSettings Current { get; private set; } = new();

    public event Action? Changed;

    public void Load() => Current = Json.Load<AppSettings>(paths.SettingsFile) ?? new AppSettings();

    public void Save()
    {
        Json.Save(paths.SettingsFile, Current);
        Changed?.Invoke();
    }
}
