using System.Reflection;
using Lockpick.Core.Adapters;
using Lockpick.Core.Fingerprinting;
using Lockpick.Core.Memory;
using Lockpick.Core.Processes;
using Lockpick.Core.Profiles;

namespace Lockpick.GameAdapter.RE1;

/// <summary>Adapter for the original Resident Evil (1996) PC release.</summary>
public sealed class Re1Adapter : IGameAdapter
{
    public const string Id = "re1";

    /// <summary>GOG product id of "Resident Evil" (used to locate the install via the registry).</summary>
    public const string GogProductId = "1580232252";

    /// <summary>Steam app id of "Resident Evil (1996)" (used to locate the install via Steam's library folders).</summary>
    public static string SteamAppId => Re1Install.KnownSteam.AppId;

    private const string ResourcePrefix = "Lockpick.GameAdapter.RE1.Profiles.";

    private static readonly Re1RunRules RunRules = new();

    public Re1Adapter(IEnumerable<VersionProfile>? additionalProfiles = null)
    {
        var profiles = LoadEmbeddedProfiles().ToList();
        foreach (var extra in additionalProfiles ?? [])
        {
            if (extra.GameId != Id)
            {
                continue;
            }

            // A user-supplied profile with the same id overrides the built-in one.
            profiles.RemoveAll(p => p.Id == extra.Id);
            profiles.Add(extra);
        }

        // The most live-verified build first: the default where no game is attached yet (the Debug and Progress pages).
        Profiles = [.. profiles.OrderByDescending(p => p.Fields.Count(f => f.Verification == Verification.Runtime))];
    }

    public string GameId => Id;

    public string DisplayName => "Resident Evil";

    /// <summary>GOG's English release runs as ResidentEvil.exe; the Japanese PC game (MediaKite, GOG/Steam Japanese, with
    /// or without Classic REbirth) as Biohazard.exe. Which build it is comes from the hash (profiles).</summary>
    public IReadOnlyCollection<string> ExecutableNames { get; } = ["ResidentEvil.exe", "Biohazard.exe"];

    public IReadOnlyList<VersionProfile> Profiles { get; }

    public ProfileMatch Identify(ExecutableFingerprint fingerprint) => ProfileMatcher.Match(Profiles, fingerprint);

    public IGameSession Attach(IAttachedProcess process, VersionProfile profile)
    {
        var problems = ImageVerifier.Verify(process, profile);
        if (problems.Count > 0)
        {
            throw new AttachRejectedException($"Running image does not match profile {profile.Id}: {string.Join("; ", problems)}");
        }

        return new Re1Session(new ValidatedMemory(process, ImageVerifier.AllowedRanges(profile)), profile, ActorInfo);
    }

    public Core.Insight.IChangeExplainer CreateChangeExplainer(VersionProfile profile) =>
        new Re1ChangeExplainer(Re1Knowledge.LoadEmbedded());

    public Core.Trainer.IGameTrainer CreateTrainer(VersionProfile profile) => _trainer ??= new Re1Trainer(ItemName);

    private Re1Trainer? _trainer;

    public Core.Challenges.IChallengeRules CreateChallengeRules(VersionProfile profile) => (Re1Trainer)CreateTrainer(profile);

    public Core.Runs.IRunRules CreateRunRules(VersionProfile profile) => RunRules;

    /// <summary>
    /// The GOG game saves to SAVE\savedatN.dat next to the executable (one file per slot, 1–8; B112, #37); with Classic
    /// REbirth the same files go to its save folder (savedata\ unless config.ini says otherwise).
    /// </summary>
    public string? SaveDirectory(string executablePath) =>
        Path.GetDirectoryName(executablePath) is { } folder ? Re1Install.Inspect(folder).SaveDirectory : null;

    public int? SaveSlot(string fileName) =>
        fileName.Length == 12 && fileName.StartsWith("savedat", StringComparison.OrdinalIgnoreCase)
            && fileName.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) && fileName[7] is >= '1' and <= '8'
            ? fileName[7] - '0'
            : null;

    /// <summary>GOG's English game's window, then the Japanese game's (live 2026-10-17: class "BIOHAZARD").</summary>
    public string WindowClass => "RESIDENT EVIL;BIOHAZARD";

    /// <summary>
    /// GOG's DirectDraw wrapper settings (<c>dxcfg.ini</c>, docs/DISPLAY.md): <c>aspect=disabled</c> stretches the
    /// 640×480 picture over the window, <c>scaling=integer</c> uses whole multiples, otherwise it is fitted at 4:3.
    /// With Classic REbirth (its own DirectDraw replacement) the wrapper's file doesn't apply: the picture is taken as fitted.
    /// </summary>
    public Core.Display.GameDisplaySettings DisplaySettings(string installDirectory) =>
        Re1Install.Inspect(installDirectory).DisplaySettings is { } ini ? Re1Display.Read(ini) : Re1Display.Default;

    /// <summary>GOG's wrapper settings to change from Lockpick (on Steam, in its launcher's ini); none with Classic
    /// REbirth (it has its own setup).</summary>
    public Core.Display.IGameDisplayConfig? DisplayConfig(string installDirectory, string backupDirectory) =>
        Re1Install.Inspect(installDirectory).DisplaySettings is { } ini ? new Re1DisplayConfig(ini, backupDirectory) : null;

    public Core.Insight.IProgressTracker CreateProgressTracker(VersionProfile profile) =>
        new Re1ProgressTracker(Re1Knowledge.LoadEmbedded());

    private static readonly Lazy<Re1Knowledge> Knowledge = new(Re1Knowledge.LoadEmbedded);

    // Bosses by enemy type (names.json): Black Tiger, Plant 42, Neptune, Tyrant, Yawn, final Tyrant, Yawn's second fight.
    private static readonly HashSet<int> Bosses = [4, 8, 11, 12, 13, 16, 18];

    internal static (string Name, Core.State.ActorKind Kind) ActorInfo(int type) =>
        (Knowledge.Value.Enemy(type), type >= 0x20 ? Core.State.ActorKind.Npc : Bosses.Contains(type) ? Core.State.ActorKind.Boss : Core.State.ActorKind.Enemy);

    public string? ItemName(int itemId) => Knowledge.Value.ItemNames.TryGetValue(itemId, out var name) ? name : null;

    public string? RoomName(string roomId) => Knowledge.Value.RoomNames.TryGetValue(roomId, out var name) ? name : null;

    public string? KeyName(Core.Routes.GameControl control, Core.State.GameSnapshot snapshot) => Re1Controls.KeyName(control, snapshot);

    /// <summary>
    /// stateFlags bits that mean the player isn't in control (docs/MEMORY_MAP.md, the bits the dev harness waits out:
    /// 0x800 interacting and 0x8000 a message while examining or picking up, the menus' 0x20000000 / 0x40000000 cycle,
    /// camera locks and voice in cutscenes, death); 0x4 / 0x8 are left by room scripts and don't count.
    /// </summary>
    private const long NotInControl = 0x80 | 0xFF00 | 0x10000 | 0x20000 | 0x40000 | 0x100000 | 0x01000000 | 0x04000000 | 0x20000000;

    public bool PlayerBusy(Core.State.GameSnapshot snapshot) =>
        snapshot.RawFields.TryGetValue(Re1Session.Fields.StateFlags, out var flags) && flags.HasValue && (flags.Value & NotInControl) != 0;

    /// <summary>The room files of the game being played: JPN for Biohazard.exe, USA for ResidentEvil.exe.</summary>
    public Core.Maps.IRoomMapSource CreateRoomMaps(string executablePath)
    {
        var knowledge = Re1Knowledge.LoadEmbedded();
        var install = Re1Install.Inspect(Path.GetDirectoryName(executablePath) ?? ".");
        return new Re1RoomMaps(install.DataDirectoryFor(Path.GetFileName(executablePath)), knowledge.Item, knowledge.Fills);
    }

    public static IEnumerable<VersionProfile> LoadEmbeddedProfiles()
    {
        var assembly = typeof(Re1Adapter).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Missing embedded profile {name}.");
            yield return ProfileLoader.Load(stream);
        }
    }
}
