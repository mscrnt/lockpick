using System.Text.Json;

namespace Lockpick.GameAdapter.RE1.Routing;

/// <summary>A flag change a rule makes: [bank, flag].</summary>
internal readonly record struct BankFlag(int Bank, int Flag);

/// <summary>A rule's effect (set and clear flags, take and give items) and the conditions some kinds test.</summary>
internal sealed class RouteRule
{
    public string? Room { get; init; }

    public string? To { get; init; }

    public int? Slot { get; init; }

    public int Item { get; init; }

    public IReadOnlyList<BankFlag> Needs { get; init; } = [];

    public IReadOnlyList<BankFlag> Without { get; init; } = [];

    public IReadOnlyList<BankFlag> Set { get; init; } = [];

    public IReadOnlyList<BankFlag> Clear { get; init; } = [];

    public IReadOnlyList<int> Take { get; init; } = [];

    public IReadOnlyList<(int Item, int Quantity)> Give { get; init; } = [];

    public IReadOnlyList<BankFlag> Flags { get; init; } = [];
}

/// <summary>Where a new game hands over control: the room, the flags set and cleared, the inventory, the position.</summary>
internal sealed record NewGameRule(string Room, IReadOnlyList<BankFlag> Set, IReadOnlyList<BankFlag> Clear, IReadOnlyList<(int Item, int Quantity)> Inventory, (int X, int Z)? Pos);

/// <summary>
/// What the room scripts don't say, per character (data/games/re1/route-rules.json, shared with tools/routesim): walls
/// and water (gates, ways), what the exe does (use, spots, kills, examine, heldBy), tools that aren't used up, Jill's
/// lockpick for the Sword-key doors, items her game never places, and the new game.
/// </summary>
internal sealed class RouteRules
{
    public required NewGameRule NewGame { get; init; }

    public IReadOnlySet<int> Keep { get; init; } = new HashSet<int>();

    public IReadOnlyDictionary<int, int> KeyAlias { get; init; } = new Dictionary<int, int>();

    public IReadOnlySet<int> Absent { get; init; } = new HashSet<int>();

    public IReadOnlyList<RouteRule> Gates { get; init; } = [];

    public IReadOnlyList<RouteRule> Ways { get; init; } = [];

    public IReadOnlyList<RouteRule> Use { get; init; } = [];

    public IReadOnlyList<RouteRule> Spots { get; init; } = [];

    public IReadOnlyList<RouteRule> Kills { get; init; } = [];

    public IReadOnlyList<RouteRule> Examine { get; init; } = [];

    public IReadOnlyList<RouteRule> HeldBy { get; init; } = [];

    public static RouteRules Parse(Stream json, string character)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var mine = root.TryGetProperty(character, out var c) ? c : default;
        var start = root.GetProperty("newGame").GetProperty(character);
        IReadOnlyList<RouteRule> List(string name) => mine.ValueKind == JsonValueKind.Object && mine.TryGetProperty(name, out var list)
            ? [.. list.EnumerateArray().Select(Rule)]
            : [];
        IReadOnlySet<int> Ints(string name) => mine.ValueKind == JsonValueKind.Object && mine.TryGetProperty(name, out var list)
            ? list.EnumerateArray().Select(e => e.GetInt32()).ToHashSet()
            : new HashSet<int>();
        return new RouteRules
        {
            NewGame = new NewGameRule(
                start.TryGetProperty("room", out var room) ? room.GetString()! : "106",
                Pairs(start, "set"),
                Pairs(start, "clear"),
                start.TryGetProperty("inventory", out var inv) ? [.. inv.EnumerateArray().Select(p => (p[0].GetInt32(), p[1].GetInt32()))] : [],
                start.TryGetProperty("pos", out var pos) ? (pos[0].GetInt32(), pos[1].GetInt32()) : null),
            Keep = Ints("keep"),
            Absent = Ints("absent"),
            KeyAlias = mine.ValueKind == JsonValueKind.Object && mine.TryGetProperty("keyAlias", out var alias)
                ? alias.EnumerateObject().ToDictionary(p => int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture), p => p.Value.GetInt32())
                : new Dictionary<int, int>(),
            Gates = List("gates"),
            Ways = List("ways"),
            Use = List("use"),
            Spots = List("spots"),
            Kills = List("kills"),
            Examine = List("examine"),
            HeldBy = List("heldBy"),
        };
    }

    private static List<BankFlag> Pairs(JsonElement e, string name) =>
        e.TryGetProperty(name, out var list) ? [.. list.EnumerateArray().Select(p => new BankFlag(p[0].GetInt32(), p[1].GetInt32()))] : [];

    private static RouteRule Rule(JsonElement e) => new()
    {
        Room = e.TryGetProperty("room", out var room) ? room.GetString() : null,
        To = e.TryGetProperty("to", out var to) ? to.GetString() : null,
        Slot = e.TryGetProperty("slot", out var slot) ? slot.GetInt32() : null,
        Item = e.TryGetProperty("item", out var item) ? item.GetInt32() : 0,
        Needs = Pairs(e, "needs"),
        Without = Pairs(e, "without"),
        Set = Pairs(e, "set"),
        Clear = Pairs(e, "clear"),
        Take = e.TryGetProperty("take", out var take) ? [.. take.EnumerateArray().Select(t => t.GetInt32())] : [],
        Give = e.TryGetProperty("give", out var give) ? [.. give.EnumerateArray().Select(p => (p[0].GetInt32(), p[1].GetInt32()))] : [],
        Flags = Pairs(e, "flags"),
    };
}
