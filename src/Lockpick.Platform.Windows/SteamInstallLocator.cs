using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Lockpick.Platform.Windows;

/// <summary>
/// Finds a Steam game's install folder (read-only): Steam's path from the registry, its library folders
/// (<c>steamapps\libraryfolders.vdf</c>), and the app's manifest in one of them (<c>appmanifest_&lt;id&gt;.acf</c>).
/// The result's version is Steam's build id; the executable is the adapter's to pick.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class SteamInstallLocator
{
    public static InstalledGame? Find(string appId)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is not string steam)
            {
                return null;
            }

            return FindIn(Path.GetFullPath(steam), appId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The app in the libraries of the Steam folder <paramref name="steam"/>.</summary>
    public static InstalledGame? FindIn(string steam, string appId)
    {
        var folders = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        IEnumerable<string> libraries = File.Exists(folders)
            ? [steam, .. Values(File.ReadAllText(folders), "path")]
            : [steam];
        foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var manifest = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(manifest))
            {
                continue;
            }

            var text = File.ReadAllText(manifest);
            if (Values(text, "installdir").FirstOrDefault() is not { Length: > 0 } folder)
            {
                continue;
            }

            var path = Path.Combine(library, "steamapps", "common", folder);
            if (Directory.Exists(path))
            {
                return new InstalledGame(appId, Values(text, "name").FirstOrDefault() ?? appId, path, null, Values(text, "buildid").FirstOrDefault());
            }
        }

        return null;
    }

    // Steam's KeyValues text: "key" "value" pairs, backslashes escaped.
    private static IEnumerable<string> Values(string text, string key) =>
        Pair().Matches(text).Where(m => m.Groups[1].Value.Equals(key, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Groups[2].Value.Replace(@"\\", @"\", StringComparison.Ordinal));

    [GeneratedRegex("\"([^\"]+)\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex Pair();
}
