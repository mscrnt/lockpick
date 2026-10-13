using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lockpick.App.Services;
using Lockpick.GameAdapter.RE1;
using Lockpick.Platform.Windows;

namespace Lockpick.App.ViewModels;

/// <summary>
/// First-time setup: welcome and safety notes, locating the game (with a build check), update preferences.
/// Completion is stored as <see cref="AppSettings.SetupVersion"/>; bumping <see cref="CurrentVersion"/> shows
/// setup again after an update that adds new steps. Re-runnable from Settings.
/// </summary>
public sealed partial class SetupViewModel : ObservableObject
{
    /// <summary>Increase when setup gains steps that existing users should see.</summary>
    public const int CurrentVersion = 1;

    public static readonly IReadOnlyList<string> StepNames = ["Welcome", "Game", "Updates", "Ready"];

    private readonly AppServices _services;
    private readonly Action _onFinished;
    private readonly string? _detectedExecutable;

    public SetupViewModel(AppServices services, Action onFinished)
    {
        _services = services;
        _onFinished = onFinished;
        var current = services.Settings.Current;

        var gog = GogInstallLocator.Find(Re1Adapter.GogProductId);
        _detectedExecutable = gog?.ExecutablePath;
        DetectedInstall = gog is null
            ? "No GOG install of Resident Evil was found automatically."
            : $"Found the GOG install: {gog.InstallPath} (version {gog.Version ?? "unknown"})";
        GameExecutablePath = current.GameExecutablePath ?? _detectedExecutable ?? string.Empty;
        CheckForUpdates = current.CheckForUpdates;
        IncludePrereleases = current.IncludePrereleases;
        UpdatesNote = services.Updates.IsSupported
            ? "Lockpick checks GitHub for new versions 20 seconds after starting and every 6 hours. Updates are only installed when you click Update."
            : "This copy wasn't installed with Setup.exe (for example a development build), so it can't update itself. Your choice is saved for installed builds.";
        CheckBuild();
    }

    public string DetectedInstall { get; }

    public string UpdatesNote { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcome), nameof(IsGame), nameof(IsUpdates), nameof(IsReady), nameof(IsFirst), nameof(IsLast), nameof(StepLabel), nameof(StepNumber), nameof(NextLabel))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    public partial int Step { get; private set; }

    [ObservableProperty]
    public partial string GameExecutablePath { get; set; }

    [ObservableProperty]
    public partial string BuildSummary { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string BuildDetails { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool BuildSupported { get; private set; }

    [ObservableProperty]
    public partial bool BuildProblem { get; private set; }

    [ObservableProperty]
    public partial bool CheckForUpdates { get; set; }

    [ObservableProperty]
    public partial bool IncludePrereleases { get; set; }

    public bool IsWelcome => Step == 0;

    public bool IsGame => Step == 1;

    public bool IsUpdates => Step == 2;

    public bool IsReady => Step == 3;

    public bool IsFirst => Step == 0;

    public bool IsLast => Step == StepNames.Count - 1;

    public int StepNumber => Step + 1;

    public string NextLabel => IsLast ? "Start Lockpick" : "Next";

    public string StepLabel => $"Step {Step + 1} of {StepNames.Count} · {StepNames[Step]}";

    public string ReadySummary => BuildSupported
        ? "Your game build is supported. Start Resident Evil normally (not as administrator) and Lockpick connects automatically."
        : "Lockpick will keep watching for a supported game. You can change the game location any time in Settings.";

    [RelayCommand]
    private void Next()
    {
        if (IsLast)
        {
            Finish();
            return;
        }

        Step++;
        OnPropertyChanged(nameof(ReadySummary));
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back() => Step--;

    private bool CanGoBack() => Step > 0;

    [RelayCommand]
    public void CheckBuild()
    {
        var result = BuildCheck.Run(_services.Fingerprints, _services.Adapter, GameExecutablePath);
        BuildSummary = result.Summary;
        BuildDetails = result.Details;
        BuildSupported = result.Supported;
        BuildProblem = !result.Supported;
    }

    [RelayCommand]
    private void Skip() => Finish();

    private void Finish()
    {
        var path = GameExecutablePath.Trim();
        var store = _services.Settings;
        store.Save(store.Current with
        {
            SetupVersion = CurrentVersion,
            // Only store an override when it differs from what the registry already tells us.
            GameExecutablePath = path.Length == 0 || string.Equals(path, _detectedExecutable, StringComparison.OrdinalIgnoreCase) ? null : path,
            CheckForUpdates = CheckForUpdates,
            IncludePrereleases = IncludePrereleases,
        });
        _onFinished();
    }
}
