using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lockpick.App.Services;

namespace Lockpick.App.ViewModels;

/// <summary>
/// Stream → OBS → OBS WebSocket: Lockpick's own connection to OBS (#65), for OBS on another PC or without the script.
/// The password box is cleared as soon as Save is pressed; the password is kept encrypted (<see cref="AppServices.Secrets"/>)
/// and only ever sent to OBS, as the answer to its challenge (never the password itself).
/// </summary>
public sealed partial class ObsLinkViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly ObsLink _link;
    private bool _syncing;

    public ObsLinkViewModel(AppServices services)
    {
        _services = services;
        _link = services.Obs;
        _syncing = true;
        var obs = services.Settings.Current.Obs;
        Enabled = obs.Enabled;
        Host = obs.Host;
        Port = obs.Port;
        RecordWithRuns = obs.RecordWithRuns;
        StopDelay = obs.StopDelaySeconds;
        Chapters = obs.Chapters;
        ReplayOnDeath = obs.ReplayOnDeath;
        ReplayOnFail = obs.ReplayOnFail;
        ReplayOnBest = obs.ReplayOnBest;
        PauseSceneEnabled = obs.PauseScene is not null;
        PauseScene = obs.PauseScene;
        _syncing = false;
        HasPassword = services.Secrets.Exists(AppServices.ObsPasswordName);
        _link.StatusChanged += (_, _) => Dispatcher.UIThread.Post(Sync);
        Sync();
    }

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    public partial string Host { get; set; }

    [ObservableProperty]
    public partial decimal Port { get; set; }

    /// <summary>What the player typed; cleared when they press Save.</summary>
    [ObservableProperty]
    public partial string PasswordInput { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasPassword { get; private set; }

    [ObservableProperty]
    public partial string PasswordStatus { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Status { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Note { get; private set; } = string.Empty;

    /// <summary>On, but not connected (the status says why).</summary>
    [ObservableProperty]
    public partial bool Problem { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetUpSceneCommand))]
    public partial bool Connected { get; private set; }

    [ObservableProperty]
    public partial bool RecordWithRuns { get; set; }

    [ObservableProperty]
    public partial decimal StopDelay { get; set; }

    [ObservableProperty]
    public partial bool Chapters { get; set; }

    [ObservableProperty]
    public partial bool ReplayOnDeath { get; set; }

    [ObservableProperty]
    public partial bool ReplayOnFail { get; set; }

    [ObservableProperty]
    public partial bool ReplayOnBest { get; set; }

    [ObservableProperty]
    public partial bool PauseSceneEnabled { get; set; }

    [ObservableProperty]
    public partial string? PauseScene { get; set; }

    /// <summary>OBS's scenes, for the "paused" scene (the chosen one stays listed while OBS is away).</summary>
    public ObservableCollection<string> Scenes { get; } = [];

    [ObservableProperty]
    public partial string SetUpResult { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetUpSceneCommand))]
    public partial bool Busy { get; private set; }

    partial void OnEnabledChanged(bool value) => Change(s => s with { Enabled = value }, reconnect: true);

    partial void OnRecordWithRunsChanged(bool value) => Change(s => s with { RecordWithRuns = value });

    partial void OnStopDelayChanged(decimal value) => Change(s => s with { StopDelaySeconds = (int)value });

    partial void OnChaptersChanged(bool value) => Change(s => s with { Chapters = value });

    partial void OnReplayOnDeathChanged(bool value) => Change(s => s with { ReplayOnDeath = value });

    partial void OnReplayOnFailChanged(bool value) => Change(s => s with { ReplayOnFail = value });

    partial void OnReplayOnBestChanged(bool value) => Change(s => s with { ReplayOnBest = value });

    partial void OnPauseSceneEnabledChanged(bool value)
    {
        if (value && PauseScene is null)
        {
            // The scene called "Paused" if OBS has one (as the script's list would show it), else the first.
            PauseScene = Scenes.FirstOrDefault(s => s.Equals("Paused", StringComparison.OrdinalIgnoreCase)) ?? Scenes.FirstOrDefault();
        }

        Change(s => s with { PauseScene = value ? PauseScene : null });
    }

    partial void OnPauseSceneChanged(string? value)
    {
        if (PauseSceneEnabled && value is not null)
        {
            Change(s => s with { PauseScene = value });
        }
    }

    /// <summary>Connects again with the address typed in (host and port).</summary>
    [RelayCommand]
    private void Connect()
    {
        var host = Host.Trim();
        if (host.Length == 0)
        {
            Host = host = "127.0.0.1";
        }

        // A password typed but not saved goes with the connection (pressing Connect means "use this").
        if (PasswordInput.Length > 0)
        {
            SavePassword();
        }

        Change(s => s with { Host = host, Port = (int)Port, Enabled = true }, reconnect: true);
        _syncing = true;
        Enabled = true;
        _syncing = false;
    }

    [RelayCommand]
    private void SavePassword()
    {
        var password = PasswordInput;
        PasswordInput = string.Empty; // off the screen and out of the box at once
        if (password.Length == 0)
        {
            PasswordStatus = "Type OBS's WebSocket password first (OBS: Tools → WebSocket Server Settings → Show Connect Info).";
            return;
        }

        Keep(password);
        PasswordStatus = "Password saved, encrypted for your Windows account on this PC. Lockpick sends OBS only the answer to its challenge, never the password.";
    }

    [RelayCommand]
    private void RemovePassword()
    {
        _services.Secrets.Delete(AppServices.ObsPasswordName);
        HasPassword = false;
        PasswordStatus = "Password removed from this PC.";
        _link.Configure();
    }

    /// <summary>OBS on this PC: its WebSocket port and password, from OBS's own settings.</summary>
    [RelayCommand]
    private void FillFromObs()
    {
        if (ObsServerConfig.Read() is not { } config)
        {
            PasswordStatus = "OBS's WebSocket settings aren't on this PC (OBS 28 or later, for this Windows account).";
            return;
        }

        Host = "127.0.0.1";
        Port = config.Port;
        if (config.Password is { } password)
        {
            Keep(password);
        }
        else
        {
            _services.Secrets.Delete(AppServices.ObsPasswordName);
            HasPassword = false;
        }

        PasswordStatus = (config.Enabled ? string.Empty : "OBS's WebSocket server is off: in OBS, Tools → WebSocket Server Settings → Enable WebSocket server. ")
            + (config.Password is null ? $"Filled in from OBS: port {config.Port}, no password." : $"Filled in from OBS: port {config.Port} and its password (kept encrypted).");
        Connect();
    }

    [RelayCommand(CanExecute = nameof(CanSetUp))]
    private async Task SetUpSceneAsync()
    {
        Busy = true;
        SetUpResult = "Setting up OBS…";
        try
        {
            var settings = _services.Settings.Current;
            var executable = _services.GameExecutableName();
            var liveSplit = _services.LiveSplitEngine.Available;
            SetUpResult = await _link.SetUpSceneAsync(canvas => ObsScenePlan.Sources(canvas, settings, executable, liveSplit));
        }
        catch (ObsException ex)
        {
            SetUpResult = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    private bool CanSetUp() => Connected && !Busy;

    private void Keep(string password)
    {
        try
        {
            _services.Secrets.Save(AppServices.ObsPasswordName, password);
            HasPassword = true;
            _link.Configure();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PasswordStatus = $"The password couldn't be kept: {ex.Message}";
        }
    }

    private void Change(Func<ObsSettings, ObsSettings> change, bool reconnect = false)
    {
        if (_syncing)
        {
            return;
        }

        try
        {
            _services.Settings.Save(_services.Settings.Current with { Obs = change(_services.Settings.Current.Obs) });
        }
        catch (IOException)
        {
            // Applies now; only not remembered.
        }

        if (reconnect)
        {
            _link.Configure();
        }
    }

    private void Sync()
    {
        Status = _link.Status;
        Note = _link.Note;
        Connected = _link.Connected;
        Problem = _services.Settings.Current.Obs.Enabled && !_link.Connected;
        var chosen = PauseScene;
        var names = _link.Scenes.Concat(chosen is null || _link.Scenes.Contains(chosen) ? [] : [chosen]).ToList();
        if (!names.SequenceEqual(Scenes))
        {
            _syncing = true;
            Scenes.Clear();
            foreach (var name in names)
            {
                Scenes.Add(name);
            }

            PauseScene = chosen;
            _syncing = false;
        }
    }
}
