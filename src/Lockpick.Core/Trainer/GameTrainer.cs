using Lockpick.Core.State;

namespace Lockpick.Core.Trainer;

/// <summary>
/// A game's trainer features, in terms of its profile fields. The adapter supplies it (the core never knows what a
/// field means); every write still goes through <see cref="TrainerService"/>, which validates, reads back and logs.
/// </summary>
public interface IGameTrainer
{
    /// <summary>The writable field holding the player's health.</summary>
    string HealthField { get; }

    /// <summary>The field holding the player's maximum health (the upper bound for <see cref="HealthField"/>).</summary>
    string MaxHealthField { get; }

    /// <summary>
    /// A rule for <see cref="TrainerService.SetRule"/> that keeps the equipped weapon's rounds from going down: each
    /// call returns the writes that restore the highest count seen for that weapon (reloads raise it). Each rule keeps
    /// its own memory, so create a new one each time the feature is switched on.
    /// </summary>
    Func<GameSnapshot, IReadOnlyList<FieldWrite>> CreateInfiniteAmmoRule();

    /// <summary>The fields keep-running writes: it is offered only where the profile has them all writable.</summary>
    IReadOnlyList<string> KeepRunningFields { get; }

    /// <summary>
    /// A rule that keeps the game running while it isn't the active window (<paramref name="inFront"/> says whether it
    /// is) and keeps keys typed in other windows away from it; it puts the game's own state back when the window is in
    /// front again and when the rule ends. Each rule keeps its own memory, so create a new one each time.
    /// </summary>
    TrainerRule CreateKeepRunningRule(Func<bool> inFront);

    /// <summary>
    /// The profile's code patches (<see cref="Profiles.VersionProfile.Patches"/>, by name) that keep the controller and the
    /// sound working while the game isn't in front, added to keep-running when the player asks; offered only where the
    /// profile has them all usable. None when the game has no such patches.
    /// </summary>
    IReadOnlyList<string> BackgroundPatches => [];

    /// <summary>Items the player can be given (through <see cref="AddToStorage"/>), by id, with their names.</summary>
    IReadOnlyList<GivableItem> GivableItems { get; }

    /// <summary>True when the held item in <paramref name="slot"/> has a count the trainer may change (rounds, ammo, …).</summary>
    bool HasQuantity(int itemId);

    /// <summary>The write that changes the count of the held item in <paramref name="slot"/>, or null with the reason.</summary>
    FieldWrite? SetQuantity(GameSnapshot snapshot, int slot, int quantity, out string? refusal);

    /// <summary>
    /// The write that puts an item into the game's storage (RE1: the first empty item-box entry), where the game loads it
    /// properly when the player takes it out; null with the reason when it can't.
    /// </summary>
    FieldWrite? AddToStorage(GameSnapshot snapshot, int itemId, int quantity, out string? refusal);
}

/// <summary>An item the trainer can give, and whether it comes with a count.</summary>
public sealed record GivableItem(int Id, string Name, bool HasQuantity);
