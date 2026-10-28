using Lockpick.Core.Profiles;
using Lockpick.Core.State;
using Lockpick.Core.Trainer;
using Lockpick.GameAdapter.RE1;
using Lockpick.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lockpick.Tests.RE1;

/// <summary>
/// Keep-running (#21) on GOG English: while the game isn't the active window its focus flag is held at 1 (it runs on)
/// and its keyboard map is blank (keys typed elsewhere don't reach it); the player's map comes back when the game is in
/// front again and whenever the rule ends.
/// </summary>
public sealed class Re1KeepRunningTests : IDisposable
{
    private const ulong Flag = 0x4BCB2C;
    private const ulong Map = 0xAC4030;

    // The owner's remapped keyboard map as read live 2026-10-26.
    private static readonly byte[] PlayerMap = Convert.FromHexString("412625270056000000005843005A0000000000000000000000000028110D201B");

    private readonly Re1Adapter _adapter = new();
    private readonly FakeStateSource _source = new();
    private readonly FakeWriteHost _host = new();
    private readonly TrainerService _trainer;
    private bool _inFront = true;
    private bool _inGame = true;

    public Re1KeepRunningTests()
    {
        _host.Memory.WriteU32(Flag, 1).Write(Map, PlayerMap);
        _trainer = new TrainerService(_source, _host, new TrainerActionLog(null), NullLogger.Instance);
        _source.Attach(1234, Gog);
        Poll();
        _trainer.SetMode(TrainerMode.Trainer);
    }

    public void Dispose()
    {
        _trainer.Dispose();
        _source.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private VersionProfile Gog => _adapter.Profiles.Single(p => p.Id == "re1-gog-usa-hotfix4");

    private IGameTrainer Game => _adapter.CreateTrainer(Gog);

    private uint FlagNow() => BitConverter.ToUInt32(Read(Flag, 4));

    private byte[] MapNow() => Read(Map, 32);

    private byte[] Read(ulong address, int length)
    {
        var bytes = new byte[length];
        Assert.True(_host.Memory.TryRead(address, bytes));
        return bytes;
    }

    /// <summary>A poll of the game as the fake memory holds it (as Lockpick reads every profile field each poll).</summary>
    private void Poll()
    {
        var raw = new Dictionary<string, Field<long>> { ["windowFocused"] = Field<long>.Valid(FlagNow()) };
        var map = MapNow();
        for (var i = 0; i < 8; i++)
        {
            raw[$"keyMap{i}"] = Field<long>.Valid(BitConverter.ToUInt32(map, i * 4));
        }

        _source.Poll(new GameSnapshot { InGameplay = Field<bool>.Valid(_inGame), RawFields = raw });
    }

    /// <summary>The game loses focus: its WM_ACTIVATE clears the flag; then Lockpick polls.</summary>
    private void ToBackground()
    {
        _inFront = false;
        _host.Memory.WriteU32(Flag, 0);
        Poll();
    }

    private void ToFront()
    {
        _inFront = true;
        _host.Memory.WriteU32(Flag, 1);
        Poll();
    }

    private void TurnOn() =>
        Assert.Equal(TrainerResult.Applied, _trainer.SetRule(TrainerRules.KeepRunning, Game.CreateKeepRunningRule(() => _inFront)).Result);

    [Fact]
    public void In_the_background_the_game_runs_on_and_the_keyboard_is_blank_until_it_is_in_front_again()
    {
        TurnOn();
        Poll();
        Assert.Equal(PlayerMap, MapNow()); // in front: nothing changes

        ToBackground();
        Assert.Equal(1u, FlagNow());
        Assert.Equal(new byte[32], MapNow());

        Poll(); // nothing more to do
        ToFront();
        Assert.Equal(PlayerMap, MapNow());
    }

    [Fact]
    public void It_works_at_the_title_too()
    {
        _inGame = false;
        TurnOn();
        ToBackground();
        Assert.Equal(1u, FlagNow());
        Assert.Equal(new byte[32], MapNow());
        ToFront();
        Assert.Equal(PlayerMap, MapNow());
    }

    [Fact]
    public void Turning_it_off_a_challenge_or_closing_Lockpick_puts_the_keyboard_back()
    {
        TurnOn();
        ToBackground();
        _trainer.ClearRule(TrainerRules.KeepRunning);
        Assert.Equal(PlayerMap, MapNow());

        TurnOn();
        ToBackground();
        _trainer.SetMode(TrainerMode.Challenge); // a challenge or a leaderboard run
        Assert.Equal(PlayerMap, MapNow());
        Assert.False(_trainer.HasRule(TrainerRules.KeepRunning));

        _trainer.SetMode(TrainerMode.Trainer);
        TurnOn();
        ToBackground();
        _trainer.Dispose(); // Lockpick closing
        Assert.Equal(PlayerMap, MapNow());
    }

    [Fact]
    public void A_map_the_game_loads_in_the_background_is_blanked_too_and_is_the_one_put_back()
    {
        var reloaded = (byte[])PlayerMap.Clone();
        reloaded[0] = 0x57; // W
        TurnOn();
        ToBackground();
        _host.Memory.Write(Map, reloaded); // the game loaded a save, and with it the player's settings
        Poll();
        Assert.Equal(new byte[32], MapNow());

        ToFront();
        Assert.Equal(reloaded, MapNow());
    }

    [Fact]
    public void Only_versions_checked_live_offer_it()
    {
        var fields = Game.KeepRunningFields;
        Assert.Equal(9, fields.Count);
        Assert.All(fields, f => Assert.True(Gog.FindField(f) is { IsWritable: true, Write.AnyTime: true }, f));
        // GOG's Japanese build: the focus flag and the key map read live on it (2026-10-28 lab).
        var japanese = _adapter.Profiles.Single(p => p.Id == "re1-gog-jpn-hotfix4");
        Assert.All(fields, f => Assert.True(japanese.FindField(f) is { IsWritable: true, Write.AnyTime: true }, f));
        foreach (var port in _adapter.Profiles.Where(p => p.Id != Gog.Id && p.Id != japanese.Id))
        {
            // Waiting for a live check of the whole set on that build (1.01 has the focus flag, not yet the key map).
            Assert.False(fields.All(f => port.FindField(f)!.IsWritable), port.Id);
        }

        var steam = _adapter.Profiles.Single(p => p.Id == "re1-steam-usa");
        Assert.Equal(Flag, steam.FindField("windowFocused")!.Address);           // GOG's image inside Steam's wrapper
        Assert.Equal(0x4B3274ul, _adapter.Profiles.Single(p => p.Id == "re1-gog-jpn-hotfix4").FindField("windowFocused")!.Address);
    }

    [Fact]
    public void Infinite_ammo_and_other_rules_still_write_only_in_a_game()
    {
        _inGame = false;
        var asked = 0;
        _trainer.SetRule("other", "Other", _ =>
        {
            asked++;
            return [];
        });
        Poll();
        Assert.Equal(0, asked);
        _inGame = true;
        Poll();
        Assert.Equal(1, asked);
    }

    [Fact]
    public void Controller_and_sound_in_the_background_are_offered_only_where_both_patches_were_checked_live()
    {
        var patches = Game.BackgroundPatches;
        Assert.Equal(["backgroundFocus", "backgroundSound"], patches);
        var japanese = _adapter.Profiles.Single(p => p.Id == "re1-gog-jpn-hotfix4");
        Assert.All(patches, n => Assert.True(japanese.FindPatch(n) is { IsUsable: true }, n));
        Assert.Equal(0x448795ul, japanese.FindPatch("backgroundFocus")!.Address);
        Assert.Equal(0x48B6F1ul, japanese.FindPatch("backgroundSound")!.Address);
        // GOG English: the focus patch was live with the dev harness; the sound patch waits for its own listen.
        Assert.True(Gog.FindPatch("backgroundFocus") is { IsUsable: true });
        Assert.False(Gog.FindPatch("backgroundSound")!.IsUsable);
    }
}
