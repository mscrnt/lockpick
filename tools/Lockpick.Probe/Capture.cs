using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Lockpick.Core.Adapters;
using Lockpick.Core.Events;
using Lockpick.Core.Fingerprinting;
using Lockpick.Core.Processes;
using Lockpick.Core.Recording;
using Lockpick.Core.State;
using Lockpick.GameAdapter.RE1;
using Lockpick.Platform.Windows;

namespace Lockpick.Probe;

/// <summary>
/// One-click data capture for volunteers: waits for the game, records every raw profile-field change (trace),
/// the normalized recording and events, survives game restarts, and zips the session when stopped.
/// Read-only like an autosplitter. Records no machine or user names.
/// </summary>
internal static class Capture
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1000.0 / 30.0);
    private static readonly JsonSerializerOptions InfoJson = new() { WriteIndented = true };
    private static readonly HashSet<string> QuietFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "gameTimer", "positionX", "positionY", "positionZ", "yaw",
    };

    public static int Run(Re1Adapter adapter, string[] options, bool interactive = false)
    {
        var outputRoot = Path.Combine(AppContext.BaseDirectory, "captures");
        TimeSpan? duration = null;
        for (var i = 0; i < options.Length; i++)
        {
            switch (options[i])
            {
                case "--out":
                    outputRoot = options[++i];
                    break;
                case "--seconds":
                    duration = TimeSpan.FromSeconds(double.Parse(options[++i], CultureInfo.InvariantCulture));
                    break;
            }
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var sessionDir = Path.Combine(outputRoot, $"capture-{stamp}");
        Directory.CreateDirectory(sessionDir);

        using var stop = duration is { } d ? new CancellationTokenSource(d) : new CancellationTokenSource();
        using var finished = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        // Closing the console window (X) ends the process as soon as this handler returns, so block here
        // (Windows allows ~5 s) until the capture files and the zip have been written.
        using var closeHandler = PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ =>
        {
            stop.Cancel();
            finished.Wait(TimeSpan.FromSeconds(4.5));
        });

        var info = new CaptureInfo(
            typeof(Capture).Assembly.GetName().Version?.ToString(3) ?? "?",
            RuntimeInformation.OSDescription,
            DateTimeOffset.Now,
            [.. adapter.Profiles.Select(p => p.Id)]);

        Banner(sessionDir);
        var host = new Win32ProcessHost();
        using var wait = new HighResolutionWaitStrategy();
        var attachment = 0;
        string? lastStatus = null;

        void Status(string message)
        {
            if (message != lastStatus)
            {
                Say(ConsoleColor.Gray, message);
                lastStatus = message;
            }
        }

        while (!stop.IsCancellationRequested)
        {
            var candidates = host.FindProcesses(adapter.ExecutableNames);
            if (candidates.Count == 0)
            {
                Status("Waiting for Resident Evil to start...");
                stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
                continue;
            }

            var candidate = candidates[0];
            if (candidate.ExecutablePath is null)
            {
                Status("Found the game but can't read it. If it was started as administrator, restart it normally.");
                stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(3));
                continue;
            }

            var fingerprint = FileFingerprintProvider.ComputeUncached(candidate.ExecutablePath);
            var match = adapter.Identify(fingerprint);
            if (!info.Builds.Exists(b => b.Sha256 == fingerprint.Sha256))
            {
                info.Builds.Add(new BuildInfo(fingerprint.FileName, fingerprint.FileSize, fingerprint.Sha256,
                    fingerprint.Pe is { } pe ? $"0x{pe.TimeDateStamp:X8}" : null,
                    fingerprint.Pe is { } pe2 ? $"0x{pe2.SizeOfImage:X}" : null,
                    match.Profile?.Id, match.Reason, DateTimeOffset.Now));
                WriteInfo(sessionDir, info);
            }

            if (match.Profile is null)
            {
                Say(ConsoleColor.Yellow, $"This game build isn't supported yet: {match.Reason}");
                Say(ConsoleColor.Yellow, "Its fingerprint was saved - that alone helps. Keep this open or press Ctrl+C to finish.");
                WaitForExit(candidate.ProcessId, stop.Token);
                lastStatus = null;
                continue;
            }

            IAttachedProcess process;
            IGameSession session;
            try
            {
                process = host.OpenReadOnly(candidate);
                session = adapter.Attach(process, match.Profile);
            }
            catch (Exception ex) when (ex is ProcessAccessException or AttachRejectedException)
            {
                Status($"Can't attach yet: {ex.Message}");
                stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(3));
                continue;
            }

            attachment++;
            lastStatus = null;
            Say(ConsoleColor.Green, $"Recording! ({match.Profile.DisplayName}). Play normally - Ctrl+C or close this window when done.");
            using (process)
            {
                RecordAttachment(adapter, session, process, sessionDir, attachment, wait, stop.Token);
            }

            if (!stop.IsCancellationRequested)
            {
                Say(ConsoleColor.Gray, "Game closed. Still recording - start it again, or press Ctrl+C to finish.");
            }
        }

        info.EndedAt = DateTimeOffset.Now;
        WriteInfo(sessionDir, info);
        var zip = Path.Combine(outputRoot, $"LockpickCapture-{stamp}.zip");
        ZipFile.CreateFromDirectory(sessionDir, zip, CompressionLevel.SmallestSize, includeBaseDirectory: false);
        finished.Set();
        Console.WriteLine();
        Say(ConsoleColor.Green, "Done! Please send this file:");
        Say(ConsoleColor.White, "  " + zip);
        if (interactive)
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{zip}\"") { UseShellExecute = false });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Opening Explorer is a convenience only.
            }

            Console.WriteLine();
            Console.WriteLine("Press Enter to close.");
            Console.ReadLine();
        }

        return 0;
    }

    private static void RecordAttachment(
        Re1Adapter adapter, IGameSession session, IAttachedProcess process, string dir, int index, HighResolutionWaitStrategy wait, CancellationToken stop)
    {
        var start = DateTimeOffset.UtcNow;
        using var trace = new StreamWriter(Path.Combine(dir, $"trace-{index}.txt"));
        using var events = new StreamWriter(Path.Combine(dir, $"events-{index}.txt"));
        using var recorder = SessionRecorder.Create(Path.Combine(dir, $"recording-{index}.jsonl"), adapter.GameId, session.Profile.Id, start);
        var detector = new EventDetector();
        IReadOnlyDictionary<string, Field<long>>? lastRaw = null;
        GameSnapshot? previous = null;
        long sequence = 0;
        var lastFlush = Stopwatch.GetTimestamp();
        var lastHeartbeat = Stopwatch.GetTimestamp();
        var eventCount = 0;

        while (!stop.IsCancellationRequested && !process.HasExited)
        {
            var started = Stopwatch.GetTimestamp();
            var snapshot = session.Read(++sequence, DateTimeOffset.Now);
            var raw = snapshot.RawFields;

            var changed = lastRaw is null ? [.. raw.Keys] : raw.Keys.Where(k => !QuietFields.Contains(k) && raw[k] != lastRaw[k]).ToList();
            if (changed.Count > 0)
            {
                trace.WriteLine(TraceLine(DateTime.Now, raw, changed));
            }

            lastRaw = raw;

            if (!snapshot.HasSameStateAs(previous))
            {
                recorder.Append(snapshot);
            }

            foreach (var e in detector.Detect(previous, snapshot))
            {
                eventCount++;
                var line = $"[{e.Timestamp:HH:mm:ss.fff}] {e.Describe()}";
                events.WriteLine(line);
                if (e is RoomChanged or GameLoaded or PlayerDamaged)
                {
                    Console.WriteLine("  " + line);
                }
            }

            previous = snapshot;

            if (Stopwatch.GetElapsedTime(lastFlush) > TimeSpan.FromSeconds(1))
            {
                // Keep files usable even if the window is killed.
                trace.Flush();
                events.Flush();
                lastFlush = Stopwatch.GetTimestamp();
            }

            if (Stopwatch.GetElapsedTime(lastHeartbeat) > TimeSpan.FromSeconds(60))
            {
                Say(ConsoleColor.DarkGray, $"  ...still recording ({sequence:N0} samples, {eventCount} events)");
                lastHeartbeat = Stopwatch.GetTimestamp();
            }

            wait.Wait(PollInterval - Stopwatch.GetElapsedTime(started), stop);
        }
    }

    /// <summary>Same line format as `lockpick-probe trace`, so tools/fixtures/trace_to_fixture.py can convert it.</summary>
    public static string TraceLine(DateTime at, IReadOnlyDictionary<string, Field<long>> raw, IEnumerable<string> changed) =>
        $"[{at:HH:mm:ss.fff}] " + string.Join(", ", changed.Select(k => $"{k}={Hex(raw[k])}"))
        + $" | timer={Hex(raw.GetValueOrDefault("gameTimer"))} pos={Hex(raw.GetValueOrDefault("positionX"))},{Hex(raw.GetValueOrDefault("positionZ"))}";

    private static string Hex(Field<long> f) => f.HasValue ? $"0x{f.Value:X}" : f.Status.ToString();

    private static void WaitForExit(int pid, CancellationToken stop)
    {
        using var p = TryGetProcess(pid);
        while (p is not null && !stop.IsCancellationRequested && !p.WaitForExit(500))
        {
        }
    }

    private static Process? TryGetProcess(int pid)
    {
        try
        {
            return Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static void WriteInfo(string dir, CaptureInfo info) =>
        File.WriteAllText(Path.Combine(dir, "capture-info.json"), JsonSerializer.Serialize(info, InfoJson));

    private static void Banner(string dir)
    {
        Console.Title = "Lockpick Capture";
        Say(ConsoleColor.Green, "LOCKPICK CAPTURE - Master of Unlocking.");
        Say(ConsoleColor.Gray, "Records what the game is doing (read-only, like a LiveSplit autosplitter). It does not change the game.");
        Say(ConsoleColor.Gray, $"Saving to: {dir}");
        Console.WriteLine();
    }

    private static void Say(ConsoleColor color, string text)
    {
        var old = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = old;
    }

    private sealed record BuildInfo(string FileName, long FileSize, string Sha256, string? PeTimestamp, string? SizeOfImage, string? ProfileId, string Result, DateTimeOffset SeenAt);

    private sealed record CaptureInfo(string LockpickVersion, string Os, DateTimeOffset StartedAt, List<string> KnownProfiles)
    {
        public List<BuildInfo> Builds { get; } = [];

        public DateTimeOffset? EndedAt { get; set; }
    }
}
