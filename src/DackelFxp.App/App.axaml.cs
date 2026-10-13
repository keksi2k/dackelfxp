using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using DackelFxp.App.Services;
using DackelFxp.App.ViewModels;
using DackelFxp.App.Views;
using DackelFxp.Core.Infrastructure;
using DackelFxp.Ftp;
using Avalonia.Controls;

namespace DackelFxp.App;

public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        CoreDispatcher.Post = action => Dispatcher.UIThread.Post(action);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Services = new AppServices();
            ApplyTheme(Services.Settings.Current.Theme);
            ApplyLanguage();

            var window = new MainWindow();
            Services.Dialogs.Owner = window;
            var vm = new MainWindowViewModel(Services);
            window.DataContext = vm;
            desktop.MainWindow = window;
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            desktop.ShutdownRequested += (_, _) => Services.Shutdown();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static ResourceDictionary? _texts;

    /// <summary>Publishes the UI texts of the current language as resources (XAML uses DynamicResource, so it updates live).</summary>
    public static void ApplyLanguage()
    {
        if (Current is null) return;
        var texts = new ResourceDictionary();
        foreach (var (key, (de, en)) in UiText.Entries) texts[key] = Lang.German ? de : en;
        var merged = Current.Resources.MergedDictionaries;
        if (_texts is not null) merged.Remove(_texts);
        merged.Add(texts);
        _texts = texts;
    }

    public static void ApplyTheme(string theme)
    {
        if (Current is null) return;
        Current.RequestedThemeVariant = theme switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
