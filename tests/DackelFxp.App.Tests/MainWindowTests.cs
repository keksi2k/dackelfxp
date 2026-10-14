using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DackelFxp.App.Services;
using DackelFxp.App.ViewModels;
using DackelFxp.App.Views;
using DackelFxp.Core.Queue;
using DackelFxp.Core.Sites;
using DackelFxp.Ftp;
using DackelFxp.Ftp.Tests;

[assembly: AvaloniaTestApplication(typeof(DackelFxp.App.Tests.TestAppBuilder))]

namespace DackelFxp.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>Main window with a fake FTP site on the left and a local temp folder on the right.</summary>
public sealed class UiFixture : IAsyncDisposable
{
    private UiFixture(FakeFtpServer server, string root, AppServices services, MainWindow window, MainWindowViewModel vm)
    {
        Server = server;
        Root = root;
        Services = services;
        Window = window;
        Vm = vm;
    }

    public FakeFtpServer Server { get; }
    public string Root { get; }
    public string LocalDir => Path.Combine(Root, "local");
    public AppServices Services { get; }
    public MainWindow Window { get; }
    public MainWindowViewModel Vm { get; }

    public static async Task<UiFixture> CreateAsync(Action<FakeFtpServer>? setup = null, Action<AppServices>? configure = null)
    {
        var server = new FakeFtpServer();
        server.Directories["/incoming"] = true;
        server.Directories["/incoming/Release-GRP"] = true;
        server.Files["/incoming/Release-GRP/a.rar"] = new byte[2000];
        server.Files["/incoming/b.nfo"] = [1, 2, 3];
        setup?.Invoke(server);

        var root = Directory.CreateTempSubdirectory("dackel-ui").FullName;
        Directory.CreateDirectory(Path.Combine(root, "local"));
        Environment.SetEnvironmentVariable("DACKELFXP_CONFIG", Path.Combine(root, "config"));
        var services = new AppServices();
        App.Services = services;
        App.ApplyLanguage();
        services.Settings.Current.LastLocalPath = Path.Combine(root, "local");
        configure?.Invoke(services);
        App.ApplyFont(services.Settings.Current.FontFamily, services.Settings.Current.ListFontSize);

        var site = new Site { Name = "Fake", Host = "127.0.0.1", Port = server.Port, TlsMode = FtpTlsMode.None, RemotePath = "/incoming" };
        services.Sites.AddOrUpdate(site);

        var vm = new MainWindowViewModel(services);
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        window.Show();
        await WaitUntilAsync(() => vm.Right.CurrentPath.Length > 0);
        await vm.Left.ConnectAsync(site);
        await WaitUntilAsync(() => vm.Left.Items.Count > 0);
        return new UiFixture(server, root, services, window, vm);
    }

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeoutMs) throw new TimeoutException("Bedingung nicht erfüllt");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        Dispatcher.UIThread.RunJobs();
    }

    public BrowserPaneView Pane(bool left) =>
        Window.GetVisualDescendants().OfType<BrowserPaneView>().Single(p => p.DataContext == (left ? Vm.Left : Vm.Right));

    /// <summary>Center of the row showing <paramref name="name"/>, in window coordinates.</summary>
    public Point RowPoint(bool left, string name)
    {
        Window.UpdateLayout();
        var row = Pane(left).GetVisualDescendants().OfType<DataGridRow>()
            .First(r => r.DataContext is FileEntryViewModel e && e.Name == name);
        return row.TranslatePoint(new Point(40, row.Bounds.Height / 2), Window)!.Value;
    }

    public Point PaneCenter(bool left)
    {
        var pane = Pane(left);
        return pane.TranslatePoint(new Point(pane.Bounds.Width / 2, pane.Bounds.Height / 2), Window)!.Value;
    }

    /// <summary>Real mouse drag: press, move in steps, release.</summary>
    public void Drag(Point from, Point to, Func<bool>? checkWhileOver = null, List<bool>? observed = null)
    {
        Window.MouseMove(from);
        Window.MouseDown(from, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        for (var i = 1; i <= 10; i++)
        {
            Window.MouseMove(new Point(from.X + (to.X - from.X) * i / 10, from.Y + (to.Y - from.Y) * i / 10), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
        }
        if (checkWhileOver is not null) observed?.Add(checkWhileOver());
        Window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    public async ValueTask DisposeAsync()
    {
        await Services.Engine.StopAsync();
        Window.Close();
        await Services.Connections.DisposeAsync();
        await Server.DisposeAsync();
        try { Directory.Delete(Root, true); } catch (IOException) { }
    }
}

public class MainWindowTests
{
    [AvaloniaFact]
    public async Task Drag_Remote_Folder_To_Local_Pane_Downloads_It()
    {
        await using var ui = await UiFixture.CreateAsync();
        var observed = new List<bool>();

        ui.Drag(ui.RowPoint(left: true, "Release-GRP"), ui.PaneCenter(left: false), () => ui.Pane(left: false).IsDropHighlighted, observed);

        Assert.Equal([true], observed);
        Assert.False(ui.Pane(left: false).IsDropHighlighted);
        var item = Assert.Single(ui.Services.Queue.Snapshot(), i => i.ParentId is null);
        Assert.Equal(TransferKind.Download, item.Kind);
        await UiFixture.WaitUntilAsync(() => File.Exists(Path.Combine(ui.LocalDir, "Release-GRP", "a.rar")));
        Assert.Equal(2000, new FileInfo(Path.Combine(ui.LocalDir, "Release-GRP", "a.rar")).Length);
    }

    [AvaloniaFact]
    public async Task Drag_Local_File_To_Remote_Pane_Uploads_It()
    {
        await using var ui = await UiFixture.CreateAsync();
        await File.WriteAllBytesAsync(Path.Combine(ui.LocalDir, "up.bin"), new byte[1234], TestContext.Current.CancellationToken);
        await ui.Vm.Right.RefreshAsync();
        await UiFixture.WaitUntilAsync(() => ui.Vm.Right.Items.Any(i => i.Name == "up.bin"));

        ui.Drag(ui.RowPoint(left: false, "up.bin"), ui.PaneCenter(left: true));

        await UiFixture.WaitUntilAsync(() => ui.Server.Files.ContainsKey("/incoming/up.bin"));
        Assert.Equal(1234, ui.Server.Files["/incoming/up.bin"].Length);
    }

    [AvaloniaFact]
    public async Task Drag_Between_Two_Sites_Queues_Fxp()
    {
        await using var other = new FakeFtpServer();
        await using var ui = await UiFixture.CreateAsync();
        var site2 = new Site { Name = "Fake2", Host = "127.0.0.1", Port = other.Port, TlsMode = FtpTlsMode.None };
        ui.Services.Sites.AddOrUpdate(site2);
        await ui.Vm.Right.ConnectAsync(site2);

        ui.Drag(ui.RowPoint(left: true, "b.nfo"), ui.PaneCenter(left: false));

        await UiFixture.WaitUntilAsync(() => other.Files.ContainsKey("/b.nfo"));
        Assert.Equal(TransferKind.Fxp, ui.Services.Queue.Snapshot().Single().Kind);
        Assert.Equal([1, 2, 3], other.Files["/b.nfo"]);
    }

    [AvaloniaFact]
    public async Task Drop_On_Own_Pane_Does_Nothing()
    {
        await using var ui = await UiFixture.CreateAsync();
        var from = ui.RowPoint(left: true, "b.nfo");
        ui.Drag(from, new Point(from.X + 30, from.Y + 120));
        Assert.Empty(ui.Services.Queue.Snapshot());
    }

    [AvaloniaFact]
    public async Task Middle_Click_Goes_Up_One_Folder()
    {
        await using var ui = await UiFixture.CreateAsync();
        Assert.Equal("/incoming", ui.Vm.Left.CurrentPath);

        var point = ui.PaneCenter(left: true);
        ui.Window.MouseDown(point, MouseButton.Middle);
        ui.Window.MouseUp(point, MouseButton.Middle);

        await UiFixture.WaitUntilAsync(() => ui.Vm.Left.CurrentPath == "/");
    }

    [AvaloniaFact]
    public async Task Back_Button_Returns_To_Previous_Folder()
    {
        await using var ui = await UiFixture.CreateAsync();
        await ui.Vm.Left.NavigateAsync("/incoming/Release-GRP");
        var point = ui.PaneCenter(left: true);
        ui.Window.MouseDown(point, MouseButton.XButton1);
        ui.Window.MouseUp(point, MouseButton.XButton1);
        await UiFixture.WaitUntilAsync(() => ui.Vm.Left.CurrentPath == "/incoming");
    }

    [AvaloniaFact]
    public async Task Links_Are_Sorted_On_Top_When_Enabled()
    {
        await using var ui = await UiFixture.CreateAsync(server => server.Links["/incoming/today"] = "/incoming/Release-GRP");
        // Default: folders first, link among the files (it is not a folder).
        Assert.Equal(["Release-GRP", "b.nfo", "today"], ui.Vm.Left.Items.Select(i => i.Name));

        ui.Services.Settings.Current.SortLinksOnTop = true;
        FileEntryViewModel.SortLinksOnTop = true;
        ui.Vm.Left.Resort();
        Assert.Equal(["today", "Release-GRP", "b.nfo"], ui.Vm.Left.Items.Select(i => i.Name));
        FileEntryViewModel.SortLinksOnTop = false;
    }

    [AvaloniaFact]
    public async Task Opening_A_Remote_Link_Navigates_Into_It()
    {
        await using var ui = await UiFixture.CreateAsync(server => server.Links["/incoming/today"] = "/incoming/Release-GRP");
        var link = ui.Vm.Left.Items.Single(i => i.Name == "today");
        await ui.Vm.Left.OpenAsync(link);
        Assert.Equal("/incoming/Release-GRP", ui.Vm.Left.CurrentPath);
        Assert.Contains(ui.Vm.Left.Items, i => i.Name == "a.rar");
    }

    [AvaloniaFact]
    public async Task Font_Setting_Is_Applied_To_Resources()
    {
        await using var ui = await UiFixture.CreateAsync(configure: s =>
        {
            s.Settings.Current.FontFamily = "Courier New";
            s.Settings.Current.ListFontSize = 16;
        });
        var family = (Avalonia.Media.FontFamily)Application.Current!.Resources["ContentControlThemeFontFamily"]!;
        Assert.Contains("Courier New", family.ToString());
        Assert.Equal(16d, Application.Current.Resources["ListFontSize"]);

        var grid = ui.Pane(left: true).GetVisualDescendants().OfType<DataGrid>().First();
        Assert.Equal(16d, grid.FontSize);
    }

    /// <summary>Renders screenshots for manual review; only runs with DACKELFXP_SHOTS=folder.</summary>
    [AvaloniaFact]
#pragma warning disable CS0618 // Bitmap.Save(string): simple PNG output is enough here
    public async Task Screenshots()
    {
        var folder = Environment.GetEnvironmentVariable("DACKELFXP_SHOTS");
        Assert.SkipWhen(folder is null, "DACKELFXP_SHOTS nicht gesetzt");
        await using var ui = await UiFixture.CreateAsync(server => server.Links["/incoming/today"] = "/incoming/Release-GRP",
            s => s.Settings.Current.SortLinksOnTop = true);
        FileEntryViewModel.SortLinksOnTop = true;
        ui.Vm.Left.Resort();
        Dispatcher.UIThread.RunJobs();
        ui.Window.CaptureRenderedFrame()?.Save(Path.Combine(folder!, "main.png"));
        FileEntryViewModel.SortLinksOnTop = false;

        var settings = new DackelFxp.App.Views.Dialogs.SettingsWindow { DataContext = new SettingsViewModel(ui.Services), Width = 900, Height = 620 };
        settings.Show();
        Dispatcher.UIThread.RunJobs();
        settings.CaptureRenderedFrame()?.Save(Path.Combine(folder!, "settings.png"));
        settings.Close();
    }
}
