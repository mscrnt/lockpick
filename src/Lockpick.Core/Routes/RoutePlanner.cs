using Lockpick.Core.Maps;

namespace Lockpick.Core.Routes;

/// <summary>
/// Somewhere a route step leads: a spot in a room (an item's zone, a door, a use spot), or just the room when
/// <paramref name="Point"/> is null (entering it is enough).
/// </summary>
public sealed record RouteGoal(string Room, RoutePoint? Point);

/// <summary>One room's part of the way: its walking line and the door it leaves by (null in the goal's room).</summary>
public sealed record RouteLeg(string Room, IReadOnlyList<RoutePoint> Line, MapDoor? Exit);

/// <summary>The way from the player to a goal, room by room.</summary>
public sealed record RoutePlan(IReadOnlyList<RouteLeg> Legs, RouteGoal Goal)
{
    /// <summary>The step's puzzle spots and their room, numbered on the maps in this order.</summary>
    public IReadOnlyList<RouteSpot> Spots { get; init; } = [];

    public string? SpotsRoom { get; init; }

    /// <summary>How many of <see cref="Spots"/> have been pressed (in order), as far as the game's flags say.</summary>
    public int SpotsDone { get; init; }

    /// <summary>The door the player should take next, or null when the goal is in this room.</summary>
    public MapDoor? NextDoor => Legs.Count > 0 ? Legs[0].Exit : null;

    public IEnumerable<string> Rooms => Legs.Select(l => l.Room);
}

/// <summary>
/// Finds the way through the rooms: the fewest-cost chain of doors the player can go through (Dijkstra over door
/// arrival points, straight-line distances plus a cost per door for its animation), then a walking line per room
/// (<see cref="RoomPath"/>). Game-agnostic: rooms and doors come from the adapter's room maps.
/// </summary>
public sealed class RoutePlanner(IReadOnlyDictionary<string, RoomMap> rooms)
{
    /// <summary>A door's animation is worth about this much walking (room units; running covers ~5,500 a second).</summary>
    public const double DoorCost = 12000;

    private readonly Dictionary<string, RoomPath> _paths = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, RoomMap> Rooms => rooms;

    /// <summary>
    /// The way from <paramref name="room"/> at <paramref name="from"/> to the nearest of <paramref name="goals"/>, through
    /// doors <paramref name="canPass"/> allows; null when none can be reached.
    /// </summary>
    public RoutePlan? Plan(string room, RoutePoint from, IReadOnlyList<RouteGoal> goals, Func<MapDoor, bool> canPass)
    {
        if (!rooms.ContainsKey(room) || goals.Count == 0)
        {
            return null;
        }

        var start = new Node(room, from);
        var cost = new Dictionary<Node, double> { [start] = 0 };
        var back = new Dictionary<Node, (Node From, MapDoor Door)>();
        var open = new PriorityQueue<Node, double>();
        open.Enqueue(start, 0);
        var settled = new HashSet<Node>();
        (Node Node, RouteGoal Goal, double Cost)? best = null;
        while (open.TryDequeue(out var node, out var at))
        {
            if (!settled.Add(node) || (best is { } b && at >= b.Cost))
            {
                continue;
            }

            foreach (var goal in goals.Where(g => g.Room == node.Room))
            {
                var total = at + (goal.Point is { } spot ? node.Point.DistanceTo(spot) : 0);
                if (best is null || total < best.Value.Cost)
                {
                    best = (node, goal, total);
                }
            }

            foreach (var door in rooms[node.Room].Doors)
            {
                if (door.Target == node.Room || !rooms.ContainsKey(door.Target) || door.ArrivalX is not { } ax
                    || door.ArrivalZ is not { } az || !canPass(door))
                {
                    continue;
                }

                var next = new Node(door.Target, new RoutePoint(ax, az));
                var total = at + node.Point.DistanceTo(new RoutePoint(door.CenterX, door.CenterZ)) + DoorCost;
                if (!cost.TryGetValue(next, out var known) || total < known)
                {
                    cost[next] = total;
                    back[next] = (node, door);
                    open.Enqueue(next, total);
                }
            }
        }

        if (best is not { } found)
        {
            return null;
        }

        // Walk back from the goal's room to the player, then draw each room's line.
        var chain = new List<(Node Node, MapDoor? Exit)> { (found.Node, null) };
        var current = found.Node;
        while (back.TryGetValue(current, out var step))
        {
            chain.Add((step.From, step.Door));
            current = step.From;
        }

        chain.Reverse();
        var legs = chain.Select(c =>
        {
            var to = c.Exit is { } exit ? new RoutePoint(exit.CenterX, exit.CenterZ) : found.Goal.Point ?? c.Node.Point;
            return new RouteLeg(c.Node.Room, Path(c.Node.Room).Find(c.Node.Point, to), c.Exit);
        }).ToList();
        return new RoutePlan(legs, found.Goal);
    }

    /// <summary>Just the first room's line again, for a player who moved inside the room (cheaper than a new plan).</summary>
    public RouteLeg Redraw(RouteLeg leg, RoutePoint from, RouteGoal goal)
    {
        var to = leg.Exit is { } exit ? new RoutePoint(exit.CenterX, exit.CenterZ) : goal.Point ?? from;
        return leg with { Line = Path(leg.Room).Find(from, to) };
    }

    private RoomPath Path(string room)
    {
        if (!_paths.TryGetValue(room, out var path))
        {
            _paths[room] = path = new RoomPath(rooms[room]);
        }

        return path;
    }

    private readonly record struct Node(string Room, RoutePoint Point);
}
