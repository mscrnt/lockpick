using System.Text.Json;
using Lockpick.Platform.Windows;
using Microsoft.Extensions.Logging;

namespace Lockpick.App.Services;

/// <summary>
/// Per-user locations. Everything Lockpick writes lives under %LOCALAPPDATA%\Lockpick, or for the portable zip in a
/// "data" folder beside it, so each unzipped copy keeps its own settings.
/// </summary>
public static class AppPaths
{
    public static string DataRoot { get; } = DataRootFor(AppContext.BaseDirectory, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>
    /// The portable zip has a ".portable" marker beside Update.exe, with the app itself in "current" (which an update
    /// replaces), so the data folder goes beside the marker.
    /// </summary>
    internal static string DataRootFor(string appDirectory, string localAppData)
    {
        var root = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(appDirectory));
        return root is not null && File.Exists(Path.Combine(root, ".portable"))
            ? Path.Combine(root, "data")
            : Path.Combine(localAppData, "Lockpick");
    }

    public static string Logs => Path.Combine(DataRoot, "logs");

    public static string Recordings => Path.Combine(DataRoot, "recordings");

    public static string Profiles => Path.Combine(DataRoot, "profiles");

    public static string Captures => Path.Combine(DataRoot, "captures");

    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");

    /// <summary>Every trainer action (JSON lines): what was written, from what to what, and the result.</summary>
    public static string TrainerLog => Path.Combine(DataRoot, "trainer", "actions.jsonl");

    /// <summary>The game's display settings file as it was before Lockpick first changed it (kept outside the game folder).</summary>
    public static string DisplayBackup => Path.Combine(DataRoot, "backup", "game-display");

    /// <summary>Every run attempt, a JSON-lines file per split set.</summary>
    public static string Runs => Path.Combine(DataRoot, "runs");

    /// <summary>Secrets (the speedrun.com API key, OBS's WebSocket password), each encrypted for the Windows user (DPAPI); never in settings.</summary>
    public static string Secrets => Path.Combine(DataRoot, "secrets");
}

public sealed record AppSettings
{
    /// <summary>Explicit path to the game executable. When null, the GOG install is located via the registry.</summary>
    public string? GameExecutablePath { get; init; }

    /// <summary>Directory with additional/override version profiles (*.json).</summary>
    public string? ProfileDirectory { get; init; }

    /// <summary>Version of the first-time setup the user has completed (0 = never). See SetupViewModel.CurrentVersion.</summary>
    public int SetupVersion { get; init; }

    /// <summary>Check GitHub releases for updates in the background (installed builds only).</summary>
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>Also offer pre-release versions.</summary>
    public bool IncludePrereleases { get; init; }

    /// <summary>Hotkey that adds a capture marker while the game has focus (a GlobalHotkey.Choices name, or "Off").</summary>
    public string MarkerHotkey { get; init; } = "Ctrl+Shift+M";

    /// <summary>
    /// Controller button that adds a capture marker (GamepadBinding: "L2" = LT / L2, "Off", or a bound button such as
    /// "hid:0810:0001/5"). L2 does nothing in Resident Evil with the PlayStation layout, so a player can press it mid-run.
    /// </summary>
    public string MarkerButton { get; init; } = GamepadBinding.L2;

    /// <summary>
    /// Trainer hotkeys by action (TrainerHotkeys.Actions ids) to a GlobalHotkey.Choices name. Off unless the player sets
    /// one; they act only while Trainer Mode has write access.
    /// </summary>
    public IReadOnlyDictionary<string, string> TrainerHotkeys { get; init; } = new Dictionary<string, string>();

    /// <summary>Controller buttons for the same actions (TrainerHotkeys.Actions ids → GamepadBinding values).</summary>
    public IReadOnlyDictionary<string, string> GamepadActions { get; init; } = new Dictionary<string, string>();

    /// <summary>The route the guide follows (a route id from the adapter), or null for none.</summary>
    public string? RouteId { get; init; }

    /// <summary>Whose buttons the route guide shows (<see cref="ButtonStyles"/>): the pad in use, or one kind always.</summary>
    public string ButtonStyle { get; init; } = ButtonStyles.Auto;

    /// <summary>Draw the route guide's way (the line, the ring, a puzzle's numbers) in the game's own picture.</summary>
    public bool RouteInPicture { get; init; } = true;

    /// <summary>Show the route guide's buttons at the bottom middle of the game's own picture.</summary>
    public bool PromptsInPicture { get; init; } = true;

    /// <summary>Lockpick's API for other tools (docs/API.md): off unless the player turns it on.</summary>
    public bool ApiEnabled { get; init; }

    /// <summary>Listen for other devices on the network too (not just this PC).</summary>
    public bool ApiAllowNetwork { get; init; }

    public int ApiPort { get; init; } = Lockpick.Api.ApiOptions.DefaultPort;

    /// <summary>The API token; made when the API is first turned on, replaced on request.</summary>
    public string? ApiToken { get; init; }

    /// <summary>Let API clients use trainer commands (still only while Trainer Mode is on).</summary>
    public bool ApiAllowTrainer { get; init; }

    /// <summary>Show the overlay over the game window (click-through; never takes focus).</summary>
    public bool OverlayEnabled { get; init; }

    /// <summary>Where the time / HP panel sat before the overlay had several widgets (read as that widget's corner).</summary>
    public string OverlayCorner { get; init; } = OverlayCorners.TopRight;

    /// <summary>Each overlay widget's corner, or "Off" (<see cref="OverlayLayout"/>); missing: the widget's default.</summary>
    public IReadOnlyDictionary<string, string> OverlayWidgetCorners { get; init; } = new Dictionary<string, string>();

    /// <summary>The overlay's widgets may sit in the black bars beside the game's picture (GOG's 4:3 picture in a wider
    /// window): corners and spots are on the whole game window. The route in the picture and the full-screen maps stay
    /// on the picture.</summary>
    public bool OverlayUseBars { get; init; }

    /// <summary>Each widget's size on the overlay, in percent of the usual (widget id → 50–200; missing: 100).</summary>
    public IReadOnlyDictionary<string, int> OverlayWidgetSizes { get; init; } = new Dictionary<string, int>();

    /// <summary>How the overlay's panels look, and what the time/HP widget shows.</summary>
    public OverlayLook OverlayLook { get; init; } = new();

    /// <summary>Where each widget placed by hand sits (widget id → spot), for the corner <see cref="OverlayCorners.Custom"/>.</summary>
    public IReadOnlyDictionary<string, WidgetSpot> OverlayWidgetSpots { get; init; } = new Dictionary<string, WidgetSpot>();

    /// <summary>The stream layout (Stream → OBS → One browser source): each widget ticked or not, where and how large
    /// (widget id → choice; missing: the widget's default, <see cref="StreamLayout"/>).</summary>
    public IReadOnlyDictionary<string, StreamWidgetChoice> StreamLayout { get; init; } = new Dictionary<string, StreamWidgetChoice>();

    /// <summary>How the overlay's map looks and which views its hotkey cycles through.</summary>
    public MapOverlaySettings MapOverlay { get; init; } = new();

    /// <summary>The split set runs are timed against (Runs page), or null: runs off.</summary>
    public string? RunSetId { get; init; }

    /// <summary>LiveSplit files the player imported (read again at start).</summary>
    public IReadOnlyList<string> ImportedSplitFiles { get; init; } = [];

    /// <summary>A loaded save starts an attempt too (practice from a save), not only a new game.</summary>
    public bool RunStartOnLoad { get; init; }

    /// <summary>An attempt from a save is timed on the game's own clock (the save's time included); false: from zero.</summary>
    public bool RunSaveOnGameClock { get; init; } = true;

    /// <summary>Per split set, the split an attempt from a save starts at (set id → index; missing: the first).</summary>
    public IReadOnlyDictionary<string, int> RunSaveStartSplits { get; init; } = new Dictionary<string, int>();

    /// <summary>How Lockpick works with LiveSplit (<see cref="LiveSplitModes"/>): off, follow or drive.</summary>
    public string LiveSplitMode { get; init; } = LiveSplitModes.Off;

    /// <summary>LiveSplit's TCP server port (its default).</summary>
    public int LiveSplitPort { get; init; } = 16834;

    /// <summary>The connection to OBS's WebSocket server and what Lockpick does there (Stream → OBS); its password is a secret.</summary>
    public ObsSettings Obs { get; init; } = new();

    /// <summary>Lockpick's window per monitor setup (the screens' layout → where the window was), #63.</summary>
    public IReadOnlyDictionary<string, WindowPlacement> WindowPlacements { get; init; } = new Dictionary<string, WindowPlacement>();

    /// <summary>Streamer mode: the API token is shown as dots.</summary>
    public bool StreamerMode { get; init; }

    /// <summary>Play a sound when a challenge fails or the player dies (the sounds below).</summary>
    public bool AlertSound { get; init; }

    /// <summary>The sound for a failed challenge: a Windows sound's id, "none", or a .wav file's path.</summary>
    public string FailSound { get; init; } = "critical";

    /// <summary>The sound for a death: as <see cref="FailSound"/>.</summary>
    public string DeathSound { get; init; } = "exclamation";

    /// <summary>What splits are compared against (a LiveSplit comparison name: Personal Best, Best Segments, …).</summary>
    public string RunComparison { get; init; } = "Personal Best";

    /// <summary>Overlay widgets in their own windows (widget id → its window's place and options).</summary>
    public IReadOnlyDictionary<string, PopOutOptions> PopOuts { get; init; } = new Dictionary<string, PopOutOptions>();

    /// <summary>The Stream page's widgets, in order ("id:size", size small / medium / large).</summary>
    public IReadOnlyList<string>? StreamWidgets { get; init; }

    /// <summary>Leaderboard runs: Lockpick locked read-only and the rules checked (Runs → Leaderboard run).</summary>
    public bool LeaderboardMode { get; init; }

    /// <summary>Show speedrun.com's leaderboard on the Runs page (Lockpick goes online only when this is on).</summary>
    public bool SpeedrunComLeaderboard { get; init; }

    /// <summary>The player's speedrun.com name and id (from their API key or their name); never the key itself.</summary>
    public string? SpeedrunComUser { get; init; }

    public string? SpeedrunComUserId { get; init; }

    /// <summary>The leaderboard chosen per split set (set id → "filter=choice;…"), when not the one its name suggests.</summary>
    public IReadOnlyDictionary<string, string> SpeedrunComBoards { get; init; } = new Dictionary<string, string>();

    /// <summary>Set only while ask mode has the game frozen; lets the next start unfreeze it after a crash.</summary>
    public PausedGame? PausedGame { get; init; }
}

public sealed class SettingsStore(string path, ILogger logger)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public AppSettings Current { get; private set; } = new();

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(path))
            {
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read settings from {Path}; using defaults", path);
            Current = new AppSettings();
        }

        return Current;
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        File.Move(temp, path, overwrite: true);
        Current = settings;
        logger.LogInformation("Settings saved to {Path}", path);
    }
}

/// <summary>Where the overlay sits on the game's picture. Not top-left by default: Discord's overlay toast shows there.</summary>
public static class OverlayCorners
{
    public const string TopRight = "Top right";
    public const string BottomRight = "Bottom right";
    public const string BottomLeft = "Bottom left";
    public const string TopLeft = "Top left";

    /// <summary>Centred at the top of the picture (big messages).</summary>
    public const string TopCentre = "Top centre";

    /// <summary>Placed by hand in the layout editor (Settings → Overlay), anywhere on the picture.</summary>
    public const string Custom = "Your spot";

    public static IReadOnlyList<string> All { get; } = [TopRight, BottomRight, BottomLeft, TopLeft, TopCentre];
}

/// <summary>The overlay map's views: what the map hotkey cycles through, in this order.</summary>
public static class MapViews
{
    /// <summary>The minimap in its corner: the area around the player.</summary>
    public const string Around = "around";

    /// <summary>The whole room over the game's picture, on a dark backdrop.</summary>
    public const string Room = "room";

    /// <summary>The whole room over the game's picture, see-through.</summary>
    public const string RoomSeeThrough = "room-see-through";

    /// <summary>Every room of the floor in place, over the game's picture (the map's zoom key moves in on the player).</summary>
    public const string Floor = "floor";

    /// <summary>The same, see-through.</summary>
    public const string FloorSeeThrough = "floor-see-through";

    /// <summary>No map until the key is pressed again.</summary>
    public const string Hidden = "hidden";

    public static IReadOnlyList<(string Id, string Name)> All { get; } =
    [
        (Around, "Minimap (around you)"),
        (Room, "Whole room, full screen"),
        (RoomSeeThrough, "Whole room, full screen, see-through"),
        (Floor, "Whole floor, full screen"),
        (FloorSeeThrough, "Whole floor, full screen, see-through"),
        (Hidden, "Hidden"),
    ];

    /// <summary>Covers the game's picture instead of sitting in a corner.</summary>
    public static bool IsFull(string view) => view is Room or RoomSeeThrough or Floor or FloorSeeThrough;

    /// <summary>Shows the whole floor rather than one room.</summary>
    public static bool IsFloor(string view) => view is Floor or FloorSeeThrough;

    /// <summary>Drawn see-through over the game.</summary>
    public static bool IsSeeThrough(string view) => view is RoomSeeThrough or FloorSeeThrough;
}

/// <summary>The overlay map's options (Settings).</summary>
public sealed record MapOverlaySettings
{
    /// <summary>The views the map hotkey cycles through (<see cref="MapViews"/>); the first is shown at start.</summary>
    public IReadOnlyList<string> Views { get; init; } = [MapViews.Around, MapViews.Room, MapViews.RoomSeeThrough, MapViews.Floor];

    /// <summary>World units from the player to the minimap's nearer edge.</summary>
    public int Reach { get; init; } = 6000;

    /// <summary>The minimap's side, in the overlay's own units (designed for a 480-pixel-high picture).</summary>
    public int Size { get; init; } = 130;

    /// <summary>How strongly the see-through map is drawn over the game, in percent.</summary>
    public int SeeThroughOpacity { get; init; } = 70;

    /// <summary>Names of doors, items and people on the full-screen map.</summary>
    public bool Names { get; init; } = true;

    public static IReadOnlyList<(int Value, string Name)> ReachChoices { get; } = [(4000, "Close"), (6000, "Medium"), (9000, "Far")];

    public static IReadOnlyList<(int Value, string Name)> SizeChoices { get; } = [(100, "Small"), (130, "Medium"), (170, "Large")];
}

/// <summary>The overlay's look (#55 follow-up): panel transparency, text colour, font, and the time/HP widget's lines.</summary>
public sealed record OverlayLook
{
    /// <summary>How solid the panels' dark background is, in percent (0: text only, over the game).</summary>
    public int BackgroundOpacity { get; init; } = 78;

    /// <summary>A <see cref="TextColors"/> id.</summary>
    public string TextColor { get; init; } = "default";

    /// <summary>A <see cref="Fonts"/> id.</summary>
    public string Font { get; init; } = "default";

    public bool ShowTime { get; init; } = true;

    public bool ShowHealth { get; init; } = true;

    public bool ShowRoom { get; init; } = true;

    public bool ShowChallenge { get; init; } = true;

    public static IReadOnlyList<(string Id, string Name, string Color)> TextColors { get; } =
    [
        ("default", "Light (Lockpick)", "#DFE8E3"), ("white", "White", "#FFFFFF"), ("green", "Green", "#4EC36D"),
        ("amber", "Amber", "#E3B341"), ("cyan", "Cyan", "#5BC8E0"), ("pink", "Pink", "#F07AB8"),
    ];

    /// <summary>Font ids → the app resource holding the family (null: the app's own font).</summary>
    public static IReadOnlyList<(string Id, string Name, string? Resource)> Fonts { get; } =
    [
        ("default", "Lockpick (default)", null), ("mono", "Monospace", "LpMono"), ("typewriter", "Typewriter", "LpWordmark"),
    ];
}

/// <summary>Where Lockpick's window was, in physical pixels, and whether it was maximized.</summary>
public sealed record WindowPlacement(int X, int Y, double Width, double Height, bool Maximized);

/// <summary>A pop-out widget window: open, where, how big, and its background and stacking.</summary>
public sealed record PopOutOptions
{
    public bool Open { get; init; }

    public int? X { get; init; }

    public int? Y { get; init; }

    public double Width { get; init; } = 320;

    public double Height { get; init; } = 260;

    /// <summary>A green background, for OBS's chroma key (else dark).</summary>
    public bool Green { get; init; }

    public bool Topmost { get; init; }

    /// <summary>The map window's view (<see cref="PopOutMapViews"/>).</summary>
    public string MapView { get; init; } = PopOutMapViews.Around;

    /// <summary>The map window's reach around the player, in world units.</summary>
    public int MapReach { get; init; } = 6000;

    /// <summary>The map window's floor zoom (0 = the whole floor).</summary>
    public int MapZoom { get; init; }

    public bool MapNames { get; init; } = true;
}

/// <summary>What the map's own window shows.</summary>
public static class PopOutMapViews
{
    public const string Around = "around";
    public const string Room = "room";
    public const string Floor = "floor";
}

/// <summary>The overlay's widgets and where each sits. Widgets sharing a corner stack in this order from the corner.</summary>
/// <summary>Which buttons prompts show.</summary>
public static class ButtonStyles
{
    public const string Auto = "auto";
    public const string Xbox = "xbox";
    public const string PlayStation = "playstation";
    public const string Keyboard = "keyboard";

    public static IReadOnlyList<(string Id, string Name)> All { get; } =
    [
        (Auto, "The controller in use (keyboard until one is pressed)"),
        (Xbox, "Xbox"),
        (PlayStation, "PlayStation"),
        (Keyboard, "Keyboard"),
    ];
}

/// <summary>One widget in the stream layout: shown or not, its place (<see cref="OverlayCorners.All"/>) and size in percent.</summary>
public sealed record StreamWidgetChoice(bool Shown, string Corner, int Size);

/// <summary>
/// The stream layout: every widget the player ticks, in one OBS Browser Source (/overlay/stream), each in its place
/// (widgets in the same place stacked, in this list's order) at its own size times the size chosen. Separate from the
/// overlay on the game, so the stream can show more (or less) than the player sees.
/// </summary>
public static class StreamLayout
{
    /// <summary>The sizes a widget can have, in percent of the overlay's usual size.</summary>
    public static IReadOnlyList<int> Sizes { get; } = [50, 75, 100, 125, 150, 200, 250, 300, 400];

    /// <summary>The widgets in the layout and how they start: what a runner's stream shows most, in their own corners.</summary>
    public static IReadOnlyList<(string Id, string Name, StreamWidgetChoice Default)> Widgets { get; } =
    [
        (OverlayLayout.Status, "Time, HP, room, challenge", new(true, OverlayCorners.TopRight, 150)),
        (OverlayLayout.Splits, "Run splits", new(true, OverlayCorners.TopLeft, 150)),
        (OverlayLayout.Route, "Route guide (the next step and its buttons)", new(false, OverlayCorners.TopLeft, 150)),
        (OverlayLayout.Banner, "Big messages (challenge failed, died, run finished)", new(true, OverlayCorners.TopCentre, 150)),
        (OverlayLayout.Enemies, "Enemies in the room (HP)", new(false, OverlayCorners.BottomLeft, 150)),
        (OverlayLayout.Feed, "What just happened", new(true, OverlayCorners.BottomLeft, 150)),
        (OverlayLayout.Minimap, "Minimap", new(true, OverlayCorners.BottomRight, 150)),
    ];

    public static StreamWidgetChoice Of(AppSettings settings, string id)
    {
        var fallback = Widgets.FirstOrDefault(w => w.Id == id).Default ?? new(false, OverlayCorners.TopRight, 100);
        return settings.StreamLayout.TryGetValue(id, out var choice) && choice is not null
            ? choice with
            {
                Corner = OverlayCorners.All.Contains(choice.Corner) ? choice.Corner : fallback.Corner,
                Size = Math.Clamp(choice.Size, 25, 600),
            }
            : fallback;
    }

    /// <summary>The ticked widgets for the browser source, in stacking order.</summary>
    public static IReadOnlyList<Lockpick.Api.StreamPlacement> Placements(AppSettings settings) =>
    [
        .. Widgets.Select(w => (w.Id, Choice: Of(settings, w.Id))).Where(w => w.Choice.Shown)
            .Select(w => new Lockpick.Api.StreamPlacement(w.Id, Place(w.Choice.Corner), w.Choice.Size / 100.0)),
    ];

    private static string Place(string corner) => corner switch
    {
        OverlayCorners.TopLeft => "top-left",
        OverlayCorners.TopCentre => "top-centre",
        OverlayCorners.BottomLeft => "bottom-left",
        OverlayCorners.BottomRight => "bottom-right",
        _ => "top-right",
    };
}

public static class OverlayLayout
{
    public const string Off = "Off";
    public const string Status = "status";
    public const string Minimap = "minimap";
    public const string Enemies = "enemies";
    public const string Splits = "splits";
    public const string Feed = "feed";
    public const string Banner = "banner";
    public const string Route = "route";
    public const string RoutePicture = "route-picture";

    public static IReadOnlyList<(string Id, string Name, string Default)> Widgets { get; } =
    [
        (Status, "Time, HP, room, challenge", OverlayCorners.TopRight),
        (Minimap, "Minimap", OverlayCorners.BottomRight),
        (Enemies, "Enemies in the room (HP)", OverlayCorners.BottomLeft),
        (Splits, "Run splits (LiveSplit's look)", OverlayCorners.TopRight),
        (Feed, "What just happened", OverlayCorners.BottomLeft),
        (Banner, "Big messages (challenge failed, died, run finished)", OverlayCorners.TopCentre),
        (Route, "Route guide (the next step and its buttons)", OverlayCorners.TopLeft),
        (RoutePicture, "Route guide in the game picture (the way on the floor, the buttons)", OverlayCorners.TopLeft),
    ];

    /// <summary>A corner, or off.</summary>
    public static IReadOnlyList<string> Choices { get; } = [.. OverlayCorners.All, OverlayCorners.Custom, Off];

    /// <summary>The sizes a widget can have, in percent.</summary>
    public static IReadOnlyList<int> Sizes { get; } = [50, 75, 100, 125, 150, 200];

    /// <summary>The widget belongs on the game's picture even when widgets may use the black bars: it is drawn to line
    /// up with the picture (the route on the floor) or covers it (a full-screen map view).</summary>
    public static bool OnPicture(string id, bool full) => full || id == RoutePicture;

    /// <summary>The widget's size factor on the overlay (1 = the usual).</summary>
    public static double SizeOf(AppSettings settings, string id) =>
        Math.Clamp(settings.OverlayWidgetSizes.GetValueOrDefault(id, 100), 25, 400) / 100.0;

    public static string Corner(AppSettings settings, string id) =>
        settings.OverlayWidgetCorners.TryGetValue(id, out var corner) && Choices.Contains(corner) ? corner
        : id == Status && OverlayCorners.All.Contains(settings.OverlayCorner) ? settings.OverlayCorner
        : Widgets.FirstOrDefault(w => w.Id == id).Default ?? Off;
}
