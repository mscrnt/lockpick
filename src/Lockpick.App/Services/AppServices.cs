using Lockpick.Api;
using Lockpick.Core.Fingerprinting;
using Lockpick.Core.Logging;
using Lockpick.Core.Monitoring;
using Lockpick.Core.Profiles;
using Lockpick.Core.Recording;
using Lockpick.Core.Trainer;
using Lockpick.GameAdapter.RE1;
using Lockpick.Platform.Windows;
using Microsoft.Extensions.Logging;

namespace Lockpick.App.Services;

/// <summary>Composition root: builds logging, settings, the RE1 adapter and the state source.</summary>
public sealed class AppServices : IAsyncDisposable
{
    private readonly HighResolutionWaitStrategy? _wait;
    private readonly ILogger _log;
    private GlobalHotkey? _markerHotkey;
    private GamepadButtons? _gamepad;
    private GamepadBinding _markerButton = new(GamepadBinding.Off);
    private TaskCompletionSource<GamepadButton>? _binding;

    private AppServices(
        JsonLinesLoggerProvider logProvider,
        ILoggerFactory loggerFactory,
        SettingsStore settings,
        Re1Adapter adapter,
        IGameStateSource source,
        FileFingerprintProvider fingerprints,
        HighResolutionWaitStrategy? wait,
        string? replayPath,
        IProcessWriteHost? writeHost = null)
    {
        Updates = new UpdateService(settings, loggerFactory.CreateLogger("Updates"));
        LogProvider = logProvider;
        LoggerFactory = loggerFactory;
        Settings = settings;
        Adapter = adapter;
        Source = source;
        Fingerprints = fingerprints;
        _wait = wait;
        _log = loggerFactory.CreateLogger("Lockpick");
        SetMarkerHotkey(settings.Current.MarkerHotkey);
        SetMarkerButton(settings.Current.MarkerButton);
        Pause = new GamePause(settings, loggerFactory.CreateLogger("GamePause"));
        Pause.RecoverFromCrash();
        ReplayPath = replayPath;
        if (writeHost is not null)
        {
            Trainer = new TrainerService(source, writeHost, new TrainerActionLog(AppPaths.TrainerLog), loggerFactory.CreateLogger("Trainer"));
        }

        Challenges = new Core.Challenges.ChallengeService(source, Trainer, adapter.CreateChallengeRules);
        Feed = new Core.Insight.EventFeed(source, adapter.CreateChangeExplainer, adapter.RoomName);
        Runs = new Core.Runs.RunService(source, Trainer, adapter.CreateRunRules, new Core.Runs.RunHistoryStore(AppPaths.Runs));
        RunLibrary = new RunLibrary(Runs, adapter, Runs.History!, settings, loggerFactory.CreateLogger("Runs"));
        LiveSplitEngine = new LiveSplitEngine(Runs, source, AppPaths.Runs, loggerFactory.CreateLogger("LiveSplitCore"), settings.Current.RunComparison);
        SpeedrunCom = new SpeedrunCom(Updates.CurrentVersion);
        Secrets = new SecretStore(AppPaths.Secrets);
        SpeedrunAccount = new SpeedrunAccount(SpeedrunCom, Secrets, settings);
        LiveSplit = new LiveSplitLink(Runs, source, settings.Current.LiveSplitMode, settings.Current.LiveSplitPort, loggerFactory.CreateLogger("LiveSplit"));
        LeaderboardGuard = new LeaderboardGuard(this, loggerFactory.CreateLogger("Leaderboard"));
        Obs = new ObsLink(() => Settings.Current.Obs, () => Secrets.Load(ObsPasswordName), loggerFactory.CreateLogger("ObsWebSocket"));
        ObsEvents = new ObsEvents(this, loggerFactory.CreateLogger("Obs"));
        ObsEvents.Happened += (_, moment) => Obs.Post(moment);
        SpeedrunCom.ShownChanged += (_, _) => LiveSplitEngine.SetRecord(SpeedrunCom.Shown?.Record is { } wr
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(int)wr.Time.TotalMinutes}:{wr.Time.Seconds:D2}.{wr.Time.Milliseconds / 100} {wr.Runner}")
            : null);
        TrainerHotkeys = new TrainerHotkeys(_log);
        Route = new RouteService(source, adapter, settings, ResolveInstalledExecutable, () => RunLibrary.Find(settings.Current.RunSetId),
            loggerFactory.CreateLogger("Route"));
        EnsureGamepad();
        TrainerHotkeys.Pressed += (_, action) =>
        {
            switch (action)
            {
                case TrainerHotkeys.RunSplit:
                    Runs.Split();
                    break;
                case TrainerHotkeys.RunUndo:
                    Runs.Undo();
                    break;
                case TrainerHotkeys.RunSkip:
                    Runs.Skip();
                    break;
                case TrainerHotkeys.RunReset:
                    Runs.Reset();
                    break;
                case TrainerHotkeys.ChallengeRetry:
                    Challenges.Retry();
                    break;
            }
        };

        // The optional alert (Runs → Challenges): a challenge failing, or a death.
        Challenges.StatusChanged += (_, status) =>
        {
            if (status.State == Core.Challenges.ChallengeState.Failed && status.FailedAt != _alertedFail)
            {
                _alertedFail = status.FailedAt;
                Alert(Settings.Current.FailSound);
            }
        };
        Runs.Died += (_, _) => Alert(Settings.Current.DeathSound);
        foreach (var (action, key) in settings.Current.TrainerHotkeys)
        {
            TrainerHotkeys.Set(action, key, MarkerHotkeyName);
        }

        foreach (var (action, button) in settings.Current.GamepadActions)
        {
            SetActionButton(action, button);
        }
    }

    private DateTimeOffset? _alertedFail;
    private long _lastAlert;

    /// <summary>An alert sound when they're on — one for a death that also fails the challenge (both report it).</summary>
    private void Alert(string sound)
    {
        var now = Environment.TickCount64;
        if (Settings.Current.AlertSound && now - Interlocked.Exchange(ref _lastAlert, now) > 2000)
        {
            AlertSound.Play(sound);
        }
    }

    /// <summary>The challenge the player chose (Runs → Challenges, API).</summary>
    public Core.Challenges.ChallengeService Challenges { get; }

    /// <summary>What just happened in the game, in plain words (event log, overlay).</summary>
    public Core.Insight.EventFeed Feed { get; }

    /// <summary>Run timing and splits (Runs page, overlay, API).</summary>
    public Core.Runs.RunService Runs { get; }

    /// <summary>The route guide (docs/ROUTES.md).</summary>
    public RouteService Route { get; }

    /// <summary>The split sets, attempt records and comparisons.</summary>
    public RunLibrary RunLibrary { get; }

    /// <summary>The link to the runner's LiveSplit (Runs page).</summary>
    public LiveSplitLink LiveSplit { get; }

    /// <summary>livesplit-core following Lockpick's run (LiveSplit's timer, layout and files).</summary>
    public LiveSplitEngine LiveSplitEngine { get; }

    /// <summary>Leaderboard runs: Lockpick hands-off and the rules checked.</summary>
    public LeaderboardGuard LeaderboardGuard { get; }

    /// <summary>The game's window as last seen (set by the app from the overlay's tracking; null when there is none).</summary>
    public Func<Core.Display.GameWindowInfo?> GameWindow { get; set; } = () => null;

    /// <summary>The files Lockpick's OBS script reads (the API's address, the run's moments).</summary>
    public ObsEvents ObsEvents { get; }

    /// <summary>The optional connection to OBS's WebSocket server (Stream → OBS): the script's automation from Lockpick's side.</summary>
    public ObsLink Obs { get; }

    /// <summary>The name OBS's WebSocket password is kept under in <see cref="Secrets"/>.</summary>
    public const string ObsPasswordName = "obs-websocket-password";

    /// <summary>Secrets kept encrypted for this Windows user (the speedrun.com key, OBS's password); never in settings.</summary>
    public SecretStore Secrets { get; }

    /// <summary>The game's executable name for OBS's Game Capture: the one running, else the installed one.</summary>
    public string GameExecutableName() =>
        Source.Status.Profile?.Executable.FileName
        ?? (ResolveInstalledExecutable() is { } path ? Path.GetFileName(path) : "ResidentEvil.exe");

    /// <summary>Overlay widgets open in their own windows (pop-outs).</summary>
    public WidgetWindows WidgetWindows { get; } = new();

    /// <summary>speedrun.com's API (leaderboards, the player's account).</summary>
    public SpeedrunCom SpeedrunCom { get; }

    /// <summary>The player's speedrun.com account and API key (kept encrypted).</summary>
    public SpeedrunAccount SpeedrunAccount { get; }

    /// <summary>System-wide keys for trainer actions (Settings); presses are acted on by the Trainer page.</summary>
    public TrainerHotkeys TrainerHotkeys { get; }

    /// <summary>The API for other tools, once the app has given it capture control (<see cref="CreateApi"/>).</summary>
    public ApiServer? Api { get; private set; }

    /// <summary>Why the API isn't listening although it is on (e.g. the port is taken), or null.</summary>
    public string? ApiError { get; private set; }

    /// <summary>Draws an overlay widget as large as fits a box (set by the app once its widgets exist; UI thread).</summary>
    public Func<string, int, int, Lockpick.Api.WidgetPicture?>? DrawWidget { get; set; }

    private async Task<Lockpick.Api.WidgetPicture?> WidgetAsync(string id, int width, int height) =>
        DrawWidget is { } draw ? await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => draw(id, width, height)) : null;

    /// <summary>What a split set's splits are judged against: the comparison the player chose (Runs page; LiveSplit's
    /// history), or Lockpick's own personal best and best segments.</summary>
    public Core.Runs.RunComparison ComparisonFor(Core.Runs.SplitSet set) => LiveSplitEngine.Against(set, RunLibrary.Comparison(set));

    public void CreateApi(ICaptureControl capture)
    {
        Api ??= new ApiServer(new ApiHost(Source, Adapter, Trainer, capture, AppVersion, Challenges, Runs, ComparisonFor, Feed,
                LiveSplitEngine.Available ? LiveSplitEngine.Render : null, WidgetAsync),
            LoggerFactory.CreateLogger("Api"));
    }

    /// <summary>Starts, restarts or stops the API to match the settings (making a token the first time).</summary>
    public async Task ApplyApiSettingsAsync()
    {
        if (Api is null || IsReplay)
        {
            return;
        }

        var current = Settings.Current;
        if (!current.ApiEnabled)
        {
            ApiError = null;
            ObsEvents.WriteConnection();
            await Api.StopAsync().ConfigureAwait(false);
            return;
        }

        if (string.IsNullOrEmpty(current.ApiToken))
        {
            current = current with { ApiToken = ApiOptions.NewToken() };
            Settings.Save(current);
        }

        ObsEvents.WriteConnection();

        ApiError = await Api.StartAsync(new ApiOptions
        {
            AllowNetwork = current.ApiAllowNetwork,
            Port = current.ApiPort,
            Token = current.ApiToken!,
            AllowTrainer = current.ApiAllowTrainer,
        }).ConfigureAwait(false);
    }

    /// <summary>The trainer (live game only; null while replaying). Read-only until the player turns Trainer Mode on.</summary>
    public TrainerService? Trainer { get; }

    public JsonLinesLoggerProvider LogProvider { get; }

    public ILoggerFactory LoggerFactory { get; }

    public SettingsStore Settings { get; }

    public Re1Adapter Adapter { get; }

    public IGameStateSource Source { get; }

    public FileFingerprintProvider Fingerprints { get; }

    public UpdateService Updates { get; }

    /// <summary>Freezes the game while an ask-mode question is open.</summary>
    public GamePause Pause { get; }

    /// <summary>Raised (on a background thread) when the marker hotkey is pressed, whichever window has focus.</summary>
    public event EventHandler? MarkerHotkeyPressed;

    /// <summary>A capture marker asked for from elsewhere (an OBS hotkey), as the marker key does.</summary>
    public void RequestMarker() => MarkerHotkeyPressed?.Invoke(this, EventArgs.Empty);

    /// <summary>The registered marker hotkey's name, or null when it is off or Windows refused it.</summary>
    public string? MarkerHotkeyName => _markerHotkey is { IsRegistered: true } hotkey ? hotkey.Name : null;

    /// <summary>Replaces the marker hotkey; returns false if Windows refused it (another program may use it).</summary>
    public bool SetMarkerHotkey(string name)
    {
        _markerHotkey?.Dispose();
        _markerHotkey = GlobalHotkey.Create(name);
        if (_markerHotkey is null)
        {
            return name == "Off";
        }

        if (!_markerHotkey.IsRegistered)
        {
            _log.LogWarning("Marker hotkey {Key} is unavailable (error {Error}); another program may use it", name, _markerHotkey.Error);
            return false;
        }

        _markerHotkey.Pressed += (_, _) => MarkerHotkeyPressed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>The controller button that adds a marker (the same event as the hotkey).</summary>
    public GamepadBinding MarkerButton => _markerButton;

    // Set on the UI thread, read on the controller thread.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, GamepadBinding> _actionButtons = new();

    /// <summary>The controller button bound to an action (TrainerHotkeys.Actions ids), or Off.</summary>
    public GamepadBinding ActionButton(string action) => _actionButtons.GetValueOrDefault(action) ?? new GamepadBinding(GamepadBinding.Off);

    /// <summary>Binds a controller button to an action (a GamepadBinding value; Off removes it).</summary>
    public void SetActionButton(string action, string value)
    {
        var binding = new GamepadBinding(value);
        if (binding.IsOff)
        {
            _actionButtons.TryRemove(action, out _);
        }
        else
        {
            _actionButtons[action] = binding;
        }

        EnsureGamepad();
    }

    /// <summary>Uses <paramref name="value"/> (a <see cref="GamepadBinding"/> value) for markers from the controller.</summary>
    public void SetMarkerButton(string value)
    {
        _markerButton = new GamepadBinding(value);
        EnsureGamepad();
    }

    /// <summary>The next controller button pressed (for binding one), or null after <paramref name="timeout"/>.</summary>
    public async Task<GamepadButton?> NextGamepadButtonAsync(TimeSpan timeout)
    {
        var wait = new TaskCompletionSource<GamepadButton>(TaskCreationOptions.RunContinuationsAsynchronously);
        _binding = wait;
        EnsureGamepad();
        try
        {
            return await wait.Task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            Interlocked.CompareExchange(ref _binding, null, wait);
            EnsureGamepad();
        }
    }

    /// <summary>Reads the controllers only while a button is bound or being bound, or the route guide shows the pad in use.</summary>
    public void EnsureGamepad()
    {
        var needed = !_markerButton.IsOff || _binding is not null || !_actionButtons.IsEmpty
            || (Settings.Current.RouteId is not null && Settings.Current.ButtonStyle == ButtonStyles.Auto);
        if (needed && _gamepad is null)
        {
            _gamepad = new GamepadButtons();
            _gamepad.ButtonDown += OnGamepadButton;
            if (!_gamepad.HidAvailable)
            {
                _log.LogWarning("Raw Input is unavailable: only XInput controllers can add markers");
            }
        }
        else if (!needed && _gamepad is not null)
        {
            _gamepad.ButtonDown -= OnGamepadButton;
            _gamepad.Dispose();
            _gamepad = null;
        }
    }

    private void OnGamepadButton(object? sender, GamepadButton button)
    {
        Route?.Pressed(button);
        if (_binding is { } binding)
        {
            binding.TrySetResult(button);
            return;
        }

        if (_markerButton.Matches(button))
        {
            MarkerHotkeyPressed?.Invoke(this, EventArgs.Empty);
        }

        foreach (var (action, bound) in _actionButtons)
        {
            if (bound.Matches(button))
            {
                TrainerHotkeys.Fire(action);
            }
        }
    }

    public string AppVersion => Updates.CurrentVersion;

    public string? ReplayPath { get; }

    public bool IsReplay => ReplayPath is not null;

    public bool HighResolutionTimer => _wait?.IsHighResolution ?? false;

    public static AppServices Create(string[] args)
    {
        var logPath = Path.Combine(AppPaths.Logs, $"lockpick-{DateTime.Now:yyyyMMdd}.jsonl");
        var logProvider = new JsonLinesLoggerProvider(logPath, args.Contains("--verbose") ? LogLevel.Debug : LogLevel.Information);
        var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddProvider(logProvider).SetMinimumLevel(LogLevel.Trace));
        var log = loggerFactory.CreateLogger("Lockpick");
        log.LogInformation("Lockpick {Version} starting", AppInfo.Version);

        var settings = new SettingsStore(AppPaths.SettingsFile, log);
        var current = settings.Load();

        var extraProfiles = ProfileLoader.LoadDirectory(
            current.ProfileDirectory ?? AppPaths.Profiles,
            (file, ex) => log.LogWarning("Ignoring invalid profile {File}: {Error}", file, ex.Message));
        var adapter = new Re1Adapter(extraProfiles);
        log.LogInformation("Loaded {Count} RE1 profile(s): {Ids}", adapter.Profiles.Count, string.Join(", ", adapter.Profiles.Select(p => p.Id)));

        var fingerprints = new FileFingerprintProvider();
        var replayIndex = Array.IndexOf(args, "--replay");
        if (replayIndex >= 0 && replayIndex + 1 < args.Length)
        {
            var path = args[replayIndex + 1];
            var (header, frames) = SessionRecording.ReadFile(path);
            log.LogInformation("Replay mode: {Path} ({Frames} frames)", path, frames.Count);
            return new AppServices(logProvider, loggerFactory, settings, adapter, new ReplaySource(header, frames, loop: true), fingerprints, null, path);
        }

        var wait = new HighResolutionWaitStrategy();
        var host = new Win32ProcessHost();
        var monitor = new GameMonitor(
            adapter,
            host,
            fingerprints,
            new MonitorOptions { WaitStrategy = wait },
            loggerFactory.CreateLogger<GameMonitor>());
        return new AppServices(logProvider, loggerFactory, settings, adapter, monitor, fingerprints, wait, null, host);
    }

    /// <summary>The executable to inspect offline: the configured path, else the GOG install, else Steam's.</summary>
    public string? ResolveInstalledExecutable() =>
        Settings.Current.GameExecutablePath is { Length: > 0 } configured
            ? configured
            : GameInstalls.Executable();

    public async ValueTask DisposeAsync()
    {
        // Never back on the UI thread: App.Shutdown waits for this on it (resuming there hung the app on close).
        Pause.Dispose();
        if (Api is not null)
        {
            await Api.DisposeAsync().ConfigureAwait(false);
        }

        ObsEvents.Dispose();
        Obs.Dispose();
        Challenges.Dispose();
        LiveSplit.Dispose();
        LiveSplitEngine.Dispose();
        SpeedrunCom.Dispose();
        Route.Dispose();
        Runs.Dispose();
        Feed.Dispose();
        Trainer?.Dispose();
        await Source.DisposeAsync().ConfigureAwait(false);
        Updates.Dispose();
        _markerHotkey?.Dispose();
        _gamepad?.Dispose();
        TrainerHotkeys.Dispose();
        _wait?.Dispose();
        LoggerFactory.Dispose();
        LogProvider.Dispose();
    }
}
