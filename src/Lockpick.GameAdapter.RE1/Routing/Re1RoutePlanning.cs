using System.Collections.Concurrent;
using System.Text.Json;
using Lockpick.Core.Routes;
using Lockpick.Core.Runs;
using Lockpick.Core.State;

namespace Lockpick.GameAdapter.RE1.Routing;

/// <summary>
/// Works out a route for any split set from the player's own install (the room files of the game being played): the
/// route simulator plays toward each split in turn and the playthrough becomes the route guide's steps. What the room
/// scripts don't say comes from data/games/re1/route-rules.json.
/// </summary>
internal static class Re1RoutePlanning
{
    private const string RulesResource = "Lockpick.GameAdapter.RE1.Data.route-rules.json";
    private const int HeaderSize = 0x94;

    public static Route? Plan(string dataDirectory, SplitSet set, Re1Knowledge knowledge, CancellationToken cancel)
    {
        var character = set.Character == PlayableCharacter.Chris ? "chris" : "jill";
        var variant = character == "jill" ? "1" : "0";
        var files = new ConcurrentDictionary<string, byte[]?>();
        byte[]? Bytes(string room) => files.GetOrAdd(room, r =>
        {
            var path = Path.Combine(dataDirectory, $"Stage{r[0]}", $"ROOM{r}{variant}.RDT");
            var info = new FileInfo(path);
            return info.Exists && info.Length >= HeaderSize ? File.ReadAllBytes(path) : null;
        });
        var scripts = new ConcurrentDictionary<string, RoomScripts?>();
        var geometry = new ConcurrentDictionary<string, RoomGeometry?>();
        RoomScripts? Scripts(string room) => scripts.GetOrAdd(room, r => Bytes(r) is { } b ? RoomScripts.Read(b) : null);
        RoomGeometry? Geometry(string room) => geometry.GetOrAdd(room, r => Bytes(r) is { } b ? RoomGeometry.Read(b) : null);

        RouteRules rules;
        using (var stream = typeof(Re1RoutePlanning).Assembly.GetManifestResourceStream(RulesResource)
            ?? throw new InvalidOperationException("Missing embedded RE1 data route-rules.json."))
        {
            rules = RouteRules.Parse(stream, character);
        }

        var planner = new RoutePlanner(Scripts, Geometry, rules, knowledge.Item, character);
        var splits = set.Splits.Select(PlanSplit.Of).OfType<PlanSplit>().ToList();
        var (moves, end, answers) = splits.Count > 0 ? planner.PlayBest(splits, cancel) : Full(planner, cancel);
        if (splits.Count == 0 && !(end.Room == "303" && end.Quantity(42) > 0))
        {
            return null;
        }

        var rooms = Directory.Exists(dataDirectory)
            ? Directory.GetDirectories(dataDirectory, "Stage*").SelectMany(d => Directory.GetFiles(d, $"ROOM*{variant}.RDT"))
                .Select(f => Path.GetFileName(f)[4..7].ToUpperInvariant()).Where(r => Scripts(r) is not null).Distinct().ToList()
            : [];
        var named = Labels(knowledge, character);
        var steps = planner.Export(moves, rooms, named, room => Scripts(room) is { } s ? Flatten(s) : []);
        var reached = moves.Count(m => m.Split is not null);
        var document = new
        {
            routes = new[]
            {
                new
                {
                    id = $"{set.Id}-planned",
                    name = $"{set.Name} (planned, work in progress)",
                    category = set.Category,
                    character,
                    verified = false,
                    source = $"Worked out by Lockpick from your game's room files for the split set \"{set.Name}\": {reached} of {splits.Count} splits reached"
                        + (answers.Count > 0 ? $"; answers No in {string.Join(", ", answers.Keys.Order(StringComparer.Ordinal))}" : string.Empty)
                        + ". Still being worked on and not checked in a real run: if a step is wrong, record a capture"
                        + " (Debug → Capture) and press the marker key where it goes wrong.",
                    steps,
                },
            },
        };
        using var json = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(document));
        return RouteFile.Parse(json).Single();
    }

    private static (List<PlannedMove>, SimState, IReadOnlyDictionary<string, int>) Full(RoutePlanner planner, CancellationToken cancel)
    {
        var (moves, end) = planner.Play(planner.NewSimulator(), null, cancel: cancel);
        return (moves, end, new Dictionary<string, int>());
    }

    /// <summary>Every node of a room's scripts, nested ifs included (for which flags and items its scripts test).</summary>
    private static IEnumerable<ScriptNode> Flatten(RoomScripts s)
    {
        static IEnumerable<ScriptNode> Walk(IEnumerable<ScriptNode> nodes)
        {
            foreach (var n in nodes)
            {
                yield return n;
                if (n.IsIf)
                {
                    foreach (var inner in Walk(n.Then).Concat(Walk(n.Else)))
                    {
                        yield return inner;
                    }
                }
            }
        }

        return Walk(s.Init).Concat(Walk(s.Main)).Concat(s.Events.SelectMany(e => e).SelectMany(st => Walk(st.Nodes)));
    }

    /// <summary>The confirmed flag labels for this character, by bank number and flag (data/games/re1/flag-labels.json).</summary>
    private static Dictionary<(int Bank, int Flag), string> Labels(Re1Knowledge knowledge, string character)
    {
        var banks = new Dictionary<string, int> { ["flagScenarioA"] = 0, ["flagScenarioB"] = 1, ["flagLocks"] = 2, ["flagRoomItems"] = 7 };
        var labels = new Dictionary<(int, int), string>();
        foreach (var label in knowledge.LabelOrder)
        {
            var name = label.Bank.TrimEnd("0123456789".ToCharArray());
            if (!banks.TryGetValue(name, out var bank) || string.IsNullOrEmpty(label.Label)
                || (label.Characters is { } only && !only.Contains(character, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            var word = label.Bank.Length > name.Length ? int.Parse(label.Bank[name.Length..], System.Globalization.CultureInfo.InvariantCulture) : 0;
            var flag = (word * 32) + 31 - System.Numerics.BitOperations.Log2(label.Mask);
            labels.TryAdd((bank, flag), label.Label);
        }

        return labels;
    }
}
