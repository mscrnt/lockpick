using System.Security.Cryptography;
using Lockpick.Core.Adapters;
using Lockpick.Core.Monitoring;
using Lockpick.Core.Trainer;

namespace Lockpick.Api;

/// <summary>How the API listens and what it allows. Off unless the player turns it on.</summary>
public sealed record ApiOptions
{
    public const int DefaultPort = 1996;

    /// <summary>Listen on every network interface (other devices on the network) instead of this PC only.</summary>
    public bool AllowNetwork { get; init; }

    public int Port { get; init; } = DefaultPort;

    /// <summary>Every request must carry it (header or <c>?token=</c>).</summary>
    public required string Token { get; init; }

    /// <summary>Allow trainer commands (still only while the player has Trainer Mode on).</summary>
    public bool AllowTrainer { get; init; }

    /// <summary>A new random token (URL-safe, 256 bits).</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Capture control, provided by the app (captures belong to its Debug page).</summary>
public interface ICaptureControl
{
    bool IsCapturing { get; }

    /// <summary>Starts a capture; returns null on success or why it couldn't start.</summary>
    Task<string?> StartAsync();

    /// <summary>Stops and saves the capture; returns the saved file, or null when none was running.</summary>
    Task<string?> StopAsync();

    /// <summary>Adds a marker to the running capture; returns its number, or null when no capture is running.</summary>
    Task<int?> MarkAsync(string? note);
}

/// <summary>What the API reads and drives. Everything game-specific comes through the adapter.</summary>
public sealed record ApiHost(
    IGameStateSource Source,
    IGameAdapter Adapter,
    TrainerService? Trainer,
    ICaptureControl? Capture,
    string AppVersion,
    Lockpick.Core.Challenges.ChallengeService? Challenges = null,
    Lockpick.Core.Runs.RunService? Runs = null,
    Func<Lockpick.Core.Runs.SplitSet, Lockpick.Core.Runs.RunComparison>? Comparison = null,
    Lockpick.Core.Insight.EventFeed? Feed = null,
    Func<int, int, byte[]?>? LiveSplitPicture = null,
    Func<string, int, int, Task<WidgetPicture?>>? Widget = null,
    Func<string, double, Task<WidgetPicture?>>? WidgetAtScale = null,
    Func<IReadOnlyList<StreamPlacement>>? StreamLayout = null);

/// <summary>An overlay widget drawn for a browser source: premultiplied BGRA, as large as fitted the box asked.</summary>
public sealed record WidgetPicture(byte[] Pixels, int Width, int Height);

/// <summary>
/// One widget of the stream layout (every widget the player ticked, in one browser source): where it sits
/// (<c>top-left</c>, <c>top-centre</c>, <c>top-right</c>, <c>bottom-left</c>, <c>bottom-right</c>) and how large (1 = the
/// overlay's usual size). Widgets in the same place are stacked in this order. A widget placed by hand has the corner
/// <c>spot</c> and its spot: <paramref name="X"/> and <paramref name="Y"/> from 0 (left / top) to 1 (right / bottom) of the
/// room it has on the canvas (the canvas less the margins and the widget itself).
/// </summary>
public sealed record StreamPlacement(string Id, string Corner, double Scale, double? X = null, double? Y = null);
