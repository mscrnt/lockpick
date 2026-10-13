using System.Globalization;
using Lockpick.Core.Fingerprinting;
using Lockpick.Core.Monitoring;
using Lockpick.Core.Recording;
using Lockpick.Core.State;
using Lockpick.GameAdapter.RE1;
using Lockpick.Probe;
using Lockpick.Platform.Windows;

// lockpick-probe identify [exe-path]
// lockpick-probe watch [--seconds N] [--record file.jsonl] [--raw]
// lockpick-probe trace [--seconds N] [--quiet f1,f2]   (prints every raw profile-field change at 30 Hz)
// lockpick-probe capture [--out dir] [--seconds N]   (volunteer capture: waits for the game, records until Ctrl+C, zips the session)
// Double-clicking the volunteer build (renamed LockpickCapture.exe) goes straight into capture mode.
var isCaptureKit = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty)
    .Equals("LockpickCapture", StringComparison.OrdinalIgnoreCase);
var command = args.Length > 0 ? args[0] : isCaptureKit ? "capture" : "help";
var adapter = new Re1Adapter();

switch (command)
{
    case "identify":
        return Identify(args.Length > 1 ? args[1] : null);
    case "watch":
        return await WatchAsync(args.Skip(1).ToArray());
    case "trace":
        return Trace(args.Skip(1).ToArray());
    case "capture":
        return Capture.Run(adapter, args.Skip(1).ToArray(), interactive: args.Length == 0);
    default:
        Console.WriteLine("usage: lockpick-probe identify [exe-path] | watch [--seconds N] [--record file.jsonl] [--raw] | trace [--seconds N] | capture [--out dir]");
        return command == "help" ? 0 : 2;
}

int Identify(string? path)
{
    if (path is null)
    {
        var installed = GogInstallLocator.Find(Re1Adapter.GogProductId);
        if (installed?.ExecutablePath is null)
        {
            Console.Error.WriteLine("No GOG Resident Evil install found in the registry; pass the executable path.");
            return 1;
        }

        Console.WriteLine($"GOG install: {installed.InstallPath} (version {installed.Version})");
        path = installed.ExecutablePath;
    }

    var fp = FileFingerprintProvider.ComputeUncached(path);
    Console.WriteLine($"File:      {fp.Path}");
    Console.WriteLine($"Size:      {fp.FileSize:N0} bytes");
    Console.WriteLine($"SHA-256:   {fp.Sha256}");
    if (fp.Pe is { } pe)
    {
        Console.WriteLine($"PE:        {pe.Machine}, timestamp 0x{pe.TimeDateStamp:X8}, base 0x{pe.ImageBase:X}, image 0x{pe.SizeOfImage:X}, ASLR {(pe.IsRelocatable ? "yes" : "no")}");
    }

    var match = adapter.Identify(fp);
    Console.WriteLine($"Supported: {(match.IsSupported ? "YES" : "NO")} — {match.Reason}");
    return match.IsSupported ? 0 : 3;
}

async Task<int> WatchAsync(string[] options)
{
    var seconds = 0.0;
    string? recordPath = null;
    var showRaw = false;
    for (var i = 0; i < options.Length; i++)
    {
        switch (options[i])
        {
            case "--seconds":
                seconds = double.Parse(options[++i], CultureInfo.InvariantCulture);
                break;
            case "--record":
                recordPath = options[++i];
                break;
            case "--raw":
                showRaw = true;
                break;
        }
    }

    using var wait = new HighResolutionWaitStrategy();
    await using var monitor = new GameMonitor(adapter, new Win32ProcessHost(), new FileFingerprintProvider(), new MonitorOptions { WaitStrategy = wait });
    SessionRecorder? recorder = null;
    var gate = new object();

    monitor.StatusChanged += (_, s) =>
    {
        lock (gate)
        {
            Console.WriteLine($"[{s.Since:HH:mm:ss.fff}] STATUS {s.State}: {s.Message}");
            if (s.State == ConnectionState.Attached && recordPath is not null && recorder is null)
            {
                recorder = SessionRecorder.Create(recordPath, adapter.GameId, s.Profile?.Id, DateTimeOffset.UtcNow);
            }
        }
    };
    monitor.EventRaised += (_, e) =>
    {
        lock (gate)
        {
            Console.WriteLine($"[{e.Timestamp:HH:mm:ss.fff}] EVENT  {e.Describe()}");
        }
    };
    monitor.SnapshotChanged += (_, snap) =>
    {
        lock (gate)
        {
            recorder?.Append(snap);
            Console.WriteLine($"[{snap.CapturedAt:HH:mm:ss.fff}] STATE  #{snap.Sequence} gameplay={snap.InGameplay} char={snap.Character} hp={snap.Health} room={snap.Location} cam={snap.Camera} pos={snap.Position} igt={Format(snap.GameTime)}");
            if (showRaw)
            {
                Console.WriteLine("         raw: " + string.Join(", ", snap.RawFields.Select(kv => $"{kv.Key}={(kv.Value.HasValue ? $"0x{kv.Value.Value:X}" : kv.Value.Status.ToString())}")));
            }
        }
    };

    Console.WriteLine($"High-resolution timer: {wait.IsHighResolution}. Ctrl+C to stop.");
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };
    monitor.Start();
    try
    {
        await Task.Delay(seconds > 0 ? TimeSpan.FromSeconds(seconds) : Timeout.InfiniteTimeSpan, cts.Token);
    }
    catch (OperationCanceledException)
    {
    }

    var stats = monitor.Statistics;
    await monitor.StopAsync();
    lock (gate)
    {
        recorder?.Dispose();
    }

    Console.WriteLine($"Polls: {stats.Polls} (failed {stats.FailedPolls}), avg {stats.AveragePollMs:F3} ms, max {stats.MaxPollMs:F3} ms, rate {stats.MeasuredPollHz:F1} Hz");
    return 0;
}

int Trace(string[] options)
{
    var seconds = 60.0;
    var quiet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "gameTimer", "positionX", "positionY", "positionZ", "yaw" };
    for (var i = 0; i < options.Length; i++)
    {
        if (options[i] == "--seconds")
        {
            seconds = double.Parse(options[++i], CultureInfo.InvariantCulture);
        }
        else if (options[i] == "--quiet")
        {
            quiet = new HashSet<string>(options[++i].Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        }
    }

    var host = new Win32ProcessHost();
    var candidates = host.FindProcesses(adapter.ExecutableNames);
    var candidate = candidates.Count > 0 ? candidates[0] : null;
    if (candidate?.ExecutablePath is null)
    {
        Console.Error.WriteLine("Game is not running.");
        return 1;
    }

    var match = adapter.Identify(FileFingerprintProvider.ComputeUncached(candidate.ExecutablePath));
    if (match.Profile is null)
    {
        Console.Error.WriteLine(match.Reason);
        return 3;
    }

    using var process = host.OpenReadOnly(candidate);
    var session = adapter.Attach(process, match.Profile);
    using var wait = new HighResolutionWaitStrategy();
    var end = DateTime.UtcNow.AddSeconds(seconds);
    IReadOnlyDictionary<string, Field<long>>? last = null;
    long seq = 0;
    while (DateTime.UtcNow < end && !process.HasExited)
    {
        var snap = session.Read(++seq, DateTimeOffset.UtcNow);
        var raw = snap.RawFields;
        var changed = last is null
            ? raw.Keys.ToList()
            : raw.Keys.Where(k => !quiet.Contains(k) && raw[k] != last[k]).ToList();
        if (changed.Count > 0)
        {
            Console.WriteLine(Capture.TraceLine(DateTime.Now, raw, changed));
        }

        last = raw;
        wait.Wait(TimeSpan.FromMilliseconds(33), CancellationToken.None);
    }

    return 0;
}

static string Format(Field<TimeSpan> time) =>
    time.HasValue ? $"{(int)time.Value.TotalHours:D2}:{time.Value.Minutes:D2}:{time.Value.Seconds:D2}{(time.IsValid ? string.Empty : " (stale)")}" : time.Status.ToString();
