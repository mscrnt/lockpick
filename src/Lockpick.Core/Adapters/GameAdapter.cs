using Lockpick.Core.Fingerprinting;
using Lockpick.Core.Processes;
using Lockpick.Core.Profiles;
using Lockpick.Core.State;

namespace Lockpick.Core.Adapters;

/// <summary>Result of matching an executable fingerprint against an adapter's known builds.</summary>
public sealed record ProfileMatch(VersionProfile? Profile, string Reason)
{
    public bool IsSupported => Profile is not null;

    public static ProfileMatch Supported(VersionProfile profile) => new(profile, $"Matched profile {profile.Id}");

    public static ProfileMatch Unsupported(string reason) => new(null, reason);
}

/// <summary>
/// Integration point for one game. Everything game-specific (process names, builds, memory layout,
/// interpretation of raw values) lives behind this interface; the core never sees offsets.
/// </summary>
public interface IGameAdapter
{
    /// <summary>Stable short identifier, e.g. "re1".</summary>
    string GameId { get; }

    string DisplayName { get; }

    /// <summary>Executable file names this game runs as (case-insensitive), e.g. "ResidentEvil.exe".</summary>
    IReadOnlyCollection<string> ExecutableNames { get; }

    IReadOnlyList<VersionProfile> Profiles { get; }

    /// <summary>Matches a fingerprint against known builds. Unknown builds must return <see cref="ProfileMatch.Unsupported"/>.</summary>
    ProfileMatch Identify(ExecutableFingerprint fingerprint);

    /// <summary>
    /// Verifies the running image against the profile (header, anchors) and creates a session.
    /// Throws <see cref="AttachRejectedException"/> when the process does not match.
    /// </summary>
    IGameSession Attach(IAttachedProcess process, VersionProfile profile);

    /// <summary>
    /// Finds changes the game's known data doesn't explain, for captures that ask the player about them; null when the
    /// adapter has no such knowledge.
    /// </summary>
    Insight.IChangeExplainer? CreateChangeExplainer(VersionProfile profile) => null;

    /// <summary>Works out the player's progress (items, documents, rooms, enemies, story); null when unsupported.</summary>
    Insight.IProgressTracker? CreateProgressTracker(VersionProfile profile) => null;

    /// <summary>
    /// Reads room layouts (collision, doors, items, cameras) from the player's own install of the game run from
    /// <paramref name="executablePath"/> (one folder can hold two releases); null when the adapter can't.
    /// </summary>
    Maps.IRoomMapSource? CreateRoomMaps(string executablePath) => null;

    /// <summary>The folder the game writes its saves to, for the executable at <paramref name="executablePath"/>; null if unknown.</summary>
    string? SaveDirectory(string executablePath) => null;

    /// <summary>The save slot a file in <see cref="SaveDirectory"/> belongs to, or null for any other file.</summary>
    int? SaveSlot(string fileName) => null;

    /// <summary>The game window's class names (one per build, e.g. "RESIDENT EVIL;BIOHAZARD"), to tell it from the
    /// process's other windows, separated by ';'; null to take the first visible one.</summary>
    string? WindowClass => null;

    /// <summary>
    /// How the game scales its picture into its window, from the settings in the install at
    /// <paramref name="installDirectory"/> (read only); null when unknown.
    /// </summary>
    Display.GameDisplaySettings? DisplaySettings(string installDirectory) => null;

    /// <summary>The game's display settings in the install at <paramref name="installDirectory"/>, for the player to
    /// change from Lockpick (windowed / fullscreen, resolution, scaling); the original is kept in
    /// <paramref name="backupDirectory"/> (outside the game folder). Null when the game has none.</summary>
    Display.IGameDisplayConfig? DisplayConfig(string installDirectory, string backupDirectory) => null;

    /// <summary>What challenges need to know about the game (e.g. what counts as a shot); null when unsupported.</summary>
    Challenges.IChallengeRules? CreateChallengeRules(VersionProfile profile) => null;

    /// <summary>What runs need to know about the game (split sets, deaths, the ending); null when unsupported.</summary>
    Runs.IRunRules? CreateRunRules(VersionProfile profile) => null;

    /// <summary>The game's trainer features (what to write for health, ammo, …); null when the adapter has none.</summary>
    Trainer.IGameTrainer? CreateTrainer(VersionProfile profile) => null;

    /// <summary>A short name for the room (e.g. "Dining room"), or null when the adapter doesn't know it.</summary>
    string? RoomName(string roomId) => null;

    /// <summary>The item's name, or null when the adapter doesn't know it.</summary>
    string? ItemName(int itemId) => null;

    /// <summary>The keyboard key the player has set for <paramref name="control"/> ("C", "↑"), or null when unknown.</summary>
    string? KeyName(Routes.GameControl control, State.GameSnapshot snapshot) => null;

    /// <summary>The player isn't in control right now (a message, a menu, the inventory, a cutscene): prompts wait.</summary>
    bool PlayerBusy(State.GameSnapshot snapshot) => false;
}

/// <summary>A verified connection to one running game process.</summary>
public interface IGameSession
{
    VersionProfile Profile { get; }

    /// <summary>When true, <see cref="Read"/> also copies the profile's capture regions into the snapshot.</summary>
    bool CaptureRegions { get; set; }

    /// <summary>Reads the current state. Must not throw for ordinary read failures; failed fields are marked instead.</summary>
    GameSnapshot Read(long sequence, DateTimeOffset now);
}

public sealed class AttachRejectedException(string message) : Exception(message);
