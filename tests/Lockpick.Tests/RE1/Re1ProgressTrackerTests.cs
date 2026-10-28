using Lockpick.Core.Insight;
using Lockpick.Core.State;
using Lockpick.GameAdapter.RE1;

namespace Lockpick.Tests.RE1;

/// <summary>The progress tracker reads progress from flags alone; values below are the ones seen live.</summary>
public class Re1ProgressTrackerTests
{
    private readonly IProgressTracker _tracker;

    public Re1ProgressTrackerTests()
    {
        var adapter = new Re1Adapter();
        _tracker = adapter.CreateProgressTracker(adapter.Profiles[0]);
    }

    private static GameSnapshot Jill(params (string Field, long Value)[] raw)
    {
        var fields = new Dictionary<string, Field<long>> { ["character"] = Field<long>.Valid(1) };
        foreach (var bank in new[] { ("flagRoomItems", 8), ("flagEnemies", 8), ("flagRoom", 5), ("flagScenarioA", 4), ("flagScenarioB", 8) })
        {
            for (var word = 0; word < bank.Item2; word++)
            {
                // A fresh game: every item still in the world (pickup bits set), nothing else.
                fields[$"{bank.Item1}{word}"] = Field<long>.Valid(bank.Item1 == "flagRoomItems" ? 0xFFFFFFFF : 0);
            }
        }

        foreach (var (field, value) in raw)
        {
            fields[field] = Field<long>.Valid(value);
        }

        return new GameSnapshot { InGameplay = Field<bool>.Valid(true), RawFields = fields };
    }

    private static ProgressGroup Group(ProgressReport report, string area, string title) =>
        report.Areas.Single(a => a.Area == area).Groups.Single(g => g.Title == title);

    [Fact]
    public void A_fresh_game_has_nothing_done()
    {
        var report = _tracker.Evaluate(Jill())!;
        Assert.All(report.Areas.SelectMany(a => a.Groups), g => Assert.Equal(0, g.Done));
        Assert.Equal(32, Group(report, "Mansion 1F", "Rooms explored").Total); // the game's room table: 32 on 1F
        Assert.Equal(18, Group(report, "Guardhouse", "Rooms explored").Total);
        Assert.DoesNotContain(Group(report, "Mansion 1F", "Items").Entries, e => e.Name == "Lockpick"); // handed over, not picked up
        Assert.Empty(report.Story);
    }

    [Fact]
    public void Taken_items_explored_rooms_kills_and_story_count()
    {
        var report = _tracker.Evaluate(Jill(
            ("flagRoomItems1", 0xFFF7FFFF),     // blue jewel (item flag 44) taken
            ("flagRoom0", 0x06000000),          // rooms 105 and 106 visited
            ("flagRoom3", 0x00000008),          // the 1F map taken (map flag 124)
            ("flagEnemies0", 0xC0000000),       // the two dogs in 108 (kill flags 0, 1)
            ("flagScenarioA0", 0x00100000)))!;  // the statue falls (named)

        Assert.Contains(Group(report, "Mansion 1F", "Items").Entries, e => e.Name == "Blue jewel" && e.Done);
        Assert.Equal(2, Group(report, "Mansion 1F", "Rooms explored").Done);
        Assert.Equal(2, Group(report, "Mansion 1F", "Enemies defeated").Entries.Count(e => e.Done && e.Name.StartsWith("Cerberus", StringComparison.Ordinal)));
        Assert.Contains("the statue falls (blue jewel drops into the dining room)", report.Story);
        Assert.Equal(0, report.UnnamedStory);
    }

    [Fact]
    public void An_item_counts_as_taken_only_from_a_room_the_player_has_been_in()
    {
        // The blue jewel's pickup flag (44) is clear: before 105 is visited it hasn't been put there yet, not taken.
        var before = _tracker.Evaluate(Jill(("flagRoomItems1", 0xFFF7FFFF)))!;
        Assert.Contains(Group(before, "Mansion 1F", "Items").Entries, e => e.Name == "Blue jewel" && !e.Done);

        var after = _tracker.Evaluate(Jill(("flagRoomItems1", 0xFFF7FFFF), ("flagRoom0", 0x04000000)))!; // 105 visited
        Assert.Contains(Group(after, "Mansion 1F", "Items").Entries, e => e.Name == "Blue jewel" && e.Done);
    }

    [Fact]
    public void Ink_ribbons_are_listed_for_Chris_but_never_for_Jill()
    {
        static bool Ribbons(ProgressReport report) =>
            report.Areas.SelectMany(a => a.Groups).SelectMany(g => g.Entries).Any(e => e.Name == "Ink ribbon");

        Assert.False(Ribbons(_tracker.Evaluate(Jill())!));
        Assert.True(Ribbons(_tracker.Evaluate(Jill(("character", 0)))!));
    }

    [Fact]
    public void Saves_are_savedat1_to_savedat8_in_the_SAVE_folder()
    {
        var adapter = new Re1Adapter();
        Assert.Equal(Path.Combine(@"G:\GOG Games\Resident Evil", "SAVE"), adapter.SaveDirectory(@"G:\GOG Games\Resident Evil\ResidentEvil.exe"));
        Assert.Equal(1, adapter.SaveSlot("savedat1.dat"));
        Assert.Equal(8, adapter.SaveSlot("SAVEDAT8.DAT"));
        Assert.Equal(3, adapter.SaveSlot("savedat3.daj")); // GOG's Japanese build
        // The Seamless HD pack bundles the Japanese 1.01 exe with REbirth as Bio.exe: found, and read with the JPN data.
        Assert.Contains("Bio.exe", adapter.ExecutableNames);
        Assert.True(Re1Install.IsJapanese(@"G:\Games\RE HD\re1hd\Bio.exe"));
        Assert.False(Re1Install.IsJapanese("ResidentEvil.exe"));
        Assert.Null(adapter.SaveSlot("savedat9.dat"));
        Assert.Null(adapter.SaveSlot("savedat1.dat.tmp"));
        Assert.Null(adapter.SaveSlot("notes.txt"));
    }

    [Fact]
    public void The_adapter_names_rooms_it_knows()
    {
        var adapter = new Re1Adapter();
        Assert.Equal("Dining room", adapter.RoomName("105"));
        Assert.Null(adapter.RoomName("XYZ"));
    }

    [Fact]
    public void Nothing_is_reported_outside_a_game()
    {
        Assert.Null(_tracker.Evaluate(new GameSnapshot { InGameplay = Field<bool>.Valid(false) }));
    }
}
