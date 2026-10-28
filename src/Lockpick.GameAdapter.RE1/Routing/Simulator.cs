namespace Lockpick.GameAdapter.RE1.Routing;

/// <summary>
/// Runs one character's room scripts against a <see cref="SimState"/> (tools/routesim/sim.py, move for move): entering
/// a room runs its init script, then frames of the main script and the event machine until nothing changes; moves
/// (doors, pickups, uses, spots, kills, examining) change the state the way the game does. What the scripts can't say is
/// assumed in the player's favour: distance and facing tests pass, a timer has plenty left, random rolls come up 0.
/// </summary>
internal sealed class Simulator
{
    public const int Stage = 0, RoomField = 1, Camera = 2, CameFrom = 3, MenuChoice = 5, Character = 9, FwdAction = 16, EntAction = 17,
        UsedItem = 18, PickedItem = 19, PendingDoor = 1000;

    public const int ItemUseBase = 0x1B;
    private const int CountdownLeft = 30000;
    private const int NoAction = 0xFF;
    private static readonly int[] StoryBanks = [0, 1, 2, 7];
    private static readonly HashSet<int> KnownFields = [Stage, RoomField, CameFrom, Character, FwdAction, EntAction, UsedItem, PickedItem];

    private readonly Func<string, RoomScripts?> _scripts;
    private readonly Func<string, RoomGeometry?> _geometry;
    private readonly Dictionary<string, bool> _asks = [];
    private readonly Dictionary<string, SortedSet<int>> _reacts = [];

    public Simulator(Func<string, RoomScripts?> scripts, Func<string, RoomGeometry?> geometry, RouteRules rules)
    {
        _scripts = scripts;
        _geometry = geometry;
        Rules = rules;
    }

    public RouteRules Rules { get; }

    /// <summary>The answer to each question room's yes/no question (0 yes, the default; 1 no).</summary>
    public Dictionary<string, int> Answers { get; set; } = [];

    public bool Exists(string room) => _scripts(room) is not null;

    private RoomScripts World(string room) => _scripts(room) ?? throw new InvalidOperationException($"no room {room}");

    // ---- where the player is --------------------------------------------------------------------------------------

    public (int X, int Z)? Where(SimState s, IReadOnlyList<int> zone, int probe)
    {
        (int, int) centre = (zone[0] + (int)Math.Floor(zone[2] / 2.0), zone[1] + (int)Math.Floor(zone[3] / 2.0));
        var geo = s.Pos is not null ? _geometry(s.Room) : null;
        return geo is null ? centre : geo.Reachable(s.Mods, s.Pos!.Value, zone, probe);
    }

    private void StandAt(SimState s, IReadOnlyList<int> zone) =>
        s.Fields[Camera] = _geometry(s.Room)?.CameraFor(zone[0] + (int)Math.Floor(zone[2] / 2.0), zone[1] + (int)Math.Floor(zone[3] / 2.0));

    // ---- conditions and commands ----------------------------------------------------------------------------------

    public bool Test(SimState s, ScriptNode c)
    {
        switch (c.Op)
        {
            case 0x04:
                return c.Bank == 5 || s.Has(c.Bank, c.Flag) == c.IsSet; // runtime frame state: either
            case 0x06:
                if (c.Field == Camera)
                {
                    // Walking around the room the player passes every camera; standing at a spot, the one showing it.
                    return s.Fields.GetValueOrDefault(Camera) is not { } camera || Compare(camera, c.Cmp, c.Value);
                }

                if (c.Field == MenuChoice && c.Value is 0 or 1)
                {
                    return Compare(Answers.GetValueOrDefault(s.Room), c.Cmp, c.Value); // the player's answer
                }

                if (!KnownFields.Contains(c.Field))
                {
                    return true; // a message being up, attract: in the player's favour
                }

                return Compare(s.Fields.GetValueOrDefault(c.Field) ?? 0, c.Cmp, c.Value);
            case 0x07:
                // Word 3 the random seed (0), word 4 the countdown timer (plenty left: the route is on time).
                return Compare(c.Field == 4 ? CountdownLeft : 0, c.Cmp, c.Value);
            case 0x10:
                return (s.Fields.GetValueOrDefault(UsedItem) ?? 0) == c.Item;
            case 0x11:
                return (s.Fields.GetValueOrDefault(PickedItem) ?? 0) == c.Item;
            case 0x1A:
                return s.Quantity(c.Item) > 0;
            case 0x1D:
                return false; // the equipped weapon isn't modelled
            case 0x2C:
                if (s.Quantity(c.Item) > 0)
                {
                    s.Inventory.Remove(c.Item);
                    return true;
                }

                return false;
            case 0x22 or 0x36 or 0x3C or 0x3F:
                return true; // ammo count, object state, distance, facing
            default:
                return false; // d-pad held, costume
        }
    }

    /// <summary>Runs a block; false once a condition standing on its own fails, which ends the block (at the top, the
    /// rest of that chunk).</summary>
    public bool Run(SimState s, IReadOnlyList<ScriptNode> nodes, string room, bool top = true)
    {
        var skipping = false;
        foreach (var n in nodes)
        {
            if (n.Chunk)
            {
                skipping = false;
                continue;
            }

            if (skipping)
            {
                continue;
            }

            bool ok;
            if (n.IsIf)
            {
                ok = Run(s, n.Conds!.All(c => Test(s, c)) ? n.Then : n.Else, room, top: false);
            }
            else if (RoomScripts.ConditionOps.Contains(n.Op))
            {
                ok = Test(s, n);
            }
            else
            {
                Command(s, n, room);
                ok = true;
            }

            if (!ok)
            {
                if (!top)
                {
                    return false;
                }

                skipping = true;
            }
        }

        return !skipping;
    }

    private void Command(SimState s, ScriptNode c, string room)
    {
        switch (c.Op)
        {
            case 0x05:
                s.SetFlag(c.Bank, c.Flag, c.Mode == 0 || (c.Mode == 2 && !s.Has(c.Bank, c.Flag)));
                break;
            case 0x08 when c.Field != Character:
                s.Fields[c.Field] = c.Value; // cutscenes write the character byte for a model swap: the player stays
                break;
            case 0x0C:
                s.Actions[c.Slot] = new ActionRecord
                {
                    Kind = "door", Slot = c.Slot, Zone = c.Zone, Stage = c.Stage, Room = c.Room, Lock = c.Lock, Key = c.Key, Probe = c.Probe,
                    Arrival = c.Arrival,
                };
                break;
            case 0x0D:
                s.Actions[c.Slot] = new ActionRecord { Kind = "action", Slot = c.Slot, Zone = c.Zone, Handler = c.Handler, Probe = c.Probe, Words = c.Words };
                break;
            case 0x12 when s.Actions.TryGetValue(c.Slot, out var rearm):
                rearm.Handler = c.Handler;
                rearm.Probe = c.Probe;
                rearm.Words = c.Words;
                break;
            case 0x13 when s.Actions.TryGetValue(c.Slot, out var arm):
                arm.Handler = c.Handler;
                arm.Probe = c.Probe;
                break;
            case 0x18:
                s.Actions[c.Slot] = new ActionRecord
                {
                    Kind = "item", Slot = c.Slot, Zone = c.Zone, Item = c.Item, Quantity = c.Quantity, Flag = c.Flag, Probe = c.Probe,
                };
                break;
            case 0x1B:
                s.Enemies.Add(c);
                break;
            case 0x20:
                s.Pos = (c.Pos[0], c.Pos[2]);
                break;
            case 0x1F:
                s.Objects[c.Slot] = (c.Pos[0], c.Pos[2]);
                break;
            case 0x30:
                s.Mods[(c.List, c.Index)] = (c.Box[0], c.Box[1], c.Box[2], c.Box[3], c.Mode);
                break;
            case 0x14:
                Start(s, room, c.Slot, c.Script);
                break;
            case 0x44:
                s.Events.Remove(c.Slot);
                break;
            case 0x24 or 0x2D:
                // A room action run by the script: the pick-up handlers on an item record hand the item over (the serum on
                // the shelf, the pass codes); door handlers send the player through (lifts); handler 7 sets its flag.
                if (!s.Actions.TryGetValue(c.Slot, out var record))
                {
                    break;
                }

                if (record.Kind == "item" && (c.Op == 0x2D || c.Action is 3 or 4 or 0x0D or 0x0F) && s.Has(7, record.Flag))
                {
                    Give(s, record);
                    s.Fields[PickedItem] = record.Item;
                }
                else if (record.Kind == "door" && c.Op == 0x24 && c.Action is 1 or 5 or 6)
                {
                    s.Fields[PendingDoor] = c.Slot;
                }
                else if (c.Op == 0x24 && c.Action == 7 && record.Words is { } words)
                {
                    s.SetFlag(words[0], words[1], words[2] != 0);
                }

                break;
            default:
                break;
        }
    }

    private void Start(SimState s, string room, int slot, int script)
    {
        if (script >= World(room).Events.Count)
        {
            return;
        }

        if (slot > 7)
        {
            slot = Enumerable.Range(0, 8).Cast<int?>().FirstOrDefault(i => !s.Events.ContainsKey(i!.Value)) ?? 7;
        }

        s.Events[slot] = new RunningEvent(room, script);
    }

    /// <summary>One frame of the event machine: every running event takes one step.</summary>
    private void StepEvents(SimState s)
    {
        foreach (var slot in s.Events.Keys.Order().ToList())
        {
            if (!s.Events.TryGetValue(slot, out var e))
            {
                continue;
            }

            if (e.Room != s.Room)
            {
                s.Events.Remove(slot);
                continue;
            }

            var script = World(e.Room).Events[e.Script];
            if (e.Pc >= script.Count)
            {
                s.Events.Remove(slot);
                continue;
            }

            var st = script[e.Pc];
            switch (st.Kind)
            {
                case EventStepKind.Block:
                    Run(s, st.Nodes, e.Room);
                    break;
                case EventStepKind.Wait:
                    if (!st.Nodes.Where(n => !n.IsIf && RoomScripts.ConditionOps.Contains(n.Op)).All(n => Test(s, n)))
                    {
                        continue; // still waiting
                    }

                    break;
                case EventStepKind.Start:
                    Start(s, e.Room, st.Slot, st.Script);
                    break;
                case EventStepKind.Stop:
                    s.Events.Remove(st.Slot);
                    break;
                case EventStepKind.Goto:
                    e.Script = st.Script;
                    e.Pc = 0;
                    continue;
                case EventStepKind.End:
                    s.Events.Remove(slot);
                    continue;
                default:
                    break;
            }

            if (s.Events.TryGetValue(slot, out var still) && ReferenceEquals(still, e))
            {
                e.Pc++;
            }
        }
    }

    /// <summary>Frames of the main script and the event machine until nothing has changed for a while; the transient
    /// bytes last the first frame only. Then a door a script chose takes the player through.</summary>
    private void Settle(SimState s, int quiet = 30, int frames = 600)
    {
        var calm = 0;
        for (var frame = 0; frame < frames; frame++)
        {
            var before = s.Signature();
            Run(s, World(s.Room).Main, s.Room);
            StepEvents(s);
            if (frame == 0)
            {
                s.Fields[UsedItem] = 0;
                s.Fields[PickedItem] = 0;
                s.ClearBank(9);
            }

            calm = s.Signature() == before ? calm + 1 : 0;
            if (calm >= quiet || (s.Events.Count == 0 && calm >= 2))
            {
                break;
            }
        }

        if (s.Fields.Remove(PendingDoor, out var pending) && pending is { } slot && s.Actions.TryGetValue(slot, out var door))
        {
            var target = TargetOf(s, door);
            if (Exists(target))
            {
                s.Pos = door.Arrival.Count == 2 ? (door.Arrival[0], door.Arrival[1]) : null;
                Enter(s, target);
            }
        }
    }

    // ---- moves ----------------------------------------------------------------------------------------------------

    public void Enter(SimState s, string room)
    {
        if (s.Room.Length > 0)
        {
            s.Fields[CameFrom] = Convert.ToInt32(s.Room[1..], 16);
        }

        s.Room = room;
        s.Fields[Stage] = room[0] - '1';
        s.Fields[RoomField] = Convert.ToInt32(room[1..], 16);
        s.Fields[Camera] = null;
        s.Fields[FwdAction] = NoAction;
        s.Fields[EntAction] = NoAction;
        s.Actions = [];
        s.Enemies = [];
        s.Events = [];
        s.Mods = [];
        s.Objects = [];
        s.ClearBank(4);
        s.ClearBank(9);
        Run(s, World(room).Init, room);
        foreach (var record in s.Actions.Values.Where(r => r.Kind == "item" && Rules.Absent.Contains(r.Item)))
        {
            s.SetFlag(7, record.Flag, false); // items this game never places (Jill's ink ribbons): the room clears them
        }

        Settle(s);
    }

    private static void Give(SimState s, ActionRecord record)
    {
        s.SetFlag(7, record.Flag, false);
        s.Inventory[record.Item] = s.Quantity(record.Item) + Math.Max(record.Quantity, 1);
    }

    /// <summary>An item lying in the room that can be reached (armed for a pick-up, and not behind a gate).</summary>
    public bool Present(SimState s, ActionRecord record)
    {
        if (!(record.Kind == "item" && record.Item > 0 && s.Has(7, record.Flag) && record.Zone[2] > 1 && (record.Probe & 0x01) != 0
            && (record.Handler ?? 4) is 3 or 4 or 0x0F))
        {
            return false;
        }

        var gate = Rules.Gates.FirstOrDefault(g => g.Room == s.Room && g.Item == record.Item);
        return gate is null || gate.Needs.All(f => s.Has(f.Bank, f.Flag));
    }

    public bool Pick(SimState s, int slot)
    {
        var record = s.Actions[slot];
        if (Where(s, record.Zone, record.Probe) is not { } spot)
        {
            return false;
        }

        s.Pos = spot;
        StandAt(s, record.Zone);
        Give(s, record);
        s.Fields[PickedItem] = record.Item;
        s.Fields[FwdAction] = slot;
        Settle(s);
        return true;
    }

    /// <summary>Stands at a room action (walking in, then the action key): its handler runs.</summary>
    public bool At(SimState s, int slot)
    {
        var a = s.Actions[slot];
        if (Where(s, a.Zone, a.Probe) is not { } spot || !OpenWay(s, null, slot))
        {
            return false;
        }

        s.Pos = spot;
        StandAt(s, a.Zone);
        s.Fields[FwdAction] = slot;
        s.Fields[EntAction] = slot;
        foreach (var rule in Rules.Spots.Where(r => r.Room == s.Room && r.Slot == slot))
        {
            Apply(s, rule);
        }

        var words = a.Words ?? [0, 0, 0];
        if (a.Handler == 7)
        {
            s.SetFlag(words[0], words[1], words[2] != 0);
        }
        else if (a.Handler == 9)
        {
            Start(s, s.Room, words[0], words[1]);
        }

        Settle(s);
        return true;
    }

    /// <summary>Uses an item from the inventory while standing at a room action; the item menu takes one the room
    /// reacted to, unless it's a tool kept.</summary>
    public bool Use(SimState s, int slot, int item)
    {
        var a = s.Actions[slot];
        if (Where(s, a.Zone, a.Probe) is not { } spot)
        {
            return false;
        }

        s.Pos = spot;
        StandAt(s, a.Zone);
        s.Fields[FwdAction] = slot;
        s.Fields[EntAction] = slot;
        foreach (var other in s.Actions.Values.ToList())
        {
            if (other.Handler == 7 && other.Words is { } w && w[0] is 4 or 9 && SameZone(other.Zone, a.Zone))
            {
                s.SetFlag(w[0], w[1], true);
            }
        }

        s.Fields[UsedItem] = item;
        var before = string.Join('/', StoryBanks.Select(s.BankSignature));
        foreach (var rule in Rules.Use.Where(r => r.Item == item && (r.Room ?? s.Room) == s.Room))
        {
            Apply(s, rule);
        }

        Settle(s);
        if (before != string.Join('/', StoryBanks.Select(s.BankSignature)) && s.Quantity(item) > 0 && !Rules.Keep.Contains(item))
        {
            s.Inventory[item]--;
            if (s.Inventory[item] <= 0)
            {
                s.Inventory.Remove(item);
            }
        }

        return true;
    }

    public void Kill(SimState s)
    {
        foreach (var e in s.Enemies.Where(e => e.KillFlag != 255))
        {
            s.SetFlag(3, e.KillFlag, true);
        }

        foreach (var rule in Rules.Kills.Where(r => r.Room == s.Room))
        {
            Apply(s, rule);
        }

        Settle(s);
    }

    public void Examine(SimState s, int item)
    {
        foreach (var rule in Rules.Examine.Where(r => r.Item == item && s.Quantity(item) > 0))
        {
            Apply(s, rule);
        }

        Settle(s);
    }

    /// <summary>Goes through a door if it opens (unlocking it with the key when held); the room, or null.</summary>
    public string? Door(SimState s, int slot)
    {
        var d = s.Actions[slot];
        if ((d.Handler ?? 1) != 1 || (d.Probe & 0x01) == 0 || !OpenWay(s, d, null) || Where(s, d.Zone, d.Probe) is null)
        {
            return null;
        }

        var locked = d.Lock;
        if ((locked & 0x80) != 0 && locked != 0xFF && !s.Has(2, locked & 0x3F))
        {
            var key = Rules.KeyAlias.GetValueOrDefault(d.Key, d.Key);
            if (key is 0 or 255 || (key != 254 && s.Quantity(key) == 0 && !Held(s).Contains(key)))
            {
                return null;
            }

            s.SetFlag(2, locked & 0x3F, true);
        }

        var target = TargetOf(s, d);
        if (!Exists(target))
        {
            return null;
        }

        s.Pos = d.Arrival.Count == 2 ? (d.Arrival[0], d.Arrival[1]) : null;
        Enter(s, target);
        return target;
    }

    private bool OpenWay(SimState s, ActionRecord? door, int? slot)
    {
        foreach (var way in Rules.Ways.Where(w => w.Room == s.Room))
        {
            if ((door is not null && way.To == TargetOf(s, door)) || (slot is not null && way.Slot == slot))
            {
                return way.Needs.All(f => s.Has(f.Bank, f.Flag)) && !way.Without.Any(f => s.Has(f.Bank, f.Flag));
            }
        }

        return true;
    }

    public HashSet<int> Held(SimState s) => [.. Rules.HeldBy.Where(h => h.Flags.All(f => s.Has(f.Bank, f.Flag))).Select(h => h.Item)];

    public static void Apply(SimState s, RouteRule rule)
    {
        foreach (var f in rule.Set)
        {
            s.SetFlag(f.Bank, f.Flag, true);
        }

        foreach (var f in rule.Clear)
        {
            s.SetFlag(f.Bank, f.Flag, false);
        }

        foreach (var item in rule.Take)
        {
            s.Inventory.Remove(item);
        }

        foreach (var (item, quantity) in rule.Give)
        {
            s.Inventory[item] = s.Quantity(item) + quantity;
        }
    }

    /// <summary>Whether the room asks a yes/no question whose answer its scripts test.</summary>
    public bool Asks(string room)
    {
        if (!Exists(room))
        {
            return false;
        }

        if (!_asks.TryGetValue(room, out var found))
        {
            var world = World(room);
            found = Walk(world.Init) || Walk(world.Main) || world.Events.Any(e => e.Any(st => Walk(st.Nodes)));
            _asks[room] = found;
        }

        return found;

        static bool Walk(IReadOnlyList<ScriptNode> nodes) => nodes.Any(n =>
            (n.IsIf ? n.Conds! : RoomScripts.ConditionOps.Contains(n.Op) ? [n] : []).Any(c => c.Op == 0x06 && c.Field == MenuChoice && c.Value is 0 or 1)
            || (n.IsIf && (Walk(n.Then) || Walk(n.Else))));
    }

    /// <summary>Items the room's main script or events test with "item used is" (or the used-item byte).</summary>
    public SortedSet<int> ReactsTo(string room)
    {
        if (!_reacts.TryGetValue(room, out var found))
        {
            found = [];
            var world = World(room);
            Walk(world.Main, found);
            foreach (var st in world.Events.SelectMany(e => e))
            {
                Walk(st.Nodes, found);
            }

            _reacts[room] = found;
        }

        return found;

        static void Walk(IReadOnlyList<ScriptNode> nodes, SortedSet<int> into)
        {
            foreach (var n in nodes.Where(n => n.IsIf))
            {
                into.UnionWith(n.Conds!.Where(c => c.Op == 0x10).Select(c => c.Item));
                into.UnionWith(n.Conds!.Where(c => c.Op == 0x06 && c.Field == UsedItem && c.Cmp == 0).Select(c => c.Value));
                Walk(n.Then, into);
                Walk(n.Else, into);
            }
        }
    }

    public static string TargetOf(SimState s, ActionRecord door)
    {
        var stage = door.Stage != 0 ? door.Stage : s.Room[0] - '0'; // 1-based, 0 = this stage
        if (s.Has(0, 0) && stage is 1 or 2)
        {
            stage += 5; // the stage-variant bit: the mansion revisited
        }

        return $"{stage}{door.Room:X2}";
    }

    private static bool Compare(int a, int cmp, int b) => cmp switch
    {
        0 => a == b,
        1 => a > b,
        2 => a >= b,
        3 => a < b,
        4 => a <= b,
        5 => a != b,
        _ => false,
    };

    private static bool SameZone(IReadOnlyList<int> a, IReadOnlyList<int> b) =>
        a[0] < b[0] + b[2] && b[0] < a[0] + a[2] && a[1] < b[1] + b[3] && b[1] < a[1] + a[3];
}
