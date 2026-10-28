using Lockpick.Core.Adapters;
using Lockpick.Core.Maps;
using Lockpick.Core.Monitoring;
using Lockpick.Core.Routes;
using Lockpick.Core.Runs;
using Lockpick.Core.State;
using Lockpick.Platform.Windows;
using Microsoft.Extensions.Logging;

namespace Lockpick.App.Services;

/// <summary>The kind of buttons a prompt shows.</summary>
public enum ButtonKind
{
    Keyboard,
    Xbox,
    PlayStation,
}

/// <summary>
/// The route guide (docs/ROUTES.md): follows the chosen route as the game is played, four times a second, off the UI
/// thread. Loads every room of the character's game once (the room files of the game being played), keeps the guide
/// for the chosen route and remembers which controller was pressed last for the prompts. Read-only.
/// </summary>
public sealed class RouteService : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(250);

    private readonly IGameStateSource _source;
    private readonly IGameAdapter _adapter;
    private readonly SettingsStore _settings;
    private readonly Func<string?> _executable;
    private readonly Func<SplitSet?> _splitSet;
    private readonly ILogger _log;
    private readonly Dictionary<string, Route?> _planned = [];
    private string? _planningKey;
    private readonly Lock _gate = new();
    private DateTimeOffset _last;
    private (string Exe, PlayableCharacter Character)? _roomsFor;
    private RoutePlanner? _planner;
    private IRoomMapSource? _maps;
    private RouteGuide? _guide;
    private int _loads;
    private int _busy;

    public RouteService(IGameStateSource source, IGameAdapter adapter, SettingsStore settings, Func<string?> executable, Func<SplitSet?> splitSet, ILogger log)
    {
        _source = source;
        _adapter = adapter;
        _settings = settings;
        _executable = executable;
        _splitSet = splitSet;
        _log = log;
        _source.SnapshotChanged += OnSnapshot;
    }

    /// <summary>Raised (off the UI thread) when <see cref="View"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Where the player is on the route; null with no route chosen, outside a game, or while the rooms load.</summary>
    public RouteView? View { get; private set; }

    /// <summary>What "follow my splits" is doing when there's no route to show yet (working one out, or why it can't).</summary>
    public string? Planning { get; private set; }

    /// <summary>The routes for the game (built in).</summary>
    public IReadOnlyList<Route> Routes => [.. _adapter.CreateRunRules(_adapter.Profiles[0])?.Routes ?? [], .. OwnRoutes.Value];

    /// <summary>
    /// Routes for the player's own split sets, from the data folder's routes folder (each file a routes.json of its own,
    /// written by tools/routesim/route_for_splits.py); a file that doesn't read is skipped.
    /// </summary>
    private static readonly Lazy<IReadOnlyList<Route>> OwnRoutes = new(() =>
    {
        var routes = new List<Route>();
        if (!Directory.Exists(AppPaths.Routes))
        {
            return routes;
        }

        foreach (var file in Directory.EnumerateFiles(AppPaths.Routes, "*.json"))
        {
            try
            {
                using var stream = File.OpenRead(file);
                routes.AddRange(RouteFile.Parse(stream));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                or InvalidDataException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                // A broken file shouldn't take the built-in routes with it.
            }
        }

        return routes;
    });

    /// <summary>The controller pressed last (Keyboard until one is).</summary>
    public ButtonKind LastPad { get; private set; } = ButtonKind.Keyboard;

    /// <summary>The buttons prompts show now: the setting, or the pad in use.</summary>
    public ButtonKind Buttons => _settings.Current.ButtonStyle switch
    {
        ButtonStyles.Xbox => ButtonKind.Xbox,
        ButtonStyles.PlayStation => ButtonKind.PlayStation,
        ButtonStyles.Keyboard => ButtonKind.Keyboard,
        _ => LastPad,
    };

    /// <summary>A controller button was pressed: XInput pads are Xbox-style, Sony's and PS1 adapters PlayStation.</summary>
    public void Pressed(GamepadButton button)
    {
        var kind = button.Device == GamepadButton.XInput ? ButtonKind.Xbox : ButtonKind.PlayStation;
        if (kind != LastPad)
        {
            LastPad = kind;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The route was changed in the settings (or the player wants to find the step again).</summary>
    public void Restart()
    {
        lock (_gate)
        {
            _guide = null;
            _last = default;
        }

        if (_source.Latest is { } snapshot)
        {
            Update(snapshot);
        }
    }

    /// <summary>Moves on by hand, for a step the game gives no sign of.</summary>
    public void Skip(int steps)
    {
        lock (_gate)
        {
            _guide?.Skip(steps);
            _last = default;
        }

        if (_source.Latest is { } snapshot)
        {
            Update(snapshot);
        }
    }

    public void Dispose() => _source.SnapshotChanged -= OnSnapshot;

    // On the monitor thread, which must not wait: the work (a new way can take a few milliseconds) runs on the pool,
    // one at a time.
    private void OnSnapshot(object? sender, GameSnapshot snapshot)
    {
        if (snapshot.CapturedAt - _last < Interval || Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                Update(snapshot);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException or IndexOutOfRangeException)
            {
                _log.LogWarning(ex, "Route guide: update failed");
            }
            finally
            {
                Volatile.Write(ref _busy, 0);
            }
        });
    }

    /// <summary>The route choice that follows the run's split set (Runs page) instead of naming a route.</summary>
    public const string FollowSplits = "@splits";

    /// <summary>
    /// The route to follow: the one chosen, or with <see cref="FollowSplits"/> one Lockpick works out for the split set
    /// chosen on the Runs page (any set: built in, imported from LiveSplit or the player's own), from the room files of
    /// the game being played. Working one out takes seconds, in the background; until it's ready there's no route.
    /// </summary>
    private Route? Resolve(GameSnapshot snapshot)
    {
        var id = _settings.Current.RouteId;
        if (id != FollowSplits)
        {
            return Routes.FirstOrDefault(r => r.Id == id);
        }

        if (_splitSet() is not { } set)
        {
            Planning = "Choose a split set on the Runs page to follow it.";
            return null;
        }

        if (_executable() is not { } exe)
        {
            Planning = "The route for your splits is worked out from the game's own files: start the game once.";
            return null;
        }

        var key = $"{exe}|{set.Id}|{string.Join(';', set.Splits.Select(s => $"{s.Name}={s.Trigger}"))}";
        if (_planned.TryGetValue(key, out var planned))
        {
            // Not worked out: the route made for a built-in set when Lockpick was built, if it has one.
            planned ??= Routes.FirstOrDefault(r => r.Id == $"{set.Id}-sim");
            Planning = planned is null ? $"Couldn't work out a route for \"{set.Name}\" from its splits." : null;
            return planned;
        }

        if (_planningKey != key)
        {
            _planningKey = key;
            Planning = $"Working out a route for \"{set.Name}\"…";
            _ = Task.Run(() => PlanFor(key, exe, set));
        }

        return null;
    }

    private void PlanFor(string key, string exe, SplitSet set)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Route? route = null;
        try
        {
            route = _adapter.PlanRoute(exe, set, CancellationToken.None);
            _log.LogInformation("Route guide: planned \"{Set}\" in {Seconds:0.0} s ({Steps} steps)", set.Name, clock.Elapsed.TotalSeconds,
                route?.Steps.Count ?? 0);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or KeyNotFoundException
            or IndexOutOfRangeException or UnauthorizedAccessException or InvalidDataException)
        {
            _log.LogWarning(ex, "Route guide: couldn't plan \"{Set}\"", set.Name);
        }

        lock (_gate)
        {
            _planned[key] = route;
            if (_planningKey == key)
            {
                _planningKey = null;
            }

            _guide = null;
            _last = default;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        if (_source.Latest is { } snapshot)
        {
            Update(snapshot);
        }
    }

    private void Update(GameSnapshot snapshot)
    {
        RouteView? view;
        lock (_gate)
        {
            _last = snapshot.CapturedAt;
            var route = Resolve(snapshot);
            if (route is not null)
            {
                Planning = null;
            }

            if (route is null || !Rooms(snapshot, route.Character))
            {
                view = null;
            }
            else
            {
                if (_guide?.Route.Id != route.Id)
                {
                    _guide = new RouteGuide(route, _planner!, _maps!, _adapter.ItemName, _adapter.RoomName);
                }

                view = _guide.Update(snapshot);
            }
        }

        if (!ReferenceEquals(view, View) && (view is not null || View is not null))
        {
            View = view;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// True once every room of the route's character is loaded; starts loading them (in the background) for the game
    /// being played. The route's character, not the player's: a route for Jill shows Jill's rooms.
    /// </summary>
    private bool Rooms(GameSnapshot snapshot, PlayableCharacter character)
    {
        var exe = _source.Status.Process?.ExecutablePath is { Length: > 0 } running ? running : _executable();
        if (exe is null || snapshot.InGameplay is not { HasValue: true, Value: true })
        {
            return false;
        }

        if (_roomsFor != (exe, character))
        {
            _roomsFor = (exe, character);
            _planner = null;
            _guide = null;
            var load = ++_loads;
            _ = Task.Run(() => Load(exe, character, load));
        }

        return _planner is not null;
    }

    private void Load(string exe, PlayableCharacter character, int load)
    {
        try
        {
            var maps = _adapter.CreateRoomMaps(exe);
            if (maps is null)
            {
                return;
            }

            var rooms = new Dictionary<string, RoomMap>(StringComparer.Ordinal);
            for (var stage = 1; stage <= 9; stage++)
            {
                for (var room = 0; room < 0x20; room++)
                {
                    try
                    {
                        if (maps.Load(new RoomLocation(stage, room), character) is { } map)
                        {
                            rooms[map.RoomId] = map;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException)
                    {
                        // A room file that can't be read is left out of the routes.
                    }
                }
            }

            lock (_gate)
            {
                if (load == _loads)
                {
                    _maps = maps;
                    _planner = new RoutePlanner(rooms);
                    _guide = null;
                }
            }

            _log.LogInformation("Route guide: {Count} rooms loaded for {Character}", rooms.Count, character);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Route guide: the room files couldn't be read");
        }
    }
}
