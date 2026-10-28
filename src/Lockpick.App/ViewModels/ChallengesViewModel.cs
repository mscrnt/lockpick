using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lockpick.App.Services;
using Lockpick.Core.Challenges;

namespace Lockpick.App.ViewModels;

/// <summary>
/// Challenges (Runs → Challenges), laid out as steps: choose a challenge, check its rules, start it; its status shows
/// above the run's timer. While a challenge runs Lockpick can't change the game (Challenge Mode).
/// </summary>
/// <summary>An alert sound to choose: a Windows sound's id, "none", or a .wav file's path.</summary>
public sealed record SoundChoice(string Id, string Name)
{
    public override string ToString() => Name;
}

public sealed partial class ChallengesViewModel : PageViewModel
{
    private readonly ChallengeService _challenges;
    private readonly AppServices _services;
    private readonly bool _loading = true;

    public ChallengesViewModel(AppServices services, LiveState state)
        : base("Challenges", "Icon.Trophy")
    {
        _services = services;
        _challenges = services.Challenges;
        _challenges.EventRaised += (_, e) => state.Add(e);
        _challenges.StatusChanged += (_, _) => Dispatcher.UIThread.Post(Sync);
        services.Runs.Died += (_, _) => Dispatcher.UIThread.Post(Sync);
        AlertSound = services.Settings.Current.AlertSound;
        LoadSounds();
        Selected = Presets[0];
        Sync();
        _loading = false;
    }

    /// <summary>"Deaths this session: 3" (any play since Lockpick started).</summary>
    [ObservableProperty]
    public partial string Deaths { get; private set; } = string.Empty;

    /// <summary>A sound on a fail or a death.</summary>
    [ObservableProperty]
    public partial bool AlertSound { get; set; }

    /// <summary>The sounds to choose from: none, Windows' own, and the player's own files chosen so far.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<SoundChoice> Sounds { get; private set; } = [];

    [ObservableProperty]
    public partial SoundChoice? FailSound { get; set; }

    [ObservableProperty]
    public partial SoundChoice? DeathSound { get; set; }

    [ObservableProperty]
    public partial string SoundMessage { get; private set; } = string.Empty;

    partial void OnAlertSoundChanged(bool value)
    {
        if (!_loading)
        {
            _services.Settings.Save(_services.Settings.Current with { AlertSound = value });
        }
    }

    partial void OnFailSoundChanged(SoundChoice? value)
    {
        if (!_loading && value is not null)
        {
            _services.Settings.Save(_services.Settings.Current with { FailSound = value.Id });
        }
    }

    partial void OnDeathSoundChanged(SoundChoice? value)
    {
        if (!_loading && value is not null)
        {
            _services.Settings.Save(_services.Settings.Current with { DeathSound = value.Id });
        }
    }

    [RelayCommand]
    private void PlayFail() => Play(FailSound);

    [RelayCommand]
    private void PlayDeath() => Play(DeathSound);

    /// <summary>Uses the player's .wav file (the view picks it) for a fail or a death.</summary>
    public void UseFile(string path, bool forDeath)
    {
        if (!Platform.Windows.AlertSound.IsWave(path))
        {
            SoundMessage = "Choose a .wav file (Windows plays .wav files without a media player).";
            return;
        }

        var choice = new SoundChoice(path, $"Your sound: {Path.GetFileName(path)}");
        if (Sounds.All(s => s.Id != path))
        {
            Sounds = [.. Sounds, choice];
        }

        var chosen = Sounds.First(s => s.Id == path);
        if (forDeath)
        {
            DeathSound = chosen;
        }
        else
        {
            FailSound = chosen;
        }

        SoundMessage = string.Empty;
        Play(chosen);
    }

    private void Play(SoundChoice? sound)
    {
        SoundMessage = sound is null || sound.Id == Platform.Windows.AlertSound.None || Platform.Windows.AlertSound.Play(sound.Id)
            ? string.Empty
            : $"Couldn't play {sound.Name} (is the file still there?).";
    }

    private void LoadSounds()
    {
        var settings = _services.Settings.Current;
        List<SoundChoice> sounds = [new(Platform.Windows.AlertSound.None, "None"), .. Platform.Windows.AlertSound.BuiltIn.Select(b => new SoundChoice(b.Id, b.Name))];
        foreach (var file in new[] { settings.FailSound, settings.DeathSound }.Where(f => sounds.All(s => s.Id != f)).Distinct())
        {
            sounds.Add(new SoundChoice(file, $"Your sound: {Path.GetFileName(file)}"));
        }

        Sounds = sounds;
        FailSound = sounds.First(s => s.Id == settings.FailSound);
        DeathSound = sounds.First(s => s.Id == settings.DeathSound);
    }

    public IReadOnlyList<ChallengeDefinition> Presets => ChallengeDefinition.Presets;

    [ObservableProperty]
    public partial ChallengeDefinition? Selected { get; set; }

    [ObservableProperty]
    public partial bool NoDamage { get; set; }

    [ObservableProperty]
    public partial bool KnifeOnly { get; set; }

    [ObservableProperty]
    public partial bool IsActive { get; private set; }

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    public partial bool IsFailed { get; private set; }

    [ObservableProperty]
    public partial string StatusTitle { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusDetail { get; private set; } = string.Empty;

    /// <summary>Choosing is possible only while no challenge is running.</summary>
    public bool CanChoose => !IsActive;

    partial void OnSelectedChanged(ChallengeDefinition? value)
    {
        NoDamage = value?.Rules.Contains(ChallengeRule.NoDamage) ?? false;
        KnifeOnly = value?.Rules.Contains(ChallengeRule.KnifeOnly) ?? false;
    }

    partial void OnIsActiveChanged(bool value) => OnPropertyChanged(nameof(CanChoose));

    [RelayCommand]
    private void Start()
    {
        if (Selected is not { } challenge)
        {
            return;
        }

        List<ChallengeRule> rules = [];
        if (NoDamage)
        {
            rules.Add(ChallengeRule.NoDamage);
        }

        if (KnifeOnly)
        {
            rules.Add(ChallengeRule.KnifeOnly);
        }

        _challenges.Start(challenge, rules);
    }

    [RelayCommand]
    private void Retry() => _challenges.Retry();

    [RelayCommand]
    private void Stop() => _challenges.Stop();

    private void Sync()
    {
        var deaths = _services.Runs.Deaths;
        Deaths = deaths == 0 ? "No deaths this session." : deaths == 1 ? "1 death this session." : $"{deaths} deaths this session.";
        var status = _challenges.Status;
        IsActive = status.State != ChallengeState.Off;
        IsRunning = status.State == ChallengeState.Running;
        IsFailed = status.State == ChallengeState.Failed;
        var name = status.Challenge?.Name;
        var rules = status.Rules.Count == 0 ? "no extra rules" : string.Join(" + ", status.Rules.Select(ChallengeDefinition.Describe));
        (StatusTitle, StatusDetail) = status.State switch
        {
            ChallengeState.Off => ("No challenge running",
                "Pick a challenge below and press Start. Cheats stay as you set them; an attempt they changed is marked \"with cheats\"."),
            ChallengeState.Waiting => ($"{name}: ready",
                $"Start a new game or load a save in Resident Evil. The attempt begins when you're in the game ({rules})."),
            ChallengeState.Running => ($"{name}: attempt {status.Attempt} — {(status.WithCheats ? "with cheats" : "still clean")}",
                $"Playing since {status.AttemptStarted?.ToLocalTime():HH:mm:ss} ({rules}). A new game or a loaded save starts a new attempt."),
            _ => ($"{name}: failed — {status.FailReason}",
                (status.FailedAtGameTime is { } t ? $"In-game time {(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}. " : string.Empty)
                + "Press Try again, or start a new game or load a save for a new attempt."),
        };
    }
}
