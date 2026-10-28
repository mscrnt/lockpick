using Lockpick.Core.Challenges;
using Lockpick.Core.Events;
using Lockpick.Core.Runs;
using Lockpick.Core.Trainer;
using Microsoft.Extensions.Logging;

namespace Lockpick.App.Services;

public enum GuardState
{
    Pass,
    Fail,
    Warn,
}

/// <summary>One leaderboard check and how it stands.</summary>
public sealed record GuardCheck(GuardState State, string Text)
{
    public string Mark => State switch { GuardState.Pass => "✓", GuardState.Fail => "✗", _ => "!" };

    public bool IsPass => State == GuardState.Pass;

    public bool IsFail => State == GuardState.Fail;

    public bool IsWarn => State == GuardState.Warn;
}

/// <summary>
/// Leaderboard runs (Runs → Leaderboard run): keeps Lockpick hands-off and checks what the RE1 PC rules forbid, so a
/// submission doesn't fail because of Lockpick or the install. While on: the trainer is locked read-only (the
/// Speedrun challenge: no writes, and switching it on fails the attempt), ask mode is off (it pauses the game), and
/// the game files are compared with the official release's (no mods). What only the player can keep is listed as
/// reminders. Each attempt's record notes whether every check passed when it began.
/// </summary>
public sealed class LeaderboardGuard
{
    private readonly AppServices _services;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private InstallCheck? _install;
    private string? _installProblem;
    private int _checking;

    public LeaderboardGuard(AppServices services, ILogger log)
    {
        _services = services;
        _log = log;
        services.Runs.Checks = Note;
        services.Runs.EventRaised += (_, e) =>
        {
            if (e is RunEnded && Enabled)
            {
                _ = RefreshAsync(); // fresh for the next attempt
            }
        };
        services.Source.StatusChanged += (_, status) =>
        {
            // A different game attached (both releases can share a folder): check its install.
            if (Enabled && status.Process?.ExecutablePath is { Length: > 0 } path
                && !string.Equals(path, Volatile.Read(ref _checkedExe), StringComparison.OrdinalIgnoreCase))
            {
                _ = RefreshAsync();
            }
        };
        if (Enabled)
        {
            Engage();
        }
    }

    /// <summary>The executable the install check last ran for.</summary>
    private string? _checkedExe;

    public bool Enabled => _services.Settings.Current.LeaderboardMode;

    /// <summary>Turns ask mode off (set by the app: it lives on the Debug page).</summary>
    public Action? TurnOffAskMode { get; set; }

    /// <summary>Whether ask mode is on (set by the app).</summary>
    public Func<bool>? AskModeOn { get; set; }

    /// <summary>The checks as they stand, most important first.</summary>
    public IReadOnlyList<GuardCheck> Checks { get; private set; } = [];

    /// <summary>Every check passed (warnings allowed).</summary>
    public bool Ready => Checks.Count > 0 && Checks.All(c => c.State != GuardState.Fail);

    public IReadOnlyList<string> Reminders => _services.RunLibrary.Rules?.LeaderboardReminders ?? [];

    /// <summary>Raised when the checks change (any thread).</summary>
    public event EventHandler? Changed;

    public void SetEnabled(bool value)
    {
        if (value == Enabled)
        {
            return;
        }

        _services.Settings.Save(_services.Settings.Current with { LeaderboardMode = value });
        if (value)
        {
            Engage();
        }
        else
        {
            if (_services.Challenges.Status.Challenge?.Id == "speedrun")
            {
                _services.Challenges.Stop();
            }

            Checks = [];
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Runs every check again (the install is hashed off the UI thread).</summary>
    public async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1)
        {
            return;
        }

        try
        {
            Update(); // Lockpick's own checks at once, the files after
            // The game being played when there is one (both releases can share a folder), else the installed one.
            var exe = _services.Source.Status.Process?.ExecutablePath is { Length: > 0 } running ? running : _services.ResolveInstalledExecutable();
            Volatile.Write(ref _checkedExe, exe);
            (InstallCheck? install, string? problem) result = exe is null || _services.RunLibrary.Rules is not { } rules
                ? (null, "The game's install wasn't found (Settings → General → Game installation).")
                : await Task.Run(() =>
                {
                    try
                    {
                        return (rules.CheckInstall(exe), (string?)null);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        return ((InstallCheck?)null, $"The game files couldn't be read ({ex.Message}).");
                    }
                }).ConfigureAwait(false);
            lock (_gate)
            {
                (_install, _installProblem) = result;
            }

            Update();
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    private void Engage()
    {
        // The trainer locked read-only for the whole session: the Speedrun challenge (no rules of its own).
        if (_services.Challenges.Status.State == ChallengeState.Off && ChallengeDefinition.Presets.FirstOrDefault(p => p.Id == "speedrun") is { } speedrun)
        {
            _services.Challenges.Start(speedrun, lockTrainer: true);
        }

        TurnOffAskMode?.Invoke();
        _ = RefreshAsync();
    }

    private void Update()
    {
        var checks = new List<GuardCheck>();
        var trainerOff = _services.Trainer is not { Mode: TrainerMode.Trainer };
        checks.Add(trainerOff
            ? new GuardCheck(GuardState.Pass, "Lockpick's trainer is off and locked (read-only; switching it on would fail the attempt).")
            : new GuardCheck(GuardState.Fail, "Lockpick's trainer is on: turn it off (Trainer → Cheats)."));
        checks.Add(AskModeOn?.Invoke() == true
            ? new GuardCheck(GuardState.Fail, "Ask mode is on: it pauses the game. Turn it off (Debug → Capture).")
            : new GuardCheck(GuardState.Pass, "Ask mode is off (Lockpick never pauses the game)."));
        lock (_gate)
        {
            if (_installProblem is { } problem)
            {
                checks.Add(new GuardCheck(GuardState.Fail, problem));
            }
            else if (_install is null)
            {
                checks.Add(new GuardCheck(GuardState.Warn, "Checking the game files…"));
            }
            else if (!_install.FilesCompared && _install.Notes.Count > 0)
            {
                // The adapter's findings say what was compared (e.g. the data under Classic REbirth).
            }
            else if (!_install.FilesCompared)
            {
                checks.Add(new GuardCheck(GuardState.Warn,
                    $"Lockpick has no list of {_install.Release}'s files yet, so its data files aren't compared (the checks below still apply)."));
            }
            else if (_install.Unchanged)
            {
                checks.Add(new GuardCheck(GuardState.Pass, $"The game files are {_install.Release}'s, unchanged (no mods)."));
            }
            else
            {
                var changed = _install.Changed.Concat(_install.Missing.Select(m => m + " (missing)")).ToList();
                checks.Add(new GuardCheck(GuardState.Fail,
                    $"{changed.Count} game file(s) differ from {_install.Release}'s (a mod?): {string.Join(", ", changed.Take(5))}{(changed.Count > 5 ? ", …" : string.Empty)}."));
            }

            foreach (var note in _install?.Notes ?? [])
            {
                checks.Add(new GuardCheck(note.Level switch
                {
                    InstallFindingLevel.Fail => GuardState.Fail,
                    InstallFindingLevel.Warn => GuardState.Warn,
                    _ => GuardState.Pass,
                }, note.Text));
            }

            if (_install is { Extra.Count: > 0 } withExtra)
            {
                checks.Add(new GuardCheck(GuardState.Warn,
                    $"Files beside the game that the release doesn't have (check they aren't mods): {string.Join(", ", withExtra.Extra.Take(5))}{(withExtra.Extra.Count > 5 ? ", …" : string.Empty)}."));
            }
        }

        checks.Add(_services.LiveSplit.Status.StartsWith("Connected", StringComparison.Ordinal)
            ? new GuardCheck(GuardState.Pass, "LiveSplit is connected.")
            : new GuardCheck(GuardState.Warn,
                "The rules name LiveSplit's in-game time script. Lockpick's timer reads the same in-game time, but the moderators haven't said whether its overlay counts: to be safe, also run LiveSplit with the RE1 autosplitter."));
        Checks = checks;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The note on a new attempt's record (null when leaderboard runs are off).</summary>
    private string? Note()
    {
        if (!Enabled)
        {
            return null;
        }

        var failed = Checks.Where(c => c.State == GuardState.Fail).Select(c => c.Text).ToList();
        if (Checks.Count == 0)
        {
            return "Leaderboard run: the checks hadn't run when the attempt began.";
        }

        _log.LogInformation("Leaderboard attempt begins with {Failed} failed check(s)", failed.Count);
        InstallCheck? install;
        lock (_gate)
        {
            install = _install;
        }

        return failed.Count == 0
            ? "Leaderboard checks passed when the attempt began: Lockpick read-only (trainer locked, ask mode off), "
                + (install is { FilesCompared: false } ? $"{install.Release} as checked (data files not compared)." : "game files unchanged.")
            : $"Leaderboard checks NOT passed when the attempt began: {string.Join(" ", failed)}";
    }
}
