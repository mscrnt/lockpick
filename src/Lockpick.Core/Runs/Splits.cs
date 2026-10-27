using Lockpick.Core.State;

namespace Lockpick.Core.Runs;

/// <summary>What ends a split. Only the current split's trigger is checked, so the same room can end two splits.</summary>
public abstract record SplitTrigger
{
    /// <summary>A short description for the split editor and the docs ("Enter 203 from 204").</summary>
    public abstract string Describe();
}

/// <summary>
/// The player enters <paramref name="Room"/> (adapter room id, e.g. "203"): from <paramref name="From"/> when given, or
/// from another stage when <paramref name="FromOtherStage"/> is set, else from anywhere.
/// </summary>
public sealed record RoomEntered(string Room, string? From = null, bool FromOtherStage = false) : SplitTrigger
{
    public override string Describe() =>
        From is not null ? $"Enter {Room} from {From}" : FromOtherStage ? $"Enter {Room} from another stage" : $"Enter {Room}";
}

/// <summary>
/// One more of item <paramref name="ItemId"/> is held than before (a pickup), in <paramref name="Room"/> when given
/// (for items there are several of, e.g. batteries).
/// </summary>
public sealed record ItemObtained(int ItemId, string? Room = null) : SplitTrigger
{
    public override string Describe() => Room is null ? $"Get item {ItemId}" : $"Get item {ItemId} in {Room}";
}

/// <summary>The run's end as the game shows it (RE1: the final cutscene starts).</summary>
public sealed record RunFinished : SplitTrigger
{
    public override string Describe() => "The ending starts";
}

/// <summary>Only the split hotkey ends it (e.g. an imported split Lockpick has no trigger for).</summary>
public sealed record ManualSplit : SplitTrigger
{
    public override string Describe() => "Split key";
}

/// <summary>One split: the name shown (and matched against LiveSplit files) and what ends it.</summary>
public sealed record SplitDefinition(string Name, SplitTrigger Trigger);

/// <summary>
/// A list of splits for one category (e.g. "Jill - Bad Ending - NMG"). <paramref name="Verified"/> is false until the
/// triggers were seen to fire at the right moments in a real run (capture bounties B701/B702).
/// </summary>
public sealed record SplitSet(
    string Id,
    string Name,
    string Category,
    PlayableCharacter Character,
    IReadOnlyList<SplitDefinition> Splits,
    bool Verified,
    string Source)
{
    /// <summary>Where the set came from when imported (a LiveSplit file), else null.</summary>
    public string? ImportedFrom { get; init; }
}

/// <summary>What runs need to know about the game that the snapshot doesn't say; from the adapter.</summary>
public interface IRunRules
{
    /// <summary>The built-in split sets.</summary>
    IReadOnlyList<SplitSet> SplitSets { get; }

    /// <summary>The built-in routes for the route guide.</summary>
    IReadOnlyList<Routes.Route> Routes => [];

    /// <summary>The player has died (the game over / death sequence).</summary>
    bool PlayerDied(GameSnapshot snapshot);

    /// <summary>The run's end: the ending started between <paramref name="previous"/> and <paramref name="current"/>.</summary>
    bool RunFinished(GameSnapshot previous, GameSnapshot current);

    /// <summary>The game is at its title menu (the player quit, or reset the game).</summary>
    bool AtTitle(GameSnapshot snapshot);

    /// <summary>
    /// Whether a new game seen in <paramref name="snapshot"/> is a real one a run can start on, not the game's own demo
    /// (attract mode), which can look like a new game.
    /// </summary>
    bool IsRealNewGame(GameSnapshot snapshot) => true;

    /// <summary>The game's speedrun.com leaderboard, or null when it has none Lockpick knows.</summary>
    LeaderboardDefinition? Leaderboard => null;

    /// <summary>The board a split set's runs belong on (filter key → choice key), as far as its name says.</summary>
    IReadOnlyDictionary<string, string> LeaderboardFor(SplitSet splits) => new Dictionary<string, string>();

    /// <summary>The install of the game run from <paramref name="executablePath"/> against the official release's files,
    /// or null when the adapter has no list for it. Reads (hashes) every file: call it off the UI thread.</summary>
    InstallCheck? CheckInstall(string executablePath) => null;

    /// <summary>The leaderboard's rules only the player can keep (no music over the game, …), as reminders.</summary>
    IReadOnlyList<string> LeaderboardReminders => [];
}
