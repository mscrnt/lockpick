using Lockpick.Core.Insight;
using Lockpick.Core.State;
using Lockpick.GameAdapter.RE1;

namespace Lockpick.Tests.RE1;

/// <summary>RE1's ask-mode knowledge: what the room index and confirmed labels explain, and what gets asked about.</summary>
public class Re1ChangeExplainerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private readonly IChangeExplainer _explainer;

    public Re1ChangeExplainerTests()
    {
        var adapter = new Re1Adapter();
        _explainer = adapter.CreateChangeExplainer(adapter.Profiles[0]);
    }

    /// <summary>Jill in the dining room (105), the values seen live before she took the blue jewel.</summary>
    private static GameSnapshot Jill(double seconds, string room = "105", params (string Field, long Value)[] raw)
    {
        var fields = new Dictionary<string, Field<long>>
        {
            ["character"] = Field<long>.Valid(1),
            ["flagRoomItems1"] = Field<long>.Valid(0xFEDFFFFF),
            ["flagScenarioB0"] = Field<long>.Valid(0x80000000),
            ["flagRoom0"] = Field<long>.Valid(0x06000000),
            ["flagRoom3"] = Field<long>.Valid(0),
            ["inv0"] = Field<long>.Valid(0x0001),
        };
        foreach (var (field, value) in raw)
        {
            fields[field] = Field<long>.Valid(value);
        }

        return new GameSnapshot
        {
            CapturedAt = T0.AddSeconds(seconds),
            InGameplay = Field<bool>.Valid(true),
            Location = Field<RoomLocation>.Valid(new RoomLocation(room[0] - '0', Convert.ToInt32(room[1..], 16))),
            GameTime = Field<TimeSpan>.Valid(TimeSpan.FromSeconds(100 + seconds)),
            RawFields = fields,
        };
    }

    [Fact]
    public void Pickups_from_the_room_index_are_explained_and_named()
    {
        var after = Jill(1, raw: ("flagRoomItems1", 0xFED7FFFF)); // blue jewel, item flag 44
        var report = _explainer.Explain(Jill(0), after);
        Assert.Empty(report.Unexplained);
        Assert.Equal(["picked up Blue jewel"], report.Known);

        // Putting it back (the Emblem trick) is explained too.
        Assert.Equal(["put Blue jewel back"], _explainer.Explain(after, Jill(2)).Known);
    }

    [Fact]
    public void Jills_ribbon_flags_clearing_as_she_enters_a_room_are_not_pickups()
    {
        // Live 2026-10-17: entering 107 as Jill cleared ink ribbon flag 39 (word 1, bit 24) with nothing picked up.
        var before = Jill(0, "106", ("flagRoomItems1", 0xFFFFFFFF));
        var after = Jill(1, "107", ("flagRoomItems1", 0xFEFFFFFF));

        var report = _explainer.Explain(before, after);

        Assert.Empty(report.Unexplained);
        Assert.DoesNotContain(report.Known, k => k.Contains("Ink ribbon", StringComparison.Ordinal));
    }

    [Fact]
    public void Story_flags_are_unexplained_unless_confirmed()
    {
        var change = Assert.Single(_explainer.Explain(Jill(0), Jill(1, raw: ("flagScenarioB0", 0xC0000000))).Unexplained);
        Assert.Equal("flagScenarioB0:0x40000000:set", change.Key);
        Assert.Contains("story B flag #1 set", change.Description, StringComparison.Ordinal);

        // The statue falling is a confirmed label: not asked about.
        Assert.Empty(_explainer.Explain(Jill(0, "202"), Jill(1, "202", ("flagScenarioA0", 0x00100000))).Unexplained);
    }

    [Fact]
    public void Hits_are_named_by_the_damage_chart_and_poison_by_the_status()
    {
        (string, long)[] zombie = [("enemy0Flags", 1), ("enemy0Type", 0), ("playerStatus", 0)];
        var hit = _explainer.Explain(Jill(0, "105", [.. zombie, ("hp", 96)]), Jill(1, "105", [.. zombie, ("hp", 90)]));
        Assert.Contains("took damage (96 → 90, Zombie ankle bite (6))", hit.Known);

        // Damage no enemy here deals: no name.
        Assert.Contains("took damage (96 → 89)", _explainer.Explain(Jill(0, "105", [.. zombie, ("hp", 96)]), Jill(1, "105", [.. zombie, ("hp", 89)])).Known);

        // Poisoned, nothing in the chart fits: the poison.
        (string, long)[] poisoned = [("playerStatus", 0x02)];
        Assert.Contains("took damage (50 → 48, poison)", _explainer.Explain(Jill(0, "20E", [.. poisoned, ("hp", 50)]), Jill(1, "20E", [.. poisoned, ("hp", 48)])).Known);
    }

    [Fact]
    public void A_zombie_getting_back_up_is_told()
    {
        (string, long)[] slot = [("enemy0Flags", 1), ("enemy0Type", 0)];
        var explainer = new Re1Adapter().CreateChangeExplainer(new Re1Adapter().Profiles[0]);
        explainer.Explain(Jill(0, "104", [.. slot, ("enemy0Hp", 20)]), Jill(1, "104", [.. slot, ("enemy0Hp", -5)])); // down
        var up = explainer.Explain(Jill(2, "104", [.. slot, ("enemy0Hp", -5)]), Jill(3, "104", [.. slot, ("enemy0Hp", 40)]));
        Assert.Contains("Zombie got back up", up.Known);
    }

    [Fact]
    public void An_unremembered_enemy_is_named_from_the_one_that_fell()
    {
        // Barry shoots the zombie from Kenneth's corridor in the dining room: kill flag 255 (flagEnemies7 bit 0).
        (string, long)[] slot = [("enemy0Flags", 1), ("enemy0Type", 0)];
        var explainer = new Re1Adapter().CreateChangeExplainer(new Re1Adapter().Profiles[0]);
        explainer.Explain(Jill(0, "105", [.. slot, ("enemy0Hp", 20), ("flagEnemies7", 0)]), Jill(1, "105", [.. slot, ("enemy0Hp", -5), ("flagEnemies7", 0)]));
        var dead = explainer.Explain(Jill(2, "105", [.. slot, ("enemy0Hp", -5), ("flagEnemies7", 0)]), Jill(3, "105", [.. slot, ("enemy0Hp", -5), ("flagEnemies7", 1)]));
        Assert.Contains("Zombie died", dead.Known);
    }

    [Fact]
    public void People_turning_up_are_not_enemies_appearing()
    {
        var explainer = new Re1Adapter().CreateChangeExplainer(new Re1Adapter().Profiles[0]);
        var barry = explainer.Explain(Jill(0, "105", ("enemy0Flags", 0), ("enemy0Type", 0x22), ("enemy0Hp", 100)),
            Jill(1, "105", ("enemy0Flags", 1), ("enemy0Type", 0x22), ("enemy0Hp", 100)));
        Assert.DoesNotContain(barry.Known, line => line.EndsWith("appears", StringComparison.Ordinal));
    }

    [Fact]
    public void A_push_is_told_with_its_length_when_it_ends()
    {
        var explainer = new Re1Adapter().CreateChangeExplainer(new Re1Adapter().Profiles[0]);
        Assert.Contains("started pushing something", explainer.Explain(Jill(0, "202", ("stateFlags", 0)), Jill(1, "202", ("stateFlags", 0x40))).Known);
        Assert.Contains("pushed something (5.5 s)", explainer.Explain(Jill(6, "202", ("stateFlags", 0x40)), Jill(6.5, "202", ("stateFlags", 0))).Known);
    }

    [Fact]
    public void A_label_is_used_only_in_the_games_it_was_confirmed_in()
    {
        // Story B1 0x20000000: Jill is carried to the save room after fainting; Chris meets Rebecca there (live 2026-10-16).
        (string, long) rescue = ("flagScenarioB1", 0x20000000), noRescue = ("flagScenarioB1", 0);
        Assert.Equal(["rescued after fainting and carried to the save room"], _explainer.Explain(Jill(0, "100", noRescue), Jill(1, "100", rescue)).Known);
        (string, long) chris = ("character", 0);
        Assert.Equal(["Chris meets Rebecca in the save room (100)"], _explainer.Explain(Jill(0, "100", chris, noRescue), Jill(1, "100", chris, rescue)).Known);

        // The Emblem slot flag is starting state in Chris's game: nothing to show; in Jill's it's her putting it back.
        (string, long) emblem = ("flagScenarioB5", 0x01000000), noEmblem = ("flagScenarioB5", 0);
        Assert.Empty(_explainer.Explain(Jill(0, "106", chris, noEmblem), Jill(1, "106", chris, emblem)).Known);
        Assert.Equal(["wood Emblem put in the 10F slot, the door reopens"], _explainer.Explain(Jill(0, "10F", noEmblem), Jill(1, "10F", emblem)).Known);

        // A label confirmed only in Jill's game isn't used in Chris's (Barry's lockpick): the change is asked about instead.
        (string, long) lockpick = ("flagScenarioA3", 0x00000008), noLockpick = ("flagScenarioA3", 0);
        Assert.Equal(["Barry gives Jill the lockpick"], _explainer.Explain(Jill(0, raw: noLockpick), Jill(1, raw: lockpick)).Known);
        var forChris = _explainer.Explain(Jill(0, "105", chris, noLockpick), Jill(1, "105", chris, lockpick));
        Assert.DoesNotContain("Barry gives Jill the lockpick", forChris.Known);
        Assert.Single(forChris.Unexplained);
    }

    [Fact]
    public void First_visits_are_explained_but_other_room_bank_bits_are_not()
    {
        Assert.Empty(_explainer.Explain(Jill(0, "104"), Jill(1, "104", ("flagRoom0", 0x0E000000))).Unexplained); // room 104 = bit 27
        // Visits are numbered on across stages: the courtyard (300) is flag 63, and the map's own room numbers count
        // (107's back room is map room 11D, flag 29).
        Assert.Equal(["first visit to room 300"], _explainer.Explain(Jill(0, "300", ("flagRoom1", 0)), Jill(1, "300", ("flagRoom1", 0x1))).Known);
        Assert.Equal(["first visit to room 11D"], _explainer.Explain(Jill(0, "107"), Jill(1, "107", ("flagRoom0", 0x06000004))).Known);

        // Room-bank flags are only ever set in play; one being cleared is unexpected and asked about.
        var odd = Assert.Single(_explainer.Explain(Jill(0, "107", ("flagRoom4", 0x1)), Jill(1, "107", ("flagRoom4", 0))).Unexplained);
        Assert.Equal("flagRoom4:0x00000001:clear", odd.Key);
    }

    [Fact]
    public void Story_flags_from_room_scripts_maps_and_documents_are_explained()
    {
        // Room 10B's script sets story A #9 on the first visit (also labelled, but the script alone explains it).
        Assert.Empty(_explainer.Explain(Jill(0, "10B", ("flagScenarioA0", 0)), Jill(1, "10B", ("flagScenarioA0", 0x00400000))).Unexplained);

        // Unlabelled maps and documents follow the rule: map flag = item + 46 (127 → item 81), document = item + 35
        // (133 → document 98).
        Assert.Equal(["took item 0x51"], _explainer.Explain(Jill(0, "300", ("flagRoom3", 0)), Jill(1, "300", ("flagRoom3", 0x1))).Known);
        Assert.Equal(["took document 98"], _explainer.Explain(Jill(0, "10E", ("flagRoom4", 0)), Jill(1, "10E", ("flagRoom4", 0x04000000))).Known);
    }

    [Fact]
    public void Player_actions_are_reported_and_unknown_story_flags_can_be_attributed_to_them()
    {
        // Placing the star crest (item 0x2D) in 11A while an unlabelled story flag (A3 bit 0x2) is set.
        var report = _explainer.Explain(
            Jill(0, "11A", ("inv2", 0x012D), ("flagScenarioA3", 0)),
            Jill(1, "11A", ("flagScenarioA3", 0x2)));
        Assert.Contains("used Star crest", report.Actions);
        Assert.True(Assert.Single(report.Unexplained).ByAction);

        // A cleared story flag just ends a state: not asked about.
        Assert.Empty(_explainer.Explain(Jill(0, raw: ("flagScenarioB0", 0x80000001)), Jill(1, raw: ("flagScenarioB0", 0x80000000))).Unexplained);
    }

    [Fact]
    public void Unknown_items_and_enemies_are_reported()
    {
        var changes = _explainer.Explain(Jill(0), Jill(1, raw: [("inv3", 0x014C), ("enemy0Flags", 1), ("enemy0Type", 60)])).Unexplained;
        Assert.Contains(changes, c => c.Key == "item:0x4C");
        Assert.Contains(changes, c => c.Key == "enemy:60");
    }

    [Fact]
    public void Nothing_is_reported_across_a_new_game_or_load()
    {
        var later = Jill(0, raw: ("flagScenarioB0", 0xFFFFFFFF));
        var newGame = Jill(1) with { GameTime = Field<TimeSpan>.Valid(TimeSpan.Zero) };
        Assert.Empty(_explainer.Explain(later, newGame).Unexplained);
    }
}
