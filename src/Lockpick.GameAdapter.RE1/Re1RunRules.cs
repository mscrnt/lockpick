using System.Text.Json;
using Lockpick.Core.Routes;
using Lockpick.Core.Runs;
using Lockpick.Core.State;

namespace Lockpick.GameAdapter.RE1;

/// <summary>
/// What runs need to know about RE1 (docs/RUNS.md): the built-in split sets (data/games/re1/splits.json), a death,
/// the title menu and the ending.
/// </summary>
public sealed class Re1RunRules : IRunRules
{
    private const string Resource = "Lockpick.GameAdapter.RE1.Data.splits.json";

    /// <summary>Stage index (0-based) of the heliport, where the ending starts.</summary>
    private const int EndingStage = 2;

    private static readonly Lazy<IReadOnlyList<SplitSet>> Sets = new(LoadEmbedded);
    private static readonly Lazy<LeaderboardDefinition> Board = new(LoadBoard);
    private static readonly Lazy<IReadOnlyList<Route>> RouteList = new(() =>
    {
        using var stream = typeof(Re1RunRules).Assembly.GetManifestResourceStream("Lockpick.GameAdapter.RE1.Data.routes.json")
            ?? throw new InvalidOperationException("Missing embedded RE1 data routes.json.");
        return RouteFile.Parse(stream);
    });

    public IReadOnlyList<SplitSet> SplitSets => Sets.Value;

    /// <summary>The built-in routes (data/games/re1/routes.json).</summary>
    public IReadOnlyList<Route> Routes => RouteList.Value;

    /// <summary>RE1 PC on speedrun.com (data/games/re1/speedrun-com.json).</summary>
    public LeaderboardDefinition Leaderboard => Board.Value;

    /// <summary>
    /// The board from the split set's category and name: Jill or Chris, the ending (an any% key-item route ends badly:
    /// the fastest ending), glitches, knife only. What a name doesn't say is left for the player to pick.
    /// </summary>
    public IReadOnlyDictionary<string, string> LeaderboardFor(SplitSet splits)
    {
        var text = $"{splits.Category} {splits.Name}";
        bool Has(string word) => text.Contains(word, StringComparison.OrdinalIgnoreCase);
        var board = new Dictionary<string, string>();
        if (splits.Character is PlayableCharacter.Jill or PlayableCharacter.Chris)
        {
            board["character"] = splits.Character == PlayableCharacter.Jill ? "jill" : "chris";
        }

        if (Has("Good Ending") || Has("Best Ending"))
        {
            board["ending"] = "best";
        }
        else if (Has("Bad Ending") || Has("Any%"))
        {
            board["ending"] = "bad";
        }

        if (Has("NMG") || Has("No Major"))
        {
            board["glitches"] = "nmg";
        }
        else if (Has("MG") || Has("Major Glitches"))
        {
            board["glitches"] = "mg";
        }

        board["restriction"] = Has("Knife") ? "knife" : "none";
        return board;
    }

    /// <summary>
    /// HP below 0 in a game (docs/MEMORY_MAP.md: every kill leaves HP below 0; poison and wasps clamp at 1). The state
    /// flag 0x01000000 comes with it but may lag the HP; the HP alone is enough. Read from the raw fields: once HP is
    /// below 0 the snapshot no longer counts as in game.
    /// </summary>
    public bool PlayerDied(GameSnapshot snapshot) =>
        Raw(snapshot, Re1Session.Fields.MainMenu) == 0
        && Raw(snapshot, Re1Session.Fields.MaxHp) > 0
        && Raw(snapshot, Re1Session.Fields.Hp) is < 0;

    /// <summary>The title / main menu flag (runtime-verified field <c>mainMenu</c>).</summary>
    public bool AtTitle(GameSnapshot snapshot) => Raw(snapshot, Re1Session.Fields.MainMenu) == 1;

    /// <summary>
    /// Every real new game opens in the main hall (106); the attract demo starts elsewhere (GOG English: 107 with HP 0;
    /// the Japanese 1.01 under REbirth: 11B with HP 60, live 2026-10-18, when it started an attempt). The community
    /// RE1 autosplitter starts on room 106 for the same reason.
    /// </summary>
    public bool IsRealNewGame(GameSnapshot snapshot) =>
        !snapshot.Location.TryGet(out var room) || room.Id == "106";

    /// <summary>
    /// The final cutscene: the state flags drop to 0 at the heliport (the community autosplitter's final split, RE1aio.asl).
    /// Not yet seen live by Lockpick (capture bounty B703).
    /// </summary>
    public bool RunFinished(GameSnapshot previous, GameSnapshot current) =>
        Raw(current, Re1Session.Fields.StateFlags) == 0
        && Raw(previous, Re1Session.Fields.StateFlags) is not (null or 0)
        && Raw(current, Re1Session.Fields.Stage) == EndingStage
        && Raw(current, Re1Session.Fields.MainMenu) == 0;

    private static LeaderboardDefinition LoadBoard()
    {
        using var stream = typeof(Re1RunRules).Assembly.GetManifestResourceStream("Lockpick.GameAdapter.RE1.Data.speedrun-com.json")
            ?? throw new InvalidOperationException("Missing embedded RE1 data speedrun-com.json.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        string S(JsonElement e, string name) => e.GetProperty(name).GetString()!;
        return new LeaderboardDefinition(S(root, "game"), S(root, "category"), S(root, "platform"), S(root, "timeField"), S(root, "weblink"),
            [.. root.GetProperty("filters").EnumerateArray().Select(f => new LeaderboardFilter(S(f, "key"), S(f, "label"), S(f, "variable"),
                [.. f.GetProperty("values").EnumerateArray().Select(v => new LeaderboardChoice(S(v, "key"), S(v, "label"), S(v, "id")))]))]);
    }

    /// <summary>
    /// The install of the game being played against what the PC leaderboard allows, by its executable (both GOG
    /// releases can share one folder). GOG's English release (ResidentEvil.exe) and GOG's Japanese release
    /// (Biohazard.exe): every file against the ones the release ships (data/games/re1/install/gog-hotfix4.sha256 and
    /// gog-jpn-hotfix4.sha256, made by tools/re1/install_manifest.py from untouched installs); files that change in
    /// normal use are not compared (saves, the display settings, screenshots, GOG's log and uninstaller data), and the
    /// other release's files are not "extra". The Japanese game with Classic REbirth, as the rules recommend: the
    /// executable and REbirth by hash (data/games/re1/install/rebirth.json), the JPN data against GOG's Japanese files
    /// (a difference warns: a MediaKite disc's data may not match GOG's byte for byte), REbirth's banned options.
    /// </summary>
    public InstallCheck CheckInstall(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath) ?? ".";
        var install = Re1Install.Inspect(directory);
        if (install.Steam is { } steam && install.Rebirth is null)
        {
            return CheckSteam(directory, executablePath, steam);
        }

        var japanese = string.Equals(Path.GetFileName(executablePath), "Biohazard.exe", StringComparison.OrdinalIgnoreCase);
        var expected = japanese ? JapaneseManifest.Value : EnglishManifest.Value;
        var other = japanese ? EnglishManifest.Value : JapaneseManifest.Value;
        var rebirth = japanese ? install.Rebirth : null;

        var changed = new List<string>();
        var missing = new List<string>();
        foreach (var (relative, digest) in expected)
        {
            if (Varies(relative))
            {
                continue;
            }

            // With REbirth only the game data is compared: it replaces the executable and GOG's wrapper on purpose.
            if (rebirth is not null && !relative.StartsWith("JPN/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                missing.Add(relative);
                continue;
            }

            using var stream = File.OpenRead(path);
            if (!string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)), digest, StringComparison.OrdinalIgnoreCase))
            {
                changed.Add(relative);
            }
        }

        var extra = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(directory, p).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(r => !expected.ContainsKey(r) && !other.ContainsKey(r) && !Varies(r) && !(rebirth is not null && RebirthOwns(r)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!japanese)
        {
            List<InstallFinding> notes = install.Rebirth is null
                ? []
                : [new(InstallFindingLevel.Warn, "Classic REbirth's files are here, but REbirth runs only on the Japanese game (Biohazard.exe), not GOG's English one.")];
            return new InstallCheck("GOG 1.0 hotfix4", changed, missing, extra, notes);
        }

        if (rebirth is null)
        {
            return new InstallCheck("GOG's Japanese 1.0 hotfix4", changed, missing, extra);
        }

        // REbirth: data differences warn instead of failing (see above); the rest are findings.
        var findings = RebirthFindings(directory, rebirth);
        if (changed.Count + missing.Count > 0)
        {
            var differ = changed.Concat(missing.Select(m => m + " (missing)")).ToList();
            findings.Add(new InstallFinding(InstallFindingLevel.Warn,
                $"{differ.Count} JPN data file(s) differ from GOG's Japanese release: {string.Join(", ", differ.Take(5))}{(differ.Count > 5 ? ", …" : string.Empty)}. "
                + "A MediaKite copy's data can differ; otherwise check for a mod."));
        }
        else
        {
            findings.Add(new InstallFinding(InstallFindingLevel.Pass, "The JPN data files are GOG's Japanese release's, unchanged."));
        }

        // FilesCompared false: the findings say how the data compared (the executable and wrapper are REbirth's).
        return new InstallCheck($"the Japanese PC game with Classic REbirth{(rebirth.Version is { } v ? " " + v : string.Empty)}", [], [], extra, findings, FilesCompared: false);
    }

    /// <summary>
    /// Steam's release (data/games/re1/install/steam.json): a language folder holds GOG's data for that language and
    /// GOG's executable inside Steam's wrapper. The data is compared against GOG's manifest, the executable by Steam's
    /// hash; dxcfg.exe is GOG's settings tool, which Steam's launcher replaces.
    /// </summary>
    private static InstallCheck CheckSteam(string directory, string executablePath, SteamInstall steam)
    {
        var known = Re1Install.KnownSteam;
        var language = known.Executables.First(e => string.Equals(e.Folder, steam.Language, StringComparison.OrdinalIgnoreCase));
        var expected = language.Manifest == "gog-jpn-hotfix4.sha256" ? JapaneseManifest.Value : EnglishManifest.Value;
        bool Skipped(string relative) =>
            Varies(relative) || relative.Equals(language.File, StringComparison.OrdinalIgnoreCase) || relative.Equals("dxcfg.exe", StringComparison.OrdinalIgnoreCase);

        var changed = new List<string>();
        var missing = new List<string>();
        foreach (var (relative, digest) in expected.Where(e => !Skipped(e.Key)))
        {
            var path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                missing.Add(relative);
            }
            else if (!string.Equals(Re1Install.Hash(path), digest, StringComparison.OrdinalIgnoreCase))
            {
                changed.Add(relative);
            }
        }

        var extra = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(directory, p).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(r => !expected.ContainsKey(r) && !Skipped(r))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var file = Path.GetFileName(executablePath);
        var hash = Re1Install.Hash(executablePath);
        var release = known.Executables.FirstOrDefault(e => string.Equals(e.Sha256, hash, StringComparison.OrdinalIgnoreCase));
        List<InstallFinding> findings =
        [
            release is not null
                ? new(InstallFindingLevel.Pass, $"{file} is the one {release.Release} ships, unchanged.")
                : new(InstallFindingLevel.Fail,
                    $"{file} ({(hash is null ? "unreadable" : hash[..12] + "…")}) isn't Steam's: in Steam, check the game's files (Properties → Installed Files → Verify integrity)."),
        ];
        return new InstallCheck(language.Release, changed, missing, extra, findings);
    }

    private static List<InstallFinding> RebirthFindings(string directory, RebirthInstall rebirth)
    {
        var findings = new List<InstallFinding>();
        var known = Re1Install.Known;
        var exeHash = Re1Install.Hash(Path.Combine(directory, "Biohazard.exe"));
        findings.Add(exeHash is not null && known.Executables.TryGetValue(exeHash, out var exeRelease)
            ? new InstallFinding(InstallFindingLevel.Pass, $"Biohazard.exe is {exeRelease}.")
            : new InstallFinding(InstallFindingLevel.Warn,
                $"Biohazard.exe ({(exeHash is null ? "unreadable" : exeHash[..12] + "…")}) isn't one Lockpick knows, so it can't confirm it's an official release (REbirth installs the Japanese 1.01 executable)."));
        findings.Add(rebirth.Version is { } version
            ? new InstallFinding(InstallFindingLevel.Pass, $"Classic REbirth {version} (allowed by the rules).")
            : new InstallFinding(InstallFindingLevel.Warn, "ddraw.dll is a Classic REbirth build Lockpick doesn't know (a newer one?): check its version."));
        if (rebirth.Settings.Count == 0)
        {
            findings.Add(new InstallFinding(InstallFindingLevel.Warn,
                "REbirth hasn't saved its settings yet (config.ini): start the game once, then check again (quickturn, tactical reload and the debug menu must be off)."));
        }
        else
        {
            foreach (var (key, name, where) in BannedRebirthOptions)
            {
                if (rebirth.On(key))
                {
                    findings.Add(new InstallFinding(InstallFindingLevel.Fail, $"REbirth's {name} is on: the rules ban it (turn it off {where})."));
                }
            }

            if (!BannedRebirthOptions.Any(o => rebirth.On(o.Key)))
            {
                findings.Add(new InstallFinding(InstallFindingLevel.Pass, "REbirth's quickturn, tactical reload and debug menu are off."));
            }

            if (rebirth.On("CutSkip"))
            {
                findings.Add(new InstallFinding(InstallFindingLevel.Warn, "REbirth's cutscene skip is on: check the leaderboard allows it before submitting."));
            }
        }

        if (rebirth.ModFolders.Count > 0)
        {
            findings.Add(new InstallFinding(InstallFindingLevel.Warn,
                $"REbirth mod folders are here ({string.Join(", ", rebirth.ModFolders.Take(5))}): the rules allow no mods, so make sure none is enabled."));
        }

        return findings;
    }

    /// <summary>Files Classic REbirth installs or writes beside the game (its mods are reported on their own).</summary>
    private static bool RebirthOwns(string relative)
    {
        var name = Path.GetFileName(relative).ToLowerInvariant();
        return relative.StartsWith("savedata/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("mod_", StringComparison.OrdinalIgnoreCase)
            || (!relative.Contains('/', StringComparison.Ordinal)
                && name is "biohazard.exe" or "ddraw.dll" or "sdl3.dll" or "gamecontrollerdb.txt" or "license.txt" or "config.ini" or "steam_appid.txt" or "xmessagebox.ini");
    }

    /// <summary>The REbirth options the PC rules ban (config.ini [DLL]), with where to turn them off.</summary>
    private static readonly (string Key, string Name, string Where)[] BannedRebirthOptions =
    [
        ("QuickTurn", "quickturn", "in REbirth's setup: Enable Quickturn"),
        ("TactReload", "tactical reload", "in REbirth's setup: Enable Tactical Reload"),
        ("EnableDebug", "debug mode", "in REbirth's setup, or config.ini EnableDebug = 0"),
    ];

    /// <summary>The PC leaderboard's rules that only the player can keep (speedrun.com, RE1 PC rules, read 2026-10-16).</summary>
    public IReadOnlyList<string> LeaderboardReminders { get; } =
    [
        "Start the run at the title screen.",
        "Show the in-game time on screen the whole run (the rules name LiveSplit's IGT script), and the final in-game time on the end screen in your video.",
        "Don't tab out or click off the game during the run.",
        "Don't open the options menu as a pause (it pauses the in-game time).",
        "No music over the game, no mods, no turbo or macros, no auto-aim cancel.",
        "With Classic REbirth: quickturn and tactical reload off, and don't open its debug menu.",
    ];

    private static readonly Lazy<Dictionary<string, string>> EnglishManifest = new(() => LoadManifest("gog-hotfix4.sha256"));

    private static readonly Lazy<Dictionary<string, string>> JapaneseManifest = new(() => LoadManifest("gog-jpn-hotfix4.sha256"));

    private static Dictionary<string, string> LoadManifest(string name)
    {
        using var stream = typeof(Re1RunRules).Assembly.GetManifestResourceStream("Lockpick.GameAdapter.RE1.Data." + name)
            ?? throw new InvalidOperationException($"Missing embedded RE1 data {name}.");
        using var reader = new StreamReader(stream);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 66 && line[64] == ' ')
            {
                files[line[66..]] = line[..64];
            }
        }

        return files;
    }

    /// <summary>
    /// Files that change in normal use (as tools/re1/install_manifest.py leaves them out), and GOG's own bookkeeping,
    /// which the other language's installer rewrites when both share a folder (install metadata, the shortcut, the
    /// readme and EULA, the uninstaller): none of them changes the game.
    /// </summary>
    private static bool Varies(string relative)
    {
        var name = Path.GetFileName(relative).ToLowerInvariant();
        return relative.StartsWith("SAVE/", StringComparison.OrdinalIgnoreCase)
            || name is "dxcfg.ini" or "dxcfg.ini.bak" or "goglog.ini" or "unins000.dat" or "unins000.ini" or "unins000.msg" or "webcache.zip"
            || name.EndsWith(".bmp", StringComparison.Ordinal)
            || (!relative.Contains('/', StringComparison.Ordinal)
                && (name.StartsWith("goggame-", StringComparison.Ordinal)
                    || name is "launch resident evil.lnk" or "readme.txt" or "eula.txt" or "unins000.exe" or "gog.ico" or "support.ico"));
    }

    private static long? Raw(GameSnapshot snapshot, string field) =>
        snapshot.RawFields.TryGetValue(field, out var value) && value.TryGet(out var v) ? v : null;

    internal static IReadOnlyList<SplitSet> LoadEmbedded()
    {
        using var stream = typeof(Re1RunRules).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"Missing embedded RE1 data {Resource}.");
        using var document = JsonDocument.Parse(stream);
        return [.. document.RootElement.GetProperty("sets").EnumerateArray().Select(Set)];
    }

    private static SplitSet Set(JsonElement set)
    {
        var character = set.GetProperty("character").GetString() switch
        {
            "jill" => PlayableCharacter.Jill,
            "chris" => PlayableCharacter.Chris,
            "rebecca" => PlayableCharacter.Rebecca,
            _ => PlayableCharacter.Unknown,
        };
        var splits = set.GetProperty("splits").EnumerateArray().Select(s => new SplitDefinition(s.GetProperty("name").GetString()!, Trigger(s))).ToList();
        return new SplitSet(set.GetProperty("id").GetString()!, set.GetProperty("name").GetString()!, set.GetProperty("category").GetString()!,
            character, splits, Verified: false, Source: "Built in (the community's splits, not yet checked in a full run)");
    }

    private static SplitTrigger Trigger(JsonElement split)
    {
        if (split.TryGetProperty("end", out var end) && end.GetBoolean())
        {
            return new RunFinished();
        }

        if (split.TryGetProperty("item", out var item))
        {
            return new ItemObtained(item.GetInt32(), split.TryGetProperty("in", out var room) ? room.GetString() : null);
        }

        if (split.TryGetProperty("room", out var to))
        {
            return new RoomEntered(to.GetString()!,
                split.TryGetProperty("from", out var from) ? from.GetString() : null,
                split.TryGetProperty("fromOtherStage", out var other) && other.GetBoolean());
        }

        return new ManualSplit();
    }
}
