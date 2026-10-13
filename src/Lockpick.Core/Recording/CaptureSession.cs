using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Lockpick.Core.Events;
using Lockpick.Core.Monitoring;
using Lockpick.Core.State;

namespace Lockpick.Core.Recording;

/// <summary>Build identity recorded in a capture (no paths: the folder may contain a user name).</summary>
public sealed record CapturedBuild(string FileName, long FileSize, string Sha256, string? PeTimestamp, string? SizeOfImage, string? ProfileId, string Result, DateTimeOffset SeenAt);

/// <summary>Contents of capture-info.json.</summary>
public sealed record CaptureInfo(string LockpickVersion, string Os, DateTimeOffset StartedAt)
{
    public List<CapturedBuild> Builds { get; init; } = [];

    public int Attachments { get; set; }

    public long Samples { get; set; }

    public long Events { get; set; }

    public DateTimeOffset? EndedAt { get; set; }
}

/// <summary>
/// Records a play session for research: every raw profile-field change (trace-N.txt, the same format as
/// <c>lockpick-probe trace</c>), the normalized recording (recording-N.jsonl), events (events-N.txt) and the build
/// fingerprints seen (capture-info.json), one set of files per game attachment. <see cref="Stop"/> zips the session.
/// Records no machine names, user names or paths.
/// </summary>
public sealed class CaptureSession : IDisposable
{
    private static readonly JsonSerializerOptions InfoJson = new() { WriteIndented = true };

    /// <summary>Raw fields that change continuously; printed as context on every trace line instead of as changes.</summary>
    public static readonly IReadOnlySet<string> ContextFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "gameTimer", "positionX", "positionY", "positionZ", "yaw",
    };

    private readonly IGameStateSource _source;
    private readonly string _gameId;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly CaptureInfo _info;

    private StreamWriter? _trace;
    private StreamWriter? _events;
    private SessionRecorder? _recorder;
    private IReadOnlyDictionary<string, Field<long>>? _lastRaw;
    private DateTimeOffset _lastFlush;
    private bool _stopped;

    public CaptureSession(IGameStateSource source, string rootDirectory, string gameId, string appVersion, TimeProvider? time = null)
    {
        _source = source;
        _gameId = gameId;
        _time = time ?? TimeProvider.System;
        var now = _time.GetLocalNow();
        Name = $"capture-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
        RootDirectory = rootDirectory;
        Directory = Path.Combine(rootDirectory, Name);
        System.IO.Directory.CreateDirectory(Directory);
        _info = new CaptureInfo(appVersion, RuntimeInformation.OSDescription, now);
        WriteInfo();

        _source.StatusChanged += OnStatus;
        _source.Polled += OnPolled;
        _source.SnapshotChanged += OnSnapshot;
        _source.EventRaised += OnEvent;

        // Started while already attached: begin recording immediately.
        OnStatus(this, _source.Status);
    }

    public string Name { get; }

    public string RootDirectory { get; }

    public string Directory { get; }

    public bool IsRecordingGame
    {
        get
        {
            lock (_gate)
            {
                return _trace is not null;
            }
        }
    }

    public long Samples
    {
        get
        {
            lock (_gate)
            {
                return _info.Samples;
            }
        }
    }

    public long EventCount
    {
        get
        {
            lock (_gate)
            {
                return _info.Events;
            }
        }
    }

    public int Attachments
    {
        get
        {
            lock (_gate)
            {
                return _info.Attachments;
            }
        }
    }

    /// <summary>Same line format as <c>lockpick-probe trace</c>; tools/fixtures/trace_to_fixture.py reads it.</summary>
    public static string TraceLine(DateTime at, IReadOnlyDictionary<string, Field<long>> raw, IEnumerable<string> changed) =>
        $"[{at.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)}] "
        + string.Join(", ", changed.Select(k => $"{k}={Hex(raw[k])}"))
        + $" | timer={Hex(raw.GetValueOrDefault("gameTimer"))} pos={Hex(raw.GetValueOrDefault("positionX"))},{Hex(raw.GetValueOrDefault("positionZ"))}";

    /// <summary>Stops recording and writes <c>LockpickCapture-*.zip</c> next to the session folder. Returns the zip path.</summary>
    public string Stop()
    {
        Dispose();
        var zip = Path.Combine(RootDirectory, $"LockpickCapture-{Name["capture-".Length..]}.zip");
        if (!File.Exists(zip))
        {
            ZipFile.CreateFromDirectory(Directory, zip, CompressionLevel.SmallestSize, includeBaseDirectory: false);
        }

        return zip;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            Unsubscribe();
            CloseAttachment();
            _info.EndedAt = _time.GetLocalNow();
            WriteInfo();
        }
    }

    private static string Hex(Field<long> f) => f.HasValue ? $"0x{f.Value:X}" : f.Status.ToString();

    private void Unsubscribe()
    {
        _source.StatusChanged -= OnStatus;
        _source.Polled -= OnPolled;
        _source.SnapshotChanged -= OnSnapshot;
        _source.EventRaised -= OnEvent;
    }

    private void OnStatus(object? sender, MonitorStatus status)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            if (status.Fingerprint is { } fp && !_info.Builds.Exists(b => b.Sha256 == fp.Sha256))
            {
                _info.Builds.Add(new CapturedBuild(
                    fp.FileName,
                    fp.FileSize,
                    fp.Sha256,
                    fp.Pe is { } pe ? $"0x{pe.TimeDateStamp:X8}" : null,
                    fp.Pe is { } pe2 ? $"0x{pe2.SizeOfImage:X}" : null,
                    status.Profile?.Id,
                    status.Message,
                    _time.GetLocalNow()));
                WriteInfo();
            }

            var recordable = status.State is ConnectionState.Attached or ConnectionState.Replaying;
            if (recordable && _trace is null)
            {
                OpenAttachment(status.Profile?.Id);
            }
            else if (!recordable && _trace is not null)
            {
                CloseAttachment();
                WriteInfo();
            }
        }
    }

    private void OnPolled(object? sender, GameSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_trace is null)
            {
                return;
            }

            var raw = snapshot.RawFields;
            var changed = _lastRaw is null
                ? raw.Keys.ToList()
                : raw.Keys.Where(k => !ContextFields.Contains(k) && (!_lastRaw.TryGetValue(k, out var old) || old != raw[k])).ToList();
            if (changed.Count > 0)
            {
                _trace.WriteLine(TraceLine(snapshot.CapturedAt.LocalDateTime, raw, changed));
            }

            _lastRaw = raw;
            _info.Samples++;

            // Keep files usable if the app is killed; at most one flush per second.
            var now = _time.GetUtcNow();
            if (now - _lastFlush >= TimeSpan.FromSeconds(1))
            {
                _trace.Flush();
                _events?.Flush();
                _lastFlush = now;
            }
        }
    }

    private void OnSnapshot(object? sender, GameSnapshot snapshot)
    {
        lock (_gate)
        {
            _recorder?.Append(snapshot);
        }
    }

    private void OnEvent(object? sender, GameEvent gameEvent)
    {
        lock (_gate)
        {
            if (_events is null)
            {
                return;
            }

            _events.WriteLine($"[{gameEvent.Timestamp.LocalDateTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)}] {gameEvent.Describe()}");
            _info.Events++;
        }
    }

    private void OpenAttachment(string? profileId)
    {
        var n = ++_info.Attachments;
        _trace = new StreamWriter(Path.Combine(Directory, $"trace-{n}.txt"));
        _events = new StreamWriter(Path.Combine(Directory, $"events-{n}.txt"));
        _recorder = SessionRecorder.Create(Path.Combine(Directory, $"recording-{n}.jsonl"), _gameId, profileId, _time.GetUtcNow());
        _lastRaw = null;
        WriteInfo();
    }

    private void CloseAttachment()
    {
        _trace?.Dispose();
        _events?.Dispose();
        _recorder?.Dispose();
        _trace = null;
        _events = null;
        _recorder = null;
        _lastRaw = null;
    }

    private void WriteInfo() =>
        File.WriteAllText(Path.Combine(Directory, "capture-info.json"), JsonSerializer.Serialize(_info, InfoJson));
}
