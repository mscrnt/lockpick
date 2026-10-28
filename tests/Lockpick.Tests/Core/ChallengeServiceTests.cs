using Lockpick.Core.Challenges;
using Lockpick.Core.Events;
using Lockpick.Core.State;
using Lockpick.Core.Trainer;
using Lockpick.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lockpick.Tests.Core;

public sealed class ChallengeServiceTests : IDisposable
{
    private readonly FakeStateSource _source = new();
    private readonly FakeWriteHost _host = new();
    private readonly TrainerService _trainer;
    private readonly ChallengeService _challenges;
    private readonly StubRules _rules = new();
    private readonly List<GameEvent> _events = [];

    public ChallengeServiceTests()
    {
        _trainer = new TrainerService(_source, _host, new TrainerActionLog(null), NullLogger.Instance);
        _challenges = new ChallengeService(_source, _trainer, _ => _rules);
        _challenges.EventRaised += (_, e) => _events.Add(e);
        _source.Attach(1234, TrainerServiceTests.Profile);
    }

    public void Dispose()
    {
        _challenges.Dispose();
        _trainer.Dispose();
        _source.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static GameSnapshot Game(bool inGame = true) => new() { InGameplay = Field<bool>.Valid(inGame), GameTime = Field<TimeSpan>.Valid(TimeSpan.FromMinutes(3)) };

    private static ChallengeDefinition Preset(string id) => ChallengeDefinition.Presets.Single(p => p.Id == id);

    [Fact]
    public void Starting_a_challenge_leaves_the_trainer_alone_and_waits_for_a_game()
    {
        _source.Poll(Game(inGame: false));
        _trainer.SetMode(TrainerMode.Trainer);
        _challenges.Start(Preset("no-damage"));
        Assert.Equal(TrainerMode.Trainer, _trainer.Mode);
        Assert.Equal(ChallengeState.Waiting, _challenges.Status.State);

        _source.Poll(Game());
        Assert.Equal(ChallengeState.Running, _challenges.Status.State);
        Assert.Equal(1, _challenges.Status.Attempt);
        Assert.IsType<ChallengeStarted>(Assert.Single(_events));
    }

    [Fact]
    public void Damage_fails_a_no_damage_run_with_the_reason_and_game_time()
    {
        _source.Poll(Game());
        _challenges.Start(Preset("no-damage"));
        _source.Raise(new PlayerDamaged(DateTimeOffset.UtcNow, 10, 86));

        Assert.Equal(ChallengeState.Failed, _challenges.Status.State);
        Assert.Equal("took 10 damage", _challenges.Status.FailReason);
        Assert.Equal(TimeSpan.FromMinutes(3), _challenges.Status.FailedAtGameTime);
        Assert.Single(_events.OfType<ChallengeFailed>());
    }

    [Fact]
    public void Damage_does_not_fail_a_challenge_without_that_rule()
    {
        _source.Poll(Game());
        _challenges.Start(Preset("knife-only"));
        _source.Raise(new PlayerDamaged(DateTimeOffset.UtcNow, 10, 86));
        Assert.Equal(ChallengeState.Running, _challenges.Status.State);
    }

    [Fact]
    public void A_shot_from_a_gun_fails_knife_only()
    {
        _source.Poll(Game());
        _challenges.Start(Preset("knife-only"));
        _source.Poll(Game());
        _rules.Shot = "Beretta";
        _source.Poll(Game());

        Assert.Equal(ChallengeState.Failed, _challenges.Status.State);
        Assert.Equal("fired the Beretta", _challenges.Status.FailReason);
    }

    [Fact]
    public void A_locked_challenge_turns_on_challenge_mode_and_turning_the_trainer_on_fails_it()
    {
        _source.Poll(Game());
        _challenges.Start(Preset("speedrun"), lockTrainer: true);
        Assert.Equal(TrainerMode.Challenge, _trainer.Mode);
        _trainer.SetMode(TrainerMode.Trainer);
        Assert.Equal(ChallengeState.Failed, _challenges.Status.State);
        Assert.Contains("trainer", _challenges.Status.FailReason, StringComparison.Ordinal);

        _challenges.Stop();
        Assert.Equal(TrainerMode.Trainer, _trainer.Mode);
    }

    [Fact]
    public void Cheats_during_a_challenge_mark_the_attempt_and_a_new_attempt_starts_clean()
    {
        _host.Memory.WriteI16(TrainerServiceTests.Hp, 50).WriteU8(TrainerServiceTests.MaxHp, 96);
        _source.Poll(Game() with { RawFields = new Dictionary<string, Field<long>> { ["hp"] = Field<long>.Valid(50), ["maxHp"] = Field<long>.Valid(96) } });
        _challenges.Start(Preset("no-damage"));
        _trainer.SetMode(TrainerMode.Trainer);
        Assert.Equal(ChallengeState.Running, _challenges.Status.State);
        Assert.False(_challenges.Status.WithCheats);

        Assert.Equal(TrainerResult.Applied, _trainer.Write("hp", 96).Result);
        Assert.Equal(ChallengeState.Running, _challenges.Status.State);
        Assert.True(_challenges.Status.WithCheats);

        _challenges.Retry();
        Assert.False(_challenges.Status.WithCheats);
        Assert.Equal(TrainerMode.Trainer, _trainer.Mode);
    }

    [Fact]
    public void A_new_game_or_load_starts_a_new_attempt_and_stop_goes_back_to_read_only()
    {
        _source.Poll(Game());
        _challenges.Start(Preset("no-damage"), lockTrainer: true);
        _source.Raise(new PlayerDamaged(DateTimeOffset.UtcNow, 10, 86));
        _source.Raise(new GameLoaded(DateTimeOffset.UtcNow, TimeSpan.Zero, true));
        Assert.Equal(ChallengeState.Running, _challenges.Status.State);
        Assert.Equal(2, _challenges.Status.Attempt);

        _challenges.Stop();
        Assert.Equal(ChallengeState.Off, _challenges.Status.State);
        Assert.Equal(TrainerMode.ReadOnly, _trainer.Mode);
    }

    [Fact]
    public void Closing_the_game_ends_the_attempt_and_the_next_game_starts_another()
    {
        _source.Poll(Game());
        _challenges.Start(Preset("speedrun"));
        Assert.Equal(ChallengeState.Running, _challenges.Status.State);

        _source.Raise(new GameStopped(DateTimeOffset.UtcNow, "Game process exited"));
        Assert.Equal(ChallengeState.Waiting, _challenges.Status.State);

        _source.Poll(Game());
        Assert.Equal(ChallengeState.Running, _challenges.Status.State);
        Assert.Equal(2, _challenges.Status.Attempt);
    }

    private sealed class StubRules : IChallengeRules
    {
        public string? Shot { get; set; }

        public string? NonMeleeShot(GameSnapshot previous, GameSnapshot current) => Shot;
    }
}
