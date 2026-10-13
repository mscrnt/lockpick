using System.IO.Compression;
using System.Text.Json;
using Lockpick.Core.Monitoring;
using Lockpick.Core.Processes;
using Lockpick.Core.Recording;
using Lockpick.GameAdapter.RE1;
using Lockpick.Tests.Fakes;

namespace Lockpick.Tests.Core;

/// <summary>End-to-end capture: real RE1 adapter over fake memory, driven through the real monitor.</summary>
public sealed class CaptureSessionTests : IDisposable
{
    private const string ExePath = @"C:\Users\someone\Games\Resident Evil\ResidentEvil.exe";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lockpick-capture-test-{Guid.NewGuid():N}");
    private readonly Re1Adapter _adapter = new();
    private readonly FakeProcessHost _host = new();
    private readonly FakeFingerprintProvider _fingerprints = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Records_traces_recordings_events_and_build_info_then_zips()
    {
        var profile = _adapter.Profiles[0];
        _fingerprints.ByPath[ExePath] = FakeFingerprintProvider.For(profile, ExePath);
        var memory = new FakeMemory().WithVerifiedImage(profile);
        void Set(string field, uint value, int size = 1)
        {
            var address = profile.FindField(field)!.Address;
            if (size == 1)
            {
                memory.WriteU8(address, (byte)value);
            }
            else if (size == 2)
            {
                memory.WriteI16(address, (short)value);
            }
            else
            {
                memory.WriteU32(address, value);
            }
        }

        // Chris in the main hall with a knife and a Beretta.
        Set("room", 0x06);
        Set("hp", 140, 2);
        Set("maxHp", 140);
        Set("gameTimer", 300, 4);
        Set("inv0", 0x0001, 2);
        Set("inv1", 0x0F02, 2);

        var monitor = new GameMonitor(_adapter, _host, _fingerprints, time: _time);
        using var capture = new CaptureSession(monitor, _root, "re1", "9.9.9");
        var process = _host.Launch(4242, ExePath, memory, new ModuleImage(0x400000, 0x49B2000, ExePath));

        _time.Advance(TimeSpan.FromSeconds(2));
        monitor.Tick(); // attach + first poll
        Assert.True(capture.IsRecordingGame);

        Set("inv1", 0x0E02, 2); // fired one shot
        Set("gameTimer", 330, 4);
        monitor.Tick();
        Set("room", 0x05); // through the dining-room door
        monitor.Tick();

        _host.Exit(4242);
        monitor.Tick(); // detach
        Assert.False(capture.IsRecordingGame);
        Assert.True(process.Disposed);

        var zip = capture.Stop();
        Assert.True(File.Exists(zip));

        using var archive = ZipFile.OpenRead(zip);
        string Read(string name)
        {
            using var reader = new StreamReader(archive.GetEntry(name)!.Open());
            return reader.ReadToEnd();
        }

        var trace = Read("trace-1.txt");
        Assert.Contains("inv1=0xE02", trace, StringComparison.Ordinal);
        Assert.Contains("room=0x5", trace, StringComparison.Ordinal);
        Assert.Contains("timer=0x14A", trace, StringComparison.Ordinal);

        Assert.Contains("Room 106 → 105", Read("events-1.txt"), StringComparison.Ordinal);

        var (header, frames) = SessionRecording.Read(new StringReader(Read("recording-1.jsonl")));
        Assert.Equal("re1-gog-usa-hotfix4", header.ProfileId);
        Assert.Equal(3, frames.Count); // one per poll here: the timer advanced, then the room changed

        var infoText = Read("capture-info.json");
        Assert.DoesNotContain("someone", infoText, StringComparison.Ordinal); // no paths / user names
        var info = JsonSerializer.Deserialize<CaptureInfo>(infoText)!;
        Assert.Equal("9.9.9", info.LockpickVersion);
        Assert.Equal(1, info.Attachments);
        Assert.Equal(3, info.Samples);
        var build = Assert.Single(info.Builds);
        Assert.Equal("ResidentEvil.exe", build.FileName);
        Assert.Equal("re1-gog-usa-hotfix4", build.ProfileId);
        Assert.NotNull(info.EndedAt);
    }

    [Fact]
    public void Unsupported_builds_are_fingerprinted_but_not_recorded()
    {
        _fingerprints.ByPath[ExePath] = FakeFingerprintProvider.For(_adapter.Profiles[0], ExePath) with { Sha256 = new string('C', 64) };
        _host.Launch(7, ExePath, new FakeMemory(), new ModuleImage(0x400000, 0x49B2000, ExePath));
        var monitor = new GameMonitor(_adapter, _host, _fingerprints, time: _time);
        using var capture = new CaptureSession(monitor, _root, "re1", "9.9.9");

        _time.Advance(TimeSpan.FromSeconds(2));
        monitor.Tick();

        Assert.False(capture.IsRecordingGame);
        var info = JsonSerializer.Deserialize<CaptureInfo>(File.ReadAllText(Path.Combine(capture.Directory, "capture-info.json")))!;
        var build = Assert.Single(info.Builds);
        Assert.Null(build.ProfileId);
        Assert.Equal(new string('C', 64), build.Sha256);
    }
}
