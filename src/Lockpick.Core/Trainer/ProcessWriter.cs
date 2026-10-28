using Lockpick.Core.Memory;
using Lockpick.Core.State;

namespace Lockpick.Core.Trainer;

/// <summary>
/// A second handle to the game process that may write. It exists only while Trainer Mode is on (opened when the mode is
/// switched on or the game is attached in it, closed when the mode changes or the game goes away); monitoring never
/// uses it.
/// </summary>
public interface IProcessWriter : IProcessMemory, IDisposable
{
    int ProcessId { get; }

    /// <summary>Writes all of <paramref name="source"/> at <paramref name="address"/>; false (never throws) on failure.</summary>
    bool TryWrite(ulong address, ReadOnlySpan<byte> source);
}

/// <summary>Opens write-capable handles. Kept apart from <see cref="Processes.IProcessHost"/>, which only reads.</summary>
public interface IProcessWriteHost
{
    /// <summary>Opens the process for writing. Throws <see cref="Processes.ProcessAccessException"/> on failure.</summary>
    IProcessWriter OpenForWrite(int processId);
}

/// <summary>What Lockpick may do to the game.</summary>
public enum TrainerMode
{
    /// <summary>Monitoring only (the default every time Lockpick starts): no write handle exists.</summary>
    ReadOnly,

    /// <summary>The player asked for trainer features: a write handle is opened and validated writes are allowed.</summary>
    Trainer,

    /// <summary>A challenge or record run: no write handle, every write refused, the run marked clean.</summary>
    Challenge,
}

/// <summary>Who asked for a write.</summary>
public enum TrainerSource
{
    User,
    Hotkey,
    Lock,
    Rule,

    /// <summary>Another program through Lockpick's local API (only when the player allowed trainer commands there).</summary>
    Api,
}

public enum TrainerResult
{
    /// <summary>Written and read back with the requested value.</summary>
    Applied,

    /// <summary>Not attempted: the mode, the field or the value doesn't allow it.</summary>
    Refused,

    /// <summary>The write call failed.</summary>
    Failed,

    /// <summary>Written, but reading it back gave a different value.</summary>
    Mismatch,
}

/// <summary>One trainer action, as shown in the app and kept on disk.</summary>
public sealed record TrainerAction(
    DateTimeOffset Timestamp,
    string Action,
    string? Field,
    long? OldValue,
    long? NewValue,
    TrainerSource Source,
    TrainerResult Result,
    string? Message = null)
{
    public string Describe()
    {
        var change = Field is null ? string.Empty
            : OldValue is { } old && NewValue is { } value ? $" {Field} {old} → {value}"
            : NewValue is { } requested ? $" {Field} → {requested}"
            : $" {Field}";
        var outcome = Result == TrainerResult.Applied ? string.Empty : $" — {Result.ToString().ToLowerInvariant()}";
        return $"{Action}{change}{outcome}{(Message is null ? string.Empty : $": {Message}")}";
    }
}

/// <summary>Ids of the rules shared by the Trainer page, hotkeys and the API, so they all switch the same rule.</summary>
public static class TrainerRules
{
    public const string InfiniteAmmo = "infinite-ammo";

    public const string KeepRunning = "keep-running";
}

/// <summary>
/// A trainer feature that writes on every poll (<see cref="TrainerService.SetRule(string, TrainerRule, TrainerSource)"/>):
/// <paramref name="Writes"/> says which fields the latest snapshot calls for (none when the game is already right).
/// </summary>
public sealed record TrainerRule(string Name, Func<GameSnapshot, IReadOnlyList<FieldWrite>> Writes)
{
    /// <summary>Also applies outside gameplay (at the title, in menus); its fields must allow it (<see cref="Profiles.WriteSpec.AnyTime"/>).</summary>
    public bool OutsideGameplay { get; init; }

    /// <summary>The writes that put the game back when the rule ends: switched off, Trainer Mode left (a challenge, a
    /// leaderboard run) or Lockpick closing.</summary>
    public Func<IReadOnlyList<FieldWrite>>? Restore { get; init; }

    /// <summary>
    /// Code patches from the profile (<see cref="Profiles.VersionProfile.Patches"/>, by name) applied when the rule is
    /// switched on and put back when it ends, as <see cref="Restore"/>. The rule is refused unless every one is usable
    /// on this build and the game's code is as expected.
    /// </summary>
    public IReadOnlyList<string> Patches { get; init; } = [];
}

/// <summary>One field value a trainer feature wants written.</summary>
public readonly record struct FieldWrite(string Field, long Value);
