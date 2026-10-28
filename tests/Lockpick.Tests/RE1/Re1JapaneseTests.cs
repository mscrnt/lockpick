using Lockpick.Core.Adapters;
using Lockpick.Core.Processes;
using Lockpick.Core.Profiles;
using Lockpick.Core.Runs;
using Lockpick.Core.State;
using Lockpick.GameAdapter.RE1;
using Lockpick.Tests.Fakes;

namespace Lockpick.Tests.RE1;

/// <summary>The Japanese PC 1.01 build (what Classic REbirth runs on) and REbirth installs.</summary>
public sealed class Re1JapaneseTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);
    private readonly Re1Adapter _adapter = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "lockpick-jp-" + Guid.NewGuid().ToString("N"));

    public Re1JapaneseTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private VersionProfile Japanese => _adapter.Profiles.Single(p => p.Id == "re1-jpn-101");

    private VersionProfile Gog => _adapter.Profiles.Single(p => p.Id == "re1-gog-usa-hotfix4");

    [Fact]
    public void The_japanese_profile_is_the_official_1_01_executable_checked_in_play_under_rebirth()
    {
        var jp = Japanese;
        Assert.Equal("Biohazard.exe", jp.Executable.FileName);
        Assert.Equal("25BE34D4F4586E9EC84E39412915AE1DE38AF1FC5924C02824D38E15C8406458", jp.Executable.Sha256);
        Assert.Equal(0x990000u, jp.Executable.SizeOfImage); // the RE1 autosplitter's "JapaneseREbirth" module size
        Assert.True(jp.IsLiveVerified); // Classic REbirth 1.1.4, 2026-10-17
        Assert.Equal(Verification.Runtime, jp.FindField("gameTimer")!.Verification);
        Assert.Equal(Verification.Static, jp.FindField("inv7")!.Verification); // not shown by a trace yet: no writes
        Assert.False(jp.FindField("inv7Qty")!.IsWritable);
        Assert.True(jp.FindField("inv5Qty")!.IsWritable); // the Seamless HD pack's trace, 2026-10-28
        // REbirth replaces the frame loop's timer tick in memory, so the timer's anchor is the New Game reset.
        Assert.Contains(jp.Anchors, a => a.Name == "New Game zeroes the game timer" && a.Address == 0x48CBAA);
        Assert.DoesNotContain(jp.Anchors, a => a.Address == 0x48D36E);
        Assert.Contains(_adapter.ExecutableNames, n => n == "Biohazard.exe");
    }

    [Fact]
    public void Everything_the_gog_profile_reads_was_carried_over()
    {
        var jp = Japanese;
        Assert.Equal(Gog.Fields.Select(f => f.Name), jp.Fields.Select(f => f.Name));
        Assert.Equal(Gog.Addresses.Select(a => a.Name), jp.Addresses.Select(a => a.Name));
        Assert.Equal(Gog.Anchors.Count, jp.Anchors.Count);
        Assert.Equal(Gog.CaptureRegions.Select(r => r.Name), jp.CaptureRegions.Select(r => r.Name));
        // The leads the autosplitter gives for this build agree (module-relative there, 0x400000 + offset here).
        Assert.Equal(0x400000ul + 0x6A8E10, jp.FindField("gameTimer")!.Address);
        Assert.Equal(0x400000ul + 0x8386F0, jp.FindField("stage")!.Address);
        Assert.Equal(0x400000ul + 0x83523C, jp.FindField("hp")!.Address);
        Assert.Equal(0x400000ul + 0x838814, jp.TrustedAddress("inventory"));
    }

    [Fact]
    public void Gogs_japanese_exe_has_the_1_01_layout_and_what_its_live_trace_showed_is_runtime()
    {
        var gogJp = _adapter.Profiles.Single(p => p.Id == "re1-gog-jpn-hotfix4");
        Assert.Equal(0x4991000u, gogJp.Executable.SizeOfImage); // the autosplitter's "JapaneseGOG"
        Assert.Equal(Japanese.Fields.Select(f => (f.Name, f.Address)), gogJp.Fields.Select(f => (f.Name, f.Address)));
        Assert.True(gogJp.IsLiveVerified);
        Assert.Equal(Verification.Runtime, gogJp.FindField("hp")!.Verification);
        Assert.True(gogJp.FindField("hp")!.IsWritable);
        Assert.Equal(Verification.Static, gogJp.FindField("stateFlags")!.Verification); // not shown by the trace
        Assert.Equal("re1-gog-usa-hotfix4", _adapter.Profiles[0].Id); // live-verified English first, the default
    }

    [Fact]
    public void The_build_is_identified_by_its_hash()
    {
        var match = _adapter.Identify(FakeFingerprintProvider.For(Japanese, @"G:\Games\Biohazard\Biohazard.exe"));
        Assert.True(match.IsSupported);
        Assert.Equal("re1-jpn-101", match.Profile!.Id);
    }

    [Fact]
    public void A_session_reads_the_japanese_addresses()
    {
        var jp = Japanese;
        var memory = new FakeMemory().WithVerifiedImage(jp);
        var session = _adapter.Attach(new FakeAttachedProcess(1, memory, new ModuleImage(0x400000, jp.Executable.SizeOfImage, null)), jp);
        ulong At(string field) => jp.FindField(field)!.Address;
        memory.WriteU8(At("stage"), 0).WriteU8(At("room"), 0x06).WriteU8(At("camera"), 1).WriteI16(At("hp"), 96).WriteU8(At("maxHp"), 96)
            .WriteU8(At("character"), 1).WriteU32(At("gameTimer"), 300).WriteU32(At("stateFlags"), 0x8013000C);
        var inventory = jp.TrustedAddress("inventory")!.Value;
        memory.Write(jp.TrustedAddress("inventoryPointer")!.Value, BitConverter.GetBytes((uint)inventory))
            .WriteU8(jp.TrustedAddress("heldCount")!.Value, 8)
            .Write(inventory, [0x01, 0x00, 0x02, 0x0F]);

        var s = session.Read(1, T0);

        Assert.Equal(new RoomLocation(1, 0x06), s.Location.Value);
        Assert.Equal(new Health(96, 96), s.Health.Value);
        Assert.Equal(PlayableCharacter.Jill, s.Character.Value);
        Assert.Equal(TimeSpan.FromSeconds(10), s.GameTime.Value);
        Assert.Equal([new HeldItem(0, 1, 0), new HeldItem(1, 2, 15)], s.Inventory.Value);
    }

    [Fact]
    public void Rebirth_installs_save_to_its_save_folder_and_have_no_gog_display_settings()
    {
        File.WriteAllText(Path.Combine(_folder, "Biohazard.exe"), "exe");
        Directory.CreateDirectory(Path.Combine(_folder, "JPN"));
        File.WriteAllText(Path.Combine(_folder, "config.ini"), "[RE1]\nPlay_Number = 0\n[DLL]\nQuickTurn = 0\n");

        Assert.Equal(Path.Combine(_folder, "savedata"), _adapter.SaveDirectory(Path.Combine(_folder, "Biohazard.exe")));
        Assert.Null(_adapter.DisplayConfig(_folder, _folder));
        Assert.Equal(Path.Combine(_folder, "JPN"), Re1Install.Inspect(_folder).DataDirectory);

        File.WriteAllText(Path.Combine(_folder, "config.ini"), "[RE1]\nSave_folder = my saves\n[DLL]\n");
        Assert.Equal(Path.Combine(_folder, "my saves"), _adapter.SaveDirectory(Path.Combine(_folder, "Biohazard.exe")));
    }

    [Fact]
    public void A_shared_folder_gives_each_game_its_own_room_files()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "USA"));
        Directory.CreateDirectory(Path.Combine(_folder, "JPN"));
        var install = Re1Install.Inspect(_folder);

        Assert.Equal(Path.Combine(_folder, "JPN"), install.DataDirectoryFor("Biohazard.exe"));
        Assert.Equal(Path.Combine(_folder, "USA"), install.DataDirectoryFor("ResidentEvil.exe"));
    }

    [Fact]
    public void A_gog_install_keeps_its_save_folder_and_display_settings()
    {
        File.WriteAllText(Path.Combine(_folder, "ResidentEvil.exe"), "exe");
        File.WriteAllText(Path.Combine(_folder, "dxcfg.ini"), "[dxcfg]");

        Assert.Equal(Path.Combine(_folder, "SAVE"), _adapter.SaveDirectory(Path.Combine(_folder, "ResidentEvil.exe")));
        Assert.NotNull(_adapter.DisplayConfig(_folder, _folder));
        Assert.Null(Re1Install.Inspect(_folder).Rebirth);
    }

    [Fact]
    public void The_leaderboard_check_fails_rebirths_banned_options_and_notes_what_it_cant_confirm()
    {
        var rules = (Re1RunRules)_adapter.CreateRunRules(Japanese);
        File.WriteAllText(Path.Combine(_folder, "Biohazard.exe"), "not the real one");
        File.WriteAllText(Path.Combine(_folder, "config.ini"), "[RE1]\n[DLL]\nQuickTurn = 1\nTactReload = 0\nEnableDebug = 0\nCutSkip = 1\n");
        Directory.CreateDirectory(Path.Combine(_folder, "mod_hud"));

        var check = rules.CheckInstall(Path.Combine(_folder, "Biohazard.exe"));

        Assert.False(check.Unchanged);
        Assert.Contains(check.Notes, n => n.Level == InstallFindingLevel.Warn && n.Text.Contains("JPN data file(s) differ", StringComparison.Ordinal));
        Assert.StartsWith("the Japanese PC game with Classic REbirth", check.Release, StringComparison.Ordinal);
        Assert.Contains(check.Notes, n => n.Level == InstallFindingLevel.Fail && n.Text.Contains("quickturn", StringComparison.Ordinal));
        Assert.DoesNotContain(check.Notes, n => n.Level == InstallFindingLevel.Fail && n.Text.Contains("tactical", StringComparison.Ordinal));
        Assert.Contains(check.Notes, n => n.Level == InstallFindingLevel.Warn && n.Text.Contains("isn't one Lockpick knows", StringComparison.Ordinal));
        Assert.Contains(check.Notes, n => n.Level == InstallFindingLevel.Warn && n.Text.Contains("mod_hud", StringComparison.Ordinal));
        Assert.Contains(check.Notes, n => n.Level == InstallFindingLevel.Warn && n.Text.Contains("cutscene skip", StringComparison.Ordinal));
    }

    [Fact]
    public void Rebirth_with_its_banned_options_off_passes_them()
    {
        var rules = (Re1RunRules)_adapter.CreateRunRules(Japanese);
        File.WriteAllText(Path.Combine(_folder, "Biohazard.exe"), "exe");
        File.WriteAllText(Path.Combine(_folder, "config.ini"), "[DLL]\nQuickTurn = 0\nTactReload = 0\nEnableDebug = 0\n");

        var check = rules.CheckInstall(Path.Combine(_folder, "Biohazard.exe"));

        Assert.DoesNotContain(check.Notes, n => n.Level == InstallFindingLevel.Fail);
        Assert.Contains(check.Notes, n => n.Level == InstallFindingLevel.Pass && n.Text.Contains("are off", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_published_rebirth_release_is_known_by_hash()
    {
        var known = Re1Install.Known;
        Assert.Equal(12, known.Dlls.Count);
        Assert.Equal("1.1.4", known.RebirthVersion("144764BF113D0F19767A9F2E35927B30CCFBB978790A427189583544B6C52889"));
        Assert.Equal("1.1.3", known.RebirthVersion("6c17ec863c17b2834cfd1f802c4dac5fdc4e146f516671559ad5b9e0f4948019"));
        Assert.Contains(Japanese.Executable.Sha256, known.Executables.Keys);
    }
}
