#:project ../../src/Lockpick.GameAdapter.RE1/Lockpick.GameAdapter.RE1.csproj
#:property TargetFramework=net10.0
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
using System.Text.Json;
using Lockpick.Core.Maps;
using Lockpick.Core.Routes;
using Lockpick.Core.State;
using Lockpick.GameAdapter.RE1;

// Feeds a simulated playthrough (plan.py --trace) to the app's own route guide and reports how it follows: the step
// it ends on, any step it goes back to, states with no way to the goal, and the longest it waits on one step.
//   dotnet run tools/routesim/GuideCheck.cs -- <route id> <trace.jsonl> [--verbose]
var routeId = args[0];
var adapter = new Re1Adapter();
var maps = adapter.CreateRoomMaps(Environment.GetEnvironmentVariable("RE1_EXE") ?? @"G:\GOG Games\Resident Evil\ResidentEvil.exe");
var route = adapter.CreateRunRules(adapter.Profiles[0]).Routes.Single(r => r.Id == routeId);
var rooms = new Dictionary<string, RoomMap>();
for (var st = 1; st <= 7; st++) for (var r = 0; r < 0x20; r++) if (maps.Load(new RoomLocation(st, r), route.Character) is { } m) rooms[m.RoomId] = m;
var guide = new RouteGuide(route, new RoutePlanner(rooms), maps, adapter.ItemName, adapter.RoomName);
var t0 = DateTimeOffset.UtcNow;
int line = 0, lastIndex = -1, backwards = 0, noWay = 0, sinceChange = 0, longest = 0;
string longestAt = "";
var verbose = args.Contains("--verbose");
foreach (var json in File.ReadLines(args[1]))
{
    var o = JsonDocument.Parse(json).RootElement;
    var room = o.GetProperty("room").GetString()!;
    var raw = o.GetProperty("fields").EnumerateObject().ToDictionary(p => p.Name, p => Field<long>.Valid(p.Value.GetInt64()));
    raw["character"] = Field<long>.Valid(route.Character == PlayableCharacter.Jill ? 1 : 0);
    var inventory = o.GetProperty("inventory").EnumerateArray().Select((e, i) => new HeldItem(i, e[0].GetInt32(), e[1].GetInt32())).ToList();
    var snap = new GameSnapshot
    {
        CapturedAt = t0.AddSeconds(line * 5),
        InGameplay = Field<bool>.Valid(true),
        Character = Field<PlayableCharacter>.Valid(route.Character),
        Location = Field<RoomLocation>.Valid(new RoomLocation(room[0] - '0', Convert.ToInt32(room[1..], 16))),
        Position = Field<Position>.Valid(new Position(o.GetProperty("x").GetInt32(), 0, o.GetProperty("z").GetInt32())),
        GameTime = Field<TimeSpan>.Valid(TimeSpan.FromSeconds(line * 5)),
        Inventory = Field<IReadOnlyList<HeldItem>>.Valid(inventory),
        RawFields = raw,
    };
    // Twice, as the app polls more than once per move (the guide's leave-the-room rule needs a second look).
    guide.Update(snap);
    var v = guide.Update(snap);
    line++;
    if (v is null) { Console.WriteLine($"{line} {room}: no view"); continue; }
    if (v.StepIndex < lastIndex) { backwards++; Console.WriteLine($"{line} {room}: BACK {lastIndex + 1} -> {v.StepIndex + 1} ({v.Text}) after '{o.GetProperty("label").GetString()}'"); }
    if (v.Plan is null && !v.Finished && rooms.ContainsKey(room)) { noWay++; if (verbose || noWay <= 15) Console.WriteLine($"{line} {room}: NO WAY to '{v.Text}' ({v.Note})"); }
    if (v.StepIndex != lastIndex)
    {
        if (verbose) Console.WriteLine($"{line} {room}: {v.Progress} {v.Text}");
        sinceChange = 0;
    }
    else if (++sinceChange > longest) { longest = sinceChange; longestAt = $"{line} {room}: {v.Progress} {v.Text}"; }
    lastIndex = v.StepIndex;
}
Console.WriteLine($"{line} states; last step {lastIndex + 1} of {route.Steps.Count} ({(route.Steps[Math.Max(lastIndex, 0)].Kind)}); {backwards} backwards; {noWay} states with no way; longest wait {longest} moves at {longestAt}");
