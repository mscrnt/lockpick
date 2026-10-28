using System.Text;

namespace Lockpick.GameAdapter.RE1.Routing;

/// <summary>A room-action table entry as the simulator keeps it: a door, an item lying in the room, or a room action
/// (a spot), changed in place when a script re-arms it.</summary>
internal sealed class ActionRecord
{
    public required string Kind { get; init; } // "door", "item", "action"

    public int Slot { get; init; }

    public IReadOnlyList<int> Zone { get; init; } = [];

    public int? Handler { get; set; }

    public int Probe { get; set; }

    public IReadOnlyList<int>? Words { get; set; }

    public int Item { get; init; }

    public int Quantity { get; init; }

    public int Flag { get; init; }

    public int Stage { get; init; }

    public int Room { get; init; }

    public int Lock { get; init; }

    public int Key { get; init; }

    public IReadOnlyList<int> Arrival { get; init; } = [];

    public ActionRecord Copy() => (ActionRecord)MemberwiseClone();
}

/// <summary>A running event script: the room it belongs to, which script, and the step it's on.</summary>
internal sealed class RunningEvent(string room, int script)
{
    public string Room { get; } = room;

    public int Script { get; set; } = script;

    public int Pc { get; set; }

    public RunningEvent Copy() => new(Room, Script) { Pc = Pc };
}

/// <summary>
/// What the room scripts read and write (tools/routesim/sim.py State): the flag banks, the inventory, the room, the
/// state bytes, the room-action table, the running events, where the player stands, and this visit's script-placed
/// objects and collision changes.
/// </summary>
internal sealed class SimState
{
    public const int Banks = 10;

    private readonly ulong[] _flags = new ulong[Banks * 4];

    public required string Character { get; init; }

    public string Room { get; set; } = string.Empty;

    public Dictionary<int, int> Inventory { get; private set; } = [];

    /// <summary>State bytes (fields); a null value is "not known" (the camera before the player stands anywhere).</summary>
    public Dictionary<int, int?> Fields { get; private set; } = [];

    public Dictionary<int, ActionRecord> Actions { get; set; } = [];

    public List<ScriptNode> Enemies { get; set; } = [];

    public Dictionary<int, RunningEvent> Events { get; set; } = [];

    public (int X, int Z)? Pos { get; set; }

    public Dictionary<int, (int X, int Z)> Objects { get; set; } = [];

    public Dictionary<(int List, int Index), (int X1, int Z1, int X0, int Z0, int Mode)> Mods { get; set; } = [];

    public bool Has(int bank, int flag) => (_flags[(bank * 4) + (flag >> 6)] & (1UL << (flag & 63))) != 0;

    public void SetFlag(int bank, int flag, bool on)
    {
        var i = (bank * 4) + (flag >> 6);
        _flags[i] = on ? _flags[i] | (1UL << (flag & 63)) : _flags[i] & ~(1UL << (flag & 63));
    }

    public void ClearBank(int bank) => Array.Clear(_flags, bank * 4, 4);

    public IEnumerable<int> FlagsOf(int bank)
    {
        for (var f = 0; f < 256; f++)
        {
            if (Has(bank, f))
            {
                yield return f;
            }
        }
    }

    public int Quantity(int item) => Inventory.GetValueOrDefault(item);

    /// <summary>What tells two states apart for the search: the room, the saved flags (bar the rooms visited), the
    /// inventory.</summary>
    public string Key()
    {
        var sb = new StringBuilder(Room);
        foreach (var bank in (int[])[0, 1, 2, 3, 7])
        {
            for (var w = 0; w < 4; w++)
            {
                sb.Append(':').Append(_flags[(bank * 4) + w].ToString("x", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        foreach (var (item, quantity) in Inventory.OrderBy(i => i.Key))
        {
            sb.Append('|').Append(item).Append('x').Append(quantity);
        }

        return sb.ToString();
    }

    /// <summary>The flags and inventory and known state bytes, for telling whether a frame changed anything.</summary>
    public string Signature()
    {
        var sb = new StringBuilder();
        foreach (var w in _flags)
        {
            sb.Append(w.ToString("x", System.Globalization.CultureInfo.InvariantCulture)).Append(',');
        }

        foreach (var (item, quantity) in Inventory.OrderBy(i => i.Key))
        {
            sb.Append('|').Append(item).Append('x').Append(quantity);
        }

        foreach (var (field, value) in Fields.Where(f => f.Value is not null).OrderBy(f => f.Key))
        {
            sb.Append(';').Append(field).Append('=').Append(value);
        }

        return sb.ToString();
    }

    public string BankSignature(int bank) => string.Join(',', Enumerable.Range(0, 4).Select(w => _flags[(bank * 4) + w]));

    public SimState Clone()
    {
        var copy = new SimState
        {
            Character = Character,
            Room = Room,
            Inventory = new Dictionary<int, int>(Inventory),
            Fields = new Dictionary<int, int?>(Fields),
            Actions = Actions.ToDictionary(a => a.Key, a => a.Value.Copy()),
            Enemies = [.. Enemies],
            Events = Events.ToDictionary(e => e.Key, e => e.Value.Copy()),
            Pos = Pos,
            Objects = new Dictionary<int, (int, int)>(Objects),
            Mods = new Dictionary<(int, int), (int, int, int, int, int)>(Mods),
        };
        Array.Copy(_flags, copy._flags, _flags.Length);
        return copy;
    }
}
