using Lockpick.Core.Maps;
using Lockpick.Core.State;

namespace Lockpick.Core.Routes;

/// <summary>A time limit running now: the time left, or null when it began before Lockpick saw it start.</summary>
public sealed record RouteClock(string Label, TimeSpan? Left);

/// <summary>Where the player is on a route: the step, the way there, and what to press.</summary>
/// <param name="Text">The step in words ("Get the Emblem").</param>
/// <param name="Where">The step's room ("Dining room"), or empty.</param>
/// <param name="Plan">The way there; null when the step has no place or none can be reached.</param>
/// <param name="MayBeLocked">The way goes through a door Lockpick can't tell is open (opened from the other side or by
/// an event).</param>
/// <param name="Note">Why there's no way shown, or another thing the player should know; empty when none.</param>
public sealed record RouteView(
    Route Route,
    int StepIndex,
    string Text,
    string Where,
    string? Hint,
    string? NextText,
    RoutePlan? Plan,
    bool MayBeLocked,
    IReadOnlyList<RoutePrompt> Prompts,
    string Note)
{
    /// <summary>The time limits running now.</summary>
    public IReadOnlyList<RouteClock> Clocks { get; init; } = [];

    public RouteStep Step => Route.Steps[StepIndex];

    public bool Finished => Step.Kind == RouteStepKind.End;

    /// <summary>"Step 4 of 31".</summary>
    public string Progress => $"Step {StepIndex + 1} of {Route.Steps.Count}";
}

/// <summary>
/// Follows the player along a route. A step is done when the game says so: an item to get is held or gone from where it
/// was (pickup flags, so a loaded save starts at the right step), a use spot is filled or the door the item opens is
/// unlocked, or, when the game doesn't say, the item has left the inventory (not into the item box) while that step
/// was shown. The way to each step comes from <see cref="RoutePlanner"/>: doors open, unlocked, or opened by a key the
/// player holds. Read-only: it never changes the game.
/// </summary>
public sealed class RouteGuide(Route route, RoutePlanner planner, IRoomMapSource maps, Func<int, string?> itemName,
    Func<string, string?> roomName)
{
    /// <summary>A door or item this close (room units) gets its button prompt.</summary>
    public const double PromptDistance = 2200;

    // A use repeated at the same spot (the square crank, twice at the water gate): the spot can't tell the two apart.
    // Also an item got twice (the batteries): holding one doesn't mean the second was picked up.
    private readonly HashSet<RouteStep> _repeats = Repeats(route);

    // When each timer's start flag was seen going on (game time); null: it was already on when first seen.
    private readonly Dictionary<RouteTimer, TimeSpan?> _timerStarts = [];
    private readonly Dictionary<RouteTimer, bool> _timerOn = [];

    private int _index = -1;
    private TimeSpan _lastTime;
    private bool _heldOnStep;
    private bool _inStepRoom;

    // Whether the step's item was already held when the step came up (null: not seen yet), to tell a pickup from an item
    // carried in (the second battery).
    private bool? _heldAtStart;
    private (int Index, string Room, string Keys, string State)? _planKey;
    private RoutePlan? _plan;
    private bool _mayBeLocked;

    public Route Route => route;

    /// <summary>Moves to the next step by hand (a step the game gives no sign for).</summary>
    public void Skip(int steps = 1)
    {
        _index = Math.Clamp(Math.Max(_index, 0) + steps, 0, route.Steps.Count - 1);
        _heldOnStep = false;
        _inStepRoom = false;
        _heldAtStart = null;
        _planKey = null;
    }

    /// <summary>The route as of <paramref name="snapshot"/>; null outside a game.</summary>
    public RouteView? Update(GameSnapshot snapshot)
    {
        if (snapshot.InGameplay is not { HasValue: true, Value: true } || !snapshot.Location.TryGet(out var location))
        {
            return null;
        }

        var held = snapshot.Inventory.TryGet(out var inventory) ? inventory.Select(i => i.ItemId).ToHashSet() : [];
        held.UnionWith(maps.HeldElsewhere(snapshot));
        var boxed = snapshot.ItemBox.TryGet(out var box) ? box.Select(i => i.ItemId).ToHashSet() : [];

        // A loaded save or a new game: find the step again from the flags.
        if (snapshot.GameTime.TryGet(out var time))
        {
            if (time < _lastTime)
            {
                _index = -1;
            }

            _lastTime = time;
        }

        // The step the flags say the player is on: the first one they say isn't done. A step they can't tell about,
        // or an optional one, counts as done when a later one is (the player has moved on), so steps done out of order
        // (a crest placed early) don't hold the guide back or make it skip what's still to do.
        var byFlags = route.Steps.Select(s => DoneByFlags(s, snapshot, held)).ToList();
        var first = 0;
        while (first < byFlags.Count - 1
            && (byFlags[first] == true
                || ((byFlags[first] is null || route.Steps[first].Optional) && byFlags.Skip(first + 1).Contains(true))))
        {
            first++;
        }

        if (_index < first)
        {
            _index = first;
            _heldOnStep = false;
            _inStepRoom = false;
            _heldAtStart = null;
        }

        _index = Math.Min(_index, route.Steps.Count - 1);
        // An optional step, or an item got from the inventory (a Doom book's medal, used before the step came up), is
        // passed once a later step is done: nothing else could ever say it's done.
        while (_index < route.Steps.Count - 1
            && (byFlags[_index] == true
                || ((route.Steps[_index].Optional || route.Steps[_index] is { Kind: RouteStepKind.Get, Room: null })
                    && byFlags[_index] != true && byFlags.Skip(_index + 1).Contains(true))
                || Done(route.Steps[_index], snapshot, location.Id, held, boxed)))
        {
            _index++;
            _heldOnStep = false;
            _inStepRoom = false;
            _heldAtStart = null;
        }

        var step = route.Steps[_index];
        if (step is { Kind: RouteStepKind.Use, Item: { } usedItem } && held.Contains(usedItem))
        {
            _heldOnStep = true;
        }

        if (step.Room == location.Id)
        {
            _inStepRoom = true;
        }

        _heldAtStart ??= step.Item is { } stepItem && held.Contains(stepItem);

        var note = string.Empty;
        if (snapshot.Character.TryGet(out var character) && character != route.Character && route.Character != PlayableCharacter.Unknown)
        {
            note = $"This route is for {route.Character}.";
        }

        Plan(snapshot, location.Id, held);
        // The game reuses the puzzle flags in other rooms (live 2026-10-27: sys flags flicker in 11A): count them in the
        // puzzle's room only.
        var spotsDone = step.Room == location.Id ? SpotsDone(step, snapshot) : 0;
        if (_plan is not null && step.Spots.Count > 0)
        {
            _plan = _plan with { Spots = step.Spots, SpotsRoom = step.Room, SpotsDone = spotsDone };
        }

        if (_plan is null && step.Kind != RouteStepKind.End && note.Length == 0)
        {
            note = Goals(step, snapshot).Count == 0
                ? "Lockpick can't place this step in the room files; the hint says where."
                : "No way there through the doors you can open yet.";
        }

        return new RouteView(route, _index, Text(step), Where(step, _plan), step.Hint,
            _index + 1 < route.Steps.Count ? Text(route.Steps[_index + 1]) : null,
            _plan, _mayBeLocked, Prompts(step, snapshot, location.Id, held, spotsDone), note) { Clocks = Clocks(snapshot) };
    }

    /// <summary>
    /// The time limits running: from the game time the start flag was seen going on, until the stop flag is set. One
    /// already on when Lockpick first looks (it was started late, or a save was loaded) has no known start.
    /// </summary>
    private List<RouteClock> Clocks(GameSnapshot snapshot)
    {
        var clocks = new List<RouteClock>();
        snapshot.GameTime.TryGet(out var now);
        foreach (var timer in route.Timers)
        {
            bool? Read(RouteFlag f) => snapshot.RawFields.TryGetValue(f.Field, out var w) && w.HasValue ? (w.Value & f.Mask) != 0 : null;
            if (Read(timer.Start) is not { } on)
            {
                continue;
            }

            var running = on && Read(timer.Stop) != true;
            if (running && !_timerStarts.ContainsKey(timer))
            {
                _timerStarts[timer] = _timerOn.TryGetValue(timer, out var wasOn) && !wasOn ? now : null;
            }
            else if (!running)
            {
                _timerStarts.Remove(timer);
            }

            _timerOn[timer] = on;
            if (running)
            {
                var left = _timerStarts[timer] is { } start ? TimeSpan.FromMinutes(timer.Minutes) - (now - start) : (TimeSpan?)null;
                clocks.Add(new RouteClock(timer.Label, left is { } l && l < TimeSpan.Zero ? TimeSpan.Zero : left));
            }
        }

        return clocks;
    }

    /// <summary>The puzzle spots pressed so far: up to the furthest one whose flag is set (the game may keep only the last).</summary>
    private static int SpotsDone(RouteStep step, GameSnapshot snapshot)
    {
        for (var i = step.Spots.Count - 1; i >= 0; i--)
        {
            if (step.Spots[i].Pressed is { } flag && snapshot.RawFields.TryGetValue(flag.Field, out var word) && word.HasValue
                && (word.Value & flag.Mask) != 0)
            {
                return i + 1;
            }
        }

        return 0;
    }

    private void Plan(GameSnapshot snapshot, string room, HashSet<int> held)
    {
        var step = route.Steps[_index];
        if (!snapshot.Position.TryGet(out var position))
        {
            _plan = null;
            return;
        }

        var from = new RoutePoint(position.X, position.Z);
        var keys = string.Join(',', held.Order());
        var locks = string.Join(',', snapshot.RawFields.Where(f => f.Key.StartsWith("flagLocks", StringComparison.Ordinal))
            .Select(f => f.Value.HasValue ? f.Value.Value : -1));
        // The people in the room (a step can lead to one), to the nearest 500 units.
        var people = snapshot.Actors.TryGet(out var actors)
            ? string.Join(';', actors.Where(a => a.Kind == ActorKind.Npc).Select(a => $"{a.X / 500},{a.Z / 500}"))
            : string.Empty;
        var key = (_index, room, keys, locks + "|" + people);
        if (_planKey == key && _plan is { Legs.Count: > 0 } plan && plan.Legs[0].Room == room)
        {
            // Same step, room and doors: only the line from the player moves.
            var legs = plan.Legs.ToList();
            legs[0] = planner.Redraw(legs[0], from, plan.Goal);
            _plan = plan with { Legs = legs };
            return;
        }

        _planKey = key;
        var goals = Goals(step, snapshot);
        _mayBeLocked = false;
        _plan = planner.Plan(room, from, goals, d => CanPass(d, snapshot, held) == true);
        if (_plan is null && goals.Count > 0)
        {
            // Doors locked with no key open from the other side or by an event; show the way through them, marked.
            _plan = planner.Plan(room, from, goals, d => CanPass(d, snapshot, held) != false);
            _mayBeLocked = _plan is not null;
        }
    }

    /// <summary>
    /// True: open, or the player can open it (the key is held, or it opens from this side); false: locked (the key
    /// isn't held, or it opens only from the other side); null: the story opens it, and hasn't yet as far as the game
    /// says.
    /// </summary>
    private bool? CanPass(MapDoor door, GameSnapshot snapshot, HashSet<int> held)
    {
        if (door.LockFlag is null || maps.IsUnlocked(door, snapshot) == true)
        {
            return true;
        }

        return door.Unlock switch
        {
            MapUnlock.ThisSide => true,
            MapUnlock.Key => door.Key is { } key && held.Contains(key),
            MapUnlock.OtherSide => false,
            _ => null,
        };
    }

    /// <summary>The places a step leads to (items still there, spots not yet filled, doors still locked).</summary>
    private List<RouteGoal> Goals(RouteStep step, GameSnapshot snapshot)
    {
        var goals = new List<RouteGoal>();
        foreach (var (id, map) in planner.Rooms)
        {
            if (step.Room is { } only && only != id)
            {
                continue;
            }

            switch (step.Kind)
            {
                case RouteStepKind.Get:
                    goals.AddRange(map.Items.Where(i => i.ItemId == step.Item && maps.IsTaken(i, snapshot) != true)
                        .Select(i => new RouteGoal(id, Centre(i.X, i.Z, i.Width, i.Depth))));
                    break;
                case RouteStepKind.Use:
                    goals.AddRange(map.Sockets.Where(k => k.ItemId == step.Item && maps.IsFilled(map, k, snapshot) != true)
                        .Select(k => new RouteGoal(id, Centre(k.X, k.Z, k.Width, k.Depth))));
                    goals.AddRange(map.Doors.Where(d => d.Key == step.Item && maps.IsUnlocked(d, snapshot) != true)
                        .Select(d => new RouteGoal(id, new RoutePoint(d.CenterX, d.CenterZ))));
                    break;
                case RouteStepKind.Go when step.Room == id:
                    goals.Add(new RouteGoal(id, null));
                    break;
            }
        }

        // An item a script places (not in the room file), or a spot the room file doesn't list: the step's own spot, or the
        // room.
        if (goals.Count == 0 && step is { Kind: RouteStepKind.Get or RouteStepKind.Use, Room: { } room } && planner.Rooms.ContainsKey(room)
            && DoneByFlags(step, snapshot, []) != true)
        {
            goals.Add(new RouteGoal(room, step.At));
        }

        if (step is { Kind: RouteStepKind.Go, Room: { } there, At: { } spot } && goals is [{ Point: null }])
        {
            goals[0] = new RouteGoal(there, spot);
        }

        // Just a room, and the player is in it: the person there is what the step is about (Richard, waiting for the
        // serum), so the way leads to them.
        if (goals is [{ Point: null } roomGoal] && snapshot.Location.TryGet(out var here) && here.Id == roomGoal.Room
            && snapshot.Actors.TryGet(out var actors) && snapshot.Position.TryGet(out var player)
            && actors.Where(a => a.Kind == ActorKind.Npc).MinBy(a => Math.Abs(a.X - player.X) + Math.Abs(a.Z - player.Z)) is { } person)
        {
            goals[0] = roomGoal with { Point = new RoutePoint(person.X, person.Z) };
        }

        return goals;
    }

    /// <summary>Done as the flags say (true), not done (false), or the flags don't say (null).</summary>
    private bool? DoneByFlags(RouteStep step, GameSnapshot snapshot, HashSet<int> held)
    {
        if (step.Done is { } flag && snapshot.RawFields.TryGetValue(flag.Field, out var word) && word.HasValue)
        {
            return (word.Value & flag.Mask) != 0;
        }

        switch (step.Kind)
        {
            case RouteStepKind.Get when step.Room is null:
                // Got from the inventory (a medal out of a Doom book): held says so; once used, the flags can't tell
                // (the item's records elsewhere, the fountain's medal slots, aren't where it was got).
                return step.Item is { } got && held.Contains(got) ? true : null;
            case RouteStepKind.Get:
                var items = Rooms(step).SelectMany(m => m.Items.Where(i => i.ItemId == step.Item)).ToList();
                var taken = items.Select(i => maps.IsTaken(i, snapshot)).ToList();
                // No pickup spot in the room file (a script places it, like the serum): the flags can't tell.
                return taken.Contains(true) ? true : step.Room is null && step.Item is { } item && held.Contains(item) ? true
                    : taken.Count == 0 || taken.Contains(null) ? null : false;
            case RouteStepKind.Use when _repeats.Contains(step):
                return null;
            case RouteStepKind.Use:
                var known = Rooms(step).SelectMany(m => m.Sockets.Where(k => k.ItemId == step.Item).Select(k => maps.IsFilled(m, k, snapshot))
                    .Concat(m.Doors.Where(d => d.Key == step.Item).Select(d => maps.IsUnlocked(d, snapshot)))).ToList();
                return known.Contains(true) ? true : null;
            default:
                return null;
        }
    }

    private bool Done(RouteStep step, GameSnapshot snapshot, string room, HashSet<int> held, HashSet<int> boxed)
    {
        var byFlags = DoneByFlags(step, snapshot, held);
        if (byFlags == true || (step.Done is not null && byFlags == false))
        {
            return byFlags == true; // a step with its own flag is done when the game says so, not by being in the room
        }

        // A step whose flag can't be read here (another build) falls back to where the player is.
        var flagUnread = step.Done is not null;

        // Steps the game gives no sign of (a lever pulled, a crank turned and kept) count as done once the player has been
        // in the step's room and left it, so the hint stays up while they're there. A plain "go" step whose next step is
        // in the same room is done on arriving.
        var leftRoom = _inStepRoom && step.Room is not null && step.Room != room;
        return step.Kind switch
        {
            RouteStepKind.Get => step.Item is { } item && held.Contains(item)
                && (step.Room is null || step.Room == room || !_repeats.Contains(step) || _heldAtStart == false),
            RouteStepKind.Use => step.Item is { } used && !boxed.Contains(used) && ((_heldOnStep && !held.Contains(used)) || leftRoom),
            RouteStepKind.Go => leftRoom || (step.Room == room && (flagUnread || NextRoom() == room)),
            _ => false,
        };
    }

    private static HashSet<RouteStep> Repeats(Route route)
    {
        var seen = new HashSet<(RouteStepKind, int?, string?)>();
        var items = new HashSet<int?>();
        var repeats = new HashSet<RouteStep>(ReferenceEqualityComparer.Instance);
        foreach (var step in route.Steps)
        {
            // A use at the same spot again; an item to get that an earlier step got too (the second battery).
            if ((step.Kind == RouteStepKind.Use && !seen.Add((step.Kind, step.Item, step.Room)))
                || (step.Kind == RouteStepKind.Get && step.Item is not null && !items.Add(step.Item)))
            {
                repeats.Add(step);
            }
        }

        return repeats;
    }

    private string? NextRoom() => _index + 1 < route.Steps.Count ? route.Steps[_index + 1].Room : null;

    private IEnumerable<RoomMap> Rooms(RouteStep step) =>
        step.Room is { } only ? planner.Rooms.TryGetValue(only, out var map) ? [map] : [] : planner.Rooms.Values;

    private List<RoutePrompt> Prompts(RouteStep step, GameSnapshot snapshot, string room, HashSet<int> held, int spotsDone)
    {
        var prompts = new List<RoutePrompt>();
        if (!snapshot.Position.TryGet(out var position))
        {
            return prompts;
        }

        var here = new RoutePoint(position.X, position.Z);
        var name = step.Item is { } item ? itemName(item) ?? $"item {item}" : string.Empty;
        if (step.Spots.Count > 0 && step.Room == room)
        {
            // A puzzle: the spot the player stands at, by its number in the order.
            var at = step.Spots.Select((s, i) => (Spot: s, Number: i + 1)).MinBy(s => s.Spot.Centre.DistanceTo(here));
            if (at.Spot is not null && at.Spot.Centre.DistanceTo(here) < PromptDistance)
            {
                // Done / not yet only where the game keeps the presses (spots with a flag).
                var next = spotsDone < step.Spots.Count && step.Spots.Any(s => s.Pressed is not null) ? step.Spots[spotsDone] : null;
                prompts.Add(at.Number == spotsDone + 1 || next is null
                    ? new RoutePrompt([GameControl.Action], $"Push the switch: {at.Number} of {step.Spots.Count}, {at.Spot.Label}")
                    : new RoutePrompt([], at.Number <= spotsDone
                        ? $"Done ({at.Number}, {at.Spot.Label}); next: {spotsDone + 1}, {next.Label}"
                        : $"Not yet ({at.Number}, {at.Spot.Label}); next: {spotsDone + 1}, {next.Label}"));
                return prompts;
            }
        }

        if (_plan is { Legs.Count: 1 } plan && plan.Goal.Room == room && plan.Goal.Point is { } spot && here.DistanceTo(spot) < PromptDistance)
        {
            if (step.Kind == RouteStepKind.Get)
            {
                prompts.Add(new RoutePrompt([GameControl.Action], $"Pick up the {name}"));
            }
            else if (step.Kind == RouteStepKind.Use)
            {
                prompts.Add(new RoutePrompt([GameControl.Inventory], $"Use the {name} here (items → USE)"));
            }
        }
        else if (_plan?.NextDoor is { } door && here.DistanceTo(new RoutePoint(door.CenterX, door.CenterZ)) < PromptDistance)
        {
            var locked = door.LockFlag is not null && maps.IsUnlocked(door, snapshot) != true;
            var key = !locked ? string.Empty
                : door.Unlock == MapUnlock.ThisSide ? " (it unlocks from this side)"
                : door.Key is { } k && held.Contains(k) ? $" (the {itemName(k) ?? $"item {k}"} opens it)"
                : string.Empty;
            prompts.Add(new RoutePrompt([GameControl.Action], door.Exit switch
            {
                MapExitKind.Stairs => "Take the stairs",
                MapExitKind.Passage => "Go through",
                _ => $"Open the door{key}",
            }));
        }

        return prompts;
    }

    private string Text(RouteStep step)
    {
        var name = step.Item is { } item ? itemName(item) ?? $"item {item}" : string.Empty;
        return step.Kind switch
        {
            RouteStepKind.Get => $"Get the {name}",
            RouteStepKind.Use => $"Use the {name}",
            RouteStepKind.Go => $"Go to {Room(step.Room)}",
            _ => "Finish the game",
        };
    }

    private string Where(RouteStep step, RoutePlan? plan) =>
        step.Kind == RouteStepKind.Go ? string.Empty : (step.Room ?? plan?.Goal.Room) is { } room ? Room(room) : string.Empty;

    private string Room(string? id) => id is null ? string.Empty : roomName(id) is { } n ? $"{n} ({id})" : $"room {id}";

    private static RoutePoint Centre(int x, int z, int width, int depth) => new(x + (width / 2.0), z + (depth / 2.0));
}
