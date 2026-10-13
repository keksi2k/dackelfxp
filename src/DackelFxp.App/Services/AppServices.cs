using DackelFxp.Core.Connections;
using DackelFxp.Core.Infrastructure;
using DackelFxp.Core.Queue;
using DackelFxp.Core.Settings;
using DackelFxp.Core.Sites;
using DackelFxp.Core.Tools;
using DackelFxp.Ftp;

namespace DackelFxp.App.Services;

/// <summary>Composition root: creates and owns all long-lived services.</summary>
public sealed class AppServices
{
    public AppServices()
    {
        // --config <dir> or DACKELFXP_CONFIG: own config directory (portable mode, tests).
        Paths = new AppPaths(Program.Option("config") ?? Environment.GetEnvironmentVariable("DACKELFXP_CONFIG"));
        Settings = new SettingsStore(Paths);
        Settings.Load();
        if (Settings.Current.Language.Length == 0)
            Settings.Current.Language = "de";
        Lang.Code = Settings.Current.Language;
        Sites = new SiteStore(Paths);
        Sites.Load();
        Dialogs = new DialogService();
        Connections = new ConnectionManager(Sites, Settings, new UiCertificatePrompt(Dialogs));
        Queue = new TransferQueue();
        Engine = new TransferEngine(Queue, Connections, Sites, Settings) { ExistsPrompt = new UiExistsPrompt(Dialogs) };
        Scheduler = new Scheduler(Settings);
        Log = new LogStore(Connections);
        ApplyIdent();
    }

    public AppPaths Paths { get; }
    public SettingsStore Settings { get; }
    public SiteStore Sites { get; }
    public DialogService Dialogs { get; }
    public ConnectionManager Connections { get; }
    public TransferQueue Queue { get; }
    public TransferEngine Engine { get; }
    public Scheduler Scheduler { get; }
    public LogStore Log { get; }
    public IdentServer? Ident { get; private set; }

    /// <summary>Starts/stops the ident responder according to the settings.</summary>
    public void ApplyIdent()
    {
        var s = Settings.Current;
        if (Ident is not null && (!s.IdentEnabled || Ident.Port != s.IdentPort))
        {
            _ = Ident.DisposeAsync().AsTask();
            Ident = null;
        }
        if (s.IdentEnabled && Ident is null)
        {
            try
            {
                Ident = new IdentServer(s.IdentPort, s.IdentUser);
                Ident.Log += text => Log.AddInfo("Ident", text);
            }
            catch (System.Net.Sockets.SocketException ex)
            {
                var hint = OperatingSystem.IsLinux() && s.IdentPort < 1024
                    ? Lang.T(" (Ports unter 1024 brauchen unter Linux Root oder 'setcap cap_net_bind_service')", " (ports below 1024 need root or 'setcap cap_net_bind_service' on Linux)")
                    : "";
                Log.AddInfo("Ident", Lang.T($"Ident-Server konnte nicht starten: {ex.Message}{hint}", $"Ident server could not start: {ex.Message}{hint}"));
            }
        }
        if (Ident is not null) Ident.User = s.IdentUser;
    }

    public void Shutdown()
    {
        try { Queue.Save(Paths.QueueFile); } catch (IOException) { }
        try { Settings.Save(); } catch (IOException) { }
        var tasks = new List<Task> { Engine.StopAsync(), Connections.DisposeAsync().AsTask(), Scheduler.DisposeAsync().AsTask() };
        if (Ident is not null) tasks.Add(Ident.DisposeAsync().AsTask());
        Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(3));
    }
}
