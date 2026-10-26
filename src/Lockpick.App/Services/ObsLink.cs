using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Lockpick.App.Services;

/// <summary>Lockpick's connection to OBS's WebSocket server and what it does there (Stream → OBS). Off by default.</summary>
public sealed record ObsSettings
{
    /// <summary>Connect to OBS's WebSocket server (instead of, or as well as, the OBS script).</summary>
    public bool Enabled { get; init; }

    /// <summary>The PC OBS runs on: this one, or another on the network (a streaming PC).</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>OBS's WebSocket port (its default).</summary>
    public int Port { get; init; } = 4455;

    /// <summary>Start recording with each run and stop when it ends (only a recording Lockpick started).</summary>
    public bool RecordWithRuns { get; init; }

    /// <summary>How long the recording goes on after a run ends, in seconds.</summary>
    public int StopDelaySeconds { get; init; } = 5;

    /// <summary>Recording chapters at the run's start, splits, deaths, fails and its end (Hybrid MP4, OBS 30.2+).</summary>
    public bool Chapters { get; init; } = true;

    public bool ReplayOnDeath { get; init; }

    public bool ReplayOnFail { get; init; }

    public bool ReplayOnBest { get; init; } = true;

    /// <summary>The scene shown while the game isn't in front, or null: no switching.</summary>
    public string? PauseScene { get; init; }
}

/// <summary>One of the run's moments, as the OBS script gets them in events.txt (<see cref="ObsEvents"/>).</summary>
public sealed record ObsMoment(string Kind, string Detail);

/// <summary>OBS's canvas and where OBS is, for setting up the Lockpick scene.</summary>
/// <param name="ThisPc">OBS runs on this PC (it can capture the game; the widgets' address is 127.0.0.1).</param>
/// <param name="LocalAddress">This PC's address as the PC running OBS reaches it (for the widgets on another PC).</param>
public sealed record ObsCanvas(int Width, int Height, bool ThisPc, string? LocalAddress);

/// <summary>A source of the Lockpick scene: its OBS name, input kind and settings, and where it goes on the canvas.</summary>
/// <param name="FillCanvas">Scaled to fit the whole canvas, keeping its shape, centred (the game's picture).</param>
public sealed record ObsSource(string Name, string Kind, IReadOnlyDictionary<string, object> Settings, double X, double Y, bool FillCanvas = false);

/// <summary>
/// The OBS script's automation (obs/lockpick.lua, #66) done from Lockpick through OBS's WebSocket server (#65): for OBS on
/// another PC, or without installing the script. Records with runs, adds chapters, saves the replay buffer on the
/// moments the player picked, switches to a "paused" scene while the game isn't in front, and sets up a Lockpick scene
/// with the widgets. Only OBS is ever told anything; the game and Lockpick's run are never changed from here. Reconnects
/// by itself; a refused password waits until it is changed. The password is passed in (kept in <c>SecretStore</c>).
/// </summary>
public sealed class ObsLink : IDisposable
{
    /// <summary>The scene "Set up OBS" makes (the script's button adds to the current scene instead).</summary>
    public const string SceneName = "Lockpick";

    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(5);

    private readonly Func<ObsSettings> _settings;
    private readonly Func<string?> _password;
    private readonly ILogger _log;
    private readonly Channel<object> _moments = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile ObsWebSocket? _obs;
    private int _run;                 // the attempt the next delayed stop belongs to
    private bool _startedRecording;   // the recording is Lockpick's (a recording the player started is left alone)
    private string? _sceneBeforePause;
    private readonly Dictionary<string, string> _noted = []; // what Note says, by subject (each once until the settings change)

    public ObsLink(Func<ObsSettings> settings, Func<string?> password, ILogger log)
    {
        _settings = settings;
        _password = password;
        _log = log;
        _loop = Task.Run(() => LoopAsync(_stop.Token));
    }

    /// <summary>"Off", "Connected to OBS 32.2.2 …", or why not.</summary>
    public string Status { get; private set; } = "Off";

    /// <summary>Something worth knowing about what OBS did with a request (chapters need Hybrid MP4), or empty.</summary>
    public string Note { get; private set; } = string.Empty;

    public bool Connected => _obs is not null;

    /// <summary>OBS runs on this PC (set on connecting).</summary>
    public bool ThisPc { get; private set; }

    /// <summary>OBS's scenes (for the "paused" scene), read on connecting and after setting up.</summary>
    public IReadOnlyList<string> Scenes { get; private set; } = [];

    public event EventHandler? StatusChanged;

    /// <summary>The connection settings or the password changed: connect again with them.</summary>
    public void Configure() =>
        Interlocked.Exchange(ref _changed, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    /// <summary>A run moment for OBS; dropped while OBS isn't connected (as the script starts at the end of events.txt).</summary>
    public void Post(ObsMoment moment)
    {
        if (_obs is not null)
        {
            _moments.Writer.TryWrite(moment);
        }
    }

    /// <summary>
    /// Makes (or completes) the Lockpick scene: the sources <paramref name="plan"/> lists for OBS's canvas. Sources that
    /// already exist (from an earlier set-up or the script) keep their place and size; only the widgets' address (with
    /// the current token) and the game's executable are updated. Returns what was done, for the player.
    /// </summary>
    public async Task<string> SetUpSceneAsync(Func<ObsCanvas, IReadOnlyList<ObsSource>> plan, CancellationToken cancel = default)
    {
        var obs = _obs ?? throw new ObsException("Lockpick isn't connected to OBS.");
        var video = await obs.RequestAsync("GetVideoSettings", cancel: cancel).ConfigureAwait(false);
        var settings = _settings();
        var canvas = new ObsCanvas(video.GetProperty("baseWidth").GetInt32(), video.GetProperty("baseHeight").GetInt32(), ThisPc, LocalAddressFor(settings.Host, settings.Port));
        var sources = plan(canvas);

        if (!(await SceneNamesAsync(obs, cancel).ConfigureAwait(false)).Contains(SceneName))
        {
            await obs.RequestAsync("CreateScene", new { sceneName = SceneName }, cancel).ConfigureAwait(false);
        }

        var (added, reused, kept) = (0, 0, 0); // new sources; ones from other scenes (the script's); ones already here
        foreach (var source in sources)
        {
            try
            {
                var created = await obs.RequestAsync("CreateInput", new { sceneName = SceneName, inputName = source.Name, inputKind = source.Kind, inputSettings = source.Settings }, cancel).ConfigureAwait(false);
                await PlaceAsync(obs, created.GetProperty("sceneItemId").GetInt32(), source, canvas, cancel).ConfigureAwait(false);
                added++;
            }
            catch (ObsException ex) when (ex.Code == ObsWebSocket.AlreadyExists)
            {
                // Only the address (a new token or port) and the game's executable: sizes the player changed stay.
                var refresh = source.Settings.Where(s => s.Key is "url" or "window").ToDictionary();
                if (refresh.Count > 0)
                {
                    await obs.RequestAsync("SetInputSettings", new { inputName = source.Name, inputSettings = refresh, overlay = true }, cancel).ConfigureAwait(false);
                }

                try
                {
                    await obs.RequestAsync("GetSceneItemId", new { sceneName = SceneName, sourceName = source.Name }, cancel).ConfigureAwait(false);
                }
                catch (ObsException missing) when (missing.Code == ObsWebSocket.NotFound)
                {
                    var item = await obs.RequestAsync("CreateSceneItem", new { sceneName = SceneName, sourceName = source.Name }, cancel).ConfigureAwait(false);
                    await PlaceAsync(obs, item.GetProperty("sceneItemId").GetInt32(), source, canvas, cancel).ConfigureAwait(false);
                    reused++;
                    continue;
                }

                kept++;
            }
        }

        Scenes = await SceneNamesAsync(obs, cancel).ConfigureAwait(false);
        StatusChanged?.Invoke(this, EventArgs.Empty);
        _log.LogInformation("OBS scene set up: {Added} sources added, {Reused} from other scenes, {Kept} already there", added, reused, kept);
        var parts = new[] { (added, "new"), (reused, "from your other scenes"), (kept, "already in it, left where you put them") }
            .Where(p => p.Item1 > 0).Select(p => $"{p.Item1} {p.Item2}");
        return $"The \"{SceneName}\" scene in OBS has its {sources.Count} sources ({string.Join(", ", parts)}); their addresses are up to date. Move and resize them as you like.";
    }

    public void Dispose()
    {
        _stop.Cancel();
        _moments.Writer.TryComplete();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
    }

    private async Task LoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var changed = _changed.Task;
            var settings = _settings();
            if (!settings.Enabled)
            {
                SetStatus("Off");
                await WaitAsync(changed, Timeout.InfiniteTimeSpan, stop).ConfigureAwait(false);
                continue;
            }

            ObsWebSocket obs;
            try
            {
                obs = await ObsWebSocket.ConnectAsync(settings.Host, settings.Port, _password(), stop).ConfigureAwait(false);
            }
            catch (ObsException ex)
            {
                SetStatus(ex.Message);
                // A refused password stays refused: wait for a new one instead of asking OBS again and again.
                var lasting = ex.Code is ObsWebSocket.PasswordRefused or ObsWebSocket.TooOld;
                await WaitAsync(changed, lasting ? Timeout.InfiniteTimeSpan : Retry, stop).ConfigureAwait(false);
                continue;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                ThisPc = IsThisPc(settings.Host, settings.Port);
                Scenes = await SceneNamesAsync(obs, stop).ConfigureAwait(false);
                _obs = obs;
                _log.LogInformation("Connected to OBS {Version} (WebSocket {WebSocket}) at {Host}:{Port}", obs.ObsVersion, obs.WebSocketVersion, settings.Host, settings.Port);
                SetStatus($"Connected to OBS {obs.ObsVersion} (WebSocket {obs.WebSocketVersion}){(ThisPc ? " on this PC" : $" at {settings.Host}")}.");
                while (true)
                {
                    var ready = _moments.Reader.WaitToReadAsync(stop).AsTask();
                    if (await Task.WhenAny(ready, obs.Closed, changed).ConfigureAwait(false) != ready || !await ready.ConfigureAwait(false))
                    {
                        break;
                    }

                    while (_moments.Reader.TryRead(out var moment))
                    {
                        await HandleAsync(obs, moment, stop).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
            }
            catch (ObsException ex)
            {
                _log.LogWarning("OBS: {Message}", ex.Message);
            }
            finally
            {
                _obs = null;
                await obs.DisposeAsync().ConfigureAwait(false);
            }

            if (!stop.IsCancellationRequested && !changed.IsCompleted)
            {
                SetStatus($"OBS closed the connection; trying again (OBS at {settings.Host}:{settings.Port}).");
                await WaitAsync(_changed.Task, Retry, stop).ConfigureAwait(false);
            }
        }
    }

    /// <summary>As the OBS script's on_event: each moment's chapter, recording, replay buffer and scene.</summary>
    private async Task HandleAsync(ObsWebSocket obs, object moment, CancellationToken stop)
    {
        var options = _settings();
        try
        {
            switch (moment)
            {
                case StopRecording later:
                    if (later.Run == _run && _startedRecording)
                    {
                        _startedRecording = false;
                        await QuietlyAsync(obs, "StopRecord", ObsWebSocket.OutputNotRunning, stop).ConfigureAwait(false);
                    }

                    break;
                case ObsMoment { Kind: "run-start" } m:
                    _run++; // a stop still waiting from the attempt before isn't this one's
                    if (options.RecordWithRuns && !_startedRecording && !await RecordingAsync(obs, stop).ConfigureAwait(false))
                    {
                        await obs.RequestAsync("StartRecord", cancel: stop).ConfigureAwait(false);
                        _startedRecording = true;
                        // StartRecord answers before the recording runs, and a chapter asked for until then is refused
                        // (seen in OBS 32.2.2: the run's chapter went missing): wait for it, a few seconds at most.
                        for (var i = 0; i < 30 && !await RecordingAsync(obs, stop).ConfigureAwait(false); i++)
                        {
                            await Task.Delay(100, stop).ConfigureAwait(false);
                        }
                    }

                    await ChapterAsync(obs, options, "Run: " + m.Detail, stop).ConfigureAwait(false);
                    break;
                case ObsMoment { Kind: "split" } m:
                    await ChapterAsync(obs, options, m.Detail, stop).ConfigureAwait(false);
                    break;
                case ObsMoment { Kind: "death" }:
                    await ChapterAsync(obs, options, "Death", stop).ConfigureAwait(false);
                    await ReplayAsync(obs, options.ReplayOnDeath, stop).ConfigureAwait(false);
                    break;
                case ObsMoment { Kind: "challenge-fail" } m:
                    await ChapterAsync(obs, options, "Challenge failed: " + m.Detail, stop).ConfigureAwait(false);
                    await ReplayAsync(obs, options.ReplayOnFail, stop).ConfigureAwait(false);
                    break;
                case ObsMoment { Kind: "new-best" } m:
                    await ChapterAsync(obs, options, "New personal best: " + m.Detail, stop).ConfigureAwait(false);
                    await ReplayAsync(obs, options.ReplayOnBest, stop).ConfigureAwait(false);
                    break;
                case ObsMoment { Kind: "finished" } m:
                    await ChapterAsync(obs, options, "Run finished: " + m.Detail, stop).ConfigureAwait(false);
                    break;
                case ObsMoment { Kind: "run-end" } m:
                    await ChapterAsync(obs, options, m.Detail, stop).ConfigureAwait(false);
                    if (_startedRecording)
                    {
                        var run = _run;
                        _ = Task.Delay(TimeSpan.FromSeconds(Math.Max(0, options.StopDelaySeconds)), stop)
                            .ContinueWith(_ => _moments.Writer.TryWrite(new StopRecording(run)), TaskScheduler.Default);
                    }

                    break;
                case ObsMoment { Kind: "game-inactive" }:
                    if (options.PauseScene is { Length: > 0 } pause)
                    {
                        var now = await CurrentSceneAsync(obs, stop).ConfigureAwait(false);
                        if (now != pause)
                        {
                            _sceneBeforePause = now;
                            await obs.RequestAsync("SetCurrentProgramScene", new { sceneName = pause }, stop).ConfigureAwait(false);
                        }
                    }

                    break;
                case ObsMoment { Kind: "game-active" }:
                    if (_sceneBeforePause is { } before)
                    {
                        _sceneBeforePause = null;
                        await obs.RequestAsync("SetCurrentProgramScene", new { sceneName = before }, stop).ConfigureAwait(false);
                    }

                    break;
            }
        }
        catch (ObsException ex) when (obs.Closed.IsCompleted is false)
        {
            _log.LogWarning("OBS didn't do what {Moment} asked: {Message}", moment, ex.Message);
        }
    }

    private async Task ChapterAsync(ObsWebSocket obs, ObsSettings options, string name, CancellationToken stop)
    {
        if (!options.Chapters)
        {
            return;
        }

        try
        {
            await obs.RequestAsync("CreateRecordChapter", new { chapterName = name }, stop).ConfigureAwait(false);
        }
        catch (ObsException ex) when (ex.Code == ObsWebSocket.OutputNotRunning)
        {
            // Not recording: nothing to mark (the script checks first; asking is the same here).
        }
        catch (ObsException ex) when (!obs.Closed.IsCompleted)
        {
            // OBS refuses chapters for formats other than Hybrid MP4, and before 30.2 doesn't know the request (seen
            // with OBS 29.1.3): say so once, keep the rest working.
            NoteOnce("chapters", ex, "Chapter markers need OBS 30.2 or later recording to Hybrid MP4 (OBS: Settings → Output → Recording Format).");
        }
    }

    private async Task ReplayAsync(ObsWebSocket obs, bool wanted, CancellationToken stop)
    {
        if (!wanted)
        {
            return;
        }

        try
        {
            await obs.RequestAsync("SaveReplayBuffer", cancel: stop).ConfigureAwait(false);
            Unnote("replay-stopped");
        }
        catch (ObsException ex) when (ex.Code == ObsWebSocket.OutputNotRunning)
        {
            // Turned on in OBS's settings but not started: nothing is saved, so say so (once).
            NoteOnce("replay-stopped", ex, "OBS's replay buffer isn't running, so nothing was saved: press Start Replay Buffer in OBS (Controls).");
        }
        catch (ObsException ex) when (!obs.Closed.IsCompleted)
        {
            // The replay buffer turned off in OBS's output settings ("not available", seen with OBS 29.1.3).
            NoteOnce("replay", ex, "OBS's replay buffer is off: OBS → Settings → Output → Replay Buffer → Enable, then Start Replay Buffer.");
        }
    }

    private void NoteOnce(string what, ObsException ex, string note)
    {
        if (!_noted.ContainsKey(what))
        {
            _noted[what] = note;
            _log.LogInformation("OBS refused {What}: {Message}", what, ex.Message);
            Note = string.Join(Environment.NewLine, _noted.Values);
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A note that no longer holds (the replay buffer was started after all).</summary>
    private void Unnote(string what)
    {
        if (_noted.Remove(what))
        {
            Note = string.Join(Environment.NewLine, _noted.Values);
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static async Task QuietlyAsync(ObsWebSocket obs, string request, int harmless, CancellationToken stop)
    {
        try
        {
            await obs.RequestAsync(request, cancel: stop).ConfigureAwait(false);
        }
        catch (ObsException ex) when (ex.Code == harmless)
        {
        }
    }

    private static async Task<bool> RecordingAsync(ObsWebSocket obs, CancellationToken stop) =>
        (await obs.RequestAsync("GetRecordStatus", cancel: stop).ConfigureAwait(false)).GetProperty("outputActive").GetBoolean();

    private static async Task<string?> CurrentSceneAsync(ObsWebSocket obs, CancellationToken stop)
    {
        var scene = await obs.RequestAsync("GetCurrentProgramScene", cancel: stop).ConfigureAwait(false);
        // sceneName since WebSocket 5.5; currentProgramSceneName before (still sent, deprecated).
        return scene.TryGetProperty("sceneName", out var name) || scene.TryGetProperty("currentProgramSceneName", out name) ? name.GetString() : null;
    }

    private static async Task<IReadOnlyList<string>> SceneNamesAsync(ObsWebSocket obs, CancellationToken cancel)
    {
        var list = await obs.RequestAsync("GetSceneList", cancel: cancel).ConfigureAwait(false);
        // OBS lists scenes bottom to top; shown top to bottom as in its Scenes dock.
        return [.. list.GetProperty("scenes").EnumerateArray().Select(s => s.GetProperty("sceneName").GetString() ?? string.Empty).Where(n => n.Length > 0).Reverse()];
    }

    private static Task<JsonElement> PlaceAsync(ObsWebSocket obs, int item, ObsSource source, ObsCanvas canvas, CancellationToken cancel) =>
        obs.RequestAsync("SetSceneItemTransform", new
        {
            sceneName = SceneName,
            sceneItemId = item,
            sceneItemTransform = source.FillCanvas
                ? new Dictionary<string, object> { ["positionX"] = 0, ["positionY"] = 0, ["boundsType"] = "OBS_BOUNDS_SCALE_INNER", ["boundsAlignment"] = 0, ["boundsWidth"] = canvas.Width, ["boundsHeight"] = canvas.Height }
                : new Dictionary<string, object> { ["positionX"] = source.X, ["positionY"] = source.Y },
        }, cancel);

    /// <summary>This PC's address on the way to <paramref name="host"/> (no packet is sent: a UDP socket only picks the route).</summary>
    internal static string? LocalAddressFor(string host, int port)
    {
        try
        {
            using var socket = new Socket(SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(host, port);
            return socket.LocalEndPoint is IPEndPoint { Address: var address } ? (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString() : null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    /// <summary>OBS's host is this PC: a loopback address, or one of this PC's own (the route to it starts and ends here).</summary>
    internal static bool IsThisPc(string host, int port)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip)))
        {
            return true;
        }

        try
        {
            var local = LocalAddressFor(host, port);
            return Dns.GetHostAddresses(host).Any(a => IPAddress.IsLoopback(a) || (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).ToString() == local);
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private async Task WaitAsync(Task changed, TimeSpan delay, CancellationToken stop)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop);
        await Task.WhenAny(changed, Task.Delay(delay, wait.Token)).ConfigureAwait(false);
        await wait.CancelAsync().ConfigureAwait(false); // the delay's timer goes with it

        if (changed.IsCompleted)
        {
            Note = string.Empty;
            _noted.Clear();
        }
    }

    private void SetStatus(string status)
    {
        if (status != Status)
        {
            Status = status;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The recording's stop, <see cref="ObsSettings.StopDelaySeconds"/> after the run it belongs to ended.</summary>
    private sealed record StopRecording(int Run);
}
