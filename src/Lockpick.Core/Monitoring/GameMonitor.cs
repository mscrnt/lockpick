using System.Diagnostics;
using Lockpick.Core.Adapters;
using Lockpick.Core.Events;
using Lockpick.Core.Fingerprinting;
using Lockpick.Core.Processes;
using Lockpick.Core.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lockpick.Core.Monitoring;

/// <summary>
/// Finds, verifies and polls one game. Runs on a dedicated background thread; <see cref="Tick"/> is
/// public so tests can drive the state machine deterministically without timers.
/// </summary>
public sealed class GameMonitor : IGameStateSource
{
    private readonly IGameAdapter _adapter;
    private readonly IProcessHost _host;
    private readonly IFingerprintProvider _fingerprints;
    private readonly MonitorOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly EventDetector _detector = new();

    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private IAttachedProcess? _process;
    private IGameSession? _session;
    private DateTimeOffset _nextScan = DateTimeOffset.MinValue;
    private long _sequence;
    private int _consecutiveFailures;

    private long _polls;
    private long _failedPolls;
    private double _lastPollMs;
    private double _totalPollMs;
    private double _maxPollMs;
    private long _firstPollTimestamp;
    private long _lastPollTimestamp;

    public GameMonitor(
        IGameAdapter adapter,
        IProcessHost host,
        IFingerprintProvider fingerprints,
        MonitorOptions? options = null,
        ILogger<GameMonitor>? logger = null,
        TimeProvider? time = null)
    {
        _adapter = adapter;
        _host = host;
        _fingerprints = fingerprints;
        _options = options ?? new MonitorOptions();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        Status = MonitorStatus.Stopped(_time.GetUtcNow());
    }

    public event EventHandler<MonitorStatus>? StatusChanged;

    public event EventHandler<GameSnapshot>? SnapshotChanged;

    public event EventHandler<GameSnapshot>? Polled;

    public event EventHandler<GameEvent>? EventRaised;

    public MonitorStatus Status { get; private set; }

    public GameSnapshot? Latest { get; private set; }

    public bool IsAttached => _session is not null;

    public MonitorStatistics Statistics
    {
        get
        {
            lock (_gate)
            {
                // Measured between the first and last poll, so time spent detached doesn't dilute the rate.
                var elapsed = _firstPollTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(_firstPollTimestamp, _lastPollTimestamp).TotalSeconds;
                return new MonitorStatistics(
                    _polls,
                    _failedPolls,
                    _lastPollMs,
                    _polls == 0 ? 0 : _totalPollMs / _polls,
                    _maxPollMs,
                    elapsed > 0 ? (_polls - 1) / elapsed : 0);
            }
        }
    }

    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SetStatus(ConnectionState.Searching, $"Waiting for {_adapter.DisplayName}");
        _thread = new Thread(() => Run(token))
        {
            IsBackground = true,
            Name = $"Lockpick monitor ({_adapter.GameId})",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    public async Task StopAsync()
    {
        if (_thread is null || _cts is null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        var thread = _thread;
        await Task.Run(() => thread.Join()).ConfigureAwait(false);
        _thread = null;
        _cts.Dispose();
        _cts = null;
        Detach("Monitoring stopped");
        SetStatus(ConnectionState.Stopped, "Not monitoring");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>Performs one scan or poll step, depending on whether a session is attached.</summary>
    public void Tick()
    {
        var now = _time.GetUtcNow();
        if (_session is null)
        {
            if (now >= _nextScan)
            {
                _nextScan = now + _options.ScanInterval;
                Scan();
            }

            return;
        }

        Poll(now);
    }

    private void Run(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                // Never let an adapter or platform bug kill the monitor; drop the session and rescan.
                _logger.LogError(ex, "Monitor tick failed");
                Detach($"Internal error: {ex.Message}");
            }

            var interval = _session is null ? _options.ScanInterval : _options.PollInterval;
            _options.WaitStrategy.Wait(interval - Stopwatch.GetElapsedTime(started), token);
        }
    }

    private void Scan()
    {
        IReadOnlyList<ProcessCandidate> candidates;
        try
        {
            candidates = _host.FindProcesses(_adapter.ExecutableNames);
        }
        catch (Exception ex) when (ex is ProcessAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogWarning(ex, "Process enumeration failed");
            SetStatus(ConnectionState.Searching, $"Process enumeration failed: {ex.Message}");
            return;
        }

        if (candidates.Count == 0)
        {
            SetStatus(ConnectionState.Searching, $"Waiting for {_adapter.DisplayName}");
            return;
        }

        MonitorStatus? rejection = null;
        foreach (var candidate in candidates)
        {
            rejection = TryAttach(candidate);
            if (rejection is null)
            {
                return;
            }
        }

        SetStatus(rejection!);
    }

    /// <summary>Attempts to attach; returns null on success or the status explaining the rejection.</summary>
    private MonitorStatus? TryAttach(ProcessCandidate candidate)
    {
        var now = _time.GetUtcNow();
        if (string.IsNullOrEmpty(candidate.ExecutablePath))
        {
            return new MonitorStatus(ConnectionState.Unsupported, "Cannot read the game's executable path (try running Lockpick with the same privileges as the game).", now, candidate);
        }

        ExecutableFingerprint fingerprint;
        try
        {
            fingerprint = _fingerprints.Compute(candidate.ExecutablePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new MonitorStatus(ConnectionState.Unsupported, $"Cannot fingerprint executable: {ex.Message}", now, candidate);
        }

        var match = _adapter.Identify(fingerprint);
        if (match.Profile is null)
        {
            return new MonitorStatus(ConnectionState.Unsupported, match.Reason, now, candidate, fingerprint);
        }

        IAttachedProcess? process = null;
        try
        {
            process = _host.OpenReadOnly(candidate);
            var session = _adapter.Attach(process, match.Profile);
            _process = process;
            _session = session;
            _sequence = 0;
            _consecutiveFailures = 0;
            Latest = null;
            _detector.Reset();
            ResetStatistics();
            _logger.LogInformation(
                "Attached to PID {ProcessId} ({Path}) using profile {Profile}, SHA-256 {Sha256}",
                candidate.ProcessId,
                candidate.ExecutablePath,
                match.Profile.Id,
                fingerprint.Sha256);
            SetStatus(new MonitorStatus(ConnectionState.Attached, $"Attached — {match.Profile.DisplayName}", now, candidate, fingerprint, match.Profile));
            Raise(new GameStarted(now, _adapter.DisplayName, match.Profile.Id, candidate.ProcessId));
            Poll(now);
            return null;
        }
        catch (Exception ex) when (ex is ProcessAccessException or AttachRejectedException)
        {
            process?.Dispose();
            _logger.LogWarning("Attach to PID {ProcessId} rejected: {Reason}", candidate.ProcessId, ex.Message);
            return new MonitorStatus(ConnectionState.Unsupported, ex.Message, now, candidate, fingerprint, match.Profile);
        }
    }

    private void Poll(DateTimeOffset now)
    {
        var process = _process!;
        var session = _session!;
        if (process.HasExited)
        {
            Detach("Game process exited");
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var snapshot = session.Read(++_sequence, now);
        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        var anyValid = snapshot.InGameplay.IsValid || snapshot.Location.IsValid || snapshot.Health.IsValid || snapshot.GameTime.IsValid;
        RecordPoll(elapsedMs, failed: !anyValid);
        if (!anyValid)
        {
            if (process.HasExited)
            {
                Detach("Game process exited");
                return;
            }

            if (++_consecutiveFailures >= _options.MaxConsecutiveFailedPolls)
            {
                Detach($"Memory reads failed {_consecutiveFailures} times in a row");
                return;
            }
        }
        else
        {
            _consecutiveFailures = 0;
        }

        var previous = Latest;
        Latest = snapshot;
        Polled?.Invoke(this, snapshot);
        if (!snapshot.HasSameStateAs(previous))
        {
            SnapshotChanged?.Invoke(this, snapshot);
        }

        foreach (var gameEvent in _detector.Detect(previous, snapshot))
        {
            Raise(gameEvent);
        }
    }

    private void Detach(string reason)
    {
        if (_session is null && _process is null)
        {
            return;
        }

        _process?.Dispose();
        _process = null;
        _session = null;
        _nextScan = DateTimeOffset.MinValue;
        _logger.LogInformation("Detached: {Reason}", reason);
        Raise(new GameStopped(_time.GetUtcNow(), reason));
        SetStatus(ConnectionState.Searching, $"{reason}. Waiting for {_adapter.DisplayName}");
    }

    private void Raise(GameEvent gameEvent)
    {
        if (gameEvent is GameStarted or GameStopped)
        {
            _logger.LogInformation("Event {Event}", gameEvent.Describe());
        }
        else
        {
            _logger.LogDebug("Event {Event}", gameEvent.Describe());
        }

        EventRaised?.Invoke(this, gameEvent);
    }

    private void SetStatus(ConnectionState state, string message) =>
        SetStatus(new MonitorStatus(state, message, _time.GetUtcNow()));

    private void SetStatus(MonitorStatus status)
    {
        if (Status.IsSameSituation(status))
        {
            return;
        }

        Status = status;
        if (status.State == ConnectionState.Unsupported)
        {
            _logger.LogWarning("Unsupported: {Message} (SHA-256 {Sha256})", status.Message, status.Fingerprint?.Sha256 ?? "n/a");
        }

        StatusChanged?.Invoke(this, status);
    }

    private void RecordPoll(double elapsedMs, bool failed)
    {
        lock (_gate)
        {
            _lastPollTimestamp = Stopwatch.GetTimestamp();
            if (_polls == 0)
            {
                _firstPollTimestamp = _lastPollTimestamp;
            }

            _polls++;
            _lastPollMs = elapsedMs;
            _totalPollMs += elapsedMs;
            _maxPollMs = Math.Max(_maxPollMs, elapsedMs);
            if (failed)
            {
                _failedPolls++;
            }
        }
    }

    private void ResetStatistics()
    {
        lock (_gate)
        {
            _polls = _failedPolls = 0;
            _lastPollMs = _totalPollMs = _maxPollMs = 0;
            _firstPollTimestamp = _lastPollTimestamp = 0;
        }
    }
}
