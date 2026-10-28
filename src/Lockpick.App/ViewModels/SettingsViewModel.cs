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

    public SettingsViewModel(AppServices services, Action runSetup, OverlayViewModel overlay)
        : base("Settings", "Icon.Settings")
    {
        SetSections(
            new PageSection("general", "General"),
            new PageSection("display", "Game display"),
            new PageSection("overlay", "Overlay"),
            new PageSection("hotkeys", "Hotkeys"),
            new PageSection("speedrun", "speedrun.com"),
            new PageSection("advanced", "API and profiles"));
        _services = services;
        _runSetup = runSetup;
        Overlay = overlay;
        GameDisplay = new GameDisplayViewModel(services);
        Speedrun = new SpeedrunAccountViewModel(services.SpeedrunAccount);
        MapOptions = new MapOverlayOptionsViewModel(services);
        services.TrainerHotkeys.Pressed += (_, action) =>
        {
            if (action == TrainerHotkeys.ToggleOverlay)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => OverlayEnabled = !OverlayEnabled);
            }
        };
        Updates = services.Updates;
        var current = services.Settings.Current;
        GameExecutablePath = current.GameExecutablePath ?? string.Empty;
        CheckForUpdates = current.CheckForUpdates;
        IncludePrereleases = current.IncludePrereleases;
        MarkerHotkey = MarkerHotkeyChoices.Contains(current.MarkerHotkey) ? current.MarkerHotkey : "Off";
        MarkerHotkeyStatus = services.MarkerHotkeyName is null && MarkerHotkey != "Off" ? $"{MarkerHotkey} is used by another program." : string.Empty;
        MarkerButtonName = services.MarkerButton.DisplayName;
        OverlayEnabled = current.OverlayEnabled;
        OverlayUseBars = current.OverlayUseBars;
        LoadLook(current.OverlayLook);
        StreamerMode = current.StreamerMode;
        OverlayWidgetRows = [.. OverlayLayout.Widgets.Select(w =>
            new OverlayWidgetRow(w.Id, w.Name, OverlayLayout.Corner(current, w.Id), OverlayLayout.Choices, ApplyOverlayWidget, services.WidgetWindows,
                current.OverlayWidgetSizes.GetValueOrDefault(w.Id, 100))
            {
                Spot = current.OverlayWidgetSpots.TryGetValue(w.Id, out var spot) ? spot : null,
            })];
        ApiEnabled = current.ApiEnabled;
        ApiAllowNetwork = current.ApiAllowNetwork;
        ApiAllowTrainer = current.ApiAllowTrainer;
        ApiPort = current.ApiPort;
        ApiToken = current.ApiToken ?? string.Empty;
        UpdateApiStatus();
        var registered = services.TrainerHotkeys.Registered;
        TrainerHotkeyRows = [.. TrainerHotkeys.Actions.Select(a =>
        {
            var key = current.TrainerHotkeys.TryGetValue(a.Id, out var k) && MarkerHotkeyChoices.Contains(k) ? k : "Off";
            var row = new TrainerHotkeyRow(a.Id, a.Name, key, MarkerHotkeyChoices, ApplyTrainerHotkey,
                services.ActionButton(a.Id).DisplayName, BindActionButtonAsync, ClearActionButton);
            if (key != "Off" && !registered.ContainsKey(a.Id))
            {
                row.Status = $"{key} is used by another program.";
            }

            return row;
        })];
        DetectedInstall = GameInstalls.Describe("No GOG or Steam install of Resident Evil found.");
        ProfileDirectory = current.ProfileDirectory ?? AppPaths.Profiles;
        LoadedProfiles = string.Join(Environment.NewLine, services.Adapter.Profiles.Select(p => $"{p.Id} — {p.DisplayName}"));
        CheckInstalledBuild();
        _loading = false;
    }

    public UpdateService Updates { get; }

    /// <summary>People credited in the app, from CONTRIBUTORS.md.</summary>
    public IReadOnlyList<CreditSection> Credits { get; } = Services.Credits.Load();

    public string DetectedInstall { get; }

    public string ProfileDirectory { get; }

    public string LoadedProfiles { get; }

    public string DataFolder => AppPaths.DataRoot;

    [ObservableProperty]
    public partial string SupportStatus { get; private set; } = string.Empty;

    /// <summary>Makes a support bundle (logs, settings without secrets, versions) and shows it in Explorer.</summary>
    [RelayCommand]
    private void MakeSupportBundle()
    {
        try
        {
            var path = SupportBundle.Create(_services);
            SupportStatus = $"Saved {Path.GetFileName(path)} — attach it to a GitHub issue. It holds the last few days' logs, your settings without the API token, and versions; no game files or saves.";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SupportStatus = $"The support bundle couldn't be made: {ex.Message}";
        }
    }

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

    public IReadOnlyList<string> MarkerHotkeyChoices { get; } = [.. GlobalHotkey.Choices.Keys, "Off"];

    [ObservableProperty]
    public partial string MarkerHotkey { get; set; }

    [ObservableProperty]
    public partial string MarkerHotkeyStatus { get; private set; }

    [ObservableProperty]
    public partial bool ApiEnabled { get; set; }

    [ObservableProperty]
    public partial bool ApiAllowNetwork { get; set; }

    [ObservableProperty]
    public partial bool ApiAllowTrainer { get; set; }

    [ObservableProperty]
    public partial decimal ApiPort { get; set; }

    [ObservableProperty]
    public partial string ApiToken { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ApiStatus { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool ApiProblem { get; private set; }

    partial void OnApiEnabledChanged(bool value) => ChangeApi(s => s with { ApiEnabled = value });

    partial void OnApiAllowNetworkChanged(bool value) => ChangeApi(s => s with { ApiAllowNetwork = value });

    partial void OnApiAllowTrainerChanged(bool value) => ChangeApi(s => s with { ApiAllowTrainer = value });

    partial void OnApiPortChanged(decimal value)
    {
        if (value is >= 1024 and <= 65535)
        {
            ChangeApi(s => s with { ApiPort = (int)value });
        }
    }

    [RelayCommand]
    private void NewApiToken() => ChangeApi(s => s with { ApiToken = Lockpick.Api.ApiOptions.NewToken() });

    private void ChangeApi(Func<AppSettings, AppSettings> change)
    {
        if (_loading || !Persist(change))
        {
            return;
        }

        ApiStatus = "Applying…";
        _ = ApplyApiAsync();
    }

    private async Task ApplyApiAsync()
    {
        await _services.ApplyApiSettingsAsync().ConfigureAwait(true);
        ApiToken = _services.Settings.Current.ApiToken ?? string.Empty;
        UpdateApiStatus();
    }

    private void UpdateApiStatus()
    {
        var current = _services.Settings.Current;
        ApiProblem = current.ApiEnabled && _services.ApiError is not null;
        ApiStatus = !current.ApiEnabled ? "Off. Other programs can't reach Lockpick."
            : _services.ApiError is { } error ? error
            : _services.Api?.Address is { } address
                ? $"Listening on {address}api/v1 ({(current.ApiAllowNetwork ? "this PC and other devices on your network" : "this PC only")}); trainer commands {(current.ApiAllowTrainer ? "allowed in Trainer Mode" : "off")}."
                : "Starting…";
    }

    /// <summary>Keys for trainer actions; they do nothing unless Trainer Mode has write access.</summary>
    public IReadOnlyList<TrainerHotkeyRow> TrainerHotkeyRows { get; }

    private async Task<string?> BindActionButtonAsync(TrainerHotkeyRow row)
    {
        if (await _services.NextGamepadButtonAsync(TimeSpan.FromSeconds(10)) is not { } button)
        {
            return "No controller button was pressed; nothing changed.";
        }

        _services.SetActionButton(row.Id, button.ToString());
        row.ButtonName = button.DisplayName;
        var saved = new Dictionary<string, string>(_services.Settings.Current.GamepadActions) { [row.Id] = button.ToString() };
        Persist(s => s with { GamepadActions = saved });
        return null;
    }

    private void ClearActionButton(TrainerHotkeyRow row)
    {
        _services.SetActionButton(row.Id, GamepadBinding.Off);
        var saved = new Dictionary<string, string>(_services.Settings.Current.GamepadActions);
        saved.Remove(row.Id);
        Persist(s => s with { GamepadActions = saved });
    }

    private string? ApplyTrainerHotkey(TrainerHotkeyRow row, string key)
    {
        var problem = _services.TrainerHotkeys.Set(row.Id, key, _services.MarkerHotkeyName);
        var saved = new Dictionary<string, string>(_services.Settings.Current.TrainerHotkeys) { [row.Id] = problem is null ? key : "Off" };
        Persist(s => s with { TrainerHotkeys = saved });
        return problem;
    }

    /// <summary>The controller button that adds a marker ("L2 / LT", "Off", or a bound button's name).</summary>
    [ObservableProperty]
    public partial string MarkerButtonName { get; private set; }

    [ObservableProperty]
    public partial string MarkerButtonStatus { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BindMarkerButtonCommand))]
    public partial bool BindingMarkerButton { get; private set; }

    [RelayCommand(CanExecute = nameof(CanBindMarkerButton))]
    private async Task BindMarkerButtonAsync()
    {
        BindingMarkerButton = true;
        MarkerButtonStatus = "Press the button on your controller (10 seconds)…";
        try
        {
            if (await _services.NextGamepadButtonAsync(TimeSpan.FromSeconds(10)) is { } button)
            {
                UseMarkerButton(button.ToString());
                MarkerButtonStatus = $"Markers: {button.DisplayName}.";
            }
            else
            {
                MarkerButtonStatus = "No controller button was pressed; nothing changed.";
            }
        }
        finally
        {
            BindingMarkerButton = false;
        }
    }

    private bool CanBindMarkerButton() => !BindingMarkerButton;

    [RelayCommand]
    private void UseL2ForMarkers()
    {
        UseMarkerButton(GamepadBinding.L2);
        MarkerButtonStatus = string.Empty;
    }

    [RelayCommand]
    private void TurnOffMarkerButton()
    {
        UseMarkerButton(GamepadBinding.Off);
        MarkerButtonStatus = string.Empty;
    }

    private void UseMarkerButton(string value)
    {
        _services.SetMarkerButton(value);
        MarkerButtonName = _services.MarkerButton.DisplayName;
        Persist(s => s with { MarkerButton = value });
    }

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

    /// <summary>The game's own display settings (windowed / fullscreen, resolution, scaling) and moving its window.</summary>
    public GameDisplayViewModel GameDisplay { get; }

    /// <summary>The player's speedrun.com account (name, API key).</summary>
    public SpeedrunAccountViewModel Speedrun { get; }

    public override void OnVisibilityChanged(bool visible)
    {
        base.OnVisibilityChanged(visible);
        if (visible)
        {
            GameDisplay.Load();
            // Sizes may have changed live (the size keys) while the page was hidden.
            var current = _services.Settings.Current;
            foreach (var row in OverlayWidgetRows)
            {
                row.Load(OverlayLayout.Corner(current, row.Id), current.OverlayWidgetSizes.GetValueOrDefault(row.Id, 100),
                    current.OverlayWidgetSpots.TryGetValue(row.Id, out var spot) ? spot : null);
            }
        }
    }

    /// <summary>The overlay's state (its status line shows whether it's up, or why not).</summary>
    public OverlayViewModel Overlay { get; }

    [ObservableProperty]
    public partial bool OverlayEnabled { get; set; }

    /// <summary>The overlay's widgets may sit in the black bars beside the game's picture.</summary>
    [ObservableProperty]
    public partial bool OverlayUseBars { get; set; }

    /// <summary>Blur paths, process ids and the API token on screen (for streaming Lockpick's window).</summary>
    [ObservableProperty]
    public partial bool StreamerMode { get; set; }

    partial void OnStreamerModeChanged(bool value)
    {
        Privacy.StreamerMode = value;
        if (!_loading)
        {
            Persist(s => s with { StreamerMode = value });
        }
    }

    /// <summary>Each overlay widget and its corner (or off).</summary>
    public IReadOnlyList<OverlayWidgetRow> OverlayWidgetRows { get; }

    /// <summary>The overlay map's views and look (made again when a layout is loaded).</summary>
    [ObservableProperty]
    public partial MapOverlayOptionsViewModel MapOptions { get; private set; }

    [ObservableProperty]
    public partial string LayoutMessage { get; private set; } = string.Empty;

    /// <summary>Saves the overlay's layout (corners, spots, sizes, look, map options) to share.</summary>
    public void ExportLayout(string path)
    {
        try
        {
            OverlayLayoutFile.Write(path, _services.Settings.Current);
            LayoutMessage = $"Saved {Path.GetFileName(path)}. Anyone can load it in Settings → Overlay → Layout.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LayoutMessage = $"Couldn't save the layout: {ex.Message}";
        }
    }

    /// <summary>Loads a layout: the overlay, its rows, the look and the map's options take it at once.</summary>
    public void ImportLayout(string path)
    {
        try
        {
            var settings = OverlayLayoutFile.Apply(path, _services.Settings.Current);
            Persist(_ => settings);
            foreach (var row in OverlayWidgetRows)
            {
                row.Load(OverlayLayout.Corner(settings, row.Id), settings.OverlayWidgetSizes.GetValueOrDefault(row.Id, 100),
                    settings.OverlayWidgetSpots.TryGetValue(row.Id, out var spot) ? spot : null);
            }

            LoadLook(settings.OverlayLook);
            MapOptions = new MapOverlayOptionsViewModel(_services);
            LayoutMessage = $"Loaded {Path.GetFileName(path)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            LayoutMessage = $"Couldn't load that layout: {ex.Message}";
        }
    }

    partial void OnOverlayEnabledChanged(bool value)
    {
        if (!_loading)
        {
            Persist(s => s with { OverlayEnabled = value });
        }
    }

    partial void OnOverlayUseBarsChanged(bool value)
    {
        if (!_loading)
        {
            Persist(s => s with { OverlayUseBars = value });
        }
    }

    public IReadOnlyList<string> TextColorChoices { get; } = [.. OverlayLook.TextColors.Select(c => c.Name)];

    public IReadOnlyList<string> FontChoices { get; } = [.. OverlayLook.Fonts.Select(f => f.Name)];

    /// <summary>How solid the panels' background is, in percent.</summary>
    [ObservableProperty]
    public partial double LookBackground { get; set; }

    [ObservableProperty]
    public partial string LookTextColor { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LookFont { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool LookShowTime { get; set; }

    [ObservableProperty]
    public partial bool LookShowHealth { get; set; }

    [ObservableProperty]
    public partial bool LookShowRoom { get; set; }

    [ObservableProperty]
    public partial bool LookShowChallenge { get; set; }

    partial void OnLookBackgroundChanged(double value) => SaveLook();

    partial void OnLookTextColorChanged(string value) => SaveLook();

    partial void OnLookFontChanged(string value) => SaveLook();

    partial void OnLookShowTimeChanged(bool value) => SaveLook();

    partial void OnLookShowHealthChanged(bool value) => SaveLook();

    partial void OnLookShowRoomChanged(bool value) => SaveLook();

    partial void OnLookShowChallengeChanged(bool value) => SaveLook();

    private void LoadLook(OverlayLook look)
    {
        var loading = _lookLoading;
        _lookLoading = true;
        LookBackground = look.BackgroundOpacity;
        LookTextColor = (OverlayLook.TextColors.FirstOrDefault(c => c.Id == look.TextColor).Name ?? OverlayLook.TextColors[0].Name);
        LookFont = (OverlayLook.Fonts.FirstOrDefault(f => f.Id == look.Font).Name ?? OverlayLook.Fonts[0].Name);
        LookShowTime = look.ShowTime;
        LookShowHealth = look.ShowHealth;
        LookShowRoom = look.ShowRoom;
        LookShowChallenge = look.ShowChallenge;
        _lookLoading = loading;
    }

    private bool _lookLoading;

    private void SaveLook()
    {
        if (_loading || _lookLoading)
        {
            return;
        }

        var look = new OverlayLook
        {
            BackgroundOpacity = (int)Math.Round(Math.Clamp(LookBackground, 0, 100)),
            TextColor = OverlayLook.TextColors.FirstOrDefault(c => c.Name == LookTextColor).Id ?? "default",
            Font = OverlayLook.Fonts.FirstOrDefault(f => f.Name == LookFont).Id ?? "default",
            ShowTime = LookShowTime,
            ShowHealth = LookShowHealth,
            ShowRoom = LookShowRoom,
            ShowChallenge = LookShowChallenge,
        };
        Persist(s => s with { OverlayLook = look });
    }

    /// <summary>A widget dropped in the layout editor: it keeps that spot on the picture.</summary>
    public void PlaceOverlayWidget(string id, WidgetSpot spot)
    {
        if (OverlayWidgetRows.FirstOrDefault(r => r.Id == id) is not { } row)
        {
            return;
        }

        row.Spot = spot;
        var spots = new Dictionary<string, WidgetSpot>(_services.Settings.Current.OverlayWidgetSpots) { [id] = spot };
        Persist(s => s with { OverlayWidgetSpots = spots });
        row.Corner = OverlayCorners.Custom; // saves the corner too
    }

    /// <summary>The widgets placed by hand go back to their own corners (widgets that are off stay off).</summary>
    [RelayCommand]
    private void ResetOverlayLayout()
    {
        foreach (var row in OverlayWidgetRows)
        {
            row.Spot = null;
            if (row.Corner == OverlayCorners.Custom)
            {
                row.Corner = OverlayLayout.Widgets.First(w => w.Id == row.Id).Default;
            }
        }

        Persist(s => s with { OverlayWidgetSpots = new Dictionary<string, WidgetSpot>() });
    }

    private void ApplyOverlayWidget(OverlayWidgetRow row)
    {
        var corners = new Dictionary<string, string>(_services.Settings.Current.OverlayWidgetCorners) { [row.Id] = row.Corner };
        var sizes = new Dictionary<string, int>(_services.Settings.Current.OverlayWidgetSizes) { [row.Id] = row.SizePercent };
        Persist(s => s with { OverlayWidgetCorners = corners, OverlayWidgetSizes = sizes });
    }

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

    partial void OnMarkerHotkeyChanged(string value)
    {
        if (_loading)
        {
            return;
        }

        var ok = _services.SetMarkerHotkey(value);
        MarkerHotkeyStatus = ok ? string.Empty : $"{value} is used by another program; choose another key.";
        Persist(s => s with { MarkerHotkey = value });
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
