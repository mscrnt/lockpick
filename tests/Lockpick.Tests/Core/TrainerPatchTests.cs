using Lockpick.Core.Profiles;
using Lockpick.Core.State;
using Lockpick.Core.Trainer;
using Lockpick.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lockpick.Tests.Core;

/// <summary>Code patches carried by a trainer rule: applied only over the expected bytes, read back, logged, put back.</summary>
public sealed class TrainerPatchTests : IDisposable
{
    private const ulong Jump = 0x403000;
    private const ulong Flags = 0x403100;
    private static readonly byte[] JumpOriginal = [0xC7, 0x05, 0x2C, 0xCB, 0x4B, 0x00, 0x00, 0x00, 0x00, 0x00];
    private static readonly byte[] JumpPatched = [0xE9, 0xC6, 0x00, 0x00, 0x00, 0x90, 0x90, 0x90, 0x90, 0x90];
    private static readonly byte[] FlagsOriginal = [0xE2, 0x00, 0x00, 0x00];
    private static readonly byte[] FlagsPatched = [0xE2, 0x80, 0x00, 0x00];

    private static readonly VersionProfile Profile = TrainerServiceTests.Profile with
    {
        Patches =
        [
            new CodePatch { Name = "focus", Address = Jump, Instruction = Jump, Original = JumpOriginal, Patched = JumpPatched,
                Verification = Verification.Runtime, Evidence = "test" },
            new CodePatch { Name = "sound", Address = Flags, Instruction = Flags - 4, Original = FlagsOriginal, Patched = FlagsPatched,
                Verification = Verification.Runtime, Evidence = "test" },
            new CodePatch { Name = "unchecked", Address = Flags + 0x10, Instruction = Flags + 0x10, Original = [0x01], Patched = [0x02],
                Verification = Verification.Static, Evidence = "test" },
        ],
    };

    private readonly FakeStateSource _source = new();
    private readonly FakeWriteHost _host = new();
    private readonly TrainerActionLog _log = new(null);
    private readonly TrainerService _trainer;

    public TrainerPatchTests()
    {
        _host.Memory.Write(Jump, JumpOriginal).Write(Flags, FlagsOriginal).Write(Flags + 0x10, [0x01]);
        _trainer = new TrainerService(_source, _host, _log, NullLogger.Instance);
        _source.Attach(1234, Profile);
        _source.Poll(new GameSnapshot { InGameplay = Field<bool>.Valid(true) });
        _trainer.SetMode(TrainerMode.Trainer);
    }

    public void Dispose()
    {
        _trainer.Dispose();
        _source.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static TrainerRule Rule(params string[] patches) => new("Background", _ => []) { Patches = patches };

    private byte[] At(ulong address, int length)
    {
        var bytes = new byte[length];
        Assert.True(_host.Memory.TryRead(address, bytes));
        return bytes;
    }

    [Fact]
    public void A_rule_applies_its_patches_and_puts_the_code_back_when_switched_off()
    {
        Assert.Equal(TrainerResult.Applied, _trainer.SetRule("bg", Rule("focus", "sound")).Result);
        Assert.Equal(JumpPatched, At(Jump, 10));
        Assert.Equal(FlagsPatched, At(Flags, 4));
        Assert.Contains(_log.Recent, a => a.Result == TrainerResult.Applied && a.Message?.Contains("0x403000", StringComparison.Ordinal) == true);
        Assert.True(_trainer.RunModified);

        _trainer.ClearRule("bg");
        Assert.Equal(JumpOriginal, At(Jump, 10));
        Assert.Equal(FlagsOriginal, At(Flags, 4));
    }

    [Fact]
    public void Leaving_trainer_mode_or_closing_puts_the_code_back()
    {
        _trainer.SetRule("bg", Rule("focus"));
        _trainer.SetMode(TrainerMode.ReadOnly);
        Assert.Equal(JumpOriginal, At(Jump, 10));

        _trainer.SetMode(TrainerMode.Trainer);
        _trainer.SetRule("bg", Rule("focus"));
        Assert.Equal(JumpPatched, At(Jump, 10));
        _trainer.Dispose();
        Assert.Equal(JumpOriginal, At(Jump, 10));
    }

    [Fact]
    public void Unexpected_code_or_an_unchecked_patch_refuses_the_rule_and_undoes_what_was_applied()
    {
        _host.Memory.Write(Flags, [0xE2, 0x01, 0x00, 0x00]); // not what the profile says: left alone
        var refused = _trainer.SetRule("bg", Rule("focus", "sound"));
        Assert.Equal(TrainerResult.Refused, refused.Result);
        Assert.Contains("isn't what Lockpick expects", refused.Message, StringComparison.Ordinal);
        Assert.Equal(JumpOriginal, At(Jump, 10)); // the first patch went back
        Assert.Equal(new byte[] { 0xE2, 0x01, 0x00, 0x00 }, At(Flags, 4));
        Assert.False(_trainer.HasRule("bg"));

        var notChecked = _trainer.SetRule("bg", Rule("unchecked"));
        Assert.Equal(TrainerResult.Refused, notChecked.Result);
        Assert.Equal(new byte[] { 0x01 }, At(Flags + 0x10, 1));
    }

    [Fact]
    public void A_patch_already_in_place_counts_as_applied()
    {
        _host.Memory.Write(Jump, JumpPatched);
        Assert.Equal(TrainerResult.Applied, _trainer.SetRule("bg", Rule("focus")).Result);
        _trainer.ClearRule("bg");
        Assert.Equal(JumpOriginal, At(Jump, 10));
    }

    [Fact]
    public void A_restarted_game_gets_the_patches_again()
    {
        _trainer.SetRule("bg", Rule("focus"));
        _source.Detach();
        _host.Memory.Write(Jump, JumpOriginal); // the new process has the game's own code
        _source.Attach(5678, Profile);
        Assert.Equal(JumpPatched, At(Jump, 10));
        Assert.True(_trainer.HasRule("bg"));
    }
}
