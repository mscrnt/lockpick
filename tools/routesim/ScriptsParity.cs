#:project ../../src/Lockpick.GameAdapter.RE1/Lockpick.GameAdapter.RE1.csproj
#:property TargetFramework=net10.0
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
using System.Text.Json;
using Lockpick.GameAdapter.RE1.Routing;

// Dumps every room's scripts as Lockpick's C# reader reads them, in tools/routesim/world.py's JSON shape, so the two
// readers can be compared (tools/routesim/parity.py).
//   dotnet run tools/routesim/ScriptsParity.cs -- <install> jill|chris <out.json>
var usa = Directory.Exists(Path.Combine(args[0], "USA")) ? Path.Combine(args[0], "USA") : args[0];
var variant = args[1] == "jill" ? "1" : "0";
var rooms = new SortedDictionary<string, object>(StringComparer.Ordinal);
foreach (var file in Directory.GetDirectories(usa, "Stage*").SelectMany(d => Directory.GetFiles(d, $"ROOM*{variant}.RDT")).Order(StringComparer.Ordinal))
{
    var info = new FileInfo(file);
    if (info.Length < 0x94)
    {
        continue;
    }

    var scripts = RoomScripts.Read(File.ReadAllBytes(file));
    rooms[info.Name[4..7].ToUpperInvariant()] = new
    {
        init = scripts.Init.Select(Node).ToList(),
        main = scripts.Main.Select(Node).ToList(),
        events = scripts.Events.Select(e => e.Select(Step).ToList()).ToList(),
    };
}

File.WriteAllText(args[2], JsonSerializer.Serialize(rooms));
Console.WriteLine($"{rooms.Count} rooms");

static Dictionary<string, object> Step(EventStep s) => s.Kind switch
{
    EventStepKind.Block => new() { ["block"] = s.Nodes.Select(Node).ToList() },
    EventStepKind.Wait => new() { ["wait"] = s.Nodes.Select(Node).ToList() },
    EventStepKind.Start => new() { ["start"] = s.Script, ["slot"] = s.Slot },
    EventStepKind.Stop => new() { ["stop"] = s.Slot },
    EventStepKind.Goto => new() { ["goto"] = s.Script },
    _ => new() { ["end"] = true },
};

static Dictionary<string, object> Node(ScriptNode n)
{
    if (n.IsIf)
    {
        return new() { ["if"] = n.Conds!.Select(Node).ToList(), ["then"] = n.Then.Select(Node).ToList(), ["else"] = n.Else.Select(Node).ToList() };
    }

    if (n.Chunk)
    {
        return new() { ["op"] = 0, ["chunk"] = true };
    }

    var d = new Dictionary<string, object> { ["op"] = n.Op };
    switch (n.Op)
    {
        case 0x04: d["bank"] = n.Bank; d["flag"] = n.Flag; d["set"] = n.IsSet; break;
        case 0x05: d["bank"] = n.Bank; d["flag"] = n.Flag; d["mode"] = n.Mode switch { 1 => "clear", 2 => "toggle", _ => "set" }; break;
        case 0x06 or 0x07 or 0x22: d[n.Op == 0x22 ? "family" : "field"] = n.Field; d["cmp"] = n.Cmp; d["value"] = n.Value; break;
        case 0x08: d["field"] = n.Field; d["value"] = n.Value; break;
        case 0x0C:
            d["slot"] = n.Slot; d["zone"] = n.Zone; d["stage"] = n.Stage; d["room"] = n.Room; d["lock"] = n.Lock; d["key"] = n.Key;
            d["probe"] = n.Probe; d["arrival"] = n.Arrival; break;
        case 0x0D: d["slot"] = n.Slot; d["zone"] = n.Zone; d["handler"] = n.Handler; d["probe"] = n.Probe; d["words"] = n.Words; break;
        case 0x10 or 0x11 or 0x1A or 0x1D or 0x2C: d["item"] = n.Item; break;
        case 0x12: d["slot"] = n.Slot; d["handler"] = n.Handler; d["probe"] = n.Probe; d["words"] = n.Words; break;
        case 0x13: d["slot"] = n.Slot; d["handler"] = n.Handler; d["probe"] = n.Probe; break;
        case 0x14: d["slot"] = n.Slot; d["script"] = n.Script; break;
        case 0x18: d["slot"] = n.Slot; d["zone"] = n.Zone; d["item"] = n.Item; d["quantity"] = n.Quantity; d["flag"] = n.Flag; d["probe"] = n.Probe; break;
        case 0x1B: d["type"] = n.Type; d["killFlag"] = n.KillFlag; break;
        case 0x1F: d["slot"] = n.Slot; d["pos"] = n.Pos; break;
        case 0x20: d["pos"] = n.Pos; break;
        case 0x24 or 0x2D: d["slot"] = n.Slot; d["action"] = n.Action; break;
        case 0x30: d["list"] = n.List; d["index"] = n.Index; d["mode"] = n.Mode; d["box"] = n.Box; break;
        case 0x44: d["slot"] = n.Slot; break;
        default: break;
    }

    return d;
}
