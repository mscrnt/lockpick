using System.Buffers.Binary;

namespace Lockpick.GameAdapter.RE1.Routing;

/// <summary>
/// A room's cameras and collision records for the route simulator (tools/rdt/rdt_map.py and tools/routesim/reach.py):
/// which camera shows a point, and where the player can walk once the room's scripts have changed the collision
/// (command 0x30). Same grid as the app's RoomPath: 250-unit cells, 350 clearance, walls 0x300 and furniture 0x200 solid.
/// </summary>
internal sealed class RoomGeometry
{
    private const int Cell = 250;
    private const int Clearance = 350;
    private const int Solid = 0x300;
    private const int Reach = 750; // the action probe tests 600 ahead of the player, from a cell centre

    private readonly List<(int Id, int ToX, int ToZ)> _cameras = [];
    private readonly List<(int From, bool Header, (int X, int Z)[] Quad)> _switches = [];
    private readonly List<Record> _records = [];
    private readonly int[] _lists = [];
    private readonly Dictionary<string, Grid> _areas = [];

    private sealed record Record(int X0, int Z0, int X1, int Z1, int Shape, int Flags);

    private sealed record Grid(double X0, double Z0, int Width, int Height, int[] Labels);

    private RoomGeometry(byte[] data)
    {
        var count = data[1];
        for (var i = 0; i < count; i++)
        {
            var o = 0x94 + (i * 0x2C);
            if (o + 0x20 > data.Length)
            {
                break;
            }

            _cameras.Add((i, I32(data, o + 0x14), I32(data, o + 0x1C)));
        }

        int? previous = null;
        for (var ptr = (int)U32(data, 0x48); ptr != 0 && ptr + 0x14 <= data.Length; ptr += 0x14)
        {
            var to = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(ptr));
            var from = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(ptr + 2));
            if (to == -1 && from == -1)
            {
                break;
            }

            var quad = Enumerable.Range(0, 4).Select(k => (U16(data, ptr + 4 + (4 * k)), U16(data, ptr + 6 + (4 * k)))).ToArray();
            _switches.Add((from, from != previous, quad));
            previous = from;
        }

        var collision = (int)U32(data, 0x4C);
        if (collision != 0)
        {
            _lists = [.. Enumerable.Range(0, 5).Select(i => I32(data, collision + 4 + (4 * i))).Select(c => c is >= 0 and < 4096 ? c : 0)];
            var total = _lists.Sum();
            for (var i = 0; i < total; i++)
            {
                var o = collision + 0x18 + (i * 0x0C);
                if (o + 0x0C > data.Length)
                {
                    break;
                }

                // Words x1, z1, x0, z0, kind, flags (rdt_map.py).
                _records.Add(new Record(U16(data, o + 4), U16(data, o + 6), U16(data, o), U16(data, o + 2), U16(data, o + 8) & 0xFF, U16(data, o + 10)));
            }
        }

        if (_lists.Length == 0)
        {
            _lists = [_records.Count];
        }
    }

    public static RoomGeometry Read(byte[] data) => new(data);

    /// <summary>A camera that shows (x, z): among the cameras whose coverage quad holds it, the one looking closest to
    /// it (only cameras with a group in the switch table).</summary>
    public int? CameraFor(int x, int z)
    {
        var grouped = _switches.Where(s => s.Header && s.From >= 0 && s.From < _cameras.Count).ToList();
        var inside = grouped.Where(s => InQuad(x, z, s.Quad)).Select(s => s.From).ToList();
        if (inside.Count == 0)
        {
            inside = [.. grouped.Select(s => s.From)];
        }

        if (inside.Count == 0)
        {
            return null;
        }

        return inside.MinBy(c => Math.Pow(_cameras[c].ToX - x, 2) + Math.Pow(_cameras[c].ToZ - z, 2));
    }

    /// <summary>
    /// Where to stand for a zone from <paramref name="pos"/>, or null when the scripts' collision changes keep the player
    /// from it (only what the scripts change is trusted: the base geometry alone splits some rooms that are one in the
    /// game). A zone tested at the player's own position (probe 0x40) is out of reach under a record a script made solid.
    /// </summary>
    public (int X, int Z)? Reachable(IReadOnlyDictionary<(int List, int Index), (int X1, int Z1, int X0, int Z0, int Mode)> mods,
        (int X, int Z) pos, IReadOnlyList<int> zone, int probe)
    {
        (int, int) centre = (zone[0] + Floor(zone[2], 2), zone[1] + Floor(zone[3], 2));
        if (mods.Count == 0)
        {
            return centre;
        }

        var changes = mods.OrderBy(m => m.Key.List).ThenBy(m => m.Key.Index).ToList();
        var full = Areas(changes);
        var spot = Stand(full, AreaAt(full, pos.X, pos.Z), zone);
        if (spot is null)
        {
            var cleared = Areas([.. changes.Select(c => new KeyValuePair<(int, int), (int, int, int, int, int)>(c.Key, (1, 1, 0, 0, 0)))]);
            if (Stand(cleared, AreaAt(cleared, pos.X, pos.Z), zone) is null)
            {
                spot = centre; // the base geometry can't tell either (stairs): the scripts aren't what stops it
            }
        }

        if (spot is not null && (probe & 0x40) != 0 && Covered(changes, zone))
        {
            return null;
        }

        return spot;
    }

    private bool Covered(List<KeyValuePair<(int List, int Index), (int X1, int Z1, int X0, int Z0, int Mode)>> changes, IReadOnlyList<int> zone)
    {
        var cx = zone[0] + (zone[2] / 2.0);
        var cz = zone[1] + (zone[3] / 2.0);
        foreach (var (key, (x1, z1, x0, z0, mode)) in changes)
        {
            if (Index(key.List, key.Index) is not { } i)
            {
                continue;
            }

            var flags = mode == 0 ? _records[i].Flags : (_records[i].Flags & ~0x0F00) | ((mode << 8) & 0x0F00);
            if ((flags & Solid) != 0 && x0 <= cx && cx <= x1 && z0 <= cz && cz <= z1)
            {
                return true;
            }
        }

        return false;
    }

    private int? Index(int listed, int record)
    {
        if (listed >= _lists.Length || record >= _lists[listed])
        {
            return null;
        }

        return _lists.Take(listed).Sum() + record;
    }

    private Grid Areas(List<KeyValuePair<(int List, int Index), (int X1, int Z1, int X0, int Z0, int Mode)>> changes)
    {
        var key = string.Join(';', changes.Select(c => $"{c.Key.List},{c.Key.Index}:{c.Value}"));
        if (_areas.TryGetValue(key, out var grid))
        {
            return grid;
        }

        var records = _records.ToList();
        foreach (var (k, (x1, z1, x0, z0, mode)) in changes)
        {
            if (Index(k.List, k.Index) is not { } i)
            {
                continue;
            }

            var flags = mode == 0 ? records[i].Flags : (records[i].Flags & ~0x0F00) | ((mode << 8) & 0x0F00);
            records[i] = records[i] with { X1 = x1, Z1 = z1, X0 = x0, Z0 = z0, Flags = flags };
        }

        grid = Label(records);
        _areas[key] = grid;
        return grid;
    }

    private static Grid Label(List<Record> records)
    {
        var solid = records.Where(r => (r.Flags & Solid) != 0 && r.X1 - r.X0 < 60000 && r.Z1 - r.Z0 < 60000 && r.X1 > r.X0 && r.Z1 > r.Z0).ToList();
        List<int> xs = [.. records.Select(r => r.X0), .. records.Select(r => r.X1)];
        List<int> zs = [.. records.Select(r => r.Z0), .. records.Select(r => r.Z1)];
        if (xs.Count == 0)
        {
            xs = [0, 1];
        }

        if (zs.Count == 0)
        {
            zs = [0, 1];
        }

        double x0 = xs.Min() - Cell, z0 = zs.Min() - Cell;
        var width = (int)Math.Floor((xs.Max() - x0) / Cell) + 3;
        var height = (int)Math.Floor((zs.Max() - z0) / Cell) + 3;
        var blocked = new bool[width * height];
        foreach (var r in solid)
        {
            double cx = 0, cz = 0, radius = 0;
            (double, double, double, double) box;
            if (r.Shape == 3)
            {
                cx = (r.X0 + r.X1) / 2.0;
                cz = (r.Z0 + r.Z1) / 2.0;
                radius = (Math.Max(r.X1 - r.X0, r.Z1 - r.Z0) / 2.0) + Clearance;
                box = (cx - radius, cz - radius, cx + radius, cz + radius);
            }
            else
            {
                box = (r.X0 - Clearance, r.Z0 - Clearance, r.X1 + Clearance, r.Z1 + Clearance);
            }

            for (var gx = Math.Max(0, (int)Math.Floor((box.Item1 - x0) / Cell)); gx < Math.Min(width, (int)Math.Floor((box.Item3 - x0) / Cell) + 1); gx++)
            {
                for (var gz = Math.Max(0, (int)Math.Floor((box.Item2 - z0) / Cell)); gz < Math.Min(height, (int)Math.Floor((box.Item4 - z0) / Cell) + 1); gz++)
                {
                    var px = x0 + (gx * Cell) + (Cell / 2.0);
                    var pz = z0 + (gz * Cell) + (Cell / 2.0);
                    if (r.Shape == 3 && (Math.Pow(px - cx, 2) + Math.Pow(pz - cz, 2)) > radius * radius)
                    {
                        continue;
                    }

                    if (box.Item1 <= px && px <= box.Item3 && box.Item2 <= pz && pz <= box.Item4)
                    {
                        blocked[(gz * width) + gx] = true;
                    }
                }
            }
        }

        var labels = Enumerable.Repeat(-1, width * height).ToArray();
        var area = 0;
        for (var start = 0; start < width * height; start++)
        {
            if (blocked[start] || labels[start] >= 0)
            {
                continue;
            }

            labels[start] = area;
            var queue = new Queue<int>([start]);
            while (queue.TryDequeue(out var i))
            {
                int gx = i % width, gz = i / width;
                foreach (var (nx, nz) in new[] { (gx + 1, gz), (gx - 1, gz), (gx, gz + 1), (gx, gz - 1) })
                {
                    if (nx >= 0 && nx < width && nz >= 0 && nz < height)
                    {
                        var j = (nz * width) + nx;
                        if (!blocked[j] && labels[j] < 0)
                        {
                            labels[j] = area;
                            queue.Enqueue(j);
                        }
                    }
                }
            }

            area++;
        }

        return new Grid(x0, z0, width, height, labels);
    }

    private static int? AreaAt(Grid grid, double x, double z)
    {
        var gx = (int)Math.Floor((x - grid.X0) / Cell);
        var gz = (int)Math.Floor((z - grid.Z0) / Cell);
        int start;
        if (gx >= 0 && gx < grid.Width && gz >= 0 && gz < grid.Height)
        {
            start = (gz * grid.Width) + gx;
        }
        else
        {
            start = (Math.Clamp(gz, 0, grid.Height - 1) * grid.Width) + Math.Clamp(gx, 0, grid.Width - 1);
        }

        if (grid.Labels[start] >= 0)
        {
            return grid.Labels[start];
        }

        (int D, int A)? best = null;
        for (var i = 0; i < grid.Labels.Length; i++)
        {
            if (grid.Labels[i] < 0)
            {
                continue;
            }

            var d = ((i % grid.Width) - (start % grid.Width)) * ((i % grid.Width) - (start % grid.Width))
                + ((i / grid.Width) - (start / grid.Width)) * ((i / grid.Width) - (start / grid.Width));
            if (best is null || d < best.Value.D)
            {
                best = (d, grid.Labels[i]);
            }
        }

        return best?.A;
    }

    private static (int X, int Z)? Stand(Grid grid, int? area, IReadOnlyList<int> zone)
    {
        if (area is null)
        {
            return null;
        }

        double zx = zone[0], zz = zone[1], zw = zone[2], zd = zone[3];
        (double D, int X, int Z)? best = null;
        for (var gx = Math.Max(0, (int)Math.Floor((zx - Reach - grid.X0) / Cell)); gx < Math.Min(grid.Width, (int)Math.Floor((zx + zw + Reach - grid.X0) / Cell) + 1); gx++)
        {
            for (var gz = Math.Max(0, (int)Math.Floor((zz - Reach - grid.Z0) / Cell)); gz < Math.Min(grid.Height, (int)Math.Floor((zz + zd + Reach - grid.Z0) / Cell) + 1); gz++)
            {
                if (grid.Labels[(gz * grid.Width) + gx] != area)
                {
                    continue;
                }

                var px = grid.X0 + (gx * Cell) + (Cell / 2.0);
                var pz = grid.Z0 + (gz * Cell) + (Cell / 2.0);
                var dx = Math.Max(Math.Max(zx - px, 0), px - (zx + zw));
                var dz = Math.Max(Math.Max(zz - pz, 0), pz - (zz + zd));
                if ((dx * dx) + (dz * dz) <= Reach * Reach)
                {
                    var d = Math.Pow(px - (zx + (zw / 2)), 2) + Math.Pow(pz - (zz + (zd / 2)), 2);
                    if (best is null || d < best.Value.D)
                    {
                        best = (d, (int)px, (int)pz);
                    }
                }
            }
        }

        return best is { } b ? (b.X, b.Z) : null;
    }

    private static bool InQuad(int x, int z, (int X, int Z)[] quad)
    {
        var sign = 0;
        for (var i = 0; i < 4; i++)
        {
            var (ax, az) = quad[i];
            var (bx, bz) = quad[(i + 1) % 4];
            long cross = ((long)(bx - ax) * (z - az)) - ((long)(bz - az) * (x - ax));
            if (cross != 0)
            {
                if (sign == 0)
                {
                    sign = cross > 0 ? 1 : -1;
                }
                else if ((cross > 0) != (sign > 0))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static int Floor(int value, int by) => (int)Math.Floor(value / (double)by);

    private static int U16(byte[] data, int at) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));

    private static int I32(byte[] data, int at) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at));

    private static uint U32(byte[] data, int at) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
}
