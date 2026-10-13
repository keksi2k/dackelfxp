using Avalonia;

namespace DackelFxp.App;

internal static class Program
{
    public static string[] Args { get; private set; } = [];

    /// <summary>Value of a "--name value" command line option.</summary>
    public static string? Option(string name)
    {
        var idx = Array.FindIndex(Args, a => a.Equals("--" + name, StringComparison.OrdinalIgnoreCase));
        return idx >= 0 && idx + 1 < Args.Length ? Args[idx + 1] : null;
    }

    [STAThread]
    public static void Main(string[] args)
    {
        Args = args;
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        if (Environment.GetEnvironmentVariable("DACKELFXP_TRACE") is { Length: > 0 })
        {
            System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(Console.Out));
            System.Diagnostics.Trace.AutoFlush = true;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace(Avalonia.Logging.LogEventLevel.Warning);
}
