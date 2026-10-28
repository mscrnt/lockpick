namespace Lockpick.App.Services;

/// <summary>
/// Where a widget placed by hand sits on the game's picture: 0 at the left (top) edge, 1 at the right (bottom) edge
/// of the room the widget has (the picture less the margins and the widget itself), so it stays whole on any size.
/// </summary>
public readonly record struct WidgetSpot(double X, double Y);

/// <summary>A widget as the layout editor sees it: where it sits (a corner, <see cref="OverlayCorners.Custom"/> with its
/// spot, or <see cref="OverlayLayout.Off"/>) and its size in percent.</summary>
public interface ILayoutRow
{
    string Id { get; }

    string Corner { get; }

    WidgetSpot? Spot { get; }

    int SizePercent { get; }

    /// <summary>In a window of its own, so not on this layout for now.</summary>
    bool PoppedOut { get; }
}

/// <summary>
/// Places the overlay's widgets on the game's picture, for the overlay and for the layout editor's preview alike:
/// widgets in the same corner stack from it; a widget placed by hand sits at its spot; a full-screen one (the room
/// or floor map) covers the picture.
/// </summary>
public static class OverlayPlacement
{
    /// <summary>A widget to place: its corner (or <see cref="OverlayCorners.Custom"/> with a spot) and its size.</summary>
    public sealed record Item(string Id, string Corner, WidgetSpot? Spot, double Width, double Height, bool Full);

    /// <summary>Each widget's top-left corner on a picture at (<paramref name="x"/>, <paramref name="y"/>) of the given size.</summary>
    public static Dictionary<string, (double X, double Y)> Place(IEnumerable<Item> items, double x, double y, double width, double height, double margin)
    {
        var list = items.ToList();
        var placed = new Dictionary<string, (double X, double Y)>();
        foreach (var corner in OverlayCorners.All)
        {
            var left = corner is OverlayCorners.TopLeft or OverlayCorners.BottomLeft;
            var centre = corner == OverlayCorners.TopCentre;
            var top = corner is OverlayCorners.TopLeft or OverlayCorners.TopRight or OverlayCorners.TopCentre;
            var offset = margin; // from the corner, along the picture's height
            foreach (var item in list.Where(i => i.Corner == corner))
            {
                if (item.Full)
                {
                    placed[item.Id] = (x + margin, y + margin); // over the whole picture, out of the corner's stack
                    continue;
                }

                placed[item.Id] = (
                    centre ? x + ((width - item.Width) / 2) : left ? x + margin : x + width - margin - item.Width,
                    top ? y + offset : y + height - offset - item.Height);
                offset += item.Height + margin;
            }
        }

        foreach (var item in list.Where(i => i.Corner == OverlayCorners.Custom))
        {
            var spot = item.Spot ?? new WidgetSpot(1, 0);
            placed[item.Id] = item.Full
                ? (x + margin, y + margin)
                : (x + margin + (Math.Clamp(spot.X, 0, 1) * Math.Max(0, width - (2 * margin) - item.Width)),
                   y + margin + (Math.Clamp(spot.Y, 0, 1) * Math.Max(0, height - (2 * margin) - item.Height)));
        }

        return placed;
    }

    /// <summary>The spot that puts a widget of this size with its top-left corner at (<paramref name="left"/>, <paramref name="top"/>).</summary>
    public static WidgetSpot SpotFor(double left, double top, double itemWidth, double itemHeight, double x, double y, double width, double height, double margin)
    {
        var roomX = width - (2 * margin) - itemWidth;
        var roomY = height - (2 * margin) - itemHeight;
        return new WidgetSpot(
            roomX > 0 ? Math.Clamp((left - x - margin) / roomX, 0, 1) : 0,
            roomY > 0 ? Math.Clamp((top - y - margin) / roomY, 0, 1) : 0);
    }
}
