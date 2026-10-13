using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lockpick.App.Services;
using Lockpick.Core.Monitoring;

namespace Lockpick.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private readonly SettingsViewModel _settings;
    private PageViewModel? _visiblePage;

    public MainViewModel(AppServices services)
    {
        _services = services;
        State = new LiveState(services.Source);
        Debug = new DebugViewModel(services, State);
        _settings = new SettingsViewModel(services, RunSetup);
        Pages = [new DashboardViewModel(State), Debug, _settings];
        SelectedPage = Pages[0];
        Updates = services.Updates;
        State.PropertyChanged += OnStateChanged;
        UpdateHeader();

        if (services.Settings.Current.SetupVersion < SetupViewModel.CurrentVersion && !services.IsReplay)
        {
            RunSetup();
        }
    }

    public LiveState State { get; }

    public DebugViewModel Debug { get; }

    public UpdateService Updates { get; }

    public IReadOnlyList<PageViewModel> Pages { get; }

    [ObservableProperty]
    public partial PageViewModel SelectedPage { get; set; }

    /// <summary>Non-null while first-time setup is shown instead of the main UI.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSetupVisible))]
    public partial SetupViewModel? Setup { get; private set; }

    public bool IsSetupVisible => Setup is not null;

    [ObservableProperty]
    public partial string HeaderStatus { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsConnected { get; private set; }

    [ObservableProperty]
    public partial bool IsUnsupported { get; private set; }

    [ObservableProperty]
    public partial bool IsReplaying { get; private set; }

    public void Dispose() => Debug.Dispose();

    /// <summary>The version label in the navigation opens the update controls in Settings.</summary>
    [RelayCommand]
    private void OpenUpdates() => SelectedPage = _settings;

    private void RunSetup() => Setup = new SetupViewModel(_services, () => Setup = null);

    partial void OnSelectedPageChanged(PageViewModel value)
    {
        _visiblePage?.OnVisibilityChanged(false);
        _visiblePage = value;
        value?.OnVisibilityChanged(true);
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LiveState.Status))
        {
            UpdateHeader();
        }
    }

    private void UpdateHeader()
    {
        var status = State.Status;
        IsConnected = status.State == ConnectionState.Attached;
        IsUnsupported = status.State == ConnectionState.Unsupported;
        IsReplaying = status.State == ConnectionState.Replaying;
        HeaderStatus = status.State switch
        {
            ConnectionState.Attached => $"CONNECTED · {status.Profile?.Id}",
            ConnectionState.Searching => "SEARCHING",
            ConnectionState.Unsupported => "UNSUPPORTED BUILD",
            ConnectionState.Replaying => "REPLAY",
            _ => "STOPPED",
        };
    }
}
