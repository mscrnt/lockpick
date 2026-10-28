#:project ../../src/Lockpick.GameAdapter.RE1/Lockpick.GameAdapter.RE1.csproj
#:property TargetFramework=net10.0
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Lockpick.GameAdapter.RE1.Routing;

// Plays with Lockpick's C# route planner and writes the moves' labels (and splits), for comparing with
// tools/routesim/plan.py --json on the same install.
//   dotnet run tools/routesim/PlanParity.cs -- <install> jill|chris <out.json> [--splits <set id>]
var install = args[0];
var character = args[1];
var usa = Directory.Exists(Path.Combine(install, "USA")) ? Path.Combine(install, "USA") : install;
var variant = character == "jill" ? "1" : "0";
var repo = Path.GetFullPath(Path.Combine(AppContext.GetData("EntryPointFilePath") is string entry ? Path.GetDirectoryName(entry)! : ".", "..", ".."));
var scripts = new ConcurrentDictionary<string, RoomScripts?>();
var geometry = new ConcurrentDictionary<string, RoomGeometry?>();
byte[]? Bytes(string room)
{
    var path = Path.Combine(usa, $"Stage{room[0]}", $"ROOM{room}{variant}.RDT");
    return File.Exists(path) && new FileInfo(path).Length >= 0x94 ? File.ReadAllBytes(path) : null;
}

RoomScripts? Scripts(string room) => scripts.GetOrAdd(room, r => Bytes(r) is { } b ? RoomScripts.Read(b) : null);
RoomGeometry? Geometry(string room) => geometry.GetOrAdd(room, r => Bytes(r) is { } b ? RoomGeometry.Read(b) : null);
using var rulesFile = File.OpenRead(Path.Combine(repo, "data", "games", "re1", "route-rules.json"));
var rules = RouteRules.Parse(rulesFile, character);
var names = JsonDocument.Parse(File.ReadAllText(Path.Combine(repo, "data", "games", "re1", "names.json"))).RootElement.GetProperty("items");
string ItemName(int i) => names.TryGetProperty(i.ToString(System.Globalization.CultureInfo.InvariantCulture), out var n) ? n.GetString()! : $"item {i}";
var planner = new RoutePlanner(Scripts, Geometry, rules, ItemName, character);
var clock = Stopwatch.StartNew();
List<PlannedMove> moves;
SimState end;
IReadOnlyDictionary<string, int> answers = new Dictionary<string, int>();
if (args.Contains("--splits"))
{
    var id = args[Array.IndexOf(args, "--splits") + 1];
    var adapter = new Lockpick.GameAdapter.RE1.Re1Adapter();
    var set = adapter.CreateRunRules(adapter.Profiles[0])!.SplitSets.Single(s => s.Id == id);
    var splits = set.Splits.Select(PlanSplit.Of).OfType<PlanSplit>().ToList();
    (moves, end, answers) = planner.PlayBest(splits);
}
else
{
    (moves, end) = planner.Play(planner.NewSimulator(), null);
}

File.WriteAllText(args[2], JsonSerializer.Serialize(moves.Select(m => new { label = m.Label, split = m.Split }).ToList()));
Console.WriteLine($"{moves.Count} moves in {clock.Elapsed.TotalSeconds:0.0} s; ended in {end.Room}{(end.Quantity(42) > 0 ? " with the flare" : string.Empty)}"
    + (answers.Count > 0 ? $"; answering No in {string.Join(',', answers.Keys)}" : string.Empty));
