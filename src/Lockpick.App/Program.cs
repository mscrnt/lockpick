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
            // A plain redirection surface rather than DirectComposition: OBS's Window Capture (BitBlt) sees the window
            // instead of a blank frame, so Lockpick can be shown on stream. The overlay has its own layered windows.
            .With(new Win32PlatformOptions { CompositionMode = [Win32CompositionMode.RedirectionSurface] })
            .WithInterFont()
            .LogToTrace();
}
