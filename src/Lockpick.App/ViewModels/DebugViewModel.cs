using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lockpick.App.Services;
using Lockpick.Core.Logging;
using Lockpick.Core.Monitoring;
using Lockpick.Core.Profiles;
using Lockpick.Core.Recording;
using Lockpick.Core.State;
using Microsoft.Extensions.Logging;

namespace Lockpick.App.ViewModels;

public sealed record RawFieldRow(string Name, string Address, string Type, string Verification, string Value, string Status, bool IsTrusted);

public sealed record LogRow(string Time, string Level, string Category, string Message, bool IsWarning);

/// <summary>Debug page: session capture for research, monitor statistics, raw profile fields and the live log.</summary>
public sealed partial class DebugViewModel : PageViewModel, IDisposable
{
    private const int MaxLogRows = 400;

    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
    private readonly ILogger _logger;
    private CaptureSession? _capture;

    public DebugViewModel(AppServices services, LiveState state)
        : base("Debug", "Icon.Bug")
    {
        _services = services;
        _logger = services.LoggerFactory.CreateLogger("Debug");
        State = state;
        LogFile = services.LogProvider.FilePath ?? "(file logging disabled)";
        TimerInfo = services.IsReplay ? "Replay" : services.HighResolutionTimer ? "High-resolution waitable timer" : "Default OS timer (≈15.6 ms granularity)";
        foreach (var entry in services.LogProvider.RecentEntries.TakeLast(MaxLogRows).Reverse())
        {
            LogRows.Add(ToRow(entry));
        }

        services.LogProvider.EntryLogged += entry => Dispatcher.UIThread.Post(() => AddLog(entry), DispatcherPriority.Background);
        state.PropertyChanged += OnStateChanged;

        // Capture status ticks even while the page is hidden so the nav badge stays current.
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
        RefreshRawFields();
    }

    public LiveState State { get; }

    public string LogFile { get; }

    public string TimerInfo { get; }

    public ObservableCollection<RawFieldRow> RawFields { get; } = [];

    public ObservableCollection<LogRow> LogRows { get; } = [];

    [ObservableProperty]
    public partial string PollStats { get; private set; } = "—";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCaptureCommand), nameof(StopCaptureCommand))]
    public partial bool IsCapturing { get; private set; }

    [ObservableProperty]
    public partial string CaptureStatus { get; private set; } = "Not capturing.";

    [ObservableProperty]
    public partial string? LastCaptureZip { get; private set; }

    private bool IsVisible { get; set; }

    public override void OnVisibilityChanged(bool visible)
    {
        IsVisible = visible;
        if (visible)
        {
            RefreshStats();
            RefreshRawFields();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartCapture))]
    private void StartCapture()
    {
        try
        {
            _capture = new CaptureSession(_services.Source, AppPaths.Captures, _services.Adapter.GameId, _services.AppVersion);
            IsCapturing = true;
            LastCaptureZip = null;
            _logger.LogInformation("Capture started: {Directory}", _capture.Directory);
            UpdateCaptureStatus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CaptureStatus = $"Couldn't start capture: {ex.Message}";
        }
    }

    private bool CanStartCapture() => !IsCapturing;

    [RelayCommand(CanExecute = nameof(IsCapturing))]
    private async Task StopCaptureAsync()
    {
        var capture = _capture;
        if (capture is null)
        {
            return;
        }

        _capture = null;
        IsCapturing = false;
        CaptureStatus = "Saving capture...";
        try
        {
            var zip = await Task.Run(capture.Stop).ConfigureAwait(true);
            LastCaptureZip = zip;
            CaptureStatus = $"Saved {capture.Samples:N0} samples and {capture.EventCount:N0} events.";
            _logger.LogInformation("Capture saved: {Zip}", zip);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CaptureStatus = $"Couldn't save the capture: {ex.Message}. The raw files are in {capture.Directory}.";
        }
        finally
        {
            capture.Dispose();
        }
    }

    [RelayCommand]
    private void OpenCapturesFolder()
    {
        Directory.CreateDirectory(AppPaths.Captures);
        var args = LastCaptureZip is { } zip && File.Exists(zip) ? $"/select,\"{zip}\"" : $"\"{AppPaths.Captures}\"";
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = false })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            CaptureStatus = $"Couldn't open Explorer: {ex.Message}";
        }
    }

    /// <summary>Called on app shutdown: a running capture is saved, never lost.</summary>
    public void Dispose()
    {
        _timer.Stop();
        var capture = Interlocked.Exchange(ref _capture, null);
        if (capture is not null)
        {
            try
            {
                capture.Stop();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Capture could not be zipped on exit; files remain in {Directory}", capture.Directory);
            }

            capture.Dispose();
        }
    }

    private void Tick()
    {
        if (IsCapturing)
        {
            UpdateCaptureStatus();
        }

        if (IsVisible)
        {
            RefreshStats();
        }
    }

    private void UpdateCaptureStatus()
    {
        if (_capture is not { } c)
        {
            return;
        }

        var game = c.IsRecordingGame ? "recording the game" : "waiting for a supported game";
        CaptureStatus = string.Create(CultureInfo.InvariantCulture, $"Capturing — {game}. {c.Samples:N0} samples, {c.EventCount:N0} events.");
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            RefreshRawFields();
        }
    }

    private void RefreshStats()
    {
        var s = State.Source.Statistics;
        PollStats = State.Status.State == ConnectionState.Attached
            ? string.Create(CultureInfo.InvariantCulture, $"{s.Polls:N0} polls ({s.FailedPolls:N0} failed) · {s.MeasuredPollHz:F1} Hz · read avg {s.AveragePollMs:F3} ms · max {s.MaxPollMs:F3} ms · last {s.LastPollMs:F3} ms")
            : State.Status.State == ConnectionState.Replaying ? $"{s.Polls:N0} frames replayed" : "Not attached";
    }

    private void RefreshRawFields()
    {
        RawFields.Clear();
        var profile = State.Status.Profile ?? (_services.Adapter.Profiles.Count > 0 ? _services.Adapter.Profiles[0] : null);
        if (profile is null)
        {
            return;
        }

        var raw = State.Snapshot?.RawFields;
        foreach (var f in profile.Fields)
        {
            var value = raw is not null && raw.TryGetValue(f.Name, out var v) ? v : (Field<long>?)null;
            RawFields.Add(new RawFieldRow(
                f.Name,
                $"0x{f.Address:X}",
                f.Type.ToString().ToLowerInvariant() + (f.Mask is { } m ? $" & 0x{m:X}" : string.Empty),
                f.Verification.ToString().ToLowerInvariant(),
                value is { HasValue: true } fv ? string.Create(CultureInfo.InvariantCulture, $"{fv.Value} (0x{fv.Value:X})") : "—",
                value?.Status.ToString() ?? "—",
                f.Verification >= Verification.Static));
        }
    }

    private void AddLog(LogEntry entry)
    {
        LogRows.Insert(0, ToRow(entry));
        if (LogRows.Count > MaxLogRows)
        {
            LogRows.RemoveAt(LogRows.Count - 1);
        }
    }

    private static LogRow ToRow(LogEntry e) => new(
        e.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
        e.Level.ToString().ToUpperInvariant(),
        e.Category,
        e.Exception is null ? e.Message : $"{e.Message} — {e.Exception.Split('\n')[0]}",
        e.Level >= LogLevel.Warning);
}
