using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace Lockpick.GameAdapter.RE1;

/// <summary>Classic REbirth in an install: its version (from the DLL's hash) and its settings (<c>config.ini</c>).</summary>
/// <param name="Version">"1.1.4" etc., or null for a REbirth DLL Lockpick doesn't know.</param>
/// <param name="Settings">config.ini as section → key → value (empty until REbirth has run once).</param>
/// <param name="ModFolders">REbirth mod folders (<c>mod_*</c>) beside the game.</param>
public sealed record RebirthInstall(string? Version, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Settings, IReadOnlyList<string> ModFolders)
{
    /// <summary>A [DLL] option that is on (1).</summary>
    public bool On(string key) =>
        Settings.TryGetValue("DLL", out var dll) && dll.TryGetValue(key, out var value) && value.Trim() == "1";
}

/// <summary>Steam's release: the install (<paramref name="Root"/>), the language folder the game runs from, and the
/// launcher's settings file, which it copies into every language folder's dxcfg.ini at each start.</summary>
public sealed record SteamInstall(string Root, string Language, string LauncherSettings);

/// <summary>
/// What an RE1 install folder holds, read-only: Classic REbirth (which replaces GOG's DirectDraw wrapper and keeps its
/// own settings and saves), Steam's layout, where saves and room files are. GOG's English release: SAVE\ and USA\,
/// settings in dxcfg.ini. Steam's: the same in one folder per language (english\, japanese\), the settings in the
/// launcher's ini one folder up. The Japanese 1.01 game with REbirth: savedata\ (REbirth's Save_folder) and JPN\,
/// settings in config.ini.
/// </summary>
public sealed record Re1Install(string Directory, RebirthInstall? Rebirth, SteamInstall? Steam = null)
{
    private static readonly ConcurrentDictionary<(string Path, long Length, DateTime Written), string> Hashes = new();

    /// <summary>The published Classic REbirth releases and the executable it runs on (data/games/re1/install/rebirth.json).</summary>
    internal static RebirthData Known => KnownData.Value;

    /// <summary>Steam's release: its app id and executables (data/games/re1/install/steam.json).</summary>
    internal static SteamData KnownSteam => KnownSteamData.Value;

    public static Re1Install Inspect(string directory)
    {
        var dll = Path.Combine(directory, "ddraw.dll");
        var config = Path.Combine(directory, "config.ini");
        var version = Hash(dll) is { } digest ? Known.RebirthVersion(digest) : null;
        var settings = ReadIni(config);
        var isRebirth = version is not null || settings.ContainsKey("DLL");
        var steam = SteamAt(directory);
        if (!isRebirth)
        {
            return new Re1Install(directory, null, steam);
        }

        IReadOnlyList<string> mods;
        try
        {
            mods = [.. System.IO.Directory.EnumerateDirectories(directory, "mod_*").Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            mods = [];
        }

        return new Re1Install(directory, new RebirthInstall(version, settings, mods), steam);
    }

    /// <summary>
    /// The executable Steam's launcher starts (its language setting) in a Steam install's root folder, else the first
    /// language present; null when <paramref name="root"/> isn't one.
    /// </summary>
    public static string? SteamExecutable(string root)
    {
        var known = KnownSteam;
        var language = ReadIni(Path.Combine(root, known.LauncherSettingsFile)).TryGetValue("Language", out var section)
            && section.TryGetValue("LanguageSetting", out var chosen) ? chosen : null;
        return known.Executables
            .OrderBy(e => string.Equals(e.Folder, language, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Select(e => Path.Combine(root, e.Folder, e.File))
            .FirstOrDefault(File.Exists);
    }

    private static SteamInstall? SteamAt(string directory)
    {
        var known = KnownSteam;
        var language = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory)) is not { } root
            || !known.Executables.Any(e => string.Equals(e.Folder, language, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var launcher = Path.Combine(root, known.LauncherSettingsFile);
        return File.Exists(launcher) ? new SteamInstall(root, language.ToLowerInvariant(), launcher) : null;
    }

    /// <summary>Where the game saves: REbirth's Save_folder (savedata by default), otherwise GOG's SAVE.</summary>
    public string SaveDirectory =>
        Rebirth is { } rebirth
            ? Path.Combine(Directory, rebirth.Settings.TryGetValue("RE1", out var re1) && re1.TryGetValue("Save_folder", out var folder) && folder.Trim().Length > 0
                ? folder.Trim()
                : "savedata")
            : Path.Combine(Directory, "SAVE");

    /// <summary>The room files' folder: USA (English data) or JPN (Japanese data), whichever is there.</summary>
    public string DataDirectory =>
        DataFolders.Select(d => Path.Combine(Directory, d)).FirstOrDefault(System.IO.Directory.Exists) ?? Directory;

    /// <summary>The room files of the game run as <paramref name="executableName"/>: both releases can share a folder.</summary>
    public string DataDirectoryFor(string executableName)
    {
        var own = Path.Combine(Directory, string.Equals(executableName, "Biohazard.exe", StringComparison.OrdinalIgnoreCase) ? "JPN" : "USA");
        return System.IO.Directory.Exists(own) ? own : DataDirectory;
    }

    private static readonly string[] DataFolders = ["USA", "JPN"];

    /// <summary>
    /// GOG's DirectDraw wrapper settings, which apply only when REbirth hasn't replaced the wrapper: dxcfg.ini, or on
    /// Steam the launcher's ini (it overwrites dxcfg.ini from there at every start).
    /// </summary>
    public string? DisplaySettings => Rebirth is not null ? null : Steam?.LauncherSettings ?? Path.Combine(Directory, "dxcfg.ini");

    /// <summary>SHA-256 of a file (cached by size and time written), or null when it can't be read.</summary>
    internal static string? Hash(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            return Hashes.GetOrAdd((info.FullName, info.Length, info.LastWriteTimeUtc), key =>
            {
                using var stream = File.OpenRead(key.Path);
                return Convert.ToHexString(SHA256.HashData(stream));
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Dictionary<string, IReadOnlyDictionary<string, string>> ReadIni(string path)
    {
        var sections = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        string[] lines;
        try
        {
            if (!File.Exists(path))
            {
                return sections;
            }

            lines = File.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return sections;
        }

        Dictionary<string, string>? current = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length > 2 && line[0] == '[' && line[^1] == ']')
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sections[line[1..^1].Trim()] = current;
            }
            else if (current is not null && line.IndexOf('=', StringComparison.Ordinal) is > 0 and var eq && !line.StartsWith(';'))
            {
                current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }

        return sections;
    }

    private static readonly Lazy<RebirthData> KnownData = new(() =>
    {
        using var stream = typeof(Re1Install).Assembly.GetManifestResourceStream("Lockpick.GameAdapter.RE1.Data.rebirth.json")
            ?? throw new InvalidOperationException("Missing embedded RE1 data rebirth.json.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        static Dictionary<string, string> Map(JsonElement list, string value) =>
            list.EnumerateArray().ToDictionary(e => e.GetProperty("sha256").GetString()!, e => e.GetProperty(value).GetString()!, StringComparer.OrdinalIgnoreCase);
        return new RebirthData(Map(root.GetProperty("executables"), "release"), Map(root.GetProperty("rebirth"), "version"),
            Map(root.GetProperty("rebirthFiles"), "file"));
    });

    private static readonly Lazy<SteamData> KnownSteamData = new(() =>
    {
        using var stream = typeof(Re1Install).Assembly.GetManifestResourceStream("Lockpick.GameAdapter.RE1.Data.steam.json")
            ?? throw new InvalidOperationException("Missing embedded RE1 data steam.json.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        return new SteamData(root.GetProperty("appId").GetString()!, [.. root.GetProperty("executables").EnumerateArray().Select(e =>
            new SteamExecutable(e.GetProperty("folder").GetString()!, e.GetProperty("file").GetString()!, e.GetProperty("sha256").GetString()!,
                e.GetProperty("release").GetString()!, e.GetProperty("manifest").GetString()!))]);
    });
}

/// <summary>Steam's release in steam.json: its app id and each language's executable.</summary>
internal sealed record SteamData(string AppId, IReadOnlyList<SteamExecutable> Executables)
{
    /// <summary>The launcher's settings (language, DirectDraw wrapper) beside the language folders.</summary>
    public string LauncherSettingsFile => AppId + "_Launcher.ini";
}

/// <summary>A language of Steam's release: its folder and executable, and the GOG manifest its data matches.</summary>
internal sealed record SteamExecutable(string Folder, string File, string Sha256, string Release, string Manifest);

/// <summary>The hashes in rebirth.json: official executables (→ release), REbirth DLLs (→ version), REbirth's other files.</summary>
internal sealed record RebirthData(
    IReadOnlyDictionary<string, string> Executables,
    IReadOnlyDictionary<string, string> Dlls,
    IReadOnlyDictionary<string, string> Files)
{
    public string? RebirthVersion(string sha256) => Dlls.TryGetValue(sha256, out var version) ? version : null;
}
