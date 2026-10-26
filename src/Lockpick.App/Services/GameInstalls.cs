using Lockpick.GameAdapter.RE1;
using Lockpick.Platform.Windows;

namespace Lockpick.App.Services;

/// <summary>An install of the game found on this PC: the store, its folder and version, and the executable to run.</summary>
public sealed record DetectedInstall(string Store, string Folder, string? Version, string? Executable)
{
    /// <summary>"GOG: G:\GOG Games\Resident Evil (version 1.0 hotfix4)".</summary>
    public string Describe() =>
        $"{Store}: {Folder} ({(Store == "Steam" ? "build" : "version")} {Version ?? "unknown"})";
}

/// <summary>The game's installs: GOG's (from its registry record), then Steam's (from Steam's library folders).</summary>
public static class GameInstalls
{
    public static IReadOnlyList<DetectedInstall> Find()
    {
        var found = new List<DetectedInstall>();
        if (GogInstallLocator.Find(Re1Adapter.GogProductId) is { } gog)
        {
            found.Add(new DetectedInstall("GOG", gog.InstallPath, gog.Version, gog.ExecutablePath));
        }

        if (SteamInstallLocator.Find(Re1Adapter.SteamAppId) is { } steam)
        {
            // Steam keeps one folder per language; the launcher's language setting picks the executable.
            found.Add(new DetectedInstall("Steam", steam.InstallPath, steam.Version, Re1Install.SteamExecutable(steam.InstallPath)));
        }

        return found;
    }

    /// <summary>The first install's executable, or null.</summary>
    public static string? Executable() => Find().Select(i => i.Executable).FirstOrDefault(e => e is not null);

    /// <summary>One line per install found, or <paramref name="none"/>.</summary>
    public static string Describe(string none) =>
        Find() is { Count: > 0 } installs ? string.Join(Environment.NewLine, installs.Select(i => i.Describe())) : none;
}
