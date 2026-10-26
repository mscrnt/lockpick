using Lockpick.Core.Display;
using Lockpick.Core.Profiles;
using Lockpick.Core.Runs;
using Lockpick.GameAdapter.RE1;
using Lockpick.Platform.Windows;
using Lockpick.Tests.Fakes;

namespace Lockpick.Tests.RE1;

/// <summary>Steam's Resident Evil (1996), app 4249100: GOG's exe in a wrapper, one folder per language.</summary>
public sealed class Re1SteamTests : IDisposable
{
    private const string Launcher = "4249100_Launcher.ini";
    private readonly Re1Adapter _adapter = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lockpick-steam-" + Guid.NewGuid().ToString("N"));

    public Re1SteamTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "english"));
        Directory.CreateDirectory(Path.Combine(_root, "japanese"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private VersionProfile Steam => _adapter.Profiles.Single(p => p.Id == "re1-steam-usa");

    private VersionProfile Gog => _adapter.Profiles.Single(p => p.Id == "re1-gog-usa-hotfix4");

    private string English => Path.Combine(_root, "english");

    private void WriteLauncher(string language) =>
        File.WriteAllLines(Path.Combine(_root, Launcher), ["[Language]", $"LanguageSetting={language}", "[dxcfg]", "display=desktop", "presentation=application", "aspect=enabled", "scaling=fit"]);

    [Fact]
    public void The_steam_profile_is_steams_wrapped_exe_with_gogs_english_addresses()
    {
        var steam = Steam;
        Assert.Equal("ResidentEvil.exe", steam.Executable.FileName);
        Assert.Equal(3198880, steam.Executable.FileSize);
        Assert.Equal(Re1Install.KnownSteam.Executables.Single(e => e.Folder == "english").Sha256, steam.Executable.Sha256);
        Assert.Equal(0x53AD000u, steam.Executable.SizeOfImage); // the wrapper's sections on top of GOG's image
        Assert.Equal(Gog.Executable.TimeDateStamp, steam.Executable.TimeDateStamp);
        Assert.True(steam.IsLiveVerified); // two probe traces of a Jill game, 2026-10-26
        Assert.Equal(Verification.Runtime, steam.FindField("hp")!.Verification);
        Assert.True(steam.FindField("hp")!.IsWritable);
        Assert.Equal(Verification.Static, steam.FindField("mainMenu")!.Verification); // never at the title in the traces
        // The unpacked image is GOG's: every address is the same.
        Assert.Equal(Gog.Fields.Select(f => (f.Name, f.Address)), steam.Fields.Select(f => (f.Name, f.Address)));
        Assert.Equal(Gog.Addresses.Select(a => (a.Name, a.Address)), steam.Addresses.Select(a => (a.Name, a.Address)));
        Assert.Equal(Gog.Anchors.Select(a => (a.Address, Convert.ToHexString(a.Bytes))), steam.Anchors.Select(a => (a.Address, Convert.ToHexString(a.Bytes))));
        Assert.Equal("re1-gog-usa-hotfix4", _adapter.Profiles[0].Id);
    }

    [Fact]
    public void The_japanese_steam_profile_is_steams_wrapped_biohazard_with_gogs_japanese_addresses()
    {
        var steam = _adapter.Profiles.Single(p => p.Id == "re1-steam-jpn");
        var gog = _adapter.Profiles.Single(p => p.Id == "re1-gog-jpn-hotfix4");
        Assert.Equal("Biohazard.exe", steam.Executable.FileName);
        Assert.Equal(Re1Install.KnownSteam.Executables.Single(e => e.Folder == "japanese").Sha256, steam.Executable.Sha256);
        Assert.Equal(0x5385000u, steam.Executable.SizeOfImage);
        Assert.True(steam.IsLiveVerified); // a probe trace of a Jill game, 2026-10-26
        Assert.Equal(Verification.Runtime, steam.FindField("hp")!.Verification);
        Assert.Equal(gog.Executable.TimeDateStamp, steam.Executable.TimeDateStamp);
        Assert.Equal(gog.Fields.Select(f => (f.Name, f.Address)), steam.Fields.Select(f => (f.Name, f.Address)));
        Assert.Equal(gog.Anchors.Select(a => (a.Address, Convert.ToHexString(a.Bytes))), steam.Anchors.Select(a => (a.Address, Convert.ToHexString(a.Bytes))));
    }

    [Theory]
    [InlineData("re1-steam-usa", "english", "ResidentEvil.exe")]
    [InlineData("re1-steam-jpn", "japanese", "Biohazard.exe")]
    public void The_steam_builds_are_identified_by_their_hash(string id, string folder, string file)
    {
        var profile = _adapter.Profiles.Single(p => p.Id == id);
        var match = _adapter.Identify(FakeFingerprintProvider.For(profile, Path.Combine(_root, folder, file)));
        Assert.True(match.IsSupported);
        Assert.Equal(id, match.Profile!.Id);
    }

    [Fact]
    public void A_language_folder_beside_the_launcher_is_steams_and_its_display_settings_are_the_launchers()
    {
        WriteLauncher("english");
        File.WriteAllText(Path.Combine(English, "dxcfg.ini"), "[dxcfg]"); // rewritten by the launcher at every start

        var install = Re1Install.Inspect(English);
        Assert.Equal(new SteamInstall(_root, "english", Path.Combine(_root, Launcher)), install.Steam);
        Assert.Equal(Path.Combine(_root, Launcher), install.DisplaySettings);
        Assert.Equal(Path.Combine(English, "SAVE"), _adapter.SaveDirectory(Path.Combine(English, "ResidentEvil.exe")));
        Assert.Null(Re1Install.Inspect(_root).Steam); // the root itself isn't a game folder
    }

    [Fact]
    public void A_language_folder_without_the_launcher_is_not_steams()
    {
        Assert.Null(Re1Install.Inspect(English).Steam);
        Assert.Equal(Path.Combine(English, "dxcfg.ini"), Re1Install.Inspect(English).DisplaySettings);
    }

    [Fact]
    public void Changing_the_display_from_lockpick_edits_the_launcher_and_keeps_its_language()
    {
        WriteLauncher("english");
        var backups = Path.Combine(_root, "backups");
        var config = _adapter.DisplayConfig(English, backups)!;

        config.Write(new DisplayConfiguration(DisplayPresentation.Windowed, "1920x1080@60", PictureScaling.Fit));

        var lines = File.ReadAllLines(Path.Combine(_root, Launcher));
        Assert.Contains("LanguageSetting=english", lines);
        Assert.Contains("presentation=windowed", lines);
        Assert.Contains("display=1920x1080@60", lines);
        Assert.True(File.Exists(Path.Combine(backups, Launcher))); // its own backup, not GOG's dxcfg.ini
        Assert.False(File.Exists(Path.Combine(backups, "dxcfg.ini")));
    }

    [Fact]
    public void The_executable_to_run_is_the_launchers_language()
    {
        File.WriteAllText(Path.Combine(English, "ResidentEvil.exe"), "exe");
        File.WriteAllText(Path.Combine(_root, "japanese", "Biohazard.exe"), "exe");

        WriteLauncher("japanese");
        Assert.Equal(Path.Combine(_root, "japanese", "Biohazard.exe"), Re1Install.SteamExecutable(_root));
        WriteLauncher("french"); // a language Lockpick has no profile for: the first one it knows
        Assert.Equal(Path.Combine(English, "ResidentEvil.exe"), Re1Install.SteamExecutable(_root));
    }

    [Fact]
    public void The_leaderboard_check_compares_gogs_data_and_fails_an_exe_that_isnt_steams()
    {
        WriteLauncher("english");
        var exe = Path.Combine(English, "ResidentEvil.exe");
        File.WriteAllText(exe, "not the game");
        File.WriteAllText(Path.Combine(English, "dxcfg.ini"), "[dxcfg]");

        var check = new Re1RunRules().CheckInstall(exe);

        Assert.Equal("Steam's English release (app 4249100)", check.Release);
        Assert.Contains(check.Notes, n => n.Level == InstallFindingLevel.Fail && n.Text.Contains("isn't Steam's", StringComparison.Ordinal));
        Assert.DoesNotContain("ResidentEvil.exe", check.Changed); // judged by Steam's hash, not GOG's
        Assert.DoesNotContain("dxcfg.exe", check.Missing); // Steam's launcher replaces GOG's settings tool
        Assert.Contains("USA/STAGE1/ROOM1060.RDT", check.Missing, StringComparer.OrdinalIgnoreCase); // GOG's data is expected
        Assert.Empty(check.Extra);
    }

    [Fact]
    public void Steams_install_is_found_from_its_library_folders()
    {
        var steam = Path.Combine(_root, "Steam");
        var library = Path.Combine(_root, "Library");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        Directory.CreateDirectory(Path.Combine(library, "steamapps", "common", "4249100_Biohazard"));
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
            $"\"libraryfolders\"\n{{\n\t\"0\"\n\t{{\n\t\t\"path\"\t\t\"{steam.Replace(@"\", @"\\", StringComparison.Ordinal)}\"\n\t}}\n\t\"1\"\n\t{{\n\t\t\"path\"\t\t\"{library.Replace(@"\", @"\\", StringComparison.Ordinal)}\"\n\t}}\n}}\n");
        File.WriteAllText(Path.Combine(library, "steamapps", "appmanifest_4249100.acf"),
            "\"AppState\"\n{\n\t\"appid\"\t\t\"4249100\"\n\t\"name\"\t\t\"Resident Evil (1996)\"\n\t\"buildid\"\t\t\"21744136\"\n\t\"installdir\"\t\t\"4249100_Biohazard\"\n}\n");

        var found = SteamInstallLocator.FindIn(steam, Re1Adapter.SteamAppId);

        Assert.NotNull(found);
        Assert.Equal(Path.Combine(library, "steamapps", "common", "4249100_Biohazard"), found.InstallPath);
        Assert.Equal("Resident Evil (1996)", found.Name);
        Assert.Equal("21744136", found.Version);
        Assert.Null(SteamInstallLocator.FindIn(steam, "4249110"));
    }
}
