using System.Globalization;
using System.Text.Json;
using Lockpick.Core.Events;
using Lockpick.Core.Runs;
using Microsoft.Extensions.Logging;

namespace Lockpick.App.Services;

/// <summary>
/// What Lockpick's OBS script (obs/lockpick.lua, #66) reads, since OBS's Lua has no network: <c>connection.json</c>
/// (the API's address and token, so the script adds the browser sources without any copy-paste) and
/// <c>events.txt</c>, one line per moment — <c>time-ms TAB kind TAB detail</c> — for recording with runs, chapters,
/// the replay buffer and the "paused" scene. The events file starts empty each time Lockpick starts.
/// Kinds: run-start, split, run-end, finished, new-best, death, challenge-fail, game-active, game-inactive.
/// The other way, the script's OBS hotkeys append to <c>commands.txt</c> (<c>time-ms TAB action</c>): only the run keys,
/// try again, the overlay and map keys and the capture marker are taken — never a cheat.
/// </summary>
public sealed class ObsEvents : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly AppServices _services;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private readonly Timer _timer;
    private bool? _gameActive;
    private long _commandsRead;
    private bool _websocketWritten;

    /// <summary>What an OBS hotkey may ask for (the same actions as Lockpick's own keys; no cheats).</summary>
    private static readonly HashSet<string> Commands =
    [
        TrainerHotkeys.RunSplit, TrainerHotkeys.RunUndo, TrainerHotkeys.RunSkip, TrainerHotkeys.RunReset,
        TrainerHotkeys.ChallengeRetry, TrainerHotkeys.ToggleOverlay, TrainerHotkeys.NextMapView, TrainerHotkeys.MapZoom, "marker",
        TrainerHotkeys.OverlayNextWidget, TrainerHotkeys.OverlayBigger, TrainerHotkeys.OverlaySmaller,
    ];

    public ObsEvents(AppServices services, ILogger log)
    {
        _services = services;
        _log = log;
        Directory = Path.Combine(AppPaths.DataRoot, "obs");
        Write(truncate: true, null);
        try
        {
            File.WriteAllText(Path.Combine(Directory, "commands.txt"), string.Empty); // what OBS asked before now is done
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug("The OBS commands file couldn't be emptied: {Message}", ex.Message);
        }

        WriteConnection();
        services.Obs.StatusChanged += OnObsStatus;
        services.Runs.EventRaised += OnRunEvent;
        services.Runs.AttemptEnded += OnAttemptEnded;
        services.Runs.Died += OnDied;
        services.Challenges.EventRaised += OnChallengeEvent;
        _timer = new Timer(_ => { CheckGame(); ReadCommands(); }, null, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(200));
    }

    /// <summary>Where the files are (the script finds them in %LOCALAPPDATA%\Lockpick\obs).</summary>
    public string Directory { get; }

    /// <summary>Each moment as it is written to events.txt (Lockpick's WebSocket link to OBS acts on the same ones).</summary>
    public event EventHandler<ObsMoment>? Happened;

    /// <summary>Writes connection.json again (the API was switched on or off, or its port or token changed).</summary>
    public void WriteConnection()
    {
        var settings = _services.Settings.Current;
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var connection = new
            {
                api = settings.ApiEnabled,
                port = settings.ApiPort,
                token = settings.ApiEnabled ? settings.ApiToken : null,
                version = _services.AppVersion,
                events = Path.Combine(Directory, "events.txt"),
                livesplit = _services.LiveSplitEngine.Available,
                // Lockpick drives this PC's OBS through its WebSocket: the script leaves recording, chapters, the replay
                // buffer and the scene to it (its hotkeys still work), so nothing is done twice.
                websocket = _services.Obs.Connected && _services.Obs.ThisPc,
                executable = _services.GameExecutableName(),
            };
            File.WriteAllText(Path.Combine(Directory, "connection.json"), JsonSerializer.Serialize(connection, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("The OBS connection file couldn't be written: {Message}", ex.Message);
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _services.Obs.StatusChanged -= OnObsStatus;
        _services.Runs.EventRaised -= OnRunEvent;
        _services.Runs.AttemptEnded -= OnAttemptEnded;
        _services.Runs.Died -= OnDied;
        _services.Challenges.EventRaised -= OnChallengeEvent;
    }

    private void OnObsStatus(object? sender, EventArgs e)
    {
        var driving = _services.Obs.Connected && _services.Obs.ThisPc;
        if (driving != _websocketWritten)
        {
            _websocketWritten = driving;
            WriteConnection();
        }
    }

    private void OnRunEvent(object? sender, GameEvent e)
    {
        switch (e)
        {
            case RunStarted started:
                Add("run-start", $"{started.SplitSet}, attempt {started.Attempt}");
                break;
            case SplitReached split:
                Add("split", split.Describe());
                break;
            case RunEnded ended:
                Add("run-end", ended.Describe());
                break;
        }
    }

    private void OnAttemptEnded(object? sender, RunRecord record)
    {
        if (record.Result != RunResult.Finished)
        {
            return;
        }

        var best = _services.RunLibrary.Find(record.SetId) is { } set ? _services.RunLibrary.Comparison(set).PersonalBest : [];
        var personalBest = record.IsClean && best.Count > 0 && best[^1] == record.GameTime;
        Add(personalBest ? "new-best" : "finished", $"{record.Category} in {record.GameTime:h\\:mm\\:ss\\.f}");
    }

    private void OnDied(object? sender, EventArgs e) => Add("death", $"death {_services.Runs.Deaths} this session");

    private void OnChallengeEvent(object? sender, GameEvent e)
    {
        if (e is ChallengeFailed failed)
        {
            Add("challenge-fail", $"{failed.Challenge}: {failed.Reason}");
        }
    }

    /// <summary>The game in front and showing, or not (minimized, or another window in front): for the "paused" scene.</summary>
    private void CheckGame()
    {
        if (_services.GameWindow() is not { } game)
        {
            _gameActive = null;
            return;
        }

        var active = game.Foreground && !game.Minimized && game.Visible;
        if (active != _gameActive)
        {
            _gameActive = active;
            Add(active ? "game-active" : "game-inactive", string.Empty);
        }
    }

    /// <summary>The OBS hotkeys pressed since the last look (commands.txt), each done as Lockpick's own key would.</summary>
    private void ReadCommands()
    {
        string text;
        try
        {
            using var stream = new FileStream(Path.Combine(Directory, "commands.txt"), FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < _commandsRead)
            {
                _commandsRead = 0; // emptied
            }

            if (stream.Length == _commandsRead)
            {
                return;
            }

            stream.Seek(_commandsRead, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var complete = text.LastIndexOf('\n');
        if (complete < 0)
        {
            return; // a line still being written
        }

        _commandsRead += System.Text.Encoding.UTF8.GetByteCount(text[..(complete + 1)]);
        foreach (var line in text[..complete].Split('\n'))
        {
            var parts = line.Trim().Split('\t');
            var action = parts.Length > 1 ? parts[1] : parts[0];
            if (!Commands.Contains(action))
            {
                continue;
            }

            _log.LogInformation("OBS hotkey: {Action}", action);
            if (action == "marker")
            {
                _services.RequestMarker();
            }
            else
            {
                _services.TrainerHotkeys.Fire(action);
            }
        }
    }

    private void Add(string kind, string detail)
    {
        detail = detail.Replace('\t', ' ').Replace('\n', ' ');
        Write(truncate: false, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}\t{kind}\t{detail}");
        Happened?.Invoke(this, new ObsMoment(kind, detail));
    }

    private void Write(bool truncate, string? line)
    {
        lock (_gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var path = Path.Combine(Directory, "events.txt");
                if (truncate)
                {
                    File.WriteAllText(path, string.Empty);
                }

                if (line is not null)
                {
                    File.AppendAllText(path, line + "\n");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogDebug("The OBS events file couldn't be written: {Message}", ex.Message);
            }
        }
    }
}
