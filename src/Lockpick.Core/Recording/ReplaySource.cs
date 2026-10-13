using Lockpick.Core.Events;
using Lockpick.Core.Monitoring;
using Lockpick.Core.State;

namespace Lockpick.Core.Recording;

/// <summary>Plays a recording back through the same interface as the live monitor, preserving timing.</summary>
public sealed class ReplaySource(RecordingHeader header, IReadOnlyList<RecordedFrame> frames, double speed = 1.0, bool loop = false) : IGameStateSource
{
    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private long _played;

    public event EventHandler<MonitorStatus>? StatusChanged;

    public event EventHandler<GameSnapshot>? SnapshotChanged;

    public event EventHandler<GameSnapshot>? Polled;

    public event EventHandler<GameEvent>? EventRaised;

    public MonitorStatus Status { get; private set; } = MonitorStatus.Stopped(DateTimeOffset.UtcNow);

    public GameSnapshot? Latest { get; private set; }

    public MonitorStatistics Statistics => new(Interlocked.Read(ref _played), 0, 0, 0, 0, 0);

    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _thread = new Thread(() => Run(token)) { IsBackground = true, Name = "Lockpick replay" };
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
        SetStatus(MonitorStatus.Stopped(DateTimeOffset.UtcNow));
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void Run(CancellationToken token)
    {
        do
        {
            SetStatus(new MonitorStatus(ConnectionState.Replaying, $"Replaying {header.GameId} recording ({frames.Count} frames)", DateTimeOffset.UtcNow));
            EventRaised?.Invoke(this, new GameStarted(DateTimeOffset.UtcNow, $"{header.GameId} (replay)", header.ProfileId ?? "replay", 0));
            GameSnapshot? previous = null;
            var detector = new EventDetector();
            double lastOffset = 0;
            foreach (var frame in frames)
            {
                var wait = TimeSpan.FromMilliseconds(Math.Max(0, frame.OffsetMs - lastOffset) / speed);
                lastOffset = frame.OffsetMs;
                if (token.WaitHandle.WaitOne(wait))
                {
                    return;
                }

                // Detect on the recorded timestamps (the detector compares game time with wall time, which must not
                // be distorted by the playback speed), then re-stamp so the UI shows live-looking times.
                var now = DateTimeOffset.UtcNow;
                var snapshot = frame.Snapshot with { CapturedAt = now };
                Latest = snapshot;
                Interlocked.Increment(ref _played);
                Polled?.Invoke(this, snapshot);
                SnapshotChanged?.Invoke(this, snapshot);
                foreach (var gameEvent in detector.Detect(previous, frame.Snapshot))
                {
                    EventRaised?.Invoke(this, gameEvent with { Timestamp = now });
                }

                previous = frame.Snapshot;
            }

            EventRaised?.Invoke(this, new GameStopped(DateTimeOffset.UtcNow, "Replay finished"));
        }
        while (loop && !token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1)));

        SetStatus(new MonitorStatus(ConnectionState.Replaying, "Replay finished", DateTimeOffset.UtcNow));
    }

    private void SetStatus(MonitorStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }
}
