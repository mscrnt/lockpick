using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace Lockpick.App.Views;

/// <summary>
/// Hosts the overlay widgets without ever being shown (a toolkit window over exclusive fullscreen ended up below the game;
/// docs/DISPLAY.md): <see cref="Render"/> draws the panel to premultiplied BGRA pixels for
/// <see cref="Platform.Windows.LayeredWindow"/>, which shows them over the game.
/// </summary>
public partial class OverlayWindow : Window
{
    /// <summary>The most rows the enemies widget lists; the rest are counted.</summary>
    private const int MaxEnemyRows = 6;

    public OverlayWindow() => InitializeComponent();

    /// <summary>Fills the enemies widget: a row per enemy with its name, HP and a bar.</summary>
    public void ShowEnemies(IReadOnlyList<ViewModels.EnemyRow> enemies)
    {
        EnemyList.Children.Clear();
        var muted = Brush("LpMutedBrush");
        foreach (var enemy in enemies.Take(MaxEnemyRows))
        {
            var color = new SolidColorBrush(enemy.Color);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 6 };
            row.Children.Add(new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = color, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            var name = new TextBlock { Text = enemy.Name, FontSize = 10, Foreground = enemy.IsBoss ? color : MainText, FontWeight = enemy.IsBoss ? FontWeight.SemiBold : FontWeight.Normal };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var hp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right };
            hp.Children.Add(new TextBlock { Text = enemy.Hp.ToString(System.Globalization.CultureInfo.InvariantCulture), FontSize = 10, Foreground = muted, FontFamily = Font("LpMono") });
            hp.Children.Add(new Border
            {
                Width = 50,
                Height = 4,
                CornerRadius = new CornerRadius(1),
                Background = Brush("LpPanelAltBrush"),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Child = new Border { Width = Math.Round(50 * enemy.Fraction, 1), CornerRadius = new CornerRadius(1), Background = color, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left },
            });
            Grid.SetColumn(hp, 2);
            row.Children.Add(hp);
            EnemyList.Children.Add(row);
        }

        if (enemies.Count > MaxEnemyRows)
        {
            EnemyList.Children.Add(new TextBlock { Text = $"+{enemies.Count - MaxEnemyRows} more", FontSize = 9, Foreground = muted });
        }
    }

    /// <summary>Fills the route widget: the step, where it is, its hint, the buttons to press and what comes next.</summary>
    public void ShowRoute(ViewModels.RouteViewModel guide)
    {
        RouteList.Children.Clear();
        if (!guide.HasStep)
        {
            return;
        }

        var muted = Brush("LpMutedBrush");
        RouteList.Children.Add(new TextBlock { Text = guide.Step, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = MainText, TextWrapping = TextWrapping.Wrap });
        RouteList.Children.Add(new TextBlock { Text = guide.Where, FontSize = 9, Foreground = muted, TextWrapping = TextWrapping.Wrap });
        if (guide.Clocks.Length > 0)
        {
            RouteList.Children.Add(new TextBlock { Text = guide.Clocks, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Brush("LpAmberBrush"), TextWrapping = TextWrapping.Wrap });
        }
        if (guide.Hint.Length > 0)
        {
            RouteList.Children.Add(new TextBlock { Text = guide.Hint, FontSize = 10, Foreground = MainText, TextWrapping = TextWrapping.Wrap });
        }

        foreach (var prompt in guide.Prompts)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Margin = new Thickness(0, 1, 0, 0) };
            foreach (var button in prompt.Buttons)
            {
                row.Children.Add(button.Image is { } image
                    ? new Image { Source = image, Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center }
                    : new Border
                    {
                        Background = Brush("LpPanelAltBrush"),
                        BorderBrush = Brush("LpBorderBrush"),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(2),
                        Padding = new Thickness(4, 0),
                        MinWidth = 16,
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new TextBlock { Text = button.Label, FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = MainText, HorizontalAlignment = HorizontalAlignment.Center },
                    });
            }

            row.Children.Add(new TextBlock { Text = prompt.Text, FontSize = 10, Foreground = MainText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) });
            RouteList.Children.Add(row);
        }

        if (guide.Note.Length > 0)
        {
            RouteList.Children.Add(new TextBlock { Text = guide.Note, FontSize = 9, Foreground = Brush("LpAmberBrush"), TextWrapping = TextWrapping.Wrap });
        }

        if (guide.Next.Length > 0)
        {
            RouteList.Children.Add(new TextBlock { Text = guide.Next, FontSize = 9, Foreground = muted, TextWrapping = TextWrapping.Wrap });
        }
    }

    /// <summary>Fills the event feed widget: a line per thing that happened, the newest brightest.</summary>
    public void ShowFeed(IReadOnlyList<string> lines)
    {
        FeedList.Children.Clear();
        for (var i = 0; i < lines.Count; i++)
        {
            FeedList.Children.Add(new TextBlock
            {
                Text = lines[i],
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Foreground = i == lines.Count - 1 ? MainText : Brush("LpMutedBrush"),
            });
        }
    }

    /// <summary>Fills the splits widget: the category, a row per split shown, the run's clock and the comparison.</summary>
    public void ShowSplits(ViewModels.OverlayViewModel model)
    {
        SplitList.Children.Clear();
        var muted = Brush("LpMutedBrush");
        var text = MainText;
        var mono = Font("LpMono");
        SplitList.Children.Add(new TextBlock { Text = model.RunTitle, FontSize = 9, Foreground = muted, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 2) });
        foreach (var row in model.SplitRows)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 6 };
            grid.Children.Add(new TextBlock { Text = row.Name, FontSize = 10, Foreground = row.IsDone || row.IsCurrent ? text : muted, TextTrimming = TextTrimming.CharacterEllipsis });
            var delta = new TextBlock { Text = row.Delta, FontSize = 10, FontFamily = mono, Foreground = DeltaBrush(row.Mark) };
            Grid.SetColumn(delta, 1);
            grid.Children.Add(delta);
            var time = new TextBlock { Text = row.Time, FontSize = 10, FontFamily = mono, Foreground = row.IsDone ? text : muted, MinWidth = 44, TextAlignment = TextAlignment.Right };
            Grid.SetColumn(time, 2);
            grid.Children.Add(time);
            SplitList.Children.Add(new Border
            {
                Child = grid,
                Padding = new Thickness(3, 1),
                CornerRadius = new CornerRadius(2),
                Background = row.IsCurrent ? Brush("LpGreenDimBrush") : Brushes.Transparent,
            });
        }

        SplitList.Children.Add(new TextBlock
        {
            Text = model.RunClock,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            FontFamily = mono,
            Foreground = model.RunBehind ? Brush("LpRedBrush") : Brush("LpGreenBrush"),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Margin = new Thickness(0, 2, 0, 0),
        });
        if (model.RunComparison.Length > 0)
        {
            SplitList.Children.Add(new TextBlock { Text = model.RunComparison, FontSize = 9, Foreground = muted, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right });
        }
    }

    private static IBrush DeltaBrush(ViewModels.SplitMark mark) => mark switch
    {
        ViewModels.SplitMark.Ahead => Brush("LpGreenBrush"),
        ViewModels.SplitMark.Behind => Brush("LpRedBrush"),
        ViewModels.SplitMark.Gold => new SolidColorBrush(Color.Parse("#E8C547")),
        _ => Brush("LpMutedBrush"),
    };

    /// <summary>The look's text colour (Settings → Overlay), or the app's.</summary>
    private IBrush MainText => (DataContext as ViewModels.OverlayViewModel)?.TextBrush ?? Brush("LpTextBrush");

    private static IBrush Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : Brushes.White;

    private static FontFamily Font(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is FontFamily font ? font : FontFamily.Default;

    /// <summary>The panel's own size (in the overlay's units, at scale 1), for fitting it into a box.</summary>
    public static Size Measure(LayoutTransformControl panel)
    {
        panel.LayoutTransform = new Avalonia.Media.ScaleTransform(1, 1);
        foreach (var control in panel.GetSelfAndVisualDescendants().OfType<Layoutable>())
        {
            control.InvalidateMeasure();
        }

        panel.Measure(Size.Infinity);
        return panel.DesiredSize;
    }

    /// <summary>
    /// Renders <paramref name="panel"/> (<see cref="StatusPanel"/>, <see cref="MinimapPanel"/>) at
    /// <paramref name="size"/> (1 = designed for a 480-pixel-high picture) for a monitor at <paramref name="dpi"/>;
    /// returns its pixels and size in physical pixels.
    /// </summary>
    public static (byte[] Pixels, int Width, int Height) Render(LayoutTransformControl panel, double size, int dpi)
    {
        var scale = Math.Max(1, dpi) / 96.0;
        panel.LayoutTransform = new Avalonia.Media.ScaleTransform(size, size);
        // No layout pass runs in a window that is never shown, and a parent measured with the same space skips its
        // children: invalidate the whole panel so a changed size inside (e.g. the minimap's frame) is measured again.
        foreach (var control in panel.GetSelfAndVisualDescendants().OfType<Layoutable>())
        {
            control.InvalidateMeasure();
            control.InvalidateArrange();
        }

        panel.Measure(Size.Infinity);
        panel.Arrange(new Rect(panel.DesiredSize));
        var pixelSize = new PixelSize(
            Math.Max(1, (int)Math.Ceiling(panel.DesiredSize.Width * scale)),
            Math.Max(1, (int)Math.Ceiling(panel.DesiredSize.Height * scale)));
        using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96 * scale, 96 * scale));
        bitmap.Render(panel);
        var pixels = new byte[pixelSize.Width * pixelSize.Height * 4];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(pixelSize), handle.AddrOfPinnedObject(), pixels.Length, pixelSize.Width * 4);
        }
        finally
        {
            handle.Free();
        }

        return (pixels, pixelSize.Width, pixelSize.Height);
    }
}
