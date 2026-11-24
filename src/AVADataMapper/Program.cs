using Avalonia;

namespace AVADataMapper;

internal static class Program
{
    /// <summary>Starts the window, opening a mapping (.dmap) if one is given.</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
