using System.Globalization;
using System.Text.Json;
using Lockpick.Core.Insight;
using Lockpick.Core.State;

namespace Lockpick.GameAdapter.RE1;

/// <summary>A flag's meaning confirmed in play, in the games it was confirmed in (null: both characters').</summary>
internal sealed record FlagLabel(string Bank, uint Mask, string Label, IReadOnlyList<string>? Characters)
{
    public bool For(string player) => Characters is null || Characters.Contains(player);
}

/// <summary>One room-script use of a flag: which room, which character's room file, and what it is (item id, enemy type or door target).</summary>
internal sealed record FlagUse(string Room, string Player, int Flag, int What, string? Target = null);

/// <summary>
/// What RE1's own data says each flag means: the room index (which item, enemy or door uses each flag, generated from
/// the game's room files by tools/rdt), item/enemy names and the flag labels confirmed in play. Embedded from data/.
/// </summary>
internal sealed class Re1Knowledge
{
    private const string ResourcePrefix = "Lockpick.GameAdapter.RE1.Data.";

    private Re1Knowledge(JsonDocument index, JsonDocument names, JsonDocument labels, JsonDocument rooms)
    {
        static int Int(JsonElement e, string key) => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : -1;
        static FlagUse Use(JsonElement e, string flag, string what) =>
            new(e.GetProperty("room").GetString()!, e.GetProperty("player").GetString()!, Int(e, flag), Int(e, what),
                e.TryGetProperty("target", out var t) ? t.GetString() : null);
        static Dictionary<int, string> Table(JsonElement e) =>
            e.EnumerateObject().ToDictionary(p => int.Parse(p.Name, CultureInfo.InvariantCulture), p => p.Value.GetString()!);

        var root = index.RootElement;
        Items = [.. root.GetProperty("items").EnumerateArray().Select(e => Use(e, "flag", "item"))];
        Enemies = [.. root.GetProperty("enemies").EnumerateArray().Select(e => Use(e, "flag", "type"))];
        Doors = [.. root.GetProperty("doors").EnumerateArray().Select(e => Use(e, "lockFlag", "key"))];
        Story = [.. root.GetProperty("story").EnumerateArray().Select(e =>
            new FlagUse(e.GetProperty("room").GetString()!, e.GetProperty("player").GetString()!, Int(e, "flag"),
                e.GetProperty("bank").GetString()![0]))];
        Documents = names.RootElement.TryGetProperty("documents", out var docs) ? Table(docs) : [];
        ItemNames = Table(names.RootElement.GetProperty("items"));
        foreach (var (id, name) in Table(names.RootElement.GetProperty("maps")))
        {
            ItemNames[id] = name;
        }

        EnemyNames = Table(names.RootElement.GetProperty("enemies"));
        Attacks = names.RootElement.TryGetProperty("attacks", out var attacks)
            ? attacks.EnumerateObject().Where(p => int.TryParse(p.Name, out _))
                .ToDictionary(p => int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture),
                    p => p.Value.EnumerateObject().ToDictionary(a => int.Parse(a.Name, System.Globalization.CultureInfo.InvariantCulture), a => a.Value.GetString()!))
            : [];
        LabelOrder = [.. labels.RootElement.GetProperty("labels").EnumerateArray()
            .Select(e => new FlagLabel(e.GetProperty("bank").GetString()!, Convert.ToUInt32(e.GetProperty("mask").GetString(), 16),
                e.GetProperty("label").GetString()!,
                e.TryGetProperty("characters", out var who) ? [.. who.EnumerateArray().Select(c => c.GetString()!)] : null))];
        Fills = labels.RootElement.GetProperty("labels").EnumerateArray()
            .Where(e => e.TryGetProperty("fills", out _))
            .ToDictionary(e => (e.GetProperty("fills").GetProperty("room").GetString()!, e.GetProperty("fills").GetProperty("item").GetInt32()),
                e => (e.GetProperty("bank").GetString()!, Convert.ToUInt32(e.GetProperty("mask").GetString(), 16)));
        Areas = rooms.RootElement.GetProperty("areas").EnumerateObject().ToDictionary(p => p.Name[0], p => p.Value.GetString()!);
        RoomNames = rooms.RootElement.GetProperty("rooms").EnumerateObject()
            .Where(p => p.Value.TryGetProperty("name", out _))
            .ToDictionary(p => p.Name, p => p.Value.GetProperty("name").GetString()!);
    }

    /// <summary>Story flags that put an item in place at its use spot in a room (room id, item id → bank field, mask).</summary>
    public Dictionary<(string Room, int Item), (string Bank, uint Mask)> Fills { get; }

    /// <summary>The confirmed labels in file order (roughly the order they happen in the game).</summary>
    public List<FlagLabel> LabelOrder { get; }

    /// <summary>
    /// What the flag means in <paramref name="player"/>'s game ("jill" or "chris"): its label confirmed there (one
    /// for that character first, then one for both), "" when it's hidden there, or null when its meaning in that game
    /// isn't known — a label confirmed only in the other character's game is never used (the same flag can be a
    /// different scene, e.g. Barry's rescue in Jill's game, meeting Rebecca in Chris's).
    /// </summary>
    public string? Label(string bankWord, uint mask, string player) =>
        (LabelOrder.Find(l => l.Bank == bankWord && l.Mask == mask && l.Characters?.Contains(player) == true)
         ?? LabelOrder.Find(l => l.Bank == bankWord && l.Mask == mask && l.Characters is null))?.Label;

    /// <summary>Area name by the room id's stage digit ('1' = Mansion 1F ...).</summary>
    public Dictionary<char, string> Areas { get; }

    public Dictionary<string, string> RoomNames { get; }

    public string RoomLabel(string room) => RoomNames.TryGetValue(room, out var name) ? $"{room} {name}" : room;

    public List<FlagUse> Items { get; }

    public List<FlagUse> Enemies { get; }

    public List<FlagUse> Doors { get; }

    /// <summary>Story flags set by a room's own script (What = 'A' or 'B', the bank).</summary>
    public List<FlagUse> Story { get; }

    public Dictionary<int, string> Documents { get; }

    public Dictionary<int, string> ItemNames { get; }

    public Dictionary<int, string> EnemyNames { get; }

    /// <summary>Damage per hit by enemy type → attack name (SHiiDO's chart; names.json "attacks").</summary>
    public Dictionary<int, Dictionary<int, string>> Attacks { get; }

    public static Re1Knowledge LoadEmbedded()
    {
        var assembly = typeof(Re1Knowledge).Assembly;
        JsonDocument Load(string file)
        {
            using var stream = assembly.GetManifestResourceStream(ResourcePrefix + file)
                ?? throw new InvalidOperationException($"Missing embedded RE1 data {file}.");
            return JsonDocument.Parse(stream);
        }

        using var index = Load("room-index.json");
        using var names = Load("names.json");
        using var labels = Load("flag-labels.json");
        using var rooms = Load("rooms.json");
        return new Re1Knowledge(index, names, labels, rooms);
    }

    public string Item(int id) => ItemNames.TryGetValue(id, out var name) ? name : $"item 0x{id:X2}";

    public string Enemy(int type) => EnemyNames.TryGetValue(type, out var name) ? name : $"enemy type {type}";
}

/// <summary>
/// Explains changes in RE1's flag banks with the room index and the confirmed labels: what's left (story/event flags,
/// unexpected bits, unknown item ids and enemy types) is unexplained. Flag n is bit 31 − n % 32 of word n / 32.
/// </summary>
internal sealed class Re1ChangeExplainer(Re1Knowledge knowledge) : IChangeExplainer
{
    private static readonly (string Bank, int Words, string Kind)[] Banks =
    [
        ("flagRoomItems", 8, "item"), ("flagEnemies", 8, "enemy"), ("flagLocks", 2, "lock"), ("flagRoom", 5, "room"),
        ("flagScenarioA", 4, "story A"), ("flagScenarioB", 8, "story B"),
    ];

    public ChangeReport Explain(GameSnapshot previous, GameSnapshot current)
    {
        if (previous.InGameplay is not { HasValue: true, Value: true } || current.InGameplay is not { HasValue: true, Value: true }
            || !current.Location.TryGet(out var location) || !previous.Location.TryGet(out var previousLocation)
            || (current.GameTime.TryGet(out var now) && previous.GameTime.TryGet(out var before) && now < before))
        {
            return ChangeReport.Empty; // not playing, or a new game / loaded save (everything resets at once)
        }

        var room = location.Id;
        var player = Raw(current, "character") is { } c && (c & 3) == 1 ? "jill" : "chris";
        var unexplained = new List<UnexplainedChange>();
        var known = new List<string>();
        if (previousLocation != location)
        {
            known.Add($"entered room {room}");
        }

        var actions = new List<string>();
        var picked = new HashSet<int>();
        foreach (var (bank, words, kind) in Banks)
        {
            for (var word = 0; word < words; word++)
            {
                var key = $"{bank}{word}";
                if (Raw(previous, key) is not { } old || Raw(current, key) is not { } value || old == value)
                {
                    continue;
                }

                for (var bit = 0; bit < 32; bit++)
                {
                    var mask = 1u << bit;
                    if (((old ^ value) & mask) == 0)
                    {
                        continue;
                    }

                    var set = (value & mask) != 0;
                    var flag = (word * 32) + (31 - bit);
                    if (bank == "flagRoomItems" && !set && Find(knowledge.Items, flag, room, player) is { } taken && !NoRibbonFor(player, taken))
                    {
                        picked.Add(taken.What);
                    }

                    var story = bank.StartsWith("flagScenario", StringComparison.Ordinal);
                    var fallen = bank == "flagEnemies" && set && flag == NotRemembered ? Fallen(previous, current, previousLocation == location) : null;
                    if ((fallen is null ? Explain(bank, word, mask, flag, set, room, player) : $"{fallen} died") is { } explanation)
                    {
                        if (explanation.Length > 0)
                        {
                            known.Add(explanation);
                            if (!story && !explanation.StartsWith("first visit", StringComparison.Ordinal))
                            {
                                actions.Add(explanation); // pickups, kills, unlocks, maps and documents taken
                            }
                        }
                    }
                    else if (story && !set)
                    {
                        known.Add($"story {bank[^1]} #{flag} ended"); // a state set earlier ends; not worth a question
                    }
                    else
                    {
                        unexplained.Add(new UnexplainedChange(
                            $"{key}:0x{mask:X8}:{(set ? "set" : "clear")}",
                            $"{kind} flag #{flag} {(set ? "set" : "cleared")} ({key} bit 0x{mask:X}) in room {room}",
                            ByAction: story || bank == "flagRoomItems"));
                    }
                }
            }
        }

        // Inventory: items used up, handed over or combined are actions that story flags around them belong to.
        var had = Inventory(previous);
        var has = Inventory(current);
        var boxBefore = Box(previous);
        var boxAfter = Box(current);
        var gone = had.Except(has).Where(id => !boxAfter.Contains(id) || boxBefore.Contains(id)).ToList();
        var came = has.Except(had).Where(id => !picked.Contains(id) && !boxBefore.Contains(id)).ToList();
        if (gone.Count > 0 && came.Count > 0)
        {
            actions.Add($"combined {string.Join(" + ", gone.Select(knowledge.Item))} into {string.Join(", ", came.Select(knowledge.Item))}");
        }
        else
        {
            actions.AddRange(gone.Select(id => $"used {knowledge.Item(id)}"));
            actions.AddRange(came.Select(id => $"received {knowledge.Item(id)}"));
        }

        // Getting hurt — named by the damage chart when one enemy here deals that much, or poison — and pushing
        // (stateFlags 0x40): start and end events too (Yawn's first bite, the statue falls).
        if (Raw(previous, "hp") is { } hpBefore && Raw(current, "hp") is { } hpNow && (short)hpNow < (short)hpBefore)
        {
            var amount = (short)hpBefore - (short)hpNow;
            var cause = Attack(current, amount)
                ?? ((Raw(current, "playerStatus") is { } status && (status & PoisonedBit) != 0) ? "poison" : null);
            actions.Add($"took damage ({(short)hpBefore} → {(short)hpNow}{(cause is null ? string.Empty : $", {cause}")})");
        }

        if (Raw(previous, "stateFlags") is { } sfBefore && Raw(current, "stateFlags") is { } sfNow && ((sfBefore ^ sfNow) & PushingBit) != 0)
        {
            if ((sfNow & PushingBit) != 0)
            {
                _pushStarted = current.CapturedAt;
                actions.Add("started pushing something");
            }
            else
            {
                var seconds = _pushStarted is { } start ? (current.CapturedAt - start).TotalSeconds : 0;
                _pushStarted = null;
                actions.Add(seconds >= 0.5
                    ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"pushed something ({seconds:0.0} s)")
                    : "stopped pushing");
            }
        }

        // Enemies falling (HP to 0 or below) and getting back up in the room the player is in (zombies do).
        if (previousLocation != location)
        {
            _down.Clear();
        }
        else
        {
            for (var slot = 0; slot < 8; slot++)
            {
                if (Raw(current, $"enemy{slot}Flags") is not { } active || (active & 1) == 0
                    || Raw(previous, $"enemy{slot}Hp") is not { } was || Raw(current, $"enemy{slot}Hp") is not { } hp
                    || Raw(current, $"enemy{slot}Type") is not { } type)
                {
                    continue;
                }

                if ((short)was > 0 && (short)hp <= 0)
                {
                    _down[slot] = (int)type;
                }
                else if ((short)hp > 0 && _down.TryGetValue(slot, out var downType) && downType == (int)type)
                {
                    _down.Remove(slot);
                    actions.Add($"{knowledge.Enemy((int)type)} got back up");
                }
            }
        }

        // Enemies appearing in the room the player is in (not the slots refilled by a room load).
        if (previousLocation == location)
        {
            var present = ActiveEnemies(previous);
            actions.AddRange(ActiveEnemies(current).Except(present).Where(type => type < FirstNpcType).Select(type => $"{knowledge.Enemy(type)} appears"));
        }

        known.AddRange(actions.Where(a => !known.Contains(a)));

        // Ids the name tables don't know. Enemies only once the room has loaded (same room in both polls).
        for (var slot = 0; slot < 8; slot++)
        {
            if (Raw(current, $"inv{slot}") is { } entry && (entry & 0xFF) is var id and > 0 && !knowledge.ItemNames.ContainsKey((int)id))
            {
                unexplained.Add(new UnexplainedChange($"item:0x{id:X2}", $"unknown item id 0x{id:X2} in inventory slot {slot + 1}"));
            }

            if (previousLocation == location && Raw(current, $"enemy{slot}Flags") is { } flags && (flags & 1) != 0
                && Raw(current, $"enemy{slot}Type") is { } type && !knowledge.EnemyNames.ContainsKey((int)type))
            {
                unexplained.Add(new UnexplainedChange($"enemy:{type}", $"unknown enemy type {type} in room {room}"));
            }
        }

        return new ChangeReport(unexplained, known) { Actions = actions };
    }

    /// <summary>Room-bank flags from here on are maps taken (flag = item id + 46), from 130 documents (item id + 35).</summary>
    private const int FirstMapFlag = 124;

    /// <summary>stateFlags bit set while the player pushes an object (statue in 202, stairs in 107; live 2026-10-13).</summary>
    private const uint PushingBit = 0x40;

    /// <summary>playerStatus bit set while the player is poisoned (docs/MEMORY_MAP.md; the status screen's Poison band).</summary>
    private const uint PoisonedBit = 0x02;

    /// <summary>Enemies down in this room (slot → type), to tell when one gets back up.</summary>
    private readonly Dictionary<int, int> _down = [];

    /// <summary>When the push under way started (its length is told when it ends).</summary>
    private DateTimeOffset? _pushStarted;

    /// <summary>A hit named from its damage and the enemies in the room ("Zombie ankle bite (6)"), when only one fits.</summary>
    private string? Attack(GameSnapshot snapshot, int amount)
    {
        var matches = Enumerable.Range(0, 8)
            .Where(i => Raw(snapshot, $"enemy{i}Flags") is { } flags && (flags & 1) != 0)
            .Select(i => (int)(Raw(snapshot, $"enemy{i}Type") ?? uint.MaxValue))
            .Where(type => knowledge.Attacks.TryGetValue(type, out var hits) && hits.ContainsKey(amount))
            .Select(type => (Enemy: knowledge.Enemy(type).Split(" (")[0], Attack: knowledge.Attacks[type][amount]))
            .Distinct().ToList();
        return matches.Count == 1 ? $"{matches[0].Enemy} {matches[0].Attack} ({amount})" : null;
    }

    /// <summary>Kill flag for enemies a room script doesn't remember (em_set kill flag 255).</summary>
    private const int NotRemembered = 255;

    /// <summary>Actor types from here on are people (Barry, Rebecca...), not enemies: they don't "appear" in the feed.</summary>
    private const int FirstNpcType = 0x20;

    /// <summary>
    /// The enemy behind kill flag 255, which every unremembered enemy shares: the one that fell in this room (this poll,
    /// or earlier while the room stayed loaded), when only one kind did.
    /// </summary>
    private string? Fallen(GameSnapshot previous, GameSnapshot current, bool sameRoom)
    {
        var types = Enumerable.Range(0, 8)
            .Where(slot => Raw(previous, $"enemy{slot}Hp") is { } was && (short)was > 0 && Raw(current, $"enemy{slot}Hp") is { } hp && (short)hp <= 0)
            .Select(slot => (int)(Raw(current, $"enemy{slot}Type") ?? 0))
            .Concat(sameRoom ? _down.Values : [])
            .Where(type => type < FirstNpcType)
            .Distinct().ToList();
        return types.Count == 1 ? knowledge.Enemy(types[0]) : null;
    }

    // Jill's game never places ink ribbons (Re1ProgressTracker.InkRibbon): her rooms clear their ribbon flags as she
    // enters (live 2026-10-17: flag 39 on entering 107, nothing reached the inventory), which isn't a pickup.
    private const int InkRibbon = 0x2F;

    private static bool NoRibbonFor(string player, FlagUse item) => player == "jill" && item.What == InkRibbon;
    private const int FirstDocumentFlag = 130;

    /// <summary>
    /// The game's rooms-visited table (0x4D31E0: first flag per stage; 0x4D31E8: rooms per stage), read by the map code
    /// at 0x48773B / 0x487755: flag = base[stage] + room. Stages 6 and 7 (the mansion revisited) use 1 and 2.
    /// </summary>
    private static readonly int[] StageVisitBase = [0, 32, 63, 82, 100];
    private static readonly int[] StageRooms = [32, 31, 19, 18, 24];

    private static string VisitedRoom(int flag)
    {
        var stage = Array.FindLastIndex(StageVisitBase, b => b <= flag);
        return $"{stage + 1}{flag - StageVisitBase[stage]:X2}";
    }

    /// <summary>The room id the room scripts use: the mansion revisited (6xx, 7xx) runs the 1xx/2xx scripts' flags.</summary>
    private static bool SameRoom(string a, string b) =>
        a == b || (a[1..] == b[1..] && (a[0], b[0]) is ('1', '6') or ('6', '1') or ('2', '7') or ('7', '2'));

    private static HashSet<int> Inventory(GameSnapshot s) =>
        [.. Enumerable.Range(0, 8).Select(i => Raw(s, $"inv{i}") is { } v ? (int)(v & 0xFF) : 0).Where(id => id > 0)];

    private static HashSet<int> ActiveEnemies(GameSnapshot s) =>
        [.. Enumerable.Range(0, 8)
            .Where(i => Raw(s, $"enemy{i}Flags") is { } f && (f & 1) != 0 && Raw(s, $"enemy{i}Hp") is { } hp && (short)hp > 0)
            .Select(i => (int)(Raw(s, $"enemy{i}Type") ?? 0))];

    private static HashSet<int> Box(GameSnapshot s) =>
        [.. Enumerable.Range(0, 48).Select(i => Raw(s, $"box{i}") is { } v ? (int)(v & 0xFF) : 0).Where(id => id > 0)];

    private static uint? Raw(GameSnapshot snapshot, string field) =>
        snapshot.RawFields.TryGetValue(field, out var value) && value.HasValue ? (uint)value.Value : null;

    private static FlagUse? Find(List<FlagUse> uses, int flag, string room, string player)
    {
        var matches = uses.Where(u => u.Flag == flag && (u.Player == player || u.Player == "both")).ToList();
        return matches.Find(u => u.Room == room || u.Target == room) ?? matches.FirstOrDefault();
    }

    /// <summary>A description of an explained change ("" when not worth showing), or null when it is unexplained.</summary>
    private string? Explain(string bank, int word, uint mask, int flag, bool set, string room, string player)
    {
        if (knowledge.Label($"{bank}{word}", mask, player) is { } label)
        {
            return set ? label : string.Empty;
        }

        switch (bank)
        {
            case "flagRoomItems" when !set && Find(knowledge.Items, flag, room, player) is { } ribbon && NoRibbonFor(player, ribbon):
                return string.Empty; // the room clears the flag on entering, nothing is picked up
            case "flagRoomItems" when Find(knowledge.Items, flag, room, player) is { } item:
                return set ? $"put {knowledge.Item(item.What)} back" : $"picked up {knowledge.Item(item.What)}";
            case "flagEnemies" when set && flag == NotRemembered:
                return "killed an enemy that doesn't stay dead";
            case "flagEnemies" when set && Find(knowledge.Enemies, flag, room, player) is { } enemy:
                return $"killed {knowledge.Enemy(enemy.What)}";
            case "flagLocks" when Find(knowledge.Doors, flag, room, player) is { } door:
                return $"{(set ? "unlocked" : "locked")} door {door.Room}→{door.Target}";
            case "flagLocks":
                // Lock flags no door uses belong to locked desks and boxes (Jill's lockpick opened two in 102 and 111).
                return set ? $"opened something locked in room {room} (lock #{flag})" : string.Empty;

            // Rooms visited (the map's "explored" marks): flags below FirstMapFlag, set on the first visit.
            case "flagRoom" when set && flag < FirstMapFlag:
                return $"first visit to room {VisitedRoom(flag)}";
            case "flagRoom" when set && flag < FirstDocumentFlag:
                return $"took {knowledge.Item(flag - 46)}";
            case "flagRoom" when set:
                return $"took {(knowledge.Documents.TryGetValue(flag - 35, out var doc) ? doc : $"document {flag - 35}")}";
            case "flagScenarioA" or "flagScenarioB"
                when knowledge.Story.Exists(u => u.Flag == flag && u.What == bank[^1] && SameRoom(u.Room, room) && (u.Player == player || u.Player == "both")):
                return $"room {room} event (story {bank[^1]} #{flag})";
            default:
                return null;
        }
    }
}
