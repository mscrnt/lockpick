using Lockpick.Core.Fingerprinting;
using Lockpick.Core.Logging;
using Lockpick.Core.Monitoring;
using Lockpick.Core.Profiles;
using Lockpick.Core.Recording;
using Lockpick.GameAdapter.RE1;
using Lockpick.Platform.Windows;
using Microsoft.Extensions.Logging;

namespace Lockpick.App.Services;

/// <summary>Composition root: builds logging, settings, the RE1 adapter and the state source.</summary>
public sealed class AppServices : IAsyncDisposable
{
    private readonly HighResolutionWaitStrategy? _wait;

    private AppServices(
        JsonLinesLoggerProvider logProvider,
        ILoggerFactory loggerFactory,
        SettingsStore settings,
        Re1Adapter adapter,
        IGameStateSource source,
        FileFingerprintProvider fingerprints,
        HighResolutionWaitStrategy? wait,
        string? replayPath)
    {
        Updates = new UpdateService(settings, loggerFactory.CreateLogger("Updates"));
        LogProvider = logProvider;
        LoggerFactory = loggerFactory;
        Settings = settings;
        Adapter = adapter;
        Source = source;
        Fingerprints = fingerprints;
        _wait = wait;
        ReplayPath = replayPath;
    }

    public JsonLinesLoggerProvider LogProvider { get; }

    public ILoggerFactory LoggerFactory { get; }

    public SettingsStore Settings { get; }

    public Re1Adapter Adapter { get; }

    public IGameStateSource Source { get; }

    public FileFingerprintProvider Fingerprints { get; }

    public UpdateService Updates { get; }

    public string AppVersion => Updates.CurrentVersion;

    public string? ReplayPath { get; }

    public bool IsReplay => ReplayPath is not null;

    public bool HighResolutionTimer => _wait?.IsHighResolution ?? false;

    public static AppServices Create(string[] args)
    {
        var logPath = Path.Combine(AppPaths.Logs, $"lockpick-{DateTime.Now:yyyyMMdd}.jsonl");
        var logProvider = new JsonLinesLoggerProvider(logPath, args.Contains("--verbose") ? LogLevel.Debug : LogLevel.Information);
        var loggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddProvider(logProvider).SetMinimumLevel(LogLevel.Trace));
        var log = loggerFactory.CreateLogger("Lockpick");
        log.LogInformation("Lockpick {Version} starting", typeof(AppServices).Assembly.GetName().Version);

        var settings = new SettingsStore(AppPaths.SettingsFile, log);
        var current = settings.Load();

        var extraProfiles = ProfileLoader.LoadDirectory(
            current.ProfileDirectory ?? AppPaths.Profiles,
            (file, ex) => log.LogWarning("Ignoring invalid profile {File}: {Error}", file, ex.Message));
        var adapter = new Re1Adapter(extraProfiles);
        log.LogInformation("Loaded {Count} RE1 profile(s): {Ids}", adapter.Profiles.Count, string.Join(", ", adapter.Profiles.Select(p => p.Id)));

        var fingerprints = new FileFingerprintProvider();
        var replayIndex = Array.IndexOf(args, "--replay");
        if (replayIndex >= 0 && replayIndex + 1 < args.Length)
        {
            var path = args[replayIndex + 1];
            var (header, frames) = SessionRecording.ReadFile(path);
            log.LogInformation("Replay mode: {Path} ({Frames} frames)", path, frames.Count);
            return new AppServices(logProvider, loggerFactory, settings, adapter, new ReplaySource(header, frames, loop: true), fingerprints, null, path);
        }

        var wait = new HighResolutionWaitStrategy();
        var monitor = new GameMonitor(
            adapter,
            new Win32ProcessHost(),
            fingerprints,
            new MonitorOptions { WaitStrategy = wait },
            loggerFactory.CreateLogger<GameMonitor>());
        return new AppServices(logProvider, loggerFactory, settings, adapter, monitor, fingerprints, wait, null);
    }

    /// <summary>The executable to inspect offline: the configured path, else the GOG install.</summary>
    public string? ResolveInstalledExecutable() =>
        Settings.Current.GameExecutablePath is { Length: > 0 } configured
            ? configured
            : GogInstallLocator.Find(Re1Adapter.GogProductId)?.ExecutablePath;

    public async ValueTask DisposeAsync()
    {
        await Source.DisposeAsync();
        Updates.Dispose();
        _wait?.Dispose();
        LoggerFactory.Dispose();
        LogProvider.Dispose();
    }
}
