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
    public static AppServices Services { get; internal set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        CoreDispatcher.Post = action => Dispatcher.UIThread.Post(action);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Services = new AppServices();
            ApplyTheme(Services.Settings.Current.Theme);
            ApplyLanguage();
            ApplyFont(Services.Settings.Current.FontFamily, Services.Settings.Current.ListFontSize);

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

    /// <summary>Applies the configured font to the whole UI and the list font size; fallbacks keep Linux readable.</summary>
    public static void ApplyFont(string family, double listSize)
    {
        if (Current is null) return;
        Current.Resources["ContentControlThemeFontFamily"] = FontWithFallbacks(family);
        var size = Math.Clamp(listSize, 8, 32);
        Current.Resources["ListFontSize"] = size;
        Current.Resources["ListRowHeight"] = Math.Round(size + 10);
    }

    /// <summary>Font family with monospace fallbacks, so a font missing on this system (e.g. Consolas on Linux) never fails.</summary>
    public static Avalonia.Media.FontFamily FontWithFallbacks(string? family)
    {
        var name = string.IsNullOrWhiteSpace(family) ? "Consolas" : family.Trim();
        return new Avalonia.Media.FontFamily($"{name},Consolas,DejaVu Sans Mono,Liberation Mono,Menlo,monospace");
    }

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
