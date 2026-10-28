using Lockpick.Core.Events;
using Lockpick.Core.Monitoring;
using Lockpick.Core.Profiles;
using Lockpick.Core.State;
using Lockpick.Core.Trainer;

namespace Lockpick.Core.Challenges;

/// <summary>
/// Runs the challenge the player chose. It only watches: damage fails a no-damage run, a shot from anything but the knife
/// fails knife-only. Cheats stay as the player set them: an attempt the trainer changed is marked
/// <see cref="ChallengeStatus.WithCheats"/>, not failed. A locked challenge (leaderboard mode's record runs) puts the
/// trainer in Challenge Mode (no write access) instead, and switching out of it fails the attempt. A new game or a
/// loaded save starts a new attempt.
/// </summary>
public sealed class ChallengeService : IDisposable
{
    private readonly IGameStateSource _source;
    private readonly TrainerService? _trainer;
    private readonly Func<VersionProfile, IChallengeRules?> _rulesFor;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private GameSnapshot? _previous;
    private bool _changingMode;
    private bool _locked;

    public ChallengeService(IGameStateSource source, TrainerService? trainer, Func<VersionProfile, IChallengeRules?> rulesFor, TimeProvider? time = null)
    {
        _source = source;
        _trainer = trainer;
        _rulesFor = rulesFor;
        _time = time ?? TimeProvider.System;
        _source.Polled += OnPolled;
        _source.EventRaised += OnEvent;
        if (_trainer is not null)
        {
            _trainer.StateChanged += OnTrainerChanged;
        }
    }

    public ChallengeStatus Status { get; private set; } = ChallengeStatus.Off;

    /// <summary>Raised when the status changes (any thread).</summary>
    public event EventHandler<ChallengeStatus>? StatusChanged;

    /// <summary><see cref="ChallengeStarted"/> and <see cref="ChallengeFailed"/>, for the event log, captures and the API.</summary>
    public event EventHandler<GameEvent>? EventRaised;

    /// <summary>
    /// Starts <paramref name="challenge"/> with <paramref name="rules"/> (its own by default). With
    /// <paramref name="lockTrainer"/> the trainer goes to Challenge Mode for as long as it runs (record runs); otherwise
    /// cheats are left as they are.
    /// </summary>
    public void Start(ChallengeDefinition challenge, IReadOnlyList<ChallengeRule>? rules = null, bool lockTrainer = false)
    {
        lock (_gate)
        {
            _locked = lockTrainer;
            if (lockTrainer)
            {
                _changingMode = true;
                _trainer?.SetMode(TrainerMode.Challenge);
                _changingMode = false;
            }

            Status = new ChallengeStatus(ChallengeState.Waiting, challenge, rules ?? challenge.Rules, 0, null, null, null, null);
            _previous = null;
            if (InGame(_source.Latest))
            {
                BeginAttempt();
            }
        }

        StatusChanged?.Invoke(this, Status);
    }

    /// <summary>Starts the attempt over (after a failure, or to reset the clock).</summary>
    public void Retry()
    {
        lock (_gate)
        {
            if (Status.Challenge is null)
            {
                return;
            }

            Status = Status with { State = ChallengeState.Waiting, FailReason = null, FailedAt = null, FailedAtGameTime = null };
            _previous = null;
            if (InGame(_source.Latest))
            {
                BeginAttempt();
            }
        }

        StatusChanged?.Invoke(this, Status);
    }

    /// <summary>Ends the challenge; a locked one puts Lockpick back to read-only.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (Status.State == ChallengeState.Off)
            {
                return;
            }

            Status = ChallengeStatus.Off;
            _changingMode = true;
            if (_locked && _trainer?.Mode == TrainerMode.Challenge)
            {
                _trainer.SetMode(TrainerMode.ReadOnly);
            }

            _changingMode = false;
            _locked = false;
        }

        StatusChanged?.Invoke(this, Status);
    }

    public void Dispose()
    {
        _source.Polled -= OnPolled;
        _source.EventRaised -= OnEvent;
        if (_trainer is not null)
        {
            _trainer.StateChanged -= OnTrainerChanged;
        }
    }

    private void OnPolled(object? sender, GameSnapshot snapshot)
    {
        var changed = false;
        lock (_gate)
        {
            if (Status.State == ChallengeState.Waiting && InGame(snapshot))
            {
                BeginAttempt();
                changed = true;
            }
            else if (Status.State == ChallengeState.Running && InGame(snapshot) && _previous is { } previous
                && Status.Rules.Contains(ChallengeRule.KnifeOnly)
                && _source.Status.Profile is { } profile && _rulesFor(profile)?.NonMeleeShot(previous, snapshot) is { } weapon)
            {
                Fail($"fired the {weapon}", snapshot);
                changed = true;
            }

            _previous = InGame(snapshot) ? snapshot : null;
        }

        if (changed)
        {
            StatusChanged?.Invoke(this, Status);
        }
    }

    private void OnEvent(object? sender, GameEvent gameEvent)
    {
        var changed = false;
        lock (_gate)
        {
            switch (gameEvent)
            {
                case GameLoaded when Status.Challenge is not null:
                    // A new game or a loaded save is a new attempt.
                    BeginAttempt();
                    changed = true;
                    break;
                case GameStopped when Status.State == ChallengeState.Running:
                    // The game closed: that attempt is over; the next game or load starts another.
                    Status = Status with { State = ChallengeState.Waiting };
                    _previous = null;
                    changed = true;
                    break;
                case PlayerDamaged damaged when Status.State == ChallengeState.Running && Status.Rules.Contains(ChallengeRule.NoDamage):
                    Fail($"took {damaged.Amount} damage", _source.Latest);
                    changed = true;
                    break;
            }
        }

        if (changed)
        {
            StatusChanged?.Invoke(this, Status);
        }
    }

    private void OnTrainerChanged(object? sender, EventArgs e)
    {
        var changed = false;
        lock (_gate)
        {
            if (_locked && !_changingMode && Status.State is ChallengeState.Running or ChallengeState.Waiting && _trainer?.Mode != TrainerMode.Challenge)
            {
                Fail("Lockpick's trainer was switched on", _source.Latest);
                changed = true;
            }
            else if (!_locked && Status.State == ChallengeState.Running && !Status.WithCheats && _trainer?.RunModified == true)
            {
                Status = Status with { WithCheats = true };
                changed = true;
            }
        }

        if (changed)
        {
            StatusChanged?.Invoke(this, Status);
        }
    }

    private void BeginAttempt()
    {
        var now = _time.GetUtcNow();
        Status = Status with
        {
            State = ChallengeState.Running,
            Attempt = Status.Attempt + 1,
            AttemptStarted = now,
            FailReason = null,
            FailedAt = null,
            FailedAtGameTime = null,
            WithCheats = false,
        };
        EventRaised?.Invoke(this, new ChallengeStarted(now, Status.Challenge!.Name, Status.Attempt));
    }

    private void Fail(string reason, GameSnapshot? snapshot)
    {
        var now = _time.GetUtcNow();
        var gameTime = snapshot is { GameTime.HasValue: true } s ? s.GameTime.Value : (TimeSpan?)null;
        Status = Status with { State = ChallengeState.Failed, FailReason = reason, FailedAt = now, FailedAtGameTime = gameTime };
        EventRaised?.Invoke(this, new ChallengeFailed(now, Status.Challenge!.Name, reason, gameTime));
    }

    private static bool InGame(GameSnapshot? snapshot) => snapshot?.InGameplay is { HasValue: true, Value: true };
}
