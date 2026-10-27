using System.ComponentModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Lockpick.App.Controls;
using Lockpick.App.Services;
using Lockpick.Core.Maps;
using Lockpick.Core.State;

namespace Lockpick.App.ViewModels;

/// <summary>
/// Map page: the room the player is in, drawn from the player's own room files (walls, furniture, doors, items for
/// this character), with the player's position and facing live. Read-only.
/// </summary>
/// <summary>One legend row: a label and the marker drawn for it.</summary>
public sealed record LegendEntry(string Label, Geometry Icon, IBrush Fill);

public sealed partial class MapViewModel : PageViewModel
{
    private static readonly TimeSpan Throttle = TimeSpan.FromMilliseconds(100);

    private readonly AppServices _services;
    private IRoomMapSource? _maps;
    private bool _mapsTried;
    private bool _visible;
    private bool _keepUpdating;
    private (int Stage, PlayableCharacter Character)? _floorKey;
    private int _floorBuilds;
    private DateTimeOffset _lastUpdate;

    public MapViewModel(AppServices services, LiveState state)
        : base("Map", "Icon.Map")
    {
        _services = services;
        State = state;
        Guide = new RouteViewModel(services);
        state.PropertyChanged += OnStateChanged;
    }

    public LiveState State { get; }

    /// <summary>The route guide: its step and buttons in the route card, its way on the maps.</summary>
    public RouteViewModel Guide { get; }

    [ObservableProperty]
    public partial RoomMap? Map { get; private set; }

    [ObservableProperty]
    public partial double PlayerX { get; private set; }

    [ObservableProperty]
    public partial double PlayerZ { get; private set; }

    /// <summary>The player's height (room units; −Y is up): the floor the route is drawn on in the game picture.</summary>
    [ObservableProperty]
    public partial double PlayerY { get; private set; }

    /// <summary>The camera the game shows now (index into the room's cameras).</summary>
    [ObservableProperty]
    public partial int CameraId { get; private set; }

    [ObservableProperty]
    public partial double PlayerAngle { get; private set; }

    [ObservableProperty]
    public partial bool HasPlayer { get; private set; }

    /// <summary>The player is in a game (not dead, not at the title or the menus): the overlay, OBS and the map's
    /// window show the map only then; the Map page keeps the last room.</summary>
    [ObservableProperty]
    public partial bool InGame { get; private set; }

    /// <summary>Item boxes and typewriters the game has active now (null when the game doesn't report them).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PlacedFixture>? LiveFixtures { get; private set; }

    /// <summary>Enemies and NPCs in the room right now (null when the game doesn't report them).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<LiveActor>? Actors { get; private set; }

    /// <summary>Door label per target room ("106 Main hall"), falling back to the room id.</summary>
    [ObservableProperty]
    public partial IReadOnlyDictionary<string, string>? DoorNames { get; private set; }

    /// <summary>The legend's people and enemy rows: one per family present in the room now.</summary>
    public System.Collections.ObjectModel.ObservableCollection<LegendEntry> LegendActors { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItemRows))]
    public partial bool HasPlacedItems { get; private set; }

    /// <summary>The legend's Items heading shows only when a row under it does.</summary>
    public bool HasItemRows => HasPlacedItems || HasFilledSockets || ShowAbsentItems;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItemRows))]
    public partial bool HasFilledSockets { get; private set; }

    [ObservableProperty]
    public partial bool HasItemBox { get; private set; }

    [ObservableProperty]
    public partial bool HasTypewriter { get; private set; }

    /// <summary>Whether the map also draws the room's items you've taken or that come later (off: only what's there).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasItemRows))]
    public partial bool ShowAbsentItems { get; set; }

    /// <summary>Room-file items already taken in this game.</summary>
    [ObservableProperty]
    public partial IReadOnlySet<MapItem>? TakenItems { get; private set; }

    /// <summary>Doors still locked in this game (this room's and the floor's): true when the player can open it (holds
    /// the key, or it unlocks from this side).</summary>
    [ObservableProperty]
    public partial IReadOnlyDictionary<MapDoor, bool>? LockedDoors { get; private set; }

    /// <summary>Use spots whose item has been put in place in this game.</summary>
    [ObservableProperty]
    public partial IReadOnlySet<MapSocket>? FilledSockets { get; private set; }

    /// <summary>Items the game has placed in this room right now (null when the game doesn't report them).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PlacedItem>? PlacedItems { get; private set; }

    [ObservableProperty]
    public partial string Heading { get; private set; } = "Start or load a game to see the room you're in.";

    /// <summary>Every room of the player's stage in place (built in the background on entering a stage).</summary>
    [ObservableProperty]
    public partial FloorMap? Floor { get; private set; }

    /// <summary>The room the player is in (id), for the floor map.</summary>
    [ObservableProperty]
    public partial string? CurrentRoom { get; private set; }

    /// <summary>Items already taken anywhere on the floor (the floor map leaves them out).</summary>
    [ObservableProperty]
    public partial IReadOnlySet<MapItem>? FloorTaken { get; private set; }

    /// <summary>The Map page shows the whole floor instead of the room.</summary>
    [ObservableProperty]
    public partial bool ShowFloor { get; set; }

    [ObservableProperty]
    public partial string Details { get; private set; } = string.Empty;

    /// <summary>Keep the map up to date while the page is hidden too (the overlay's minimap shows it).</summary>
    public bool KeepUpdating
    {
        get => _keepUpdating;
        set
        {
            if (value && !_keepUpdating && !_visible)
            {
                _keepUpdating = true;
                Refresh(force: true);
            }

            _keepUpdating = value;
        }
    }

    public override void OnVisibilityChanged(bool visible)
    {
        _visible = visible;
        if (visible)
        {
            Refresh(force: true);
        }
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if ((_visible || KeepUpdating) && e.PropertyName == nameof(LiveState.Snapshot))
        {
            Refresh(force: false);
        }
    }

    private void UpdateLegend(IReadOnlyList<LiveActor> actors)
    {
        var rows = actors.GroupBy(a => ActorStyles.For(a).Family)
            .Select(g =>
            {
                var (shape, color, _, family) = ActorStyles.For(g.First());
                // People and bosses are named (e.g. "People: Barry", "Boss: Yawn").
                var names = string.Join(", ", g.Select(a => a.Name).Distinct());
                var label = family is "People" or "Boss" ? $"{family}: {names}" : family;
                return new LegendEntry(label, ActorStyles.Icon(shape), new SolidColorBrush(color));
            })
            .ToList();
        if (rows.Select(r => r.Label).SequenceEqual(LegendActors.Select(r => r.Label)))
        {
            return;
        }

        LegendActors.Clear();
        foreach (var row in rows)
        {
            LegendActors.Add(row);
        }
    }

    /// <summary>The room files of the game being played (its own folder and language), else of the installed game.</summary>
    private IRoomMapSource? Maps()
    {
        var exe = State.Status.Process?.ExecutablePath is { Length: > 0 } running ? running : _services.ResolveInstalledExecutable();
        if (!_mapsTried || !string.Equals(exe, _mapsFor, StringComparison.OrdinalIgnoreCase))
        {
            _mapsTried = true;
            _mapsFor = exe;
            _maps = exe is null ? null : _services.Adapter.CreateRoomMaps(exe);
        }

        return _maps;
    }

    private string? _mapsFor;

    private void Refresh(bool force)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && now - _lastUpdate < Throttle)
        {
            return;
        }

        _lastUpdate = now;
        InGame = State.Snapshot?.InGameplay is { HasValue: true, Value: true };
        if (State.Snapshot is not { } s || !s.Location.TryGet(out var room))
        {
            HasPlayer = false;
            return;
        }

        var maps = Maps();
        if (maps is null)
        {
            Map = null;
            Heading = "The game's room files weren't found — set the game path in Settings.";
            Details = string.Empty;
            return;
        }

        var character = s.Character.TryGet(out var c) ? c : PlayableCharacter.Unknown;
        CurrentRoom = room.Id;
        BuildFloor(maps, room.Stage, character);
        if (Floor is { } floor)
        {
            var taken = floor.Rooms.SelectMany(r => r.Map.Items).Where(i => maps.IsTaken(i, s) == true).ToHashSet();
            if (FloorTaken is null || !taken.SetEquals(FloorTaken))
            {
                FloorTaken = taken;
            }
        }

        var map = maps.Load(room, character);
        UpdateLocks(maps, s, map);
        if (!ReferenceEquals(map, Map))
        {
            Map = map;
            DoorNames = map is null ? null : map.Doors.Select(d => d.Target).Distinct()
                .ToDictionary(t => t, t => _services.Adapter.RoomName(t) is { } n ? $"{t} {n}" : t);
            Details = map is null
                ? string.Empty
                : $"{Count(map.Doors.Count, "door")} · {Count(map.Items.Count, "item spot")} · {Count(map.Cameras.Count, "camera")} · from your install's room files";
        }

        var name = _services.Adapter.RoomName(room.Id);
        var title = name is null ? $"Room {room.Id}" : $"Room {room.Id} · {name}";
        Heading = map is null ? $"{title} — no map for this room" : title;
        if (map is not null)
        {
            var taken = map.Items.Where(i => maps.IsTaken(i, s) == true).ToHashSet();
            if (TakenItems is null || !taken.SetEquals(TakenItems))
            {
                TakenItems = taken;
            }

            var filled = map.Sockets.Where(k => maps.IsFilled(map, k, s) == true).ToHashSet();
            if (FilledSockets is null || !filled.SetEquals(FilledSockets))
            {
                FilledSockets = filled;
            }
        }

        var fixtures = s.PlacedFixtures.TryGet(out var active) ? active : null;
        if (fixtures is null != LiveFixtures is null || (fixtures is not null && LiveFixtures is not null && !fixtures.SequenceEqual(LiveFixtures)))
        {
            LiveFixtures = fixtures;
        }

        var actors = s.Actors.TryGet(out var live) ? live : null;
        if (actors is null != Actors is null || (actors is not null && Actors is not null && !actors.SequenceEqual(Actors)))
        {
            Actors = actors;
            UpdateLegend(actors ?? []);
        }

        HasFilledSockets = FilledSockets is { Count: > 0 };
        HasItemBox = LiveFixtures?.Any(f => f.Kind == FixtureKind.ItemBox) == true;
        HasTypewriter = LiveFixtures?.Any(f => f.Kind == FixtureKind.Typewriter) == true;

        var placed = s.PlacedItems.TryGet(out var items) ? items : null;
        HasPlacedItems = placed is { Count: > 0 } || (placed is null && map is { Items.Count: > 0 });
        if (placed is null != PlacedItems is null || (placed is not null && PlacedItems is not null && !placed.SequenceEqual(PlacedItems)))
        {
            PlacedItems = placed;
        }
        if (s.Position.TryGet(out var p))
        {
            PlayerX = p.X;
            PlayerY = p.Y;
            PlayerZ = p.Z;
            CameraId = s.Camera.TryGet(out var camera) ? camera : 0;
            PlayerAngle = s.RawFields.TryGetValue("yaw", out var yaw) && yaw.HasValue ? (short)yaw.Value : 0;
            HasPlayer = true;
        }
        else
        {
            HasPlayer = false;
        }
    }

    /// <summary>
    /// Which doors are locked (the game's lock flags; both sides of a door share one) and whether the player holds the
    /// key. A door whose state the game doesn't report is drawn as open.
    /// </summary>
    private void UpdateLocks(IRoomMapSource maps, GameSnapshot s, RoomMap? map)
    {
        var held = s.Inventory.TryGet(out var inventory) ? inventory.Select(i => i.ItemId).ToHashSet() : [];
        held.UnionWith(maps.HeldElsewhere(s));
        var doors = (Floor?.Rooms.Select(r => r.Map) ?? []).Append(map).OfType<RoomMap>().SelectMany(m => m.Doors).Distinct();
        var locked = doors.Where(d => d.LockFlag is not null && maps.IsUnlocked(d, s) == false)
            .ToDictionary(d => d, d => d.Unlock == MapUnlock.ThisSide || (d.Key is { } key && held.Contains(key)));
        if (LockedDoors is null || locked.Count != LockedDoors.Count || locked.Any(l => !LockedDoors.TryGetValue(l.Key, out var k) || k != l.Value))
        {
            LockedDoors = locked;
        }
    }

    /// <summary>Places the stage's rooms (FloorLayout) off the UI thread, once per stage and character.</summary>
    private void BuildFloor(IRoomMapSource maps, int stage, PlayableCharacter character)
    {
        if (_floorKey == (stage, character))
        {
            return;
        }

        _floorKey = (stage, character);
        var build = ++_floorBuilds;
        var adapter = _services.Adapter;
        _ = Task.Run(() =>
        {
            var rooms = new Dictionary<string, RoomMap>();
            for (var r = 0; r <= 0x1F; r++)
            {
                try
                {
                    if (maps.Load(new RoomLocation(stage, r), character) is { } loaded)
                    {
                        rooms[loaded.RoomId] = loaded;
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException)
                {
                    // A room file that can't be read is left off the floor.
                }
            }

            var floor = FloorLayout.Build(stage, rooms, adapter.RoomName);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (build == _floorBuilds)
                {
                    Floor = floor;
                }
            });
        });
    }

    private static string Count(int n, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{n} {noun}{(n == 1 ? string.Empty : "s")}");
}
