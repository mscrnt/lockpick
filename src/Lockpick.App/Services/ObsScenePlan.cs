using System.Globalization;

namespace Lockpick.App.Services;

/// <summary>
/// The Lockpick scene's sources, as the OBS script's "Add Lockpick to this scene" makes them (the same names, so either
/// finds the other's): a Game Capture of the game when OBS is on this PC, and each widget as a browser source through
/// Lockpick's API. For OBS on another PC the widgets' address is this PC's on the network, which needs the API open to
/// the network; the game there comes from a capture card, so no Game Capture is added.
/// </summary>
public static class ObsScenePlan
{
    public static IReadOnlyList<ObsSource> Sources(ObsCanvas canvas, AppSettings settings, string executable, bool liveSplit)
    {
        if (!settings.ApiEnabled || string.IsNullOrEmpty(settings.ApiToken))
        {
            throw new ObsException("Turn on Lockpick's API first (Settings → API and profiles): OBS shows the widgets through it.");
        }

        string host;
        if (canvas.ThisPc)
        {
            host = "127.0.0.1";
        }
        else if (settings.ApiAllowNetwork && canvas.LocalAddress is { } address)
        {
            host = address.Contains(':', StringComparison.Ordinal) ? $"[{address}]" : address;
        }
        else
        {
            throw new ObsException("OBS is on another PC: allow other devices on your network in Lockpick's API settings (Settings → API and profiles) so it can show the widgets.");
        }

        var (w, h) = (canvas.Width, canvas.Height);
        var root = $"http://{host}:{settings.ApiPort.ToString(CultureInfo.InvariantCulture)}/overlay/";
        var token = "token=" + Uri.EscapeDataString(settings.ApiToken);
        string Widget(string id, int width, int height, string align = "center") =>
            string.Create(CultureInfo.InvariantCulture, $"{root}widget?id={id}&{token}&width={width}&height={height}&align={align}");

        var sources = new List<ObsSource>();
        if (canvas.ThisPc)
        {
            sources.Add(new("Resident Evil (game)", "game_capture", new Dictionary<string, object>
            {
                ["capture_mode"] = "window",
                ["window"] = $"Resident Evil:RESIDENT EVIL:{executable}",
                ["priority"] = 2, // match the executable (the window's title differs between versions)
            }, 0, 0, FillCanvas: true));
        }

        // Widgets in the right-hand corners sit against the right edge of their box; big messages centred.
        sources.Add(Browser("Lockpick: time, HP, room", Widget("status", 420, 160, "right"), 420, 160, w - 440, 20));
        sources.Add(Browser("Lockpick: minimap", Widget("minimap", 360, 360, "right") + "&valign=bottom", 360, 360, w - 380, h - 380));
        sources.Add(Browser("Lockpick: enemies", Widget("enemies", 320, 240, "right") + "&valign=bottom", 320, 240, w - 760, h - 260));
        sources.Add(liveSplit
            ? Browser("Lockpick: splits", $"{root}livesplit?{token}&width=300&height=500", 300, 500, 20, 20)
            : Browser("Lockpick: splits", $"{root}splits?{token}", w, h, 0, 0));
        sources.Add(Browser("Lockpick: what just happened", $"{root}feed?{token}", w, h, 0, 0));
        sources.Add(Browser("Lockpick: big messages", Widget("banner", 900, 160), 900, 160, (w - 900) / 2, 40));
        return sources;
    }

    private static ObsSource Browser(string name, string url, int width, int height, double x, double y) =>
        new(name, "browser_source", new Dictionary<string, object> { ["url"] = url, ["width"] = width, ["height"] = height, ["shutdown"] = false }, x, y);
}
