using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lockpick.App.Services;
using Lockpick.Core.Monitoring;
using Lockpick.Core.Trainer;

namespace Lockpick.App.ViewModels;

/// <summary>One line of the trainer's action log.</summary>
public sealed record TrainerActionRow(string Time, string Text, bool Applied)
{
    public static TrainerActionRow From(TrainerAction action) => new(
        action.Timestamp.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
        action.Describe(),
        action.Result == TrainerResult.Applied);
}

/// <summary>
/// Trainer page. Lockpick starts read-only every time; the player chooses Trainer Mode (a write handle, validated
/// writes) or Challenge Mode (no write handle at all, the run kept clean). Everything here goes through
/// <see cref="TrainerService"/>, which checks, reads back and logs each write.
/// </summary>
public sealed partial class TrainerViewModel : PageViewModel
{
    private const string AmmoRule = TrainerRules.InfiniteAmmo;
    private const string KeepRunningRule = TrainerRules.KeepRunning;
    private const int MaxRows = 200;

    private readonly TrainerService? _trainer;
    private readonly AppServices _services;
    private bool _syncing;

    public TrainerViewModel(AppServices services, LiveState state)
        : base("Trainer", "Icon.Wrench")
    {
        SetSections(
            new PageSection("cheats", "Cheats"),
            new PageSection("inventory", "Inventory"),
            new PageSection("log", "Action log"));
        _services = services;
        _trainer = services.Trainer;
        State = state;
        if (_trainer is not null)
        {
            foreach (var action in _trainer.Log.Recent.Take(MaxRows))
            {
                Actions.Add(TrainerActionRow.From(action));
            }

            _trainer.Log.Added += (_, a) => Dispatcher.UIThread.Post(() => AddRow(a));
            _trainer.StateChanged += (_, _) => Dispatcher.UIThread.Post(Sync);
            _trainer.EventRaised += (_, e) => state.Add(e);
            services.TrainerHotkeys.Pressed += (_, action) => Dispatcher.UIThread.Post(() => OnHotkey(action));
        }

        state.PropertyChanged += OnStateChanged;
        services.Challenges.StatusChanged += (_, _) => Dispatcher.UIThread.Post(Sync);
        Sync();
    }

    public LiveState State { get; }

    public bool Available => _trainer is not null;

    public ObservableCollection<TrainerActionRow> Actions { get; } = [];

    public string? LogPath => _trainer?.Log.Path;

    [ObservableProperty]
    public partial TrainerMode Mode { get; private set; }

    [ObservableProperty]
    public partial string ModeText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanWrite { get; private set; }

    [ObservableProperty]
    public partial string RunText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool RunModified { get; private set; }

    [ObservableProperty]
    public partial string HealthText { get; private set; } = "—";

    [ObservableProperty]
    public partial decimal? HealthTarget { get; set; }

    [ObservableProperty]
    public partial decimal HealthMax { get; private set; } = 1;

    [ObservableProperty]
    public partial bool HealthLocked { get; set; }

    [ObservableProperty]
    public partial bool InfiniteAmmo { get; set; }

    /// <summary>The game keeps running (and ignores keys typed elsewhere) while it isn't the active window.</summary>
    [ObservableProperty]
    public partial bool KeepRunning { get; set; }

    /// <summary>This version of the game has keep-running's fields checked live (writable in its profile).</summary>
    [ObservableProperty]
    public partial bool KeepRunningAvailable { get; private set; }

    [ObservableProperty]
    public partial string KeepRunningNote { get; private set; } = string.Empty;

    /// <summary>The controller and the sound keep working in the background too (patches the game's code in memory).</summary>
    [ObservableProperty]
    public partial bool BackgroundInput { get; set; }

    [ObservableProperty]
    public partial bool BackgroundInputAvailable { get; private set; }

    [ObservableProperty]
    public partial string LastResult { get; private set; } = string.Empty;

    /// <summary>What the player carries (the count can be changed on weapons, ammo and ribbons).</summary>
    public ObservableCollection<TrainerSlotRow> Slots { get; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<GivableItem> GivableItems { get; private set; } = [];

    [ObservableProperty]
    public partial GivableItem? GiveItem { get; set; }

    [ObservableProperty]
    public partial decimal? GiveQuantity { get; set; } = 1;

    [RelayCommand]
    private void PutInItemBox()
    {
        if (_trainer is null || Game() is not { } game || GiveItem is not { } item || State.Snapshot is not { } snapshot)
        {
            return;
        }

        if (game.AddToStorage(snapshot, item.Id, (int)(GiveQuantity ?? 1), out var refusal) is { } write)
        {
            Show(_trainer.Write(write.Field, write.Value, TrainerSource.User, $"Item box + {item.Name}"));
        }
        else
        {
            LastResult = $"Can't put {item.Name} in the item box: {refusal}";
        }
    }

    private void SetSlot(TrainerSlotRow row)
    {
        if (_trainer is null || Game() is not { } game || State.Snapshot is not { } snapshot || row.Target is not { } target)
        {
            return;
        }

        if (game.SetQuantity(snapshot, row.Slot, (int)target, out var refusal) is { } write)
        {
            Show(_trainer.Write(write.Field, write.Value, TrainerSource.User, $"{row.Name} count"));
        }
        else
        {
            LastResult = $"Can't change {row.Name}: {refusal}";
        }
    }

    private string _slotsShown = string.Empty;

    private void SyncSlots(IGameTrainer? game)
    {
        var held = State.Snapshot?.Inventory is { HasValue: true, Value: { } items } ? items : [];
        var key = string.Join(";", held.Select(h => $"{h.Slot}:{h.ItemId}:{h.Quantity}"));
        if (key == _slotsShown)
        {
            return;
        }

        _slotsShown = key;
        Slots.Clear();
        foreach (var item in held)
        {
            Slots.Add(new TrainerSlotRow(item.Slot, _services.Adapter.ItemName(item.ItemId) ?? $"Item {item.ItemId}", item.Quantity,
                game?.HasQuantity(item.ItemId) ?? false, SetSlot));
        }
    }

    public bool IsReadOnly => Mode == TrainerMode.ReadOnly;

    public bool IsTrainer => Mode == TrainerMode.Trainer;

    public bool IsChallenge => Mode == TrainerMode.Challenge;

    /// <summary>
    /// Cheats on (Trainer Mode, write access) or off (read-only). Turning them on turns leaderboard mode off: a run with
    /// cheats is practice, not one for the leaderboards. Any other challenge keeps running (its attempt marked "with cheats").
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanToggleCheats))]
    private void ToggleCheats()
    {
        if (_trainer is null)
        {
            return;
        }

        if (Mode != TrainerMode.Trainer && _services.LeaderboardGuard.Enabled)
        {
            _services.LeaderboardGuard.SetEnabled(false);
        }

        _trainer.SetMode(Mode == TrainerMode.Trainer ? TrainerMode.ReadOnly : TrainerMode.Trainer);
    }

    private bool CanToggleCheats() => _trainer is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleCheatsCommand))]
    public partial bool ChallengeActive { get; private set; }

    public string CheatsButtonText => Mode == TrainerMode.Trainer ? "Turn cheats off" : "Turn cheats on";

    [RelayCommand]
    private void SetHealth()
    {
        if (_trainer is not null && Game() is { } game && HealthTarget is { } target)
        {
            Show(HealthLocked ? _trainer.Lock(game.HealthField, (long)target) : _trainer.Write(game.HealthField, (long)target));
        }
    }

    [RelayCommand]
    private void FillHealth()
    {
        if (_trainer is not null && Game() is { } game && State.Snapshot?.Health is { HasValue: true, Value.Maximum: { } max })
        {
            HealthTarget = max;
            Show(HealthLocked ? _trainer.Lock(game.HealthField, max) : _trainer.Write(game.HealthField, max));
        }
    }

    partial void OnHealthLockedChanged(bool value)
    {
        if (_syncing || _trainer is null || Game() is not { } game)
        {
            return;
        }

        if (!value)
        {
            _trainer.Unlock(game.HealthField);
            return;
        }

        var target = HealthTarget ?? (State.Snapshot?.Health is { HasValue: true, Value: var health } ? health.Current : null);
        if (target is { } t)
        {
            Show(_trainer.Lock(game.HealthField, (long)t));
        }

        Sync();
    }

    partial void OnInfiniteAmmoChanged(bool value)
    {
        if (_syncing || _trainer is null || Game() is not { } game)
        {
            return;
        }

        if (value)
        {
            Show(_trainer.SetRule(AmmoRule, "Infinite ammo", game.CreateInfiniteAmmoRule()));
        }
        else
        {
            _trainer.ClearRule(AmmoRule);
        }

        Sync();
    }

    partial void OnKeepRunningChanged(bool value)
    {
        if (_syncing || _trainer is null || Game() is not { } game)
        {
            return;
        }

        if (value)
        {
            Show(_trainer.SetRule(KeepRunningRule, KeepRunningRuleFor(game)));
        }
        else
        {
            _trainer.ClearRule(KeepRunningRule); // puts the keyboard back if the game is in the background
        }

        Sync();
    }

    /// <summary>Keep-running, with the controller and sound patches when the player asked for them and they're usable.</summary>
    private TrainerRule KeepRunningRuleFor(IGameTrainer game) =>
        BackgroundInput && BackgroundInputAvailable
            ? game.CreateKeepRunningRule(GameInFront) with { Name = "Keep running in the background (controller and sound too)", Patches = game.BackgroundPatches }
            : game.CreateKeepRunningRule(GameInFront);

    partial void OnBackgroundInputChanged(bool value)
    {
        if (_syncing || _trainer is null || Game() is not { } game)
        {
            return;
        }

        if (value && !KeepRunning)
        {
            KeepRunning = true; // its rule takes the patches
            return;
        }

        if (KeepRunning)
        {
            Show(_trainer.SetRule(KeepRunningRule, KeepRunningRuleFor(game))); // the rule again, with or without them
        }

        Sync();
    }

    /// <summary>The versions keep-running has been checked on (from the profiles, so it stays true as more are).</summary>
    private string Checked(IGameTrainer game)
    {
        var names = _services.Adapter.Profiles
            .Where(p => game.KeepRunningFields.All(f => p.FindField(f) is { IsWritable: true }))
            .Select(p => p.DisplayName).ToList();
        return names.Count switch
        {
            0 => "no version yet",
            1 => names[0],
            _ => string.Join(", ", names.SkipLast(1)) + " and " + names[^1],
        };
    }

    /// <summary>The game is the active window and showing (no window seen yet counts as in front: nothing to do).</summary>
    private bool GameInFront() => _services.GameWindow() is not { } window || (window.Foreground && !window.Minimized);

    partial void OnModeChanged(TrainerMode value)
    {
        OnPropertyChanged(nameof(CheatsButtonText));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(IsTrainer));
        OnPropertyChanged(nameof(IsChallenge));
    }

    /// <summary>A trainer hotkey: ignored unless Trainer Mode has write access (nothing happens, nothing is logged).</summary>
    private void OnHotkey(string action)
    {
        Sync();
        if (_trainer is null || !CanWrite || Game() is not { } game)
        {
            return;
        }

        switch (action)
        {
            case TrainerHotkeys.FillHealth when State.Snapshot?.Health is { HasValue: true, Value.Maximum: { } max }:
                HealthTarget = max;
                Show(HealthLocked ? _trainer.Lock(game.HealthField, max, TrainerSource.Hotkey) : _trainer.Write(game.HealthField, max, TrainerSource.Hotkey));
                break;
            case TrainerHotkeys.ToggleHealthLock:
                if (HealthLocked)
                {
                    _trainer.Unlock(game.HealthField, TrainerSource.Hotkey);
                }
                else if ((HealthTarget ?? (State.Snapshot?.Health is { HasValue: true, Value: var health } ? health.Current : null)) is { } target)
                {
                    Show(_trainer.Lock(game.HealthField, (long)target, TrainerSource.Hotkey));
                }

                break;
            case TrainerHotkeys.ToggleInfiniteAmmo:
                if (InfiniteAmmo)
                {
                    _trainer.ClearRule(AmmoRule, TrainerSource.Hotkey);
                }
                else
                {
                    Show(_trainer.SetRule(AmmoRule, "Infinite ammo", game.CreateInfiniteAmmoRule(), TrainerSource.Hotkey));
                }

                break;
        }

        Sync();
    }

    private IGameTrainer? Game() =>
        State.Status.Profile is { } profile ? _services.Adapter.CreateTrainer(profile) : null;

    private void Show(TrainerAction action) =>
        LastResult = action.Result == TrainerResult.Applied ? $"Done: {action.Describe()}" : action.Describe();

    private void AddRow(TrainerAction action)
    {
        Actions.Insert(0, TrainerActionRow.From(action));
        while (Actions.Count > MaxRows)
        {
            Actions.RemoveAt(Actions.Count - 1);
        }
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LiveState.Snapshot) or nameof(LiveState.Status))
        {
            Sync();
        }
    }

    private void Sync()
    {
        _syncing = true;
        try
        {
            if (_trainer is null)
            {
                ModeText = "The trainer isn't available while replaying a recording.";
                return;
            }

            Mode = _trainer.Mode;
            CanWrite = Mode == TrainerMode.Trainer && _trainer.HasWriteAccess;
            var challenge = _services.Challenges.Status;
            // Only a locked challenge (leaderboard mode's record runs) keeps cheats off; any other leaves them alone.
            ChallengeActive = challenge.State != Lockpick.Core.Challenges.ChallengeState.Off && _trainer.Mode == TrainerMode.Challenge;
            var attached = State.Status.State == ConnectionState.Attached;
            ModeText = Mode switch
            {
                _ when ChallengeActive => "Leaderboard mode is on, so cheats are off. Turning cheats on turns leaderboard mode off: a run with cheats doesn't count for the leaderboards.",
                TrainerMode.ReadOnly => "Cheats are off: Lockpick only reads the game and has no way to change it.",
                TrainerMode.Challenge => "Challenge Mode: Lockpick refuses every change.",
                _ when _trainer.HasWriteAccess => "Cheats are on. Every change is checked against the game's limits, read back and logged below.",
                _ when _trainer.WriteAccessError is { } error => $"Cheats are on, but Windows refused to let Lockpick change the game: {error}",
                _ when !attached => "Cheats are on: start Resident Evil and they'll work as soon as Lockpick finds it.",
                _ => "Cheats are on, but Lockpick can't change the game yet.",
            };

            RunModified = _trainer.RunModified;
            RunText = _trainer.RunModifiedAt is { } at
                ? $"This run was changed by the trainer (first change at {at.ToLocalTime():HH:mm:ss}). A new game or a loaded save starts a clean run."
                : "This run hasn't been changed by the trainer.";

            if (State.Snapshot?.Health is { HasValue: true, Value: var health })
            {
                HealthText = health.Maximum is { } max ? $"{health.Current} / {max}" : $"{health.Current}";
                HealthMax = Math.Max(1, health.Maximum ?? health.Current);
                HealthTarget ??= health.Maximum ?? health.Current;
            }
            else
            {
                HealthText = "—";
            }

            var game = Game();
            SyncSlots(game);
            if (game is not null && GivableItems.Count == 0)
            {
                GivableItems = game.GivableItems;
                GiveItem ??= GivableItems.Count > 0 ? GivableItems[0] : null;
            }

            HealthLocked = game is not null && _trainer.Locks.ContainsKey(game.HealthField);
            InfiniteAmmo = _trainer.HasRule(AmmoRule);
            KeepRunning = _trainer.HasRule(KeepRunningRule);
            var profile = State.Status.Profile;
            KeepRunningAvailable = game is not null && profile is not null
                && game.KeepRunningFields.All(f => profile.FindField(f) is { IsWritable: true });
            BackgroundInputAvailable = KeepRunningAvailable && game!.BackgroundPatches.Count > 0
                && game.BackgroundPatches.All(p => profile!.FindPatch(p) is { IsUsable: true });
            KeepRunningNote = KeepRunningAvailable || profile is null || game is null
                ? string.Empty
                : $"Not checked on this version of the game yet ({profile.DisplayName}); so far it works on {Checked(game)}.";
        }
        finally
        {
            _syncing = false;
        }
    }
}
