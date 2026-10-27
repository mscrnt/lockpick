using Lockpick.Core.Maps;

namespace Lockpick.Core.Routes;

/// <summary>A point in a room's own units (X right, Z up on the map).</summary>
public readonly record struct RoutePoint(double X, double Z)
{
    public double DistanceTo(RoutePoint other) => Math.Sqrt(((X - other.X) * (X - other.X)) + ((Z - other.Z) * (Z - other.Z)));
}

/// <summary>
/// A walking line through one room: a grid search (A*) around the room's blocking shapes, grown by the player's width,
/// then straightened wherever nothing blocks the way. Game-agnostic: only the room's collision shapes are used.
/// </summary>
public sealed class RoomPath
{
    /// <summary>Grid step in room units (RE1 rooms are about 10,000–40,000 across).</summary>
    public const int Cell = 250;

    /// <summary>How far walls are pushed out: about the player's half-width, so the line keeps clear of corners.</summary>
    public const int Clearance = 350;

    /// <summary>The margin a straightened line keeps (less than the search's, so lines run straight past corners and
    /// into doorways, where the search's wide margin would make them zigzag).</summary>
    public const int SightClearance = 120;

    private readonly int _x0;
    private readonly int _z0;
    private readonly int _width;
    private readonly int _height;
    private readonly bool[] _blocked;
    private readonly bool[] _solid;

    // Which connected open area each cell is in (0: blocked). A line's far end snaps into the start's area: doors sit in
    // the walls, and the nearest open cell to a door can lie outside the room, past its outer wall (which can even be
    // the biggest open area, when a long record far off stretches the grid).
    private readonly int[] _area;

    public RoomPath(RoomMap map)
    {
        var (x0, z0, x1, z1) = map.Bounds;
        _x0 = x0 - Cell;
        _z0 = z0 - Cell;
        _width = Math.Max(1, ((x1 - x0) / Cell) + 3);
        _height = Math.Max(1, ((z1 - z0) / Cell) + 3);
        _blocked = new bool[_width * _height];
        _solid = new bool[_width * _height];
        foreach (var shape in map.Shapes)
        {
            // Whole-room zones (floors, triggers) are never walls.
            if (!shape.Blocks || shape.X1 - shape.X0 >= 60000 || shape.Z1 - shape.Z0 >= 60000)
            {
                continue;
            }

            Block(shape, _blocked, Clearance);
            Block(shape, _solid, SightClearance);
        }

        _area = Areas();
    }

    /// <summary>The line from <paramref name="from"/> to <paramref name="to"/>, or a straight one when no way is found.</summary>
    public IReadOnlyList<RoutePoint> Find(RoutePoint from, RoutePoint to)
    {
        var start = Nearest(CellOf(from), 0);
        var goal = start < 0 ? -1 : Nearest(CellOf(to), _area[start]);
        if (start < 0 || goal < 0)
        {
            return [from, to];
        }

        var cells = Search(start, goal);
        if (cells is null)
        {
            return [from, to];
        }

        var points = new List<RoutePoint> { from };
        points.AddRange(cells.Skip(1).SkipLast(1).Select(Centre));
        points.Add(to);
        return Straighten(points);
    }

    /// <summary>The length of <see cref="Find"/>'s line.</summary>
    public static double Length(IReadOnlyList<RoutePoint> line)
    {
        var total = 0.0;
        for (var i = 1; i < line.Count; i++)
        {
            total += line[i - 1].DistanceTo(line[i]);
        }

        return total;
    }

    private void Block(MapShape shape, bool[] grid, int margin)
    {
        if (shape.Kind == MapShapeKind.Circle)
        {
            var cx = (shape.X0 + shape.X1) / 2.0;
            var cz = (shape.Z0 + shape.Z1) / 2.0;
            var r = (Math.Max(shape.X1 - shape.X0, shape.Z1 - shape.Z0) / 2.0) + margin;
            ForCells(cx - r, cz - r, cx + r, cz + r, (i, p) =>
            {
                if (p.DistanceTo(new RoutePoint(cx, cz)) <= r)
                {
                    grid[i] = true;
                }
            });
            return;
        }

        ForCells(shape.X0 - margin, shape.Z0 - margin, shape.X1 + margin, shape.Z1 + margin, (i, _) => grid[i] = true);
    }

    private void ForCells(double x0, double z0, double x1, double z1, Action<int, RoutePoint> each)
    {
        var cx0 = Math.Max(0, (int)Math.Floor((x0 - _x0) / Cell));
        var cz0 = Math.Max(0, (int)Math.Floor((z0 - _z0) / Cell));
        var cx1 = Math.Min(_width - 1, (int)Math.Floor((x1 - _x0) / Cell));
        var cz1 = Math.Min(_height - 1, (int)Math.Floor((z1 - _z0) / Cell));
        for (var z = cz0; z <= cz1; z++)
        {
            for (var x = cx0; x <= cx1; x++)
            {
                var i = (z * _width) + x;
                each(i, Centre(i));
            }
        }
    }

    private int CellOf(RoutePoint p)
    {
        var x = Math.Clamp((int)Math.Floor((p.X - _x0) / Cell), 0, _width - 1);
        var z = Math.Clamp((int)Math.Floor((p.Z - _z0) / Cell), 0, _height - 1);
        return (z * _width) + x;
    }

    private RoutePoint Centre(int i) => new(_x0 + ((i % _width) * Cell) + (Cell / 2.0), _z0 + ((i / _width) * Cell) + (Cell / 2.0));

    /// <summary>Labels the connected open areas.</summary>
    private int[] Areas()
    {
        var label = new int[_blocked.Length];
        var next = 0;
        for (var start = 0; start < _blocked.Length; start++)
        {
            if (_blocked[start] || label[start] != 0)
            {
                continue;
            }

            next++;
            var queue = new Queue<int>([start]);
            label[start] = next;
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                foreach (var (n, _) in Neighbours(c))
                {
                    if (!_blocked[n] && label[n] == 0)
                    {
                        label[n] = next;
                        queue.Enqueue(n);
                    }
                }
            }

        }

        return label;
    }

    /// <summary>The open cell closest to <paramref name="cell"/> in area <paramref name="area"/> (0: any area); the player
    /// or a door can sit inside the grown walls.</summary>
    private int Nearest(int cell, int area)
    {
        bool Fits(int c) => _area[c] != 0 && (area == 0 || _area[c] == area);
        if (Fits(cell))
        {
            return cell;
        }

        var seen = new HashSet<int> { cell };
        var queue = new Queue<int>([cell]);
        while (queue.Count > 0)
        {
            var c = queue.Dequeue();
            if (Fits(c))
            {
                return c;
            }

            foreach (var n in Neighbours(c))
            {
                if (seen.Add(n.Cell))
                {
                    queue.Enqueue(n.Cell);
                }
            }
        }

        return -1;
    }

    private IEnumerable<(int Cell, double Cost)> Neighbours(int c)
    {
        int x = c % _width, z = c / _width;
        for (var dz = -1; dz <= 1; dz++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if ((dx == 0 && dz == 0) || x + dx < 0 || x + dx >= _width || z + dz < 0 || z + dz >= _height)
                {
                    continue;
                }

                yield return (((z + dz) * _width) + x + dx, dx != 0 && dz != 0 ? Math.Sqrt(2) : 1);
            }
        }
    }

    private List<int>? Search(int start, int goal)
    {
        var cost = new Dictionary<int, double> { [start] = 0 };
        var from = new Dictionary<int, int>();
        var open = new PriorityQueue<int, double>();
        open.Enqueue(start, 0);
        var goalPoint = Centre(goal);
        while (open.TryDequeue(out var c, out _))
        {
            if (c == goal)
            {
                var path = new List<int> { c };
                while (from.TryGetValue(c, out c))
                {
                    path.Add(c);
                }

                path.Reverse();
                return path;
            }

            foreach (var (n, step) in Neighbours(c))
            {
                // Diagonal steps may not cut a blocked corner.
                if (_blocked[n] || (step > 1 && (_blocked[((c / _width) * _width) + (n % _width)] || _blocked[((n / _width) * _width) + (c % _width)])))
                {
                    continue;
                }

                var next = cost[c] + step;
                if (!cost.TryGetValue(n, out var known) || next < known)
                {
                    cost[n] = next;
                    from[n] = c;
                    open.Enqueue(n, next + (Centre(n).DistanceTo(goalPoint) / Cell));
                }
            }
        }

        return null;
    }

    /// <summary>Drops every point the line can skip without crossing a blocked cell.</summary>
    private List<RoutePoint> Straighten(List<RoutePoint> points)
    {
        var result = new List<RoutePoint> { points[0] };
        var i = 0;
        while (i < points.Count - 1)
        {
            var j = points.Count - 1;
            while (j > i + 1 && !Clear(points[i], points[j]))
            {
                j--;
            }

            result.Add(points[j]);
            i = j;
        }

        return result;
    }

    /// <summary>Nothing solid lies between the two points (checked every quarter cell against the narrow margin).</summary>
    public bool Visible(RoutePoint a, RoutePoint b) => Clear(a, b);

    private bool Clear(RoutePoint a, RoutePoint b)
    {
        var steps = Math.Max(1, (int)(a.DistanceTo(b) / (Cell / 4.0)));
        for (var s = 1; s < steps; s++)
        {
            var t = (double)s / steps;
            if (_solid[CellOf(new RoutePoint(a.X + ((b.X - a.X) * t), a.Z + ((b.Z - a.Z) * t)))])
            {
                return false;
            }
        }

        return true;
    }
}
