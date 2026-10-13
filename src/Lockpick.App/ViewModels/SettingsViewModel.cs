using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lockpick.App.Services;
using Lockpick.GameAdapter.RE1;
using Lockpick.Platform.Windows;

namespace Lockpick.App.ViewModels;

public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly AppServices _services;
    private readonly Action _runSetup;
    private bool _loading = true;

    public SettingsViewModel(AppServices services, Action runSetup)
        : base("Settings", "Icon.Settings")
    {
        _services = services;
        _runSetup = runSetup;
        Updates = services.Updates;
        var current = services.Settings.Current;
        GameExecutablePath = current.GameExecutablePath ?? string.Empty;
        CheckForUpdates = current.CheckForUpdates;
        IncludePrereleases = current.IncludePrereleases;
        var gog = GogInstallLocator.Find(Re1Adapter.GogProductId);
        DetectedInstall = gog is null
            ? "No GOG install of Resident Evil found in the registry."
            : $"GOG: {gog.InstallPath} (version {gog.Version ?? "unknown"})";
        ProfileDirectory = current.ProfileDirectory ?? AppPaths.Profiles;
        LoadedProfiles = string.Join(Environment.NewLine, services.Adapter.Profiles.Select(p => $"{p.Id} — {p.DisplayName}"));
        CheckInstalledBuild();
        _loading = false;
    }

    public UpdateService Updates { get; }

    public string DetectedInstall { get; }

    public string ProfileDirectory { get; }

    public string LoadedProfiles { get; }

    public string DataFolder => AppPaths.DataRoot;

    [ObservableProperty]
    public partial string GameExecutablePath { get; set; }

    [ObservableProperty]
    public partial string BuildCheckResult { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool BuildSupported { get; private set; }

    [ObservableProperty]
    public partial string SaveResult { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool CheckForUpdates { get; set; }

    [ObservableProperty]
    public partial bool IncludePrereleases { get; set; }

    [RelayCommand]
    public void CheckInstalledBuild()
    {
        var path = string.IsNullOrWhiteSpace(GameExecutablePath) ? _services.ResolveInstalledExecutable() : GameExecutablePath;
        var result = BuildCheck.Run(_services.Fingerprints, _services.Adapter, path);
        BuildSupported = result.Supported;
        BuildCheckResult = $"{result.Summary}\n{result.Details}";
    }

    [RelayCommand]
    private void Save()
    {
        var trimmed = GameExecutablePath.Trim();
        if (Persist(s => s with { GameExecutablePath = trimmed.Length == 0 ? null : trimmed }))
        {
            SaveResult = "Saved.";
            CheckInstalledBuild();
        }
    }

    [RelayCommand]
    private Task CheckForUpdatesNowAsync() => Updates.CheckAsync();

    [RelayCommand]
    private Task UpdateNowAsync() => Updates.UpdateAndRestartAsync();

    [RelayCommand]
    private void RunSetup() => _runSetup();

    partial void OnCheckForUpdatesChanged(bool value)
    {
        if (!_loading)
        {
            Persist(s => s with { CheckForUpdates = value });
        }
    }

    partial void OnIncludePrereleasesChanged(bool value)
    {
        if (!_loading)
        {
            Persist(s => s with { IncludePrereleases = value });
        }
    }

    private bool Persist(Func<AppSettings, AppSettings> change)
    {
        try
        {
            _services.Settings.Save(change(_services.Settings.Current));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveResult = $"Could not save settings: {ex.Message}";
            return false;
        }
    }
}
