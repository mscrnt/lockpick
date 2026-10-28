#:project ../../src/Lockpick.GameAdapter.RE1/Lockpick.GameAdapter.RE1.csproj
#:property TargetFramework=net10.0
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
using System.Text.Json;
using Lockpick.Core.Runs;
using Lockpick.GameAdapter.RE1;

// Turns LiveSplit files into split sets the way Lockpick's Runs page imports them (LiveSplitFile.ToSplitSet: triggers
// borrowed from the built-in sets by name, then item names; the rest manual), written as tools/routesim splits for
// route_for_splits.py.
//   dotnet run tools/routesim/LssToSplits.cs -- <out folder> <file.lss>...
var adapter = new Re1Adapter();
var rules = adapter.CreateRunRules(adapter.Profiles[0])!;
var items = new Dictionary<string, int>();
for (var id = 1; id < 256; id++)
{
    if (adapter.ItemName(id) is { } name)
    {
        items.TryAdd(LiveSplitFile.Normalize(name), id);
    }
}

Directory.CreateDirectory(args[0]);
foreach (var path in args.Skip(1))
{
    var run = LiveSplitFile.Read(path);
    var set = LiveSplitFile.ToSplitSet(run, path, rules.SplitSets, n => items.TryGetValue(LiveSplitFile.Normalize(n), out var i) ? i : null);
    var character = set.Character is { } c && c.ToString() is var s && s is "Jill" or "Chris" ? s.ToLowerInvariant()
        : path.Contains("chris", StringComparison.OrdinalIgnoreCase) ? "chris" : "jill";
    var splits = set.Splits.Select(sp => sp.Trigger switch
    {
        RoomEntered r => (object)new { name = sp.Name, kind = "room", room = r.Room, from = r.From, otherStage = r.FromOtherStage },
        ItemObtained i => new { name = sp.Name, kind = "item", item = i.ItemId },
        RunFinished => new { name = sp.Name, kind = "end" },
        _ => new { name = sp.Name, kind = "manual" },
    }).ToList();
    var outPath = Path.Combine(args[0], set.Id + ".json");
    File.WriteAllText(outPath, JsonSerializer.Serialize(new { id = set.Id, name = set.Name, category = set.Category, character, splits }));
    Console.WriteLine($"{set.Id}: {splits.Count} splits, {set.Splits.Count(x => x.Trigger is ManualSplit)} manual ({character})");
}
