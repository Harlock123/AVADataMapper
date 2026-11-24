using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;

[assembly: AvaloniaTestApplication(typeof(AVADataMapper.Tests.HeadlessApp))]

namespace AVADataMapper.Tests;

/// <summary>The headless Avalonia app the window tests run on: one for the whole run, with Skia so frames can be captured.</summary>
public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    /// <summary>Runs <paramref name="action"/> on the headless UI thread.</summary>
    public static void Run(Action action) =>
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HeadlessApp).Assembly)
            .Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Lets queued UI work run.</summary>
    public static void Settle(int times = 20)
    {
        for (var i = 0; i < times; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(2);
        }
    }
}

/// <summary>Before any test runs, the program's settings folder (saved connections, AI settings) is a temporary one.</summary>
internal static class SettingsIsolation
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Isolate() =>
        AVADataMapper.MainWindow.FolderOverride = Directory.CreateTempSubdirectory("avadatamapper-settings-").FullName;
}
