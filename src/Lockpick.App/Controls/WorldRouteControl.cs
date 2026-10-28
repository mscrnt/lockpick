using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Lockpick.App.ViewModels;
using Lockpick.Core.Maps;
using Lockpick.Core.Routes;

namespace Lockpick.App.Controls;

/// <summary>
/// The route guide drawn in the game's own picture (the overlay's full-picture layer): the way across the floor of the
/// room the player is in, a ring where the step is, a puzzle's numbered spots, and the buttons to press at the bottom
/// middle of the picture. Points go through the current camera's projection (<see cref="CameraProjection"/>). The control
/// sits <see cref="Inset"/> in from the picture's top-left corner (the overlay's margin) and is drawn in the overlay's
/// units, the picture being <see cref="PictureWidth"/> × <see cref="PictureHeight"/> of them.
/// </summary>
public sealed class WorldRouteControl : Control
{
    public static readonly StyledProperty<RoomMap?> MapProperty = AvaloniaProperty.Register<WorldRouteControl, RoomMap?>(nameof(Map));
    public static readonly StyledProperty<int> CameraIdProperty = AvaloniaProperty.Register<WorldRouteControl, int>(nameof(CameraId));
    public static readonly StyledProperty<double> PlayerXProperty = AvaloniaProperty.Register<WorldRouteControl, double>(nameof(PlayerX));
    public static readonly StyledProperty<double> PlayerYProperty = AvaloniaProperty.Register<WorldRouteControl, double>(nameof(PlayerY));
    public static readonly StyledProperty<double> PlayerZProperty = AvaloniaProperty.Register<WorldRouteControl, double>(nameof(PlayerZ));
    public static readonly StyledProperty<bool> HasPlayerProperty = AvaloniaProperty.Register<WorldRouteControl, bool>(nameof(HasPlayer));
    public static readonly StyledProperty<RoutePlan?> RouteProperty = AvaloniaProperty.Register<WorldRouteControl, RoutePlan?>(nameof(Route));
    public static readonly StyledProperty<IReadOnlyList<PromptRow>?> PromptsProperty =
        AvaloniaProperty.Register<WorldRouteControl, IReadOnlyList<PromptRow>?>(nameof(Prompts));
    public static readonly StyledProperty<bool> ShowLineProperty = AvaloniaProperty.Register<WorldRouteControl, bool>(nameof(ShowLine), true);
    public static readonly StyledProperty<bool> ShowPromptsProperty = AvaloniaProperty.Register<WorldRouteControl, bool>(nameof(ShowPrompts), true);
    public static readonly StyledProperty<bool> ShowSpotProperty = AvaloniaProperty.Register<WorldRouteControl, bool>(nameof(ShowSpot), true);
    public static readonly StyledProperty<double> PictureWidthProperty = AvaloniaProperty.Register<WorldRouteControl, double>(nameof(PictureWidth));
    public static readonly StyledProperty<double> PictureHeightProperty = AvaloniaProperty.Register<WorldRouteControl, double>(nameof(PictureHeight));
    public static readonly StyledProperty<double> InsetProperty = AvaloniaProperty.Register<WorldRouteControl, double>(nameof(Inset));
    public static readonly StyledProperty<IBrush?> RouteBrushProperty = AvaloniaProperty.Register<WorldRouteControl, IBrush?>(nameof(RouteBrush));
    public static readonly StyledProperty<IBrush?> TextBrushProperty = AvaloniaProperty.Register<WorldRouteControl, IBrush?>(nameof(TextBrush));
    public static readonly StyledProperty<IBrush?> PlateBrushProperty = AvaloniaProperty.Register<WorldRouteControl, IBrush?>(nameof(PlateBrush));
    public static readonly StyledProperty<string?> ClocksProperty = AvaloniaProperty.Register<WorldRouteControl, string?>(nameof(Clocks));
    public static readonly StyledProperty<IBrush?> ClockBrushProperty = AvaloniaProperty.Register<WorldRouteControl, IBrush?>(nameof(ClockBrush));

    /// <summary>Lines are cut into pieces this long (room units) so a stretch partly behind the camera still shows.</summary>
    private const double Piece = 250;

    static WorldRouteControl()
    {
        AffectsRender<WorldRouteControl>(MapProperty, CameraIdProperty, PlayerXProperty, PlayerYProperty, PlayerZProperty, HasPlayerProperty,
            RouteProperty, PromptsProperty, ShowLineProperty, ShowPromptsProperty, ShowSpotProperty, PictureWidthProperty, PictureHeightProperty, InsetProperty,
            RouteBrushProperty, TextBrushProperty, PlateBrushProperty, ClocksProperty, ClockBrushProperty);
    }

    public RoomMap? Map { get => GetValue(MapProperty); set => SetValue(MapProperty, value); }

    /// <summary>The camera the game shows now (its index in the room's camera list).</summary>
    public int CameraId { get => GetValue(CameraIdProperty); set => SetValue(CameraIdProperty, value); }

    public double PlayerX { get => GetValue(PlayerXProperty); set => SetValue(PlayerXProperty, value); }

    /// <summary>The player's height (the floor they stand on: the way is drawn at it).</summary>
    public double PlayerY { get => GetValue(PlayerYProperty); set => SetValue(PlayerYProperty, value); }

    public double PlayerZ { get => GetValue(PlayerZProperty); set => SetValue(PlayerZProperty, value); }

    public bool HasPlayer { get => GetValue(HasPlayerProperty); set => SetValue(HasPlayerProperty, value); }

    public RoutePlan? Route { get => GetValue(RouteProperty); set => SetValue(RouteProperty, value); }

    /// <summary>The buttons to press now, drawn at the bottom middle of the picture.</summary>
    public IReadOnlyList<PromptRow>? Prompts { get => GetValue(PromptsProperty); set => SetValue(PromptsProperty, value); }

    public bool ShowLine { get => GetValue(ShowLineProperty); set => SetValue(ShowLineProperty, value); }

    public bool ShowPrompts { get => GetValue(ShowPromptsProperty); set => SetValue(ShowPromptsProperty, value); }

    /// <summary>The ring where the step is and a puzzle's numbers (apart from the line, which <see cref="ShowLine"/> draws).</summary>
    public bool ShowSpot { get => GetValue(ShowSpotProperty); set => SetValue(ShowSpotProperty, value); }

    public double PictureWidth { get => GetValue(PictureWidthProperty); set => SetValue(PictureWidthProperty, value); }

    public double PictureHeight { get => GetValue(PictureHeightProperty); set => SetValue(PictureHeightProperty, value); }

    public double Inset { get => GetValue(InsetProperty); set => SetValue(InsetProperty, value); }

    public IBrush? RouteBrush { get => GetValue(RouteBrushProperty); set => SetValue(RouteBrushProperty, value); }

    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }

    public IBrush? PlateBrush { get => GetValue(PlateBrushProperty); set => SetValue(PlateBrushProperty, value); }

    /// <summary>Time limits running (a line each), drawn at the top middle.</summary>
    public string? Clocks { get => GetValue(ClocksProperty); set => SetValue(ClocksProperty, value); }

    public IBrush? ClockBrush { get => GetValue(ClockBrushProperty); set => SetValue(ClockBrushProperty, value); }

    public override void Render(DrawingContext context)
    {
        if (Map is not { } map || Route is not { } plan || RouteBrush is not { } brush || PictureWidth <= 0 || PictureHeight <= 0
            || map.Cameras.FirstOrDefault(c => c.Id == CameraId) is not { Fov: > 0 } camera)
        {
            return;
        }

        var floor = HasPlayer ? PlayerY : 0;
        Point? At(double x, double z, double y) => CameraProjection.Project(camera, x, y, z) is { } p
            ? new Point(-Inset + (p.X * PictureWidth / CameraProjection.PictureWidth), -Inset + (p.Y * PictureHeight / CameraProjection.PictureHeight))
            : null;

        if (ShowLine && plan.Legs is [{ } leg, ..] && leg.Room == map.RoomId)
        {
            var line = leg.Line.ToList();
            if (HasPlayer && line.Count > 0)
            {
                line[0] = new RoutePoint(PlayerX, PlayerZ);
            }

            DrawFloorLine(context, line, floor, brush, At);
        }

        if (plan.Legs is [{ } here, ..] && here.Room == map.RoomId)
        {
            // The ring and the prompts' place: the step's spot in this room, else the door the way leaves by.
            var target = plan.Goal.Room == map.RoomId && plan.Goal.Point is { } spot ? spot
                : here.Exit is { } door ? new RoutePoint(door.CenterX, door.CenterZ)
                : (RoutePoint?)null;
            if (target is { } t && At(t.X, t.Z, floor) is { } ring)
            {
                if (ShowSpot)
                {
                    context.DrawEllipse(null, new Pen(Brushes.Black, 6), ring, 14, 7);
                    context.DrawEllipse(null, new Pen(brush, 3), ring, 14, 7);
                }
            }
        }

        if (plan.SpotsRoom == map.RoomId && ShowSpot)
        {
            for (var i = 0; i < plan.Spots.Count; i++)
            {
                var c = plan.Spots[i].Centre;
                if (At(c.X, c.Z, floor) is { } p)
                {
                    DrawSpot(context, p, i, plan.SpotsDone, brush);
                }
            }
        }

        if (ShowPrompts && Prompts is { Count: > 0 } prompts && HasPlayer)
        {
            DrawPrompts(context, prompts);
        }

        if (Clocks is { Length: > 0 } clocks)
        {
            var text = new FormattedText(clocks, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), 15, ClockBrush ?? Brushes.Orange)
            {
                TextAlignment = TextAlignment.Center,
                MaxTextWidth = Math.Max(100, Bounds.Width - 40),
            };
            // Centred lines sit in the middle of MaxTextWidth: the text goes there, the plate around its widest line.
            var box = new Rect(((Bounds.Width - text.Width) / 2) - 8, 8, text.Width + 16, text.Height + 6);
            context.DrawRectangle(PlateBrush, new Pen(ClockBrush ?? Brushes.Orange, 1.5), box, 4, 4);
            context.DrawText(text, new Point((Bounds.Width - text.MaxTextWidth) / 2, box.Y + 3));
        }
    }

    private static void DrawFloorLine(DrawingContext context, List<RoutePoint> line, double floor, IBrush brush, Func<double, double, double, Point?> at)
    {
        var under = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), 7, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var pen = new Pen(brush, 4, new DashStyle([2.5, 1.5], 0), PenLineCap.Round, PenLineJoin.Round);
        var runs = new List<List<Point>>();
        List<Point>? run = null;
        for (var i = 1; i < line.Count; i++)
        {
            var (a, b) = (line[i - 1], line[i]);
            var pieces = Math.Max(1, (int)Math.Ceiling(a.DistanceTo(b) / Piece));
            for (var k = i == 1 ? 0 : 1; k <= pieces; k++)
            {
                var t = (double)k / pieces;
                if (at(a.X + ((b.X - a.X) * t), a.Z + ((b.Z - a.Z) * t), floor) is { } p)
                {
                    if (run is null)
                    {
                        run = [];
                        runs.Add(run);
                    }

                    run.Add(p);
                }
                else
                {
                    run = null; // behind the camera: the line resumes where it comes back into view
                }
            }
        }

        foreach (var points in runs.Where(r => r.Count > 1))
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(points[0], false);
                foreach (var p in points.Skip(1))
                {
                    g.LineTo(p);
                }

                g.EndFigure(false);
            }

            context.DrawGeometry(null, under, geometry);
            context.DrawGeometry(null, pen, geometry);
        }
    }

    private void DrawSpot(DrawingContext context, Point at, int index, int done, IBrush brush)
    {
        var plate = PlateBrush ?? Brushes.Black;
        var dim = new SolidColorBrush(Color.FromArgb(0xFF, 0x5A, 0x67, 0x62));
        (IBrush Fill, IBrush Ring, IBrush Text) look = index < done ? (plate, dim, dim) : index == done ? (brush, brush, plate) : (plate, brush, brush);
        context.DrawEllipse(look.Fill, new Pen(look.Ring, 2.5), at, 12, 12);
        var text = new FormattedText(index < done ? "✓" : (index + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), 14, look.Text);
        context.DrawText(text, at - new Vector(text.Width / 2, text.Height / 2));
    }

    /// <summary>The prompts on a plate at the bottom middle of the picture: each row's buttons, then what they do.</summary>
    private void DrawPrompts(DrawingContext context, IReadOnlyList<PromptRow> prompts)
    {
        const double Icon = 22, Gap = 4, Pad = 6, RowGap = 4;
        var text = TextBrush ?? Brushes.White;
        var rows = prompts.Select(p => (p.Buttons, Text: new FormattedText(p.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, 13, text))).ToList();
        double Width((IReadOnlyList<Services.ButtonGlyph> Buttons, FormattedText Text) row) =>
            row.Buttons.Sum(b => (b.Image is null ? KeyWidth(b.Label) : Icon) + Gap) + row.Text.Width;
        var width = rows.Max(Width) + (2 * Pad);
        var height = (rows.Count * Icon) + ((rows.Count - 1) * RowGap) + (2 * Pad);
        var left = Math.Max(0, (Bounds.Width - width) / 2);
        var top = Math.Max(0, Bounds.Height - height - 24);
        context.DrawRectangle(PlateBrush, new Pen(RouteBrush, 1.5), new Rect(left, top, width, height), 4, 4);
        var y = top + Pad;
        foreach (var (buttons, label) in rows)
        {
            var x = left + Pad;
            foreach (var button in buttons)
            {
                if (button.Image is { } image)
                {
                    context.DrawImage(image, new Rect(x, y, Icon, Icon));
                    x += Icon + Gap;
                }
                else
                {
                    var key = new FormattedText(button.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                        new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), 12, text);
                    var w = KeyWidth(button.Label);
                    context.DrawRectangle(null, new Pen(text, 1.2), new Rect(x, y + 1, w, Icon - 2), 3, 3);
                    context.DrawText(key, new Point(x + ((w - key.Width) / 2), y + ((Icon - key.Height) / 2)));
                    x += w + Gap;
                }
            }

            context.DrawText(label, new Point(x, y + ((Icon - label.Height) / 2)));
            y += Icon + RowGap;
        }
    }

    private static double KeyWidth(string label) => Math.Max(22, (label.Length * 8) + 10);
}
