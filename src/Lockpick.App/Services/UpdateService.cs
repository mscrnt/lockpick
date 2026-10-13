using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace Lockpick.App.Services;

/// <summary>
/// Self-update through Velopack against GitHub releases. Background checks run 20 s after startup and every
/// 6 hours when enabled; nothing is installed until the user clicks "Update". Only installed builds
/// (Setup.exe or the portable zip from a release) can update; development builds say so instead.
/// </summary>
public sealed partial class UpdateService : ObservableObject, IDisposable
{
    public const string RepositoryUrl = "https://github.com/mscrnt/lockpick";

    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly SettingsStore _settings;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private UpdateManager? _manager;
    private bool? _managerPrerelease;
    private UpdateInfo? _pending;

    public UpdateService(SettingsStore settings, ILogger logger)
    {
        _settings = settings;
        _logger = logger;
        CurrentVersion = typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        IsSupported = Manager?.IsInstalled == true;
        Status = IsSupported
            ? "Not checked yet."
            : "Automatic updates work in installed builds (Setup.exe from the GitHub releases page).";
    }

    public string CurrentVersion { get; }

    /// <summary>True when running from a Velopack install that can update itself.</summary>
    public bool IsSupported { get; }

    [ObservableProperty]
    public partial string Status { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(VersionLabel))]
    public partial string? AvailableVersion { get; private set; }

    [ObservableProperty]
    public partial int Progress { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    public bool HasUpdate => AvailableVersion is not null;

    public string VersionLabel => HasUpdate ? $"v{CurrentVersion} → v{AvailableVersion}" : $"v{CurrentVersion}";

    private UpdateManager? Manager
    {
        get
        {
            var prerelease = _settings.Current.IncludePrereleases;
            if (_manager is null || _managerPrerelease != prerelease)
            {
                try
                {
                    _manager = new UpdateManager(new GithubSource(RepositoryUrl, null, prerelease));
                    _managerPrerelease = prerelease;
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
                {
                    _logger.LogWarning(ex, "Update manager unavailable");
                    _manager = null;
                }
            }

            return _manager;
        }
    }

    public void StartBackgroundChecks() => _ = BackgroundLoopAsync(_cts.Token);

    public async Task CheckAsync()
    {
        if (!IsSupported || IsBusy || Manager is not { } manager)
        {
            return;
        }

        IsBusy = true;
        Status = "Checking for updates...";
        try
        {
            _pending = await manager.CheckForUpdatesAsync().ConfigureAwait(true);
            AvailableVersion = _pending?.TargetFullRelease.Version.ToString();
            Status = _pending is null
                ? $"Lockpick is up to date (checked {DateTime.Now:t})."
                : $"Version {AvailableVersion} is available.";
            if (_pending is not null)
            {
                _logger.LogInformation("Update available: {Version}", AvailableVersion);
            }
        }
#pragma warning disable CA1031 // Network/feed failures of any kind must not break the app; they are reported, not hidden.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Status = $"Couldn't check for updates: {ex.Message}";
            _logger.LogWarning(ex, "Update check failed");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Downloads the pending update and restarts into it. Only called from an explicit user action.</summary>
    public async Task UpdateAndRestartAsync()
    {
        if (_pending is null || IsBusy || Manager is not { } manager)
        {
            return;
        }

        IsBusy = true;
        Status = $"Downloading {AvailableVersion}...";
        try
        {
            await manager.DownloadUpdatesAsync(_pending, p => Dispatcher.UIThread.Post(() => Progress = p)).ConfigureAwait(true);
            Status = "Installing — Lockpick will restart.";
            _logger.LogInformation("Applying update {Version} and restarting", AvailableVersion);
            manager.ApplyUpdatesAndRestart(_pending);
        }
#pragma warning disable CA1031 // Report any download/apply failure to the user.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Status = $"Update failed: {ex.Message}";
            _logger.LogError(ex, "Update failed");
            IsBusy = false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    private async Task BackgroundLoopAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(FirstCheckDelay, token).ConfigureAwait(true);
            while (!token.IsCancellationRequested)
            {
                if (_settings.Current.CheckForUpdates)
                {
                    await CheckAsync().ConfigureAwait(true);
                }

                await Task.Delay(CheckInterval, token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
