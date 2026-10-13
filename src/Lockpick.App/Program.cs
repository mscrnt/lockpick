using Avalonia;
using Velopack;

namespace Lockpick.App;

internal static class Program
{
    // Usage: Lockpick.exe [--replay <recording.jsonl>] [--verbose]
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles Velopack install/update/uninstall hooks and exits early when invoked by them.
        VelopackApp.Build().Run();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
