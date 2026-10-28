using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lockpick.App.Services;

namespace Lockpick.App.ViewModels;

/// <summary>One overlay widget in Settings: the corner it sits in (or off), and its own window (pop-out).</summary>
public sealed partial class OverlayWidgetRow : ObservableObject, ILayoutRow
{
    private readonly Action<OverlayWidgetRow> _apply;
    private readonly WidgetWindows _windows;
    private readonly bool _loading = true;

    public OverlayWidgetRow(string id, string name, string corner, IReadOnlyList<string> choices, Action<OverlayWidgetRow> apply, WidgetWindows windows, int size = 100)
    {
        Id = id;
        Name = name;
        Choices = choices;
        _apply = apply;
        _windows = windows;
        Corner = corner;
        Size = $"{size}%";
        _loading = false;
        windows.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(PopOutText));
            OnPropertyChanged(nameof(PoppedOut));
        };
    }

    /// <summary>Where the widget sits when placed by hand (the layout editor), or null.</summary>
    public WidgetSpot? Spot { get; set; }

    /// <summary>The pop-out button's text: open the widget's window, or close it.</summary>
    public string PopOutText => _windows.IsOpen(Id) ? "Close window" : "Pop out";

    /// <summary>The widget is in its own window, so it's off the game's picture until that window closes.</summary>
    public bool PoppedOut => _windows.IsOpen(Id);

    [RelayCommand]
    private void PopOut() => _windows.Toggle(Id);

    public string Id { get; }

    public string Name { get; }

    public IReadOnlyList<string> Choices { get; }

    [ObservableProperty]
    public partial string Corner { get; set; }

    /// <summary>The widget's size on the overlay ("100%" is the usual).</summary>
    [ObservableProperty]
    public partial string Size { get; set; } = "100%";

    public static IReadOnlyList<string> SizeChoices { get; } = [.. OverlayLayout.Sizes.Select(s => $"{s}%")];

    /// <summary>Shows a loaded layout's values without saving them again (they are saved already).</summary>
    public void Load(string corner, int size, WidgetSpot? spot)
    {
        _reloading = true;
        Spot = spot;
        Corner = corner;
        Size = $"{size}%";
        _reloading = false;
    }

    private bool _reloading;

    /// <summary>The size as a number (100 = the usual).</summary>
    public int SizePercent => int.TryParse(Size.TrimEnd('%'), out var value) ? value : 100;

    partial void OnSizeChanged(string value)
    {
        if (!_loading && !_reloading && SizeChoices.Contains(value))
        {
            _apply(this);
        }
    }

    partial void OnCornerChanged(string value)
    {
        if (!_loading && !_reloading && Choices.Contains(value))
        {
            _apply(this);
        }
    }
}
