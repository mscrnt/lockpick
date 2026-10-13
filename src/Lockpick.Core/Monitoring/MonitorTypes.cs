using Lockpick.Core.Events;
using Lockpick.Core.Fingerprinting;
using Lockpick.Core.Processes;
using Lockpick.Core.Profiles;
using Lockpick.Core.State;

namespace Lockpick.Core.Monitoring;

public enum ConnectionState
{
    Stopped,

    /// <summary>No matching game process is running.</summary>
    Searching,

    /// <summary>A game process is running but its build is unknown or failed verification. Nothing is read.</summary>
    Unsupported,

    /// <summary>Attached to a verified build and polling.</summary>
    Attached,

    /// <summary>Playing back a recorded session instead of a live process.</summary>
    Replaying,
}

public sealed record MonitorStatus(
    ConnectionState State,
    string Message,
    DateTimeOffset Since,
    ProcessCandidate? Process = null,
    ExecutableFingerprint? Fingerprint = null,
    VersionProfile? Profile = null)
{
    public static MonitorStatus Stopped(DateTimeOffset now) => new(ConnectionState.Stopped, "Not monitoring", now);

    /// <summary>True when the two statuses describe the same situation (ignores the timestamp).</summary>
    public bool IsSameSituation(MonitorStatus other) =>
        State == other.State
        && Message == other.Message
        && Process?.ProcessId == other.Process?.ProcessId
        && Fingerprint?.Sha256 == other.Fingerprint?.Sha256;
}

public sealed record MonitorOptions
{
    /// <summary>Poll period while attached. 33 ms ≈ 30 Hz.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(1000.0 / 30.0);

    /// <summary>How often to look for the game process while not attached.</summary>
    public TimeSpan ScanInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Consecutive polls with no readable field before the session is dropped and re-attached.</summary>
    public int MaxConsecutiveFailedPolls { get; init; } = 30;

    /// <summary>Strategy for sleeping between ticks; platforms can supply a high-resolution timer.</summary>
    public IWaitStrategy WaitStrategy { get; init; } = DefaultWaitStrategy.Instance;
}

/// <summary>Blocks the monitor thread until the next tick or cancellation.</summary>
public interface IWaitStrategy
{
    void Wait(TimeSpan duration, CancellationToken cancellationToken);
}

/// <summary>Portable wait; resolution is limited by the OS timer (≈15.6 ms by default on Windows).</summary>
public sealed class DefaultWaitStrategy : IWaitStrategy
{
    public static readonly DefaultWaitStrategy Instance = new();

    public void Wait(TimeSpan duration, CancellationToken cancellationToken)
    {
        if (duration > TimeSpan.Zero)
        {
            cancellationToken.WaitHandle.WaitOne(duration);
        }
    }
}

/// <summary>Poll timing measurements, used by diagnostics and benchmarks.</summary>
public sealed record MonitorStatistics(
    long Polls,
    long FailedPolls,
    double LastPollMs,
    double AveragePollMs,
    double MaxPollMs,
    double MeasuredPollHz);

/// <summary>Anything that produces live or recorded game state for the UI.</summary>
public interface IGameStateSource : IAsyncDisposable
{
    /// <summary>Raised on the monitor thread when the connection situation changes.</summary>
    event EventHandler<MonitorStatus>? StatusChanged;

    /// <summary>Raised on the monitor thread when game-facing state differs from the previous snapshot.</summary>
    event EventHandler<GameSnapshot>? SnapshotChanged;

    /// <summary>
    /// Raised on the monitor thread after every poll (≈30 Hz), including polls whose game-facing state did not
    /// change. For diagnostics and capture only; handlers must be fast.
    /// </summary>
    event EventHandler<GameSnapshot>? Polled;

    /// <summary>Raised on the monitor thread for each detected event, in order.</summary>
    event EventHandler<GameEvent>? EventRaised;

    MonitorStatus Status { get; }

    GameSnapshot? Latest { get; }

    MonitorStatistics Statistics { get; }

    void Start();

    Task StopAsync();
}
