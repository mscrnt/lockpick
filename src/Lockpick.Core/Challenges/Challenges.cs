using Lockpick.Core.State;

namespace Lockpick.Core.Challenges;

/// <summary>A condition a challenge holds the player to. Breaking it fails the attempt.</summary>
public enum ChallengeRule
{
    /// <summary>Any damage fails (a hit, poison).</summary>
    NoDamage,

    /// <summary>Firing anything but the knife fails (equipping a gun alone doesn't).</summary>
    KnifeOnly,
}

/// <summary>A challenge the player can pick: a name, what it means in plain words, and its rules.</summary>
public sealed record ChallengeDefinition(string Id, string Name, string Summary, IReadOnlyList<ChallengeRule> Rules)
{
    /// <summary>The ready-made challenges, in the order the app offers them.</summary>
    public static IReadOnlyList<ChallengeDefinition> Presets { get; } =
    [
        new("speedrun", "Speedrun", "Finish as fast as you can. Lockpick can't change your game, so the run is clean; the time is the game's own clock.", []),
        new("no-damage", "No damage", "Don't get hurt. One hit (or poison) and the attempt is over.", [ChallengeRule.NoDamage]),
        new("knife-only", "Knife only", "Fight with the knife alone. Firing any other weapon ends the attempt; carrying one is fine.", [ChallengeRule.KnifeOnly]),
    ];

    /// <summary>The rules in plain words, for the app and the API.</summary>
    public static string Describe(ChallengeRule rule) => rule switch
    {
        ChallengeRule.NoDamage => "No damage",
        ChallengeRule.KnifeOnly => "Knife only",
        _ => rule.ToString(),
    };
}

/// <summary>Game knowledge a challenge needs that the core can't derive (the adapter supplies it).</summary>
public interface IChallengeRules
{
    /// <summary>
    /// The name of the weapon fired between <paramref name="previous"/> and <paramref name="current"/> when it isn't a
    /// melee weapon (a shot: the equipped gun lost rounds), else null.
    /// </summary>
    string? NonMeleeShot(GameSnapshot previous, GameSnapshot current);
}

public enum ChallengeState
{
    /// <summary>No challenge.</summary>
    Off,

    /// <summary>Chosen and started, waiting for the player to be in a game.</summary>
    Waiting,

    /// <summary>In a game, no rule broken so far.</summary>
    Running,

    /// <summary>A rule was broken; the attempt is over until a retry, a new game or a load.</summary>
    Failed,
}

/// <summary>Where the current challenge stands.</summary>
public sealed record ChallengeStatus(
    ChallengeState State,
    ChallengeDefinition? Challenge,
    IReadOnlyList<ChallengeRule> Rules,
    int Attempt,
    DateTimeOffset? AttemptStarted,
    string? FailReason,
    DateTimeOffset? FailedAt,
    TimeSpan? FailedAtGameTime)
{
    public static ChallengeStatus Off { get; } = new(ChallengeState.Off, null, [], 0, null, null, null, null);

    /// <summary>The trainer changed the game during this attempt (cheats are allowed in a challenge that isn't locked).</summary>
    public bool WithCheats { get; init; }
}
