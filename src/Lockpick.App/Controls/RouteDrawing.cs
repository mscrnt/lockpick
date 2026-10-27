using Avalonia;
using Avalonia.Media;
using Lockpick.Core.Routes;

namespace Lockpick.App.Controls;

/// <summary>Draws the route guide's way on a map: a line per room and a ring where the step is.</summary>
internal static class RouteDrawing
{
    /// <summary>
    /// Draws the legs <paramref name="place"/> can place (it maps a room's own units to the screen, or returns null for
    /// a room not on this map).
    /// </summary>
    /// <param name="player">The player's position now in the first room's units (the line is worked out four times a
    /// second, the player drawn more often: the line starts where the player is, not where they were).</param>
    public static void Draw(DrawingContext context, RoutePlan? plan, IBrush? brush, Func<string, Func<double, double, Point>?> place,
        (double X, double Z)? player = null)
    {
        if (plan is null || brush is null)
        {
            return;
        }

        var under = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)), 5.5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var pen = new Pen(brush, 3, new DashStyle([2, 1.5], 0), PenLineCap.Round, PenLineJoin.Round);
        for (var i = 0; i < plan.Legs.Count; i++)
        {
            var leg = plan.Legs[i];
            if (place(leg.Room) is not { } p || leg.Line.Count < 2)
            {
                continue;
            }

            var start = i == 0 && player is { } now ? p(now.X, now.Z) : p(leg.Line[0].X, leg.Line[0].Z);
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(start, false);
                foreach (var point in leg.Line.Skip(1))
                {
                    g.LineTo(p(point.X, point.Z));
                }

                g.EndFigure(false);
            }

            context.DrawGeometry(null, under, geometry);
            context.DrawGeometry(null, pen, geometry);
        }

        if (plan.SpotsRoom is { } spotsRoom && place(spotsRoom) is { } s)
        {
            // A puzzle's spots, numbered in the order to press them: pressed ones dim, the next one filled.
            var plate = new SolidColorBrush(Color.FromArgb(0xE0, 0x0B, 0x0F, 0x0E));
            var dim = new SolidColorBrush(Color.FromArgb(0xFF, 0x5A, 0x67, 0x62));
            for (var i = 0; i < plan.Spots.Count; i++)
            {
                var c = plan.Spots[i].Centre;
                var at = s(c.X, c.Z);
                (IBrush Fill, IBrush Ring, IBrush Text) look = i < plan.SpotsDone ? (plate, dim, dim)
                    : i == plan.SpotsDone ? (brush, brush, plate)
                    : (plate, brush, brush);
                var (fill, ring, text) = look;
                context.DrawEllipse(fill, new Pen(ring, 2), at, 9, 9);
                var label = i < plan.SpotsDone ? "✓" : (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var number = new FormattedText(label, System.Globalization.CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), 11, text);
                context.DrawText(number, at - new Vector(number.Width / 2, number.Height / 2));
            }
        }

        if (place(plan.Goal.Room) is { } goal)
        {
            var at = plan.Goal.Point is { } spot ? goal(spot.X, spot.Z) : plan.Legs[^1].Line is [.., var last] ? goal(last.X, last.Z) : default;
            context.DrawEllipse(null, new Pen(Brushes.Black, 5), at, 8, 8);
            context.DrawEllipse(null, new Pen(brush, 2.5), at, 8, 8);
        }
    }
}
