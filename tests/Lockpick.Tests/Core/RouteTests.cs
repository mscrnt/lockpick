using Lockpick.Core.Maps;
using Lockpick.Core.Routes;
using Lockpick.Core.State;
using Lockpick.GameAdapter.RE1;

namespace Lockpick.Tests.Core;

/// <summary>
/// The route guide on three hand-made rooms: 101 has a wall down the middle, an open door east to 102 (the Emblem) and a
/// door west to 103 that the Armor key opens (the Emblem's use spot is there).
/// </summary>
public class RouteTests
{
    private const int Emblem = 31;
    private const int ArmorKey = 52;

    private static readonly MapItem EmblemSpot = new(4750, 4750, 500, 500, Emblem, "Emblem", 3);
    private static readonly MapSocket EmblemSlot = new(4750, 4750, 500, 500, Emblem, "Emblem");

    private static readonly Dictionary<string, RoomMap> Rooms = new()
    {
        ["101"] = Room("101",
            [new MapShape(MapShapeKind.Rectangle, 4500, 0, 5500, 8000, true)],
            [new MapDoor(9000, 4000, 1000, 2000, "102", 500, 5000), new MapDoor(0, 4000, 1000, 2000, "103", 9500, 5000, LockFlag: 1, Key: ArmorKey)]),
        ["102"] = Room("102", [], [new MapDoor(0, 4000, 1000, 2000, "101", 9500, 5000)], items: [EmblemSpot]),
        ["103"] = Room("103", [], [new MapDoor(9000, 4000, 1000, 2000, "101", 500, 5000, LockFlag: 1, Key: ArmorKey)], sockets: [EmblemSlot]),
    };

    private static readonly Route Route = new("test", "Test route", "Test", PlayableCharacter.Jill,
        [
            new RouteStep(RouteStepKind.Get, Emblem, "102"),
            new RouteStep(RouteStepKind.Go, Room: "103", Hint: "Through the west door"),
            new RouteStep(RouteStepKind.Use, Emblem, "103"),
            new RouteStep(RouteStepKind.End),
        ], false, "tests");

    private readonly FakeMaps _maps = new();
    private int _tick;

    private static RoomMap Room(string id, MapShape[] shapes, MapDoor[] doors, MapItem[]? items = null, MapSocket[]? sockets = null) =>
        new(id, [new MapShape(MapShapeKind.Rectangle, 0, 0, 10000, 200, true), new MapShape(MapShapeKind.Rectangle, 0, 11800, 10000, 12000, true), .. shapes],
            doors, items ?? [], [], sockets ?? [], []);

    private RouteGuide Guide() => new(Route, new RoutePlanner(Rooms), _maps, i => i == Emblem ? "Emblem" : i == ArmorKey ? "Armor key" : null, _ => null);

    private GameSnapshot At(string room, double x, double z, params int[] held) => new()
    {
        InGameplay = Field<bool>.Valid(true),
        Location = Field<RoomLocation>.Valid(new RoomLocation(room[0] - '0', Convert.ToInt32(room[1..], 16))),
        Character = Field<PlayableCharacter>.Valid(PlayableCharacter.Jill),
        Position = Field<Position>.Valid(new Position((int)x, 0, (int)z)),
        GameTime = Field<TimeSpan>.Valid(TimeSpan.FromSeconds(++_tick)),
        Inventory = Field<IReadOnlyList<HeldItem>>.Valid([.. held.Select((h, i) => new HeldItem(i, h, 1))]),
        ItemBox = Field<IReadOnlyList<HeldItem>>.Valid([]),
    };

    [Fact]
    public void A_line_goes_around_a_wall_not_through_it()
    {
        var line = new RoomPath(Rooms["101"]).Find(new RoutePoint(2000, 5000), new RoutePoint(8000, 5000));
        Assert.Contains(line, p => p.Z > 8000);
        Assert.True(RoomPath.Length(line) > 7000);
        Assert.Equal(new RoutePoint(2000, 5000), line[0]);
        Assert.Equal(new RoutePoint(8000, 5000), line[^1]);
    }

    [Fact]
    public void The_way_goes_through_doors_the_player_can_open()
    {
        var planner = new RoutePlanner(Rooms);
        var goal = new RouteGoal("103", null);
        Assert.Null(planner.Plan("102", new RoutePoint(5000, 5000), [goal], d => d.LockFlag is null));

        var plan = planner.Plan("102", new RoutePoint(5000, 5000), [goal], _ => true)!;
        Assert.Equal(["102", "101", "103"], plan.Rooms);
        Assert.Equal("101", plan.NextDoor!.Target);
        Assert.Null(plan.Legs[^1].Exit);
    }

    [Fact]
    public void The_guide_follows_the_steps_with_the_way_and_the_buttons()
    {
        var guide = Guide();

        var view = guide.Update(At("101", 2000, 5000))!;
        Assert.Equal("Get the Emblem", view.Text);
        Assert.Equal(["101", "102"], view.Plan!.Rooms);
        Assert.Empty(view.Prompts); // nothing to press yet (no "run" prompt: players know how to run)

        view = guide.Update(At("102", 4500, 4500))!;
        Assert.Contains(view.Prompts, p => p.Buttons.SequenceEqual([GameControl.Action]) && p.Text == "Pick up the Emblem");

        // Picked up: the next step needs the Armor key's door.
        view = guide.Update(At("102", 5000, 5000, Emblem))!;
        Assert.Equal(1, view.StepIndex);
        Assert.Null(view.Plan);
        Assert.Equal("No way there through the doors you can open yet.", view.Note);

        view = guide.Update(At("101", 1500, 5000, Emblem, ArmorKey))!;
        Assert.Equal(["101", "103"], view.Plan!.Rooms);
        Assert.Contains(view.Prompts, p => p.Text == "Open the door (the Armor key opens it)");

        view = guide.Update(At("103", 9000, 5000, Emblem, ArmorKey))!;
        Assert.Equal("Use the Emblem", view.Text);
        Assert.DoesNotContain(view.Prompts, p => p.Buttons.SequenceEqual([GameControl.Inventory]));

        view = guide.Update(At("103", 5600, 5000, Emblem, ArmorKey))!;
        Assert.Contains(view.Prompts, p => p.Buttons.SequenceEqual([GameControl.Inventory]));

        // Used: the Emblem left the inventory (the game doesn't say the slot is filled here).
        view = guide.Update(At("103", 5000, 5000, ArmorKey))!;
        Assert.True(view.Finished);
    }

    [Fact]
    public void A_loaded_save_starts_after_the_last_step_the_flags_show()
    {
        _maps.Taken.Add(EmblemSpot);
        var view = Guide().Update(At("101", 2000, 5000))!;
        Assert.Equal(1, view.StepIndex);

        _maps.Filled.Add(EmblemSlot);
        view = Guide().Update(At("101", 2000, 5000))!;
        Assert.True(view.Finished);
    }

    [Fact]
    public void A_door_that_opens_from_one_side_is_used_only_from_that_side()
    {
        Dictionary<string, RoomMap> rooms = new()
        {
            ["101"] = Room("101", [], [new MapDoor(9000, 4000, 1000, 2000, "102", 500, 5000, LockFlag: 20, Unlock: MapUnlock.ThisSide)]),
            ["102"] = Room("102", [], [new MapDoor(0, 4000, 1000, 2000, "101", 9500, 5000, LockFlag: 20, Unlock: MapUnlock.OtherSide)], items: [EmblemSpot]),
        };
        var route = Route with { Steps = [new RouteStep(RouteStepKind.Get, Emblem, "102"), new RouteStep(RouteStepKind.Go, Room: "101"), new RouteStep(RouteStepKind.End)] };
        var guide = new RouteGuide(route, new RoutePlanner(rooms), _maps, _ => "Emblem", _ => null);

        var view = guide.Update(At("101", 8500, 5000))!;
        Assert.Equal(["101", "102"], view.Plan!.Rooms);
        Assert.False(view.MayBeLocked);
        Assert.Contains(view.Prompts, p => p.Text == "Open the door (it unlocks from this side)");

        // From the other side it stays shut until it has been opened.
        view = guide.Update(At("102", 5000, 5000, Emblem))!;
        Assert.Null(view.Plan);
    }

    [Fact]
    public void A_step_with_a_story_flag_is_done_when_the_flag_is_set_not_by_entering_the_room()
    {
        var route = Route with
        {
            Steps = [new RouteStep(RouteStepKind.Go, Room: "102", Done: new RouteFlag("flagScenarioA0", 0x100000)), new RouteStep(RouteStepKind.End)],
        };
        var guide = new RouteGuide(route, new RoutePlanner(Rooms), _maps, _ => null, _ => null);
        GameSnapshot Story(long a0) => At("102", 5000, 5000) with
        {
            RawFields = new Dictionary<string, Field<long>> { ["flagScenarioA0"] = Field<long>.Valid(a0) },
        };

        Assert.Equal(0, guide.Update(Story(0))!.StepIndex);
        Assert.True(guide.Update(Story(0x100000))!.Finished);
        Assert.Equal(RouteStepKind.Go, Assert.Single(RouteFile.Parse(new MemoryStream(
            "{\"routes\":[{\"id\":\"t\",\"name\":\"t\",\"character\":\"jill\",\"steps\":[{\"go\":\"202\",\"done\":\"flagScenarioA0:0x00100000\"}]}]}"u8.ToArray()))
            .Single().Steps, s => s.Done == new RouteFlag("flagScenarioA0", 0x100000)).Kind);
    }

    [Fact]
    public void A_puzzle_numbers_its_spots_and_says_which_one_the_player_is_at()
    {
        RouteSpot[] spots = [new(2000, 2000, 1000, 1000, "newborn baby"), new(7000, 2000, 1000, 1000, "infant")];
        var route = Route with { Steps = [new RouteStep(RouteStepKind.Get, Emblem, "102") { Spots = spots }, new RouteStep(RouteStepKind.End)] };
        var guide = new RouteGuide(route, new RoutePlanner(Rooms), _maps, _ => "Emblem", _ => null);

        var view = guide.Update(At("102", 7400, 2600))!;
        Assert.Equal(spots, view.Plan!.Spots);
        Assert.Equal("102", view.Plan.SpotsRoom);
        Assert.Equal("Push the switch: 2 of 2, infant", Assert.Single(view.Prompts).Text);
    }

    [Fact]
    public void Pressed_switches_are_counted_from_the_flag_the_game_keeps_for_the_last_one()
    {
        RouteSpot[] spots =
        [
            new(2000, 2000, 1000, 1000, "newborn baby", new RouteFlag("flagSys0", 0x20)),
            new(7000, 2000, 1000, 1000, "infant", new RouteFlag("flagSys0", 0x08)),
            new(7000, 7000, 1000, 1000, "lively boy", new RouteFlag("flagSys0", 0x04)),
        ];
        var route = Route with { Steps = [new RouteStep(RouteStepKind.Get, Emblem, "102") { Spots = spots }, new RouteStep(RouteStepKind.End)] };
        var guide = new RouteGuide(route, new RoutePlanner(Rooms), _maps, _ => "Emblem", _ => null);
        GameSnapshot With(long sys, double x, double z) => At("102", x, z) with
        {
            RawFields = new Dictionary<string, Field<long>> { ["flagSys0"] = Field<long>.Valid(sys) },
        };

        var view = guide.Update(With(0x08, 7400, 7400))!; // the infant's flag: two done
        Assert.Equal(2, view.Plan!.SpotsDone);
        Assert.Equal("Push the switch: 3 of 3, lively boy", Assert.Single(view.Prompts).Text);

        view = guide.Update(With(0x08, 2400, 2400))!;
        Assert.Equal("Done (1, newborn baby); next: 3, lively boy", Assert.Single(view.Prompts).Text);
        Assert.Empty(view.Prompts[0].Buttons);

        // Elsewhere the game uses the same flags for other things.
        Assert.Equal(0, guide.Update(At("101", 8000, 5000) with
        {
            RawFields = new Dictionary<string, Field<long>> { ["flagSys0"] = Field<long>.Valid(0x08) },
        })!.Plan!.SpotsDone);
    }

    [Fact]
    public void A_step_done_early_is_skipped_later_without_skipping_what_is_still_to_do()
    {
        // The last step's flag is already set (like a crest placed before another crest was picked up): the guide still
        // starts at the first step, and passes the done one when it gets there.
        var route = Route with
        {
            Steps =
            [
                new RouteStep(RouteStepKind.Get, Emblem, "102"),
                new RouteStep(RouteStepKind.Go, Room: "101"),
                new RouteStep(RouteStepKind.Use, ArmorKey, "103", Done: new RouteFlag("flagScenarioA3", 0x80000)),
                new RouteStep(RouteStepKind.End),
            ],
        };
        var guide = new RouteGuide(route, new RoutePlanner(Rooms), _maps, _ => "Emblem", _ => null);
        GameSnapshot With(long a3, string room, params int[] held) => At(room, 5000, 5000, held) with
        {
            RawFields = new Dictionary<string, Field<long>> { ["flagScenarioA3"] = Field<long>.Valid(a3) },
        };

        Assert.Equal(0, guide.Update(With(0x80000, "101"))!.StepIndex); // the emblem is still to get
        Assert.Equal(1, guide.Update(With(0x80000, "102", Emblem))!.StepIndex);
        Assert.Equal(1, guide.Update(With(0x80000, "101", Emblem))!.StepIndex); // there: the step's hint stays up
        Assert.True(guide.Update(With(0x80000, "102", Emblem))!.Finished); // left it; and the use was done already
    }

    [Fact]
    public void An_optional_step_or_an_item_from_the_inventory_is_passed_once_a_later_step_is_done()
    {
        // The guardhouse statue (optional, its flag never set) and a Doom book's medal (got from the inventory, used
        // before its step came up): the use after them is done, so the guide moves on instead of waiting for ever.
        var route = Route with
        {
            Steps =
            [
                new RouteStep(RouteStepKind.Go, Room: "102", Done: new RouteFlag("flagScenarioA2", 0x4000)) { Optional = true },
                new RouteStep(RouteStepKind.Get, 36),
                new RouteStep(RouteStepKind.Use, 36, "103", Done: new RouteFlag("flagScenarioA0", 0x8)),
                new RouteStep(RouteStepKind.End),
            ],
        };
        var guide = new RouteGuide(route, new RoutePlanner(Rooms), _maps, _ => "Wolf medal", _ => null);
        GameSnapshot With(long a0) => At("101", 5000, 5000) with
        {
            RawFields = new Dictionary<string, Field<long>> { ["flagScenarioA0"] = Field<long>.Valid(a0), ["flagScenarioA2"] = Field<long>.Valid(0) },
        };

        Assert.Equal(0, guide.Update(With(0))!.StepIndex); // nothing done yet: the optional step is still shown
        Assert.True(guide.Update(With(0x8))!.Finished);
    }

    [Fact]
    public void A_second_use_at_the_same_spot_is_not_taken_as_done_by_the_first()
    {
        // Like the square crank, turned twice at the water gate: the filled spot says nothing about the second turn.
        var route = Route with
        {
            Steps =
            [
                new RouteStep(RouteStepKind.Use, Emblem, "103"),
                new RouteStep(RouteStepKind.Go, Room: "101"),
                new RouteStep(RouteStepKind.Use, Emblem, "103", Hint: "again"),
                new RouteStep(RouteStepKind.End),
            ],
        };
        _maps.Filled.Add(EmblemSlot);
        var view = new RouteGuide(route, new RoutePlanner(Rooms), _maps, _ => "Emblem", _ => null).Update(At("102", 5000, 5000, Emblem))!;
        Assert.Equal(1, view.StepIndex);
    }

    [Fact]
    public void An_item_a_script_places_counts_as_got_once_a_later_step_is_done()
    {
        // Like the serum: no pickup spot in the room file; giving it to Richard (a flag) is the next step.
        var route = Route with
        {
            Steps =
            [
                new RouteStep(RouteStepKind.Get, 66, "102"),
                new RouteStep(RouteStepKind.Use, 66, "103", Done: new RouteFlag("flagScenarioB3", 0x04000000)),
                new RouteStep(RouteStepKind.Get, Emblem, "102"),
                new RouteStep(RouteStepKind.End),
            ],
        };
        var guide = new RouteGuide(route, new RoutePlanner(Rooms), _maps, _ => "Serum", _ => null);
        var view = guide.Update(At("101", 5000, 5000) with
        {
            RawFields = new Dictionary<string, Field<long>> { ["flagScenarioB3"] = Field<long>.Valid(0x04000000) },
        })!;
        Assert.Equal(2, view.StepIndex);
    }

    [Fact]
    public void In_the_room_a_step_is_about_the_way_leads_to_the_person_there()
    {
        var route = Route with { Steps = [new RouteStep(RouteStepKind.Go, Room: "102", Done: new RouteFlag("flagScenarioB3", 0x20000000)), new RouteStep(RouteStepKind.End)] };
        var guide = new RouteGuide(route, new RoutePlanner(Rooms), _maps, _ => null, _ => null);
        var view = guide.Update(At("102", 1500, 5000) with
        {
            RawFields = new Dictionary<string, Field<long>> { ["flagScenarioB3"] = Field<long>.Valid(0) },
            Actors = Field<IReadOnlyList<LiveActor>>.Valid([new LiveActor(0, 0x20, "Richard", ActorKind.Npc, 8000, 3000, 0, 100)]),
        })!;
        Assert.Equal(new RouteGoal("102", new RoutePoint(8000, 3000)), view.Plan!.Goal);
    }

    [Fact]
    public void A_time_limit_counts_down_from_when_its_flag_went_on_until_it_is_met()
    {
        var timer = new RouteTimer("Richard needs the serum", new RouteFlag("flagScenarioA2", 0x40000), new RouteFlag("flagScenarioB3", 0x04000000), 15);
        var route = Route with { Timers = [timer] };
        GameSnapshot At(int seconds, long a2, long b3 = 0) => new()
        {
            InGameplay = Field<bool>.Valid(true),
            Location = Field<RoomLocation>.Valid(new RoomLocation(1, 1)),
            Position = Field<Position>.Valid(new Position(2000, 0, 5000)),
            GameTime = Field<TimeSpan>.Valid(TimeSpan.FromSeconds(seconds)),
            RawFields = new Dictionary<string, Field<long>>
            {
                ["flagScenarioA2"] = Field<long>.Valid(a2),
                ["flagScenarioB3"] = Field<long>.Valid(b3),
            },
        };

        var guide = new RouteGuide(route, new RoutePlanner(Rooms), _maps, _ => null, _ => null);
        Assert.Empty(guide.Update(At(100, 0))!.Clocks);
        Assert.Equal(TimeSpan.FromMinutes(15), Assert.Single(guide.Update(At(101, 0x40000))!.Clocks).Left);
        Assert.Equal(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(60), guide.Update(At(161, 0x40000))!.Clocks[0].Left);
        Assert.Empty(guide.Update(At(170, 0x40000, 0x04000000))!.Clocks); // the serum was given

        // Lockpick started (or a save loaded) mid-errand: running, time left unknown.
        var late = new RouteGuide(route, new RoutePlanner(Rooms), _maps, _ => null, _ => null);
        Assert.Null(Assert.Single(late.Update(At(500, 0x40000))!.Clocks).Left);
    }

    [Fact]
    public void An_item_held_counts_as_got_wherever_the_player_is_unless_an_earlier_step_got_one_too()
    {
        // The serum, picked up and carried out of the room before Lockpick looked (or after a restart).
        var serum = Route with { Steps = [new RouteStep(RouteStepKind.Get, 66, "102"), new RouteStep(RouteStepKind.End)] };
        Assert.True(new RouteGuide(serum, new RoutePlanner(Rooms), _maps, _ => "Serum", _ => null).Update(At("101", 2000, 5000, 66))!.Finished);

        // The second battery: the first one carried in isn't it.
        var batteries = Route with
        {
            Steps = [new RouteStep(RouteStepKind.Get, 39, "103"), new RouteStep(RouteStepKind.Get, 39, "102"), new RouteStep(RouteStepKind.End)],
        };
        var guide = new RouteGuide(batteries, new RoutePlanner(Rooms), _maps, _ => "Battery", _ => null);
        Assert.Equal(1, guide.Update(At("101", 2000, 5000, 39))!.StepIndex);
    }

    [Fact]
    public void Skipping_moves_on_and_a_step_with_no_place_goes_to_its_room()
    {
        var route = Route with { Steps = [new RouteStep(RouteStepKind.Get, 99, "102"), new RouteStep(RouteStepKind.End)] };
        var guide = new RouteGuide(route, new RoutePlanner(Rooms), _maps, _ => null, _ => null);
        var view = guide.Update(At("101", 8000, 5000))!;
        Assert.Equal(new RouteGoal("102", null), view.Plan!.Goal);
        Assert.Equal("Get the item 99", view.Text);

        guide.Skip();
        Assert.True(guide.Update(At("101", 8000, 5000))!.Finished);
    }

    [Fact]
    public void The_built_in_routes_name_real_items_and_rooms()
    {
        var adapter = new Re1Adapter();
        var routes = adapter.CreateRunRules(adapter.Profiles[0]).Routes;
        Assert.Equal(PlayableCharacter.Jill, Assert.Single(routes, r => r.Id == "re1-jill-any-nmg").Character);
        // The full-game routes the simulator writes (tools/routesim/build_routes.py), one per character.
        Assert.Equal(PlayableCharacter.Jill, Assert.Single(routes, r => r.Id == "re1-jill-full-sim").Character);
        Assert.Equal(PlayableCharacter.Chris, Assert.Single(routes, r => r.Id == "re1-chris-full-sim").Character);
        foreach (var route in routes)
        {
            Assert.False(route.Verified);
            Assert.Equal(RouteStepKind.End, route.Steps[^1].Kind);
            foreach (var step in route.Steps)
            {
                if (step.Item is { } item)
                {
                    Assert.NotNull(adapter.ItemName(item));
                }

                if (step.Room is { } room)
                {
                    Assert.NotNull(adapter.RoomName(room));
                }
            }
        }
    }

    [Theory]
    [InlineData(0x80800000L, false)] // Jill, in control (docs/MEMORY_MAP.md)
    [InlineData(0x80800004L, false)] // a room script's 0x4 doesn't count
    [InlineData(0x80800800L, true)]  // picking something up
    [InlineData(0x80808000L, true)]  // its message on screen
    [InlineData(0x60400000L, true)]  // the menu opening
    public void Prompts_wait_while_the_player_is_not_in_control(long stateFlags, bool busy)
    {
        var snapshot = new GameSnapshot { RawFields = new Dictionary<string, Field<long>> { ["stateFlags"] = Field<long>.Valid(stateFlags) } };
        Assert.Equal(busy, new Re1Adapter().PlayerBusy(snapshot));
    }

    [Fact]
    public void Keyboard_prompts_show_the_keys_the_player_set()
    {
        var adapter = new Re1Adapter();
        var none = new GameSnapshot();
        Assert.Equal("C", adapter.KeyName(GameControl.Action, none));
        Assert.Equal("↑", adapter.KeyName(GameControl.Back, none));

        // Action moved to Space (byte 11 = keyMap2's fourth byte).
        var remapped = new GameSnapshot
        {
            RawFields = new Dictionary<string, Field<long>> { ["keyMap2"] = Field<long>.Valid(0x20_00_00_58L) },
        };
        Assert.Equal("Space", adapter.KeyName(GameControl.Action, remapped));
        Assert.Equal("X", adapter.KeyName(GameControl.Aim, remapped));
    }

    private sealed class FakeMaps : IRoomMapSource
    {
        public HashSet<MapItem> Taken { get; } = [];

        public HashSet<MapSocket> Filled { get; } = [];

        public RoomMap? Load(RoomLocation room, PlayableCharacter character) => Rooms.GetValueOrDefault(room.Id);

        public bool? IsTaken(MapItem item, GameSnapshot snapshot) => Taken.Contains(item);

        public bool? IsFilled(RoomMap map, MapSocket socket, GameSnapshot snapshot) => Filled.Contains(socket) ? true : null;

        public bool? IsUnlocked(MapDoor door, GameSnapshot snapshot) => door.LockFlag is null ? true : null;
    }
}
