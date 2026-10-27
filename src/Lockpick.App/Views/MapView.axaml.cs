using Avalonia;
using Avalonia.Controls;

namespace Lockpick.App.Views;

/// <summary>
/// The map gets the space. The legend sits beside it in a wide window and under it in a tall one (portrait), where the
/// map is held back by the width and the height would go unused; elsewhere it is hidden until the player asks for it
/// (Legend). A short window also drops the details line under the room name.
/// </summary>
public partial class MapView : UserControl
{
    private const double LegendBesideWidth = 1100;
    private const double ShortHeight = 640;
    private const double NarrowWidth = 560;

    /// <summary>How much taller than wide the window must be before the legend has room under the map.</summary>
    private const double LegendBelowSpare = 300;

    private bool? _legendChosen;
    private bool _arranging;

    public MapView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => Arrange(e.NewSize);
        LegendToggle.IsCheckedChanged += (_, _) =>
        {
            if (!_arranging)
            {
                _legendChosen = LegendToggle.IsChecked == true;
                Arrange(Bounds.Size);
            }
        };
    }

    private void OnFit(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => FloorMap.Fit();

    private void Arrange(Size size)
    {
        _arranging = true;
        var wide = size.Width >= LegendBesideWidth;
        var showLegend = _legendChosen ?? (wide || size.Height - size.Width >= LegendBelowSpare);
        LegendToggle.IsChecked = showLegend;
        LegendCard.IsVisible = showLegend;
        DetailsText.IsVisible = size.Height >= ShortHeight;

        // Narrow (portrait): the room name keeps the whole first row, the buttons go under it.
        var narrow = size.Width < NarrowWidth;
        Grid.SetRow(HeaderButtons, narrow ? 1 : 0);
        Grid.SetColumn(HeaderButtons, narrow ? 1 : 2);
        HeaderButtons.Margin = narrow ? new Thickness(0, 6, 0, 0) : new Thickness(8, 0, 0, 0);
        HeaderButtons.HorizontalAlignment = narrow ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Right;

        Grid.SetRow(LegendCard, wide ? 2 : 3);
        Grid.SetColumn(LegendCard, wide ? 1 : 0);
        Grid.SetColumnSpan(LegendCard, wide ? 1 : 2);
        Grid.SetColumnSpan(MapCard, wide ? 1 : 2);
        LegendCard.Margin = wide ? new Thickness(8, 0, 0, 0) : new Thickness(0, 8, 0, 0);
        LegendCard.MaxHeight = wide ? double.PositiveInfinity : Math.Max(120, size.Height * 0.3);
        _arranging = false;
    }
}
