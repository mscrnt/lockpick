using System.Collections.Concurrent;
using System.Text.Json;
using Lockpick.Core.Routes;
using Lockpick.Core.Runs;
using Lockpick.Core.State;

namespace Lockpick.GameAdapter.RE1.Routing;

/// <summary>One move of a simulated playthrough, with what it changed (tools/routesim/plan.py's step records).</summary>
internal sealed class PlannedMove
{
    public required string Label { get; init; }

    public required string Kind { get; init; } // door, take, use, spot, kill, examine

    public required string Room { get; init; }

    public required string To { get; init; }

    public IReadOnlyList<int>? Zone { get; init; }

    public int? Item { get; init; }

    public int? Key { get; init; }

    public bool Push { get; init; }

    public (int X, int Z)? Object { get; init; }

    public bool AnswerNo { get; init; }

    public IReadOnlyList<(int Bank, int Flag, bool Set)> Flags { get; init; } = [];

    public IReadOnlyList<int> Got { get; init; } = [];

    public IReadOnlyList<int> Lost { get; init; } = [];

    public IReadOnlyList<string> Gained { get; set; } = [];

    public string? Split { get; set; }

    public int Index { get; set; }
}

/// <summary>A split as a goal for the planner: entering a room (from a room, another stage, or anywhere), getting an
/// item (in a room), or the ending.</summary>
internal sealed record PlanSplit(string Name, string? Room = null, string? From = null, bool FromOtherStage = false, int? Item = null, string? In = null, bool End = false)
{
    public static PlanSplit? Of(SplitDefinition split) => split.Trigger switch
    {
        RoomEntered r => new PlanSplit(split.Name, Room: r.Room, From: r.From, FromOtherStage: r.FromOtherStage),
        ItemObtained i => new PlanSplit(split.Name, Item: i.ItemId, In: i.Room),
        RunFinished => new PlanSplit(split.Name, End: true),
        _ => null,
    };

    public bool Reached(SimState a, SimState b)
    {
        if (End)
        {
            return b.Room == "303" && b.Quantity(42) > 0;
        }

        if (Item is { } item)
        {
            return b.Quantity(item) > a.Quantity(item) && (In is null || a.Room == In);
        }

        if (FromOtherStage)
        {
            return b.Room == Room && a.Room[0] != Room![0];
        }

        return From is null ? b.Room == Room && a.Room != Room : b.Room == Room && a.Room == From;
    }
}

/// <summary>
/// Plays RE1 through in the simulator (tools/routesim/plan.py): from a new game, the nearest move that makes progress,
/// or with a split set, the way to each split in turn when it's reachable and the nearest progress when it isn't. The
/// yes/no questions are answered the way that reaches the most splits (searched in parallel). The playthrough becomes
/// a <see cref="Route"/> (tools/routesim/export.py).
/// </summary>
internal sealed class RoutePlanner
{
    private const int MaxStates = 6000;
    private const int GoalStates = 900;
    private readonly Func<string, RoomScripts?> _scripts;
    private readonly Func<string, RoomGeometry?> _geometry;
    private readonly RouteRules _rules;
    private readonly Func<int, string> _itemName;
    private readonly string _character;

    public RoutePlanner(Func<string, RoomScripts?> scripts, Func<string, RoomGeometry?> geometry, RouteRules rules, Func<int, string> itemName, string character)
    {
        _scripts = scripts;
        _geometry = geometry;
        _rules = rules;
        _itemName = itemName;
        _character = character;
    }

    public Simulator NewSimulator(IReadOnlyDictionary<string, int>? answers = null) =>
        new(_scripts, _geometry, _rules) { Answers = answers is null ? [] : new Dictionary<string, int>(answers) };

    private sealed record Option(string Text, string Kind, Func<SimState, bool> Apply, PlannedMove Meta);

    private List<Option> Moves(Simulator sim, SimState s)
    {
        var options = new List<Option>();
        PlannedMove Meta(string kind, IReadOnlyList<int>? zone = null, int? item = null, int? key = null, string? toRoom = null, bool push = false, (int, int)? obj = null)
        {
            var target = kind == "door" ? toRoom : s.Room;
            return new PlannedMove
            {
                Label = string.Empty, Kind = kind, Room = s.Room, To = toRoom ?? s.Room, Zone = zone, Item = item, Key = key, Push = push, Object = obj,
                AnswerNo = sim.Answers.GetValueOrDefault(s.Room) != 0 || (target is not null && sim.Answers.GetValueOrDefault(target) != 0),
            };
        }

        foreach (var (slot, a) in s.Actions.OrderBy(a => a.Key))
        {
            if (a.Kind == "door" && (a.Handler ?? 1) is 7 or 9)
            {
                options.Add(new Option($"step up to door {slot}", "spot", t => sim.At(t, slot), Meta("spot", a.Zone)));
            }
            else if (a.Kind == "door")
            {
                options.Add(new Option($"door {slot}", "door", t => sim.Door(t, slot) is not null, Meta("door", key: a.Key, toRoom: Simulator.TargetOf(s, a))));
            }
            else if (a.Kind == "item" && sim.Present(s, a))
            {
                options.Add(new Option($"take {_itemName(a.Item)}", "take", t => sim.Pick(t, slot), Meta("take", a.Zone, a.Item)));
            }
            else if (a.Kind == "action")
            {
                var words = a.Words ?? [0, 0, 0];
                if (a.Handler == 7 && words[0] == 9)
                {
                    var item = Simulator.ItemUseBase + words[1];
                    if (s.Quantity(item) > 0)
                    {
                        options.Add(new Option($"use {_itemName(item)}", "use", t => sim.Use(t, slot, item), Meta("use", a.Zone, item)));
                    }

                    continue;
                }

                if ((a.Handler == 7 && words[0] == 4) || a.Handler == 9)
                {
                    foreach (var item in sim.ReactsTo(s.Room).Where(i => s.Quantity(i) > 0))
                    {
                        options.Add(new Option($"use {_itemName(item)} at spot {slot}", "use", t => sim.Use(t, slot, item), Meta("use", a.Zone, item)));
                    }
                }

                if (a.Handler is 7 or 9)
                {
                    var push = (a.Probe & 0x04) != 0 && (a.Probe & 0x01) == 0;
                    options.Add(new Option($"step on spot {slot}{(push ? " (push something onto it)" : string.Empty)}", "spot", t => sim.At(t, slot),
                        Meta("spot", a.Zone, push: push, obj: push ? NearestObject(s, a.Zone) : null)));
                }
            }
        }

        foreach (var rule in _rules.Examine)
        {
            if (s.Quantity(rule.Item) > 0 && !rule.Give.All(g => s.Quantity(g.Item) > 0))
            {
                options.Add(new Option($"examine {_itemName(rule.Item)}", "examine", t =>
                {
                    sim.Examine(t, rule.Item);
                    return true;
                }, Meta("examine", item: rule.Item)));
            }
        }

        if (s.Enemies.Any(e => e.KillFlag != 255 && !s.Has(3, e.KillFlag)))
        {
            options.Add(new Option("kill the enemies", "kill", t =>
            {
                sim.Kill(t);
                return true;
            }, Meta("kill")));
        }

        return options;
    }

    private static (int, int)? NearestObject(SimState s, IReadOnlyList<int> zone)
    {
        if (s.Objects.Count == 0)
        {
            return null;
        }

        double cx = zone[0] + (zone[2] / 2.0), cz = zone[1] + (zone[3] / 2.0);
        return s.Objects.Values.MinBy(p => Math.Pow(p.X - cx, 2) + Math.Pow(p.Z - cz, 2));
    }

    private static readonly int[] ProgressBanks = [0, 1, 2, 7];
    private static readonly int[] NotBrought = [1, 49, 77]; // the knife, the lockpick, the radio
    private static readonly int[] MoveBanks = [0, 1, 2, 3, 7];

    private List<string> Progress(SimState a, SimState b, HashSet<(char, int, int)> ever)
    {
        var gained = new List<string>();
        foreach (var bank in ProgressBanks)
        {
            gained.AddRange(b.FlagsOf(bank).Where(f => !a.Has(bank, f) && !ever.Contains(('+', bank, f))).Select(f => $"+{bank}:{f}"));
            gained.AddRange(a.FlagsOf(bank).Where(f => !b.Has(bank, f) && !ever.Contains(('-', bank, f)) && (bank == 7 || !ever.Contains(('+', bank, f))))
                .Select(f => $"-{bank}:{f}"));
        }

        gained.AddRange(b.Inventory.Keys.Where(i => !a.Inventory.ContainsKey(i) && !ever.Contains(('i', i, 0))).Select(i => $"got {_itemName(i)}"));
        return gained;
    }

    private static void Remember(SimState a, SimState b, HashSet<(char, int, int)> ever)
    {
        foreach (var bank in ProgressBanks)
        {
            ever.UnionWith(b.FlagsOf(bank).Where(f => !a.Has(bank, f)).Select(f => ('+', bank, f)));
            ever.UnionWith(a.FlagsOf(bank).Where(f => !b.Has(bank, f)).Select(f => ('-', bank, f)));
        }

        ever.UnionWith(b.Inventory.Keys.Select(i => ('i', i, 0)));
    }

    private static readonly int[] StoryBanks = [0, 1];

    private static bool Undone(SimState a, SimState b) => StoryBanks.Any(bank => a.FlagsOf(bank).Any(f => !b.Has(bank, f)));

    private static string Armed(SimState s) => string.Join(';', s.Actions.OrderBy(a => a.Key).Select(a => $"{a.Key},{a.Value.Handler},{a.Value.Probe}"));

    /// <summary>Breadth-first over doors (and moves that undo or arm something) to the closest productive move, or with
    /// a goal, the closest move that reaches it.</summary>
    private (List<PlannedMove> Path, SimState State)? Nearest(Simulator sim, SimState start, HashSet<(char, int, int)> ever, int maxStates, PlanSplit? goal)
    {
        var queue = new Queue<(SimState State, List<PlannedMove> Path)>();
        queue.Enqueue((start, []));
        var visited = new HashSet<string> { start.Key() };
        var count = 0;
        while (queue.Count > 0 && count < maxStates)
        {
            var (s, path) = queue.Dequeue();
            foreach (var option in Moves(sim, s))
            {
                var t = s.Clone();
                if (!option.Apply(t))
                {
                    continue;
                }

                var gained = Progress(s, t, ever);
                var label = $"{s.Room}: {option.Text}" + (t.Room != s.Room ? $" -> {t.Room}" : string.Empty);
                var step = new PlannedMove
                {
                    Label = label, Kind = option.Kind, Room = s.Room, To = t.Room, Zone = option.Meta.Zone, Item = option.Meta.Item,
                    Key = option.Meta.Key, Push = option.Meta.Push, Object = option.Meta.Object, AnswerNo = option.Meta.AnswerNo,
                    Flags = [.. MoveBanks.SelectMany(b => t.FlagsOf(b).Where(f => !s.Has(b, f)).Select(f => (b, f, true))),
                        .. MoveBanks.SelectMany(b => s.FlagsOf(b).Where(f => !t.Has(b, f)).Select(f => (b, f, false)))],
                    Got = [.. t.Inventory.Keys.Where(i => t.Quantity(i) > s.Quantity(i)).Order()],
                    Lost = [.. s.Inventory.Keys.Where(i => !t.Inventory.ContainsKey(i)).Order()],
                    Gained = gained,
                };
                if (goal is not null ? goal.Reached(s, t) : gained.Count > 0)
                {
                    return ([.. path, step], t);
                }

                string? key = option.Kind == "door" || t.Room != s.Room || Undone(s, t) ? t.Key()
                    : Armed(t) != Armed(s) || t.BankSignature(4) != s.BankSignature(4) ? $"{t.Key()}#{Armed(t)}#{t.BankSignature(4)}" : null;
                if (key is not null && visited.Add(key))
                {
                    step.Gained = [];
                    queue.Enqueue((t, [.. path, step]));
                    count++;
                }
            }
        }

        return null;
    }

    public SimState NewGame(Simulator sim)
    {
        var start = _rules.NewGame;
        var s = new SimState { Character = _character };
        for (var f = 0; f < 256; f++)
        {
            s.SetFlag(7, f, true); // every room item in place
        }

        foreach (var f in start.Set)
        {
            s.SetFlag(f.Bank, f.Flag, true);
        }

        foreach (var f in start.Clear)
        {
            s.SetFlag(f.Bank, f.Flag, false);
        }

        foreach (var (item, quantity) in start.Inventory)
        {
            s.Inventory[item] = quantity;
        }

        s.Pos = start.Pos;
        s.Fields[Simulator.Character] = _character == "jill" ? 1 : 0;
        sim.Enter(s, start.Room);
        return s;
    }

    /// <summary>A playthrough from a new game: the moves and where it ended.</summary>
    public (List<PlannedMove> Moves, SimState End) Play(Simulator sim, IReadOnlyList<PlanSplit>? splits, int limit = 900, CancellationToken cancel = default)
    {
        var s = NewGame(sim);
        var ever = new HashSet<(char, int, int)>();
        Remember(new SimState { Character = _character }, s, ever);
        var route = new List<PlannedMove>();
        var goals = new List<PlanSplit>(splits ?? []);
        var wait = 0;
        for (var n = 0; n < limit; n++)
        {
            cancel.ThrowIfCancellationRequested();
            (List<PlannedMove> Path, SimState State)? found = null;
            PlanSplit? reached = null;
            while (goals.Count > 0 && ((goals[0].End && goals.Count > 1) || Early(goals[0], route) is not null))
            {
                var done = goals[0];
                goals.RemoveAt(0);
                if (done.Item is not null && Early(done, route) is { } took)
                {
                    took.Split = done.Name; // got before its split came up
                }
            }

            if (goals.Count > 0 && wait <= 0)
            {
                found = Nearest(sim, s, ever, GoalStates, goals[0]);
                if (found is not null)
                {
                    reached = goals[0];
                    goals.RemoveAt(0);
                }
                else
                {
                    wait = 3;
                }
            }

            wait--;
            found ??= Nearest(sim, s, ever, MaxStates, null);
            if (found is not { } f)
            {
                break;
            }

            Remember(s, f.State, ever);
            s = f.State;
            for (var i = 0; i < f.Path.Count; i++)
            {
                var step = f.Path[i];
                step.Index = n;
                if (reached is not null && i == f.Path.Count - 1)
                {
                    step.Split = reached.Name;
                }

                route.Add(step);
            }

            if (splits is not null && goals.Count == 0 && ((s.Room == "303" && s.Quantity(42) > 0) || !splits.Any(x => x.End)))
            {
                break;
            }
        }

        return (route, s);
    }

    private static PlannedMove? Early(PlanSplit split, List<PlannedMove> route) =>
        split.Item is not { } item ? null : route.LastOrDefault(m => m.Got.Contains(item) && m.Split is null && (split.In is null || m.Room == split.In));

    private static (int Hit, bool Finished, int Short) Score(List<PlannedMove> moves, SimState end) =>
        (moves.Count(m => m.Split is not null), end.Room == "303" && end.Quantity(42) > 0, -moves.Count);

    /// <summary>Toward a split set, answering the yes/no questions the way that reaches the most splits: from all Yes,
    /// each question room's answer is flipped in parallel, the best flip kept, and so on while it helps.</summary>
    public (List<PlannedMove> Moves, SimState End, IReadOnlyDictionary<string, int> Answers) PlayBest(IReadOnlyList<PlanSplit> splits, CancellationToken cancel = default)
    {
        var answers = new Dictionary<string, int>();
        var (moves, end) = Play(NewSimulator(answers), splits, cancel: cancel);
        var tried = new HashSet<string>();
        var probe = NewSimulator();
        while (Score(moves, end).Hit < splits.Count)
        {
            var candidates = moves.SelectMany(m => new[] { m.Room, m.To }).Distinct().Order(StringComparer.Ordinal)
                .Where(r => probe.Exists(r) && probe.Asks(r) && !answers.ContainsKey(r) && !tried.Contains(r)).ToList();
            if (candidates.Count == 0)
            {
                break;
            }

            tried.UnionWith(candidates);
            var results = new ConcurrentBag<(List<PlannedMove> Moves, SimState End, Dictionary<string, int> Answers)>();
            Parallel.ForEach(candidates, new ParallelOptions { CancellationToken = cancel }, room =>
            {
                var trial = new Dictionary<string, int>(answers) { [room] = 1 };
                var (m, e) = Play(NewSimulator(trial), splits, cancel: cancel);
                results.Add((m, e, trial));
            });
            var top = results.OrderByDescending(r => Score(r.Moves, r.End)).ThenBy(r => string.Join(',', r.Answers.Keys.Order(StringComparer.Ordinal)), StringComparer.Ordinal).First();
            if (Score(top.Moves, top.End).CompareTo(Score(moves, end)) <= 0)
            {
                break;
            }

            (moves, end, answers) = (top.Moves, top.End, top.Answers);
        }

        return (moves, end, answers);
    }

    // ---- the route a playthrough makes (tools/routesim/export.py) ------------------------------------------------------

    private static readonly Dictionary<int, string> FieldNames = new() { [0] = "flagScenarioA", [1] = "flagScenarioB", [2] = "flagLocks", [7] = "flagRoomItems" };

    /// <summary>The steps a player needs, as routes.json steps (parsed by <see cref="RouteFile"/>).</summary>
    public List<Dictionary<string, object>> Export(List<PlannedMove> route, IReadOnlyCollection<string> rooms, IReadOnlyDictionary<(int Bank, int Flag), string> named,
        Func<string, IEnumerable<ScriptNode>> allNodes)
    {
        var reads = new Dictionary<(int, int), HashSet<string>>();
        var tests = new Dictionary<string, HashSet<int>>();
        foreach (var room in rooms)
        {
            foreach (var n in allNodes(room).Where(n => n.IsIf))
            {
                foreach (var c in n.Conds!)
                {
                    if (c.Op == 0x04)
                    {
                        if (!reads.TryGetValue((c.Bank, c.Flag), out var set))
                        {
                            reads[(c.Bank, c.Flag)] = set = [];
                        }

                        set.Add(room);
                    }
                    else if (c.Op == 0x1A)
                    {
                        if (!tests.TryGetValue(room, out var items))
                        {
                            tests[room] = items = [];
                        }

                        items.Add(c.Item);
                    }
                }
            }
        }

        var needed = route.Where(m => m.Kind is "use" or "examine" && m.Item is not null).Select(m => m.Item!.Value).ToHashSet();
        needed.UnionWith(route.Where(m => m.Kind is "door" or "spot").SelectMany(m => m.Lost));
        var held = new HashSet<int>();
        var brought = new Dictionary<int, List<int>>();
        for (var i = 0; i < route.Count; i++)
        {
            var m = route[i];
            held.UnionWith(m.Got);
            held.ExceptWith(m.Lost);
            var room = m.Kind == "door" ? m.To : m.Room;
            if (m.Kind is "door" or "spot" && m.Flags.Any(f => f.Set && f.Bank is 0 or 1))
            {
                var items = tests.GetValueOrDefault(room, []).Intersect(held).Except(NotBrought).Order().ToList();
                if (items.Count > 0)
                {
                    brought[i] = items;
                    needed.UnionWith(items);
                }
            }
        }

        needed.UnionWith(route.Where(m => m.Kind == "door" && m.Key is not (null or 0 or 254 or 255) && m.Flags.Any(f => f.Bank == 2 && f.Set))
            .Select(m => _rules.KeyAlias.GetValueOrDefault(m.Key!.Value, m.Key!.Value)));
        needed.UnionWith(_rules.Examine.SelectMany(r => r.Give.Select(g => g.Item)));
        needed.Add(42);
        needed.UnionWith(route.Where(m => m.Split is not null).SelectMany(m => m.Got));

        List<(int, int)> Story(PlannedMove m) =>
            [.. m.Flags.Where(f => f.Set && f.Bank is 0 or 1 && reads.TryGetValue((f.Bank, f.Flag), out var r) && r.Any(x => x != m.Room)).Select(f => (f.Bank, f.Flag))];
        string? Label((int, int)? flag, string room)
        {
            if (flag is not { } fl || !named.TryGetValue(fl, out var text) || string.IsNullOrEmpty(text) || rooms.Any(r => text.Contains($"({r})", StringComparison.Ordinal) && r != room))
            {
                return null;
            }

            return char.ToUpperInvariant(text[0]) + text[1..] + ".";
        }

        (int, int)? Best(List<(int, int)> flags, string? thing)
        {
            if (flags.Count == 0)
            {
                return null;
            }

            if (thing is not null)
            {
                foreach (var f in flags)
                {
                    if (named.TryGetValue(f, out var t) && t.Contains(thing, StringComparison.OrdinalIgnoreCase))
                    {
                        return f;
                    }
                }
            }

            return flags.FirstOrDefault(named.ContainsKey) is var labelled && named.ContainsKey(labelled) ? labelled : flags[0];
        }

        static string Field((int Bank, int Flag) f) => $"{FieldNames[f.Bank]}{f.Flag / 32}:0x{1u << (31 - (f.Flag % 32)):X8}";
        static List<int>? Centre(IReadOnlyList<int>? zone) => zone is { Count: 4 } z && z[2] > 1 ? [z[0] + (z[2] / 2), z[1] + (z[3] / 2)] : null;

        var output = new List<Dictionary<string, object>>();
        void Add(Dictionary<string, object> step)
        {
            if (output.Count == 0 || JsonSerializer.Serialize(output[^1]) != JsonSerializer.Serialize(step))
            {
                output.Add(step);
            }
        }

        for (var index = 0; index < route.Count; index++)
        {
            var m = route[index];
            if (m.Split is not null && m.Kind is "door" or "spot" && m.To != m.Room && m.Got.Count == 0)
            {
                output.Add(new() { ["go"] = m.To, ["hint"] = $"Split: {m.Split}." });
                continue;
            }

            if (brought.TryGetValue(index, out var bringing) && Story(m).Count > 0)
            {
                foreach (var g in bringing)
                {
                    var where = m.Kind == "door" ? m.To : m.Room;
                    var flag = Best(Story(m), _itemName(g))!.Value;
                    var step = new Dictionary<string, object> { ["use"] = g, ["in"] = where, ["done"] = Field(flag) };
                    if (Label(flag, where) is { } hint)
                    {
                        step["hint"] = hint;
                    }

                    output.Add(step);
                }

                continue;
            }

            var got = m.Got.Where(needed.Contains).ToList();
            if (got.Count > 0 && m.Kind != "examine")
            {
                foreach (var g in got)
                {
                    var step = new Dictionary<string, object> { ["get"] = g, ["in"] = m.Room };
                    if (m.Kind != "take")
                    {
                        var story = Story(m);
                        var text = story.Count > 0 ? Label(Best(story, _itemName(g)), m.Room) : null;
                        step["hint"] = "Handed over here" + (text is not null ? $": {text}" : ".");
                        if (m.Kind == "spot" && Centre(m.Zone) is { } at)
                        {
                            step["at"] = at;
                        }
                    }

                    Add(step);
                }

                continue;
            }

            if (m.Kind is "door" or "spot" && m.Lost.Count > 0 && Story(m).Count > 0)
            {
                foreach (var lost in m.Lost)
                {
                    var flag = Best(Story(m), _itemName(lost))!.Value;
                    var where = m.Kind == "door" ? m.To : m.Room;
                    var step = new Dictionary<string, object> { ["use"] = lost, ["in"] = where, ["done"] = Field(flag) };
                    if (Label(flag, where) is { } hint)
                    {
                        step["hint"] = hint;
                    }

                    Add(step);
                }

                continue;
            }

            Dictionary<string, object> next;
            if (m.Kind == "use" && m.Flags.Any(f => f.Set))
            {
                next = new() { ["use"] = m.Item!.Value, ["in"] = m.Room };
                var flag = Best([.. m.Flags.Where(f => f.Set && FieldNames.ContainsKey(f.Bank) && f.Bank != 7).Select(f => (f.Bank, f.Flag))], _itemName(m.Item!.Value));
                if (flag is { } fl)
                {
                    next["done"] = Field(fl);
                    if (Label(fl, m.Room) is { } hint)
                    {
                        next["hint"] = hint;
                    }
                }
            }
            else if (m.Kind == "examine")
            {
                foreach (var g in m.Got)
                {
                    output.Add(new() { ["get"] = g, ["hint"] = $"Examine the {_itemName(m.Item!.Value)} in your items: the {_itemName(g)} is inside." });
                }

                continue;
            }
            else if (m.Kind is "door" or "spot" or "kill" && Story(m).Count > 0)
            {
                var flag = Best(Story(m), null)!.Value;
                var where = m.Kind != "door" ? m.Room : m.To;
                var text = Label(flag, where);
                if (m.Kind == "door" && text is not null && text.StartsWith("First time", StringComparison.Ordinal))
                {
                    continue; // bookkeeping of a first visit
                }

                next = new() { ["go"] = where, ["done"] = Field(flag) };
                next["hint"] = m.Kind == "kill" ? "Beat what's in here" + (text is not null ? $": {char.ToLowerInvariant(text[0])}{text[1..]}" : ".")
                    : text ?? (m.Kind == "spot" ? (m.Push ? "Push something onto the marked spot." : "Check the marked spot.") : "A scene plays here.");
                if (m.Kind == "spot" && Centre(m.Zone) is { } at)
                {
                    next["at"] = at;
                }

                if (m.Push && m.Object is { } obj && m.Zone is { Count: 4 } z)
                {
                    next["spots"] = new List<object>
                    {
                        new List<object> { obj.X - 500, obj.Z - 500, 1000, 1000, "push this" },
                        new List<object> { z[0], z[1], z[2], z[3], "onto here" },
                    };
                }
            }
            else
            {
                continue;
            }

            if (m.Kind == "use" && Centre(m.Zone) is { } useAt)
            {
                next["at"] = useAt;
            }

            Add(next);
        }

        foreach (var m in route.Where(m => m.Split is not null && m.Got.Count > 0))
        {
            var target = output.FirstOrDefault(s => s.TryGetValue("get", out var g) && m.Got.Contains((int)g)
                && (!s.TryGetValue("in", out var i) || (string)i == m.Room) && !(s.TryGetValue("hint", out var h) && ((string)h).StartsWith("Split", StringComparison.Ordinal)));
            if (target is not null)
            {
                target["hint"] = $"Split: {m.Split}." + (target.TryGetValue("hint", out var h) ? $" {h}" : string.Empty);
            }
        }

        foreach (var m in route.Where(m => m.AnswerNo))
        {
            var target = output.FirstOrDefault(s => (s.TryGetValue("in", out var i) ? (string)i : s.TryGetValue("go", out var g) ? (string)g : null) is { } r
                && (r == m.Room || r == m.To) && !(s.TryGetValue("hint", out var h) && ((string)h).Contains("Answer No", StringComparison.Ordinal)));
            if (target is not null)
            {
                target["hint"] = ("Answer No when asked. " + (target.TryGetValue("hint", out var h) ? (string)h : string.Empty)).Trim();
            }
        }

        output.Add(new() { ["end"] = true });
        return output;
    }
}
