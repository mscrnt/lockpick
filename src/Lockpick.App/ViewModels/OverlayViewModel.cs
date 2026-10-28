using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Lockpick.App.Controls;
using Lockpick.App.Services;
using Lockpick.Core.Challenges;
using Lockpick.Core.Events;
using Lockpick.Core.Runs;
using Lockpick.Core.State;

namespace Lockpick.App.ViewModels;

/// <summary>
/// What the in-game overlay shows: the in-game time, HP, the room and the challenge's state (ROADMAP 0.3.0 sprint 2,
/// #54/#55). Widgets come and go with their data; nothing here can change the game.
/// </summary>
public sealed partial class OverlayViewModel : ObservableObject
{
    /// <summary>Gap between a widget and the picture's edge, in the overlay's own units.</summary>
    public const double Margin = 6;

    private static readonly IBrush PoisonBrush = new SolidColorBrush(Color.Parse("#B07BE0"));
    private static readonly IBrush Backdrop = new SolidColorBrush(Color.Parse("#E0060807"));
    private static readonly IBrush Floor = new SolidColorBrush(Color.Parse("#E0202B27"));
    private static readonly IBrush DimWalls = new SolidColorBrush(Color.Parse("#5E6B66"));
    private static readonly IBrush BrightWalls = new SolidColorBrush(Color.Parse("#C8D2CD"));

    private readonly AppServices _services;
    private MapOverlaySettings _mapSettings;
    private OverlayLook? _look;
    private (double Width, double Height) _picture = (640, 480);
    private static readonly TimeSpan BannerTime = TimeSpan.FromSeconds(6);
    private int _banner;
    private const int FeedShown = 5;
    private static readonly TimeSpan FeedKeep = TimeSpan.FromSeconds(20);
    private readonly List<(DateTimeOffset At, Happened Event)> _feed = [];
    private readonly Dictionary<(int Slot, int Type), int> _enemyMax = [];
    private string? _enemyRoom;

    public OverlayViewModel(AppServices services, LiveState state, MapViewModel map)
    {
        _services = services;
        _mapSettings = services.Settings.Current.MapOverlay;
        Map = map;
        MapView = Views(_mapSettings)[0];
        map.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MapViewModel.Map) or nameof(MapViewModel.HasPlayer) or nameof(MapViewModel.Floor) or nameof(MapViewModel.InGame))
            {
                UpdateMinimap();
            }
        };
        services.TrainerHotkeys.Pressed += (_, action) =>
        {
            if (action == TrainerHotkeys.NextMapView)
            {
                Dispatcher.UIThread.Post(NextMapView);
            }
            else if (action == TrainerHotkeys.MapZoom)
            {
                Dispatcher.UIThread.Post(() => MinimapZoomLevel = (MinimapZoomLevel + 1) % FloorZooms.Length);
            }
            else if (action == TrainerHotkeys.OverlayNextWidget)
            {
                Dispatcher.UIThread.Post(() => SizeWidget(next: true, step: 0));
            }
            else if (action is TrainerHotkeys.OverlayBigger or TrainerHotkeys.OverlaySmaller)
            {
                Dispatcher.UIThread.Post(() => SizeWidget(next: false, step: action == TrainerHotkeys.OverlayBigger ? 1 : -1));
            }
        };
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LiveState.Snapshot))
            {
                Show(state.Snapshot);
            }
        };
        services.Challenges.StatusChanged += (_, s) => Dispatcher.UIThread.Post(() => Show(s));
        services.Runs.Died += (_, _) => Dispatcher.UIThread.Post(() => Show(services.Challenges.Status));
        services.Runs.StatusChanged += (_, _) => Dispatcher.UIThread.Post(() => ShowRun(state.Snapshot));
        services.Runs.AttemptEnded += (_, record) => Dispatcher.UIThread.Post(() => ShowAttemptEnded(record));
        services.Feed.EventRaised += (_, e) => Dispatcher.UIThread.Post(() =>
        {
            _feed.Add((DateTimeOffset.UtcNow, e));
            ShowFeed();
        });
        services.RunLibrary.Changed += (_, _) => Dispatcher.UIThread.Post(() => ShowRun(state.Snapshot));
        services.SpeedrunCom.ShownChanged += (_, _) => Dispatcher.UIThread.Post(() => ShowRun(state.Snapshot));
        services.LiveSplitEngine.Changed += (_, _) => Dispatcher.UIThread.Post(() => ShowRun(state.Snapshot));
        Show(state.Snapshot);
        Show(services.Challenges.Status);
    }

    /// <summary>The room map the minimap draws (the Map page's, kept up to date while the minimap is on).</summary>
    public MapViewModel Map { get; }

    [ObservableProperty]
    public partial string Time { get; private set; } = string.Empty;

    /// <summary>The map's current view (<see cref="MapViews"/>); the map hotkey moves to the next chosen one.</summary>
    [ObservableProperty]
    public partial string MapView { get; private set; }

    /// <summary>The map has something to show: a room map, the player on it, and a view that isn't hidden.</summary>
    [ObservableProperty]
    public partial bool HasMinimap { get; private set; }

    /// <summary>The map covers the game's picture instead of sitting in its corner.</summary>
    [ObservableProperty]
    public partial bool MinimapFull { get; private set; }

    /// <summary>0 for the whole room, else world units from the player to the minimap's nearer edge.</summary>
    [ObservableProperty]
    public partial double MinimapViewRadius { get; private set; }

    [ObservableProperty]
    public partial double MinimapWidth { get; private set; } = 130;

    [ObservableProperty]
    public partial double MinimapHeight { get; private set; } = 130;

    [ObservableProperty]
    public partial bool MinimapNames { get; private set; }

    /// <summary>The map shows the whole floor (a floor view) rather than a room.</summary>
    [ObservableProperty]
    public partial bool MinimapIsFloor { get; private set; }

    /// <summary>Which of <see cref="FloorZooms"/> the floor views use (the zoom key).</summary>
    [ObservableProperty]
    public partial int MinimapZoomLevel { get; set; }

    /// <summary>The floor view's zoom: 1 is the whole floor.</summary>
    [ObservableProperty]
    public partial double MinimapFloorZoom { get; private set; } = 1;

    /// <summary>Closer than the whole floor: the floor view keeps the player in the middle.</summary>
    [ObservableProperty]
    public partial bool MinimapFollow { get; private set; }

    private static readonly double[] FloorZooms = [1, 2.5, 5];

    [ObservableProperty]
    public partial double MinimapOpacity { get; private set; } = 1;

    /// <summary>Behind the map: dark, or nothing when see-through.</summary>
    [ObservableProperty]
    public partial IBrush MinimapBackdrop { get; private set; } = Backdrop;

    [ObservableProperty]
    public partial IBrush? MinimapFloor { get; private set; } = Floor;

    [ObservableProperty]
    public partial IBrush MinimapWalls { get; private set; } = DimWalls;

    [ObservableProperty]
    public partial IBrush MinimapBorder { get; private set; } = Brush("LpBorderBrush");

    [ObservableProperty]
    public partial string Health { get; private set; } = string.Empty;

    /// <summary>Fine, Caution, Danger or Poison, as the game's status screen shows it (empty when unknown).</summary>
    [ObservableProperty]
    public partial string Condition { get; private set; } = string.Empty;

    /// <summary>HP as a fraction of max HP, for the bar.</summary>
    [ObservableProperty]
    public partial double HealthFraction { get; private set; }

    /// <summary>Width of the HP bar's fill (the bar is 70 wide).</summary>
    [ObservableProperty]
    public partial double HealthBarWidth { get; private set; }

    /// <summary>Green, amber below 3/4, red below 1/4.</summary>
    [ObservableProperty]
    public partial IBrush HealthBrush { get; private set; } = Brush("LpGreenBrush");

    /// <summary>Green while the challenge is clean, red once it failed.</summary>
    [ObservableProperty]
    public partial IBrush ChallengeBrush { get; private set; } = Brush("LpGreenBrush");

    [ObservableProperty]
    public partial string Room { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasChallenge { get; private set; }

    [ObservableProperty]
    public partial bool ChallengeFailed { get; private set; }

    [ObservableProperty]
    public partial string Challenge { get; private set; } = string.Empty;

    /// <summary>Whether the overlay is showing, or why not (Settings).</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = "Off";

    /// <summary>Size of the widgets: they are designed for a 480-pixel-high picture and grow with the picture.</summary>
    [ObservableProperty]
    public partial double Scale { get; private set; } = 1;

    /// <summary>The panels' background (its transparency from the look).</summary>
    [ObservableProperty]
    public partial IBrush PanelBackground { get; private set; } = new SolidColorBrush(Color.FromArgb(0xC8, 0x0B, 0x0F, 0x0E));

    /// <summary>The main text colour of the panels.</summary>
    [ObservableProperty]
    public partial IBrush TextBrush { get; private set; } = Brush("LpTextBrush");

    /// <summary>The panels' font (the time and numbers keep the monospace one).</summary>
    [ObservableProperty]
    public partial FontFamily PanelFont { get; private set; } = FontFamily.Default;

    [ObservableProperty]
    public partial bool ShowTime { get; private set; } = true;

    [ObservableProperty]
    public partial bool ShowHealth { get; private set; } = true;

    [ObservableProperty]
    public partial bool ShowRoom { get; private set; } = true;

    [ObservableProperty]
    public partial bool ShowChallengeLine { get; private set; } = true;

    /// <summary>Raised (as a property change) when the look changed: every panel draws again.</summary>
    public const string LookChanged = "Look";

    /// <summary>Takes the look from Settings (cheap when unchanged; called by the overlay each tick).</summary>
    public void UseLook(OverlayLook look)
    {
        if (ReferenceEquals(look, _look))
        {
            return;
        }

        _look = look;
        var alpha = (byte)Math.Round(Math.Clamp(look.BackgroundOpacity, 0, 100) * 2.55);
        PanelBackground = new SolidColorBrush(Color.FromArgb(alpha, 0x0B, 0x0F, 0x0E));
        var color = OverlayLook.TextColors.FirstOrDefault(c => c.Id == look.TextColor).Color ?? OverlayLook.TextColors[0].Color;
        TextBrush = new SolidColorBrush(Color.Parse(color));
        var font = OverlayLook.Fonts.FirstOrDefault(f => f.Id == look.Font).Resource;
        PanelFont = font is not null && Avalonia.Application.Current?.TryFindResource(font, out var value) == true && value is FontFamily family
            ? family : FontFamily.Default;
        ShowTime = look.ShowTime;
        ShowHealth = look.ShowHealth;
        ShowRoom = look.ShowRoom;
        ShowChallengeLine = look.ShowChallenge;
        OnPropertyChanged(LookChanged);
    }

    /// <summary>Takes the map options from Settings (cheap when unchanged; called by the overlay each tick).</summary>
    public void UseMapSettings(MapOverlaySettings settings)
    {
        if (ReferenceEquals(settings, _mapSettings))
        {
            return;
        }

        _mapSettings = settings;
        if (!Views(settings).Contains(MapView))
        {
            MapView = Views(settings)[0];
        }

        UpdateMinimap();
    }

    /// <summary>The map hotkey: the next of the chosen views.</summary>
    public void NextMapView()
    {
        var views = Views(_mapSettings);
        MapView = views[(views.IndexOf(MapView) + 1) % views.Count];
    }

    /// <summary>The chosen views, or just the minimap when none are.</summary>
    private static List<string> Views(MapOverlaySettings settings)
    {
        var views = settings.Views.Where(v => MapViews.All.Any(a => a.Id == v)).Distinct().ToList();
        return views.Count > 0 ? views : [MapViews.Around];
    }

    partial void OnMapViewChanged(string value) => UpdateMinimap();

    partial void OnMinimapZoomLevelChanged(int value) => UpdateMinimap();

    private void UpdateMinimap()
    {
        MinimapIsFloor = MapViews.IsFloor(MapView);
        // Out of a game (after a death, at the title) the map hides: it would show the last room.
        HasMinimap = MapView != MapViews.Hidden && Map.InGame && (MinimapIsFloor ? Map.Floor is not null : Map.Map is not null && Map.HasPlayer);
        MinimapFull = MapViews.IsFull(MapView);
        MinimapFloorZoom = FloorZooms[Math.Clamp(MinimapZoomLevel, 0, FloorZooms.Length - 1)];
        MinimapFollow = MinimapFloorZoom > 1;
        var seeThrough = MapViews.IsSeeThrough(MapView);
        MinimapViewRadius = MinimapFull ? 0 : Math.Clamp(_mapSettings.Reach, 1000, 30000);
        MinimapNames = MinimapFull && _mapSettings.Names;
        MinimapOpacity = seeThrough ? Math.Clamp(_mapSettings.SeeThroughOpacity, 10, 100) / 100.0 : 1;
        MinimapBackdrop = seeThrough ? Brushes.Transparent : Backdrop;
        MinimapBorder = seeThrough ? Brushes.Transparent : Brush("LpBorderBrush");
        MinimapFloor = seeThrough ? null : Floor;
        MinimapWalls = seeThrough ? BrightWalls : DimWalls;
        if (MinimapFull)
        {
            // The whole picture, less the margin and the frame (border and padding: 3 each side).
            MinimapWidth = Math.Max(100, Math.Floor((_picture.Width / Scale) - (2 * Margin) - 6));
            MinimapHeight = Math.Max(100, Math.Floor((_picture.Height / Scale) - (2 * Margin) - 6));
        }
        else
        {
            MinimapWidth = MinimapHeight = Math.Clamp(_mapSettings.Size, 60, 400);
        }
    }

    /// <summary>The game picture's size in the overlay's own units (pixels ÷ DPI scale): sets the scale and the size
    /// of the full-screen map.</summary>
    public void SetPicture(double widthDips, double heightDips)
    {
        Scale = Math.Clamp(heightDips / 480.0, 0.75, 3.0);
        if (_picture != (widthDips, heightDips))
        {
            _picture = (widthDips, heightDips);
            UpdateMinimap();
            PictureWidth = _picture.Width / Scale;
            PictureHeight = _picture.Height / Scale;
            RoutePictureWidth = Math.Max(1, PictureWidth - (2 * Margin));
            RoutePictureHeight = Math.Max(1, PictureHeight - (2 * Margin));
        }
    }

    /// <summary>The game picture in the overlay's units (before its scale), for the route drawn in it.</summary>
    [ObservableProperty]
    public partial double PictureWidth { get; private set; }

    [ObservableProperty]
    public partial double PictureHeight { get; private set; }

    /// <summary>The route layer: the whole picture less the overlay's margin (a full-picture widget sits that far in).</summary>
    [ObservableProperty]
    public partial double RoutePictureWidth { get; private set; } = 1;

    [ObservableProperty]
    public partial double RoutePictureHeight { get; private set; } = 1;

    public double PictureInset => Margin;

    /// <summary>The route guide has something to draw in the game picture (a step with a way, in a game, a switch on).</summary>
    public bool HasRoutePicture => Map.InGame && Map.Map is not null && Map.Guide.Plan is not null && !Map.Guide.Busy
        && (Map.Guide.LineInPicture || (Map.Guide.PromptsInPicture && Map.Guide.Prompts.Count > 0) || Map.Guide.Clocks.Length > 0);

    private void Show(GameSnapshot? snapshot)
    {
        var inGame = snapshot?.InGameplay is { IsValid: true, Value: true };
        Time = inGame && snapshot!.GameTime is { IsValid: true } t ? Format(t.Value) : string.Empty;
        if (inGame && snapshot!.Health is { IsValid: true } h && h.Value.Maximum is > 0 and var max)
        {
            var fraction = Math.Clamp((double)h.Value.Current / max, 0, 1);
            Health = string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, h.Value.Current)} / {max}");
            HealthFraction = fraction;
            HealthBarWidth = Math.Round(70 * fraction, 1);
            var condition = snapshot!.Condition is { IsValid: true } c ? c.Value : (HealthCondition?)null;
            Condition = condition?.ToString() ?? string.Empty;
            HealthBrush = condition switch
            {
                HealthCondition.Poison => PoisonBrush,
                HealthCondition.Danger => Brush("LpRedBrush"),
                HealthCondition.Caution => Brush("LpAmberBrush"),
                HealthCondition.Fine => Brush("LpGreenBrush"),
                _ => Brush(fraction < 0.25 ? "LpRedBrush" : fraction < 0.75 ? "LpAmberBrush" : "LpGreenBrush"),
            };
        }
        else
        {
            Health = string.Empty;
            Condition = string.Empty;
        }

        Room = inGame && snapshot!.Location is { IsValid: true } room
            ? _services.Adapter.RoomName(room.Value.Id) ?? room.Value.Id
            : string.Empty;
        ShowEnemies(inGame ? snapshot : null);
        ShowRun(snapshot);
        ShowFeed();
    }

    /// <summary>The event feed widget's lines ("1:23 Picked up Emblem"), newest last; empty when nothing happened lately.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> FeedLines { get; private set; } = [];

    private void ShowFeed()
    {
        var now = DateTimeOffset.UtcNow;
        _feed.RemoveAll(f => now - f.At > FeedKeep);
        if (_feed.Count > FeedShown)
        {
            _feed.RemoveRange(0, _feed.Count - FeedShown);
        }

        var lines = _feed.Select(f => f.Event.GameTime is { } t ? $"{(int)t.TotalMinutes}:{t.Seconds:D2}  {f.Event.Text}" : f.Event.Text).ToList();
        if (!lines.SequenceEqual(FeedLines))
        {
            FeedLines = lines;
        }
    }

    /// <summary>The splits widget's rows: a window around the current split, with the last split always shown.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SplitRowData> SplitRows { get; private set; } = [];

    /// <summary>The run's in-game time ("12:34.5").</summary>
    [ObservableProperty]
    public partial string RunClock { get; private set; } = string.Empty;

    /// <summary>The splits' category.</summary>
    [ObservableProperty]
    public partial string RunTitle { get; private set; } = string.Empty;

    /// <summary>Personal best and best possible time.</summary>
    [ObservableProperty]
    public partial string RunComparison { get; private set; } = string.Empty;

    /// <summary>The clock is ahead of (or level with) the personal best at the current split; false when behind.</summary>
    [ObservableProperty]
    public partial bool RunBehind { get; private set; }

    private const int SplitsBefore = 2;
    private const int SplitsAfter = 3;

    private void ShowRun(GameSnapshot? snapshot)
    {
        var status = _services.Runs.Status;
        if (status.Warning is { } warning && status.State == RunState.Running && _warnedAttempt != status.Attempt)
        {
            _warnedAttempt = status.Attempt;
            ShowBanner("Wrong splits?", warning.Replace("Wrong splits? ", string.Empty, StringComparison.Ordinal), "LpAmberBrush");
        }

        if (status.Set is not { } set || status.State == RunState.Off)
        {
            SplitRows = [];
            RunClock = RunTitle = RunComparison = string.Empty;
            return;
        }

        var comparison = _services.ComparisonFor(set);
        var now = status.GameTime(snapshot);
        var rows = RunRows.Build(status, comparison, now);
        var current = status.State == RunState.Running ? status.Current : status.State == RunState.Ready ? 0 : rows.Count - 1;
        var first = Math.Clamp(current - SplitsBefore, 0, Math.Max(0, rows.Count - 1 - SplitsBefore - SplitsAfter));
        var shown = rows.Skip(first).Take(SplitsBefore + SplitsAfter + 1).ToList();
        if (rows.Count > 0 && shown[^1] != rows[^1])
        {
            shown.Add(rows[^1]);
        }

        if (!shown.SequenceEqual(SplitRows))
        {
            SplitRows = shown;
        }

        RunTitle = set.Category;
        RunClock = now is { } t ? RunRows.Clock(t) : "0:00.00";
        RunBehind = rows.ElementAtOrDefault(current) is { IsCurrent: true, IsBehind: true };
        var pb = comparison.PersonalBest.Count > 0 ? comparison.PersonalBest[^1] : null;
        var parts = new List<string>();
        if (pb is { } p)
        {
            parts.Add($"PB {RunRows.Clock(p)}");
        }

        if (comparison.SumOfBest is { } sob)
        {
            parts.Add($"SoB {RunRows.Clock(sob)}");
        }

        if (_services.SpeedrunCom.Shown?.Record is { } wr)
        {
            parts.Add($"WR {RunRows.Clock(wr.Time)}");
        }

        RunComparison = string.Join(" · ", parts);
    }

    /// <summary>The enemies and bosses in the room, with HP (the enemies widget). Empty when there are none.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<EnemyRow> Enemies { get; private set; } = [];

    private void ShowEnemies(GameSnapshot? snapshot)
    {
        var room = snapshot?.Location is { IsValid: true } location ? location.Value.Id : null;
        if (room != _enemyRoom)
        {
            _enemyRoom = room;
            _enemyMax.Clear();
        }

        var rows = new List<EnemyRow>();
        foreach (var actor in snapshot?.Actors is { IsValid: true } actors ? actors.Value : [])
        {
            var (_, color, _, family) = ActorStyles.For(actor);
            // Plants, webs and the like have no HP worth showing (the mansion plant's is always 1).
            if (actor.Kind == ActorKind.Npc || (actor.Kind == ActorKind.Enemy && family.StartsWith("Plants", StringComparison.Ordinal)) || actor.Hp <= 0)
            {
                continue;
            }

            // Enemies have no max HP of their own: the bar is against the most this one has had in this room.
            var key = (actor.Slot, actor.Type);
            var max = Math.Max(actor.Hp, _enemyMax.GetValueOrDefault(key));
            _enemyMax[key] = max;
            rows.Add(new EnemyRow(actor.Name, actor.Hp, Math.Clamp((double)actor.Hp / max, 0, 1), color, actor.Kind == ActorKind.Boss));
        }

        rows.Sort((a, b) => b.IsBoss.CompareTo(a.IsBoss));
        if (!rows.SequenceEqual(Enemies))
        {
            Enemies = rows;
        }
    }

    /// <summary>A big message is up (the banner widget).</summary>
    [ObservableProperty]
    public partial bool HasBanner { get; private set; }

    [ObservableProperty]
    public partial string BannerTitle { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string BannerDetail { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial IBrush BannerBrush { get; private set; } = Brush("LpRedBrush");

    /// <summary>Shows a big message for a few seconds (a newer one replaces it).</summary>
    /// <summary>The widget the size keys work on (Settings → Hotkeys → Overlay: …).</summary>
    private string? _sizing;

    /// <summary>
    /// Live resizing while playing (no mouse needed, the game keeps the focus): the next widget that's on, or the chosen
    /// one a step bigger or smaller; saved at once and told as a big message.
    /// </summary>
    private void SizeWidget(bool next, int step)
    {
        var settings = _services.Settings.Current;
        var on = OverlayLayout.Widgets.Where(w => w.Id != OverlayLayout.Banner && OverlayLayout.Corner(settings, w.Id) != OverlayLayout.Off)
            .Select(w => w.Id).ToList();
        if (on.Count == 0)
        {
            return;
        }

        if (next || _sizing is null || !on.Contains(_sizing))
        {
            _sizing = next && _sizing is not null && on.IndexOf(_sizing) is >= 0 and var at ? on[(at + 1) % on.Count] : on[0];
        }

        var sizes = OverlayLayout.Sizes;
        var current = settings.OverlayWidgetSizes.GetValueOrDefault(_sizing, 100);
        var index = Math.Clamp(sizes.ToList().FindIndex(s => s >= current) is var i and >= 0 ? i : sizes.Count - 1, 0, sizes.Count - 1);
        var size = sizes[Math.Clamp(index + step, 0, sizes.Count - 1)];
        if (size != current)
        {
            var all = new Dictionary<string, int>(settings.OverlayWidgetSizes) { [_sizing] = size };
            try
            {
                _services.Settings.Save(settings with { OverlayWidgetSizes = all });
            }
            catch (IOException)
            {
                // The size still applies until Lockpick closes.
            }
        }

        var name = OverlayLayout.Widgets.First(w => w.Id == _sizing).Name;
        ShowBanner(step == 0 ? "Sizing" : step > 0 ? "Bigger" : "Smaller", $"{name}: {size}%", "LpAmberBrush");
    }

    private void ShowBanner(string title, string detail, string brush)
    {
        BannerTitle = title;
        BannerDetail = detail;
        BannerBrush = Brush(brush);
        HasBanner = true;
        var shown = ++_banner;
        DispatcherTimer.RunOnce(() =>
        {
            if (shown == _banner)
            {
                HasBanner = false;
            }
        }, BannerTime);
    }

    private int _warnedAttempt;

    private void ShowAttemptEnded(Core.Runs.RunRecord record)
    {
        if (record.Result == Core.Runs.RunResult.Died)
        {
            ShowBanner("You died", $"Attempt {record.Attempt} ended at {RunRows.Clock(record.GameTime)}", "LpRedBrush");
        }
        else if (record.Result == Core.Runs.RunResult.Finished)
        {
            var best = _services.RunLibrary.Find(record.SetId) is { } set ? _services.RunLibrary.Comparison(set).PersonalBest : [];
            var personalBest = record.IsClean && best.Count > 0 && best[^1] == record.GameTime;
            ShowBanner(personalBest ? "New personal best!" : "Run finished", $"{record.Category} in {RunRows.Clock(record.GameTime)}",
                personalBest ? "LpGreenBrush" : "LpTextBrush");
        }
    }

    private void Show(ChallengeStatus status)
    {
        if (status.State == ChallengeState.Failed && !ChallengeFailed)
        {
            ShowBanner("Challenge failed", $"{status.Challenge?.Name}: {status.FailReason}", "LpRedBrush");
        }

        HasChallenge = status.State is ChallengeState.Running or ChallengeState.Failed;
        ChallengeFailed = status.State == ChallengeState.Failed;
        ChallengeBrush = Brush(ChallengeFailed ? "LpRedBrush" : "LpGreenBrush");
        var deaths = _services.Runs.Deaths switch { 0 => string.Empty, 1 => " · 1 death", var n => $" · {n} deaths" };
        // One attempt number on screen: during a run it is the run's (as the splits widget and the Runs page show),
        // not the challenge's own count (the Speedrun challenge that leaderboard runs hold counts separately).
        var attempt = _services.Runs.Status is { Attempt: > 0, State: Core.Runs.RunState.Running or Core.Runs.RunState.Finished or Core.Runs.RunState.Ended } run
            ? run.Attempt
            : status.Attempt;
        Challenge = status.State switch
        {
            ChallengeState.Running => $"{status.Challenge?.Name} · attempt {attempt} · {(status.WithCheats ? "with cheats" : "clean")}{deaths}",
            ChallengeState.Failed => $"{status.Challenge?.Name} · failed: {status.FailReason}{deaths}",
            _ => string.Empty,
        };
    }

    private static IBrush Brush(string key) =>
        Avalonia.Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : Brushes.White;

    private static string Format(TimeSpan t) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}");
}

/// <summary>One enemy in the enemies widget: its name, HP and HP against the most it has had in this room.</summary>
public sealed record EnemyRow(string Name, int Hp, double Fraction, Color Color, bool IsBoss);
