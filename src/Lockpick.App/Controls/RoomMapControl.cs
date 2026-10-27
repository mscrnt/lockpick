using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Lockpick.Core.Maps;
using Lockpick.Core.State;

namespace Lockpick.App.Controls;

/// <summary>
/// Draws a room map top-down (X right, Z up) scaled to fit: blocking collision (walls, furniture), soft zones, doors
/// with their target room, item pickups with their names, and the player as an arrow pointing the way they face.
/// Facing uses the game's angle units (4096 per turn; 0 = +X, 1024 = −Z).
/// </summary>
public sealed class RoomMapControl : Control
{
    public static readonly StyledProperty<RoomMap?> MapProperty = AvaloniaProperty.Register<RoomMapControl, RoomMap?>(nameof(Map));
    public static readonly StyledProperty<double> PlayerXProperty = AvaloniaProperty.Register<RoomMapControl, double>(nameof(PlayerX));
    public static readonly StyledProperty<double> PlayerZProperty = AvaloniaProperty.Register<RoomMapControl, double>(nameof(PlayerZ));
    public static readonly StyledProperty<double> PlayerAngleProperty = AvaloniaProperty.Register<RoomMapControl, double>(nameof(PlayerAngle));
    public static readonly StyledProperty<IReadOnlyList<PlacedItem>?> PlacedItemsProperty =
        AvaloniaProperty.Register<RoomMapControl, IReadOnlyList<PlacedItem>?>(nameof(PlacedItems));
    public static readonly StyledProperty<IReadOnlySet<MapItem>?> TakenItemsProperty =
        AvaloniaProperty.Register<RoomMapControl, IReadOnlySet<MapItem>?>(nameof(TakenItems));
    public static readonly StyledProperty<IReadOnlySet<MapSocket>?> FilledSocketsProperty =
        AvaloniaProperty.Register<RoomMapControl, IReadOnlySet<MapSocket>?>(nameof(FilledSockets));
    public static readonly StyledProperty<IReadOnlyDictionary<string, string>?> DoorNamesProperty =
        AvaloniaProperty.Register<RoomMapControl, IReadOnlyDictionary<string, string>?>(nameof(DoorNames));
    public static readonly StyledProperty<IReadOnlyList<LiveActor>?> ActorsProperty =
        AvaloniaProperty.Register<RoomMapControl, IReadOnlyList<LiveActor>?>(nameof(Actors));
    public static readonly StyledProperty<IReadOnlyList<PlacedFixture>?> LiveFixturesProperty =
        AvaloniaProperty.Register<RoomMapControl, IReadOnlyList<PlacedFixture>?>(nameof(LiveFixtures));
    public static readonly StyledProperty<bool> ShowAbsentItemsProperty =
        AvaloniaProperty.Register<RoomMapControl, bool>(nameof(ShowAbsentItems));
    public static readonly StyledProperty<bool> HasPlayerProperty = AvaloniaProperty.Register<RoomMapControl, bool>(nameof(HasPlayer));
    public static readonly StyledProperty<IBrush?> WallBrushProperty = AvaloniaProperty.Register<RoomMapControl, IBrush?>(nameof(WallBrush));
    public static readonly StyledProperty<IBrush?> SoftBrushProperty = AvaloniaProperty.Register<RoomMapControl, IBrush?>(nameof(SoftBrush));
    public static readonly StyledProperty<IBrush?> DoorBrushProperty = AvaloniaProperty.Register<RoomMapControl, IBrush?>(nameof(DoorBrush));
    public static readonly StyledProperty<IBrush?> ItemBrushProperty = AvaloniaProperty.Register<RoomMapControl, IBrush?>(nameof(ItemBrush));
    public static readonly StyledProperty<IBrush?> PlayerBrushProperty = AvaloniaProperty.Register<RoomMapControl, IBrush?>(nameof(PlayerBrush));
    public static readonly StyledProperty<IBrush?> FaintTextBrushProperty = AvaloniaProperty.Register<RoomMapControl, IBrush?>(nameof(FaintTextBrush));
    public static readonly StyledProperty<IBrush?> LabelPlateBrushProperty = AvaloniaProperty.Register<RoomMapControl, IBrush?>(nameof(LabelPlateBrush));

    /// <summary>When above 0 (and the player is known): a view centred on the player showing this many world units around
    /// them (the minimap's local view) instead of the whole room fitted in.</summary>
    public static readonly StyledProperty<double> ViewRadiusProperty = AvaloniaProperty.Register<RoomMapControl, double>(nameof(ViewRadius));

    /// <summary>When set, the room's extent is filled first, so floor and the space outside the room differ.</summary>
    public static readonly StyledProperty<IBrush?> FloorBrushProperty = AvaloniaProperty.Register<RoomMapControl, IBrush?>(nameof(FloorBrush));

    /// <summary>Draw names (doors, items, people); off for the small overlay minimap.</summary>
    public static readonly StyledProperty<bool> ShowLabelsProperty = AvaloniaProperty.Register<RoomMapControl, bool>(nameof(ShowLabels), true);
    public static readonly StyledProperty<Lockpick.Core.Routes.RoutePlan?> RouteProperty = AvaloniaProperty.Register<RoomMapControl, Lockpick.Core.Routes.RoutePlan?>(nameof(Route));
    public static readonly StyledProperty<IBrush?> RouteBrushProperty = AvaloniaProperty.Register<RoomMapControl, IBrush?>(nameof(RouteBrush));
    public static readonly StyledProperty<IReadOnlyDictionary<MapDoor, bool>?> LockedDoorsProperty = AvaloniaProperty.Register<RoomMapControl, IReadOnlyDictionary<MapDoor, bool>?>(nameof(LockedDoors));
    public static readonly StyledProperty<IBrush?> LockedBrushProperty = AvaloniaProperty.Register<RoomMapControl, IBrush?>(nameof(LockedBrush));
    public static readonly StyledProperty<IBrush?> KeyBrushProperty = AvaloniaProperty.Register<RoomMapControl, IBrush?>(nameof(KeyBrush));

    private const double Inset = 12;

    static RoomMapControl()
    {
        AffectsRender<RoomMapControl>(MapProperty, PlacedItemsProperty, TakenItemsProperty, FilledSocketsProperty, DoorNamesProperty, ActorsProperty, LiveFixturesProperty, ShowAbsentItemsProperty, PlayerXProperty, PlayerZProperty, PlayerAngleProperty, HasPlayerProperty,
            WallBrushProperty, SoftBrushProperty, DoorBrushProperty, ItemBrushProperty, PlayerBrushProperty,
            FaintTextBrushProperty, LabelPlateBrushProperty, ViewRadiusProperty, ShowLabelsProperty, FloorBrushProperty,
            RouteProperty, RouteBrushProperty, LockedDoorsProperty, LockedBrushProperty, KeyBrushProperty);
    }

    public double ViewRadius { get => GetValue(ViewRadiusProperty); set => SetValue(ViewRadiusProperty, value); }

    public bool ShowLabels { get => GetValue(ShowLabelsProperty); set => SetValue(ShowLabelsProperty, value); }

    public IBrush? FloorBrush { get => GetValue(FloorBrushProperty); set => SetValue(FloorBrushProperty, value); }

    public RoomMap? Map { get => GetValue(MapProperty); set => SetValue(MapProperty, value); }

    public double PlayerX { get => GetValue(PlayerXProperty); set => SetValue(PlayerXProperty, value); }

    public double PlayerZ { get => GetValue(PlayerZProperty); set => SetValue(PlayerZProperty, value); }

    public double PlayerAngle { get => GetValue(PlayerAngleProperty); set => SetValue(PlayerAngleProperty, value); }

    public bool HasPlayer { get => GetValue(HasPlayerProperty); set => SetValue(HasPlayerProperty, value); }

    /// <summary>
    /// Items the game has placed right now; when set, only these are drawn as present and the room files' other items
    /// are drawn faintly as "later" (story-gated or already taken).
    /// </summary>
    public IReadOnlyList<PlacedItem>? PlacedItems { get => GetValue(PlacedItemsProperty); set => SetValue(PlacedItemsProperty, value); }

    /// <summary>Room-file items the player has already taken (labelled "taken" rather than "later").</summary>
    public IReadOnlySet<MapItem>? TakenItems { get => GetValue(TakenItemsProperty); set => SetValue(TakenItemsProperty, value); }

    /// <summary>Item boxes and typewriters the game has active now; when set, only those room-file ones are drawn.</summary>
    public IReadOnlyList<PlacedFixture>? LiveFixtures { get => GetValue(LiveFixturesProperty); set => SetValue(LiveFixturesProperty, value); }

    /// <summary>Also draw the room-file items not in the room now (taken, or placed later by the story).</summary>
    public bool ShowAbsentItems { get => GetValue(ShowAbsentItemsProperty); set => SetValue(ShowAbsentItemsProperty, value); }

    /// <summary>Enemies and NPCs in the room right now.</summary>
    public IReadOnlyList<LiveActor>? Actors { get => GetValue(ActorsProperty); set => SetValue(ActorsProperty, value); }

    /// <summary>Door labels by target room id ("106 Main hall"); the id alone when missing.</summary>
    public IReadOnlyDictionary<string, string>? DoorNames { get => GetValue(DoorNamesProperty); set => SetValue(DoorNamesProperty, value); }

    /// <summary>Use spots whose item has been put in place (e.g. the gold emblem in its slot).</summary>
    public IReadOnlySet<MapSocket>? FilledSockets { get => GetValue(FilledSocketsProperty); set => SetValue(FilledSocketsProperty, value); }

    /// <summary>The route guide's way (only this room's part is drawn).</summary>
    public Lockpick.Core.Routes.RoutePlan? Route { get => GetValue(RouteProperty); set => SetValue(RouteProperty, value); }

    public IBrush? RouteBrush { get => GetValue(RouteBrushProperty); set => SetValue(RouteBrushProperty, value); }

    /// <summary>Doors still locked: true when the player holds the key that opens them (drawn in the key colour), false
    /// otherwise (the locked colour). Doors not listed are open.</summary>
    public IReadOnlyDictionary<MapDoor, bool>? LockedDoors { get => GetValue(LockedDoorsProperty); set => SetValue(LockedDoorsProperty, value); }

    public IBrush? LockedBrush { get => GetValue(LockedBrushProperty); set => SetValue(LockedBrushProperty, value); }

    public IBrush? KeyBrush { get => GetValue(KeyBrushProperty); set => SetValue(KeyBrushProperty, value); }

    private IBrush? DoorColour(MapDoor door) =>
        LockedDoors is { } locked && locked.TryGetValue(door, out var haveKey) ? (haveKey ? KeyBrush : LockedBrush) ?? DoorBrush : DoorBrush;

    public IBrush? WallBrush { get => GetValue(WallBrushProperty); set => SetValue(WallBrushProperty, value); }

    public IBrush? SoftBrush { get => GetValue(SoftBrushProperty); set => SetValue(SoftBrushProperty, value); }

    public IBrush? DoorBrush { get => GetValue(DoorBrushProperty); set => SetValue(DoorBrushProperty, value); }

    public IBrush? ItemBrush { get => GetValue(ItemBrushProperty); set => SetValue(ItemBrushProperty, value); }

    public IBrush? PlayerBrush { get => GetValue(PlayerBrushProperty); set => SetValue(PlayerBrushProperty, value); }

    /// <summary>Text of labels for taken and later items (their outlines use the soft brush).</summary>
    public IBrush? FaintTextBrush { get => GetValue(FaintTextBrushProperty); set => SetValue(FaintTextBrushProperty, value); }

    /// <summary>Plate drawn behind labels so they read over walls and furniture.</summary>
    public IBrush? LabelPlateBrush { get => GetValue(LabelPlateBrushProperty); set => SetValue(LabelPlateBrushProperty, value); }

    // Labels drawn so far this frame: a new label moves to the nearest free spot instead of covering one (several
    // items can share a spot, e.g. 50D's flame rounds and the magnum rounds for later).
    private readonly List<Rect> _labels = [];

    public override void Render(DrawingContext context)
    {
        _labels.Clear();
        if (Map is not { } map || Bounds.Width < 40 || Bounds.Height < 40)
        {
            return;
        }

        var (x0, z0, x1, z1) = map.Bounds;
        double scale, offsetX, offsetY;
        if (ViewRadius > 0 && HasPlayer)
        {
            // Local view: the player in the middle, ViewRadius world units to the nearer edge.
            scale = Math.Min(Bounds.Width, Bounds.Height) / (2 * ViewRadius);
            offsetX = (Bounds.Width / 2) - ((PlayerX - x0) * scale);
            offsetY = (Bounds.Height / 2) - ((z1 - PlayerZ) * scale);
        }
        else
        {
            scale = Math.Min((Bounds.Width - 2 * Inset) / Math.Max(1, x1 - x0), (Bounds.Height - 2 * Inset) / Math.Max(1, z1 - z0));
            offsetX = (Bounds.Width - (x1 - x0) * scale) / 2;
            offsetY = (Bounds.Height - (z1 - z0) * scale) / 2;
        }

        Point P(double x, double z) => new(offsetX + (x - x0) * scale, offsetY + (z1 - z) * scale);
        Rect R(double ax, double az, double bx, double bz) => new(P(ax, bz), P(bx, az));

        if (FloorBrush is not null)
        {
            context.DrawRectangle(FloorBrush, null, R(x0, z0, x1, z1));
        }

        foreach (var s in map.Shapes)
        {
            if (s.X1 - s.X0 >= 60000 || s.Z1 - s.Z0 >= 60000)
            {
                continue;
            }

            var brush = s.Blocks ? WallBrush : SoftBrush;
            var rect = R(s.X0, s.Z0, s.X1, s.Z1);
            if (s.Kind == MapShapeKind.Circle)
            {
                context.DrawEllipse(brush, null, rect.Center, rect.Width / 2, rect.Height / 2);
            }
            else
            {
                context.DrawRectangle(brush, null, rect);
            }
        }

        foreach (var d in map.Doors)
        {
            var colour = DoorColour(d);
            var rect = R(d.X, d.Z, d.X + d.Width, d.Z + d.Depth);
            context.DrawRectangle(null, colour is null ? null : new Pen(colour, 2), rect);
            DrawLabel(context, DoorNames?.GetValueOrDefault(d.Target) ?? d.Target, rect.Center, colour, 12);
        }

        var live = LiveFixtures;
        foreach (var f in map.Fixtures.Where(f => live is null || live.Any(l => l.Kind == f.Kind && l.X == f.X && l.Z == f.Z)))
        {
            var (fill, label) = f.Kind == FixtureKind.ItemBox
                ? (Color.Parse("#A1887F"), "Item box")
                : (Color.Parse("#4DB6AC"), "Typewriter");
            var rect = R(f.X, f.Z, f.X + f.Width, f.Z + f.Depth);
            var brush = new SolidColorBrush(fill, 0.35);
            context.DrawRectangle(brush, new Pen(new SolidColorBrush(fill), 1.5), rect);
            DrawLabel(context, label, rect.Center, new SolidColorBrush(fill), 11);
        }

        var placed = PlacedItems;
        var filled = FilledSockets ?? new HashSet<MapSocket>();
        foreach (var item in map.Items)
        {
            var present = placed is null || placed.Any(p => p.ItemId == item.ItemId && p.X == item.X && p.Z == item.Z);
            var rect = R(item.X, item.Z, item.X + item.Width, item.Z + item.Depth);
            if (!present && filled.Any(f => f.X == item.X && f.Z == item.Z))
            {
                continue; // the spot now holds the item put in place there (drawn below)
            }

            if (present)
            {
                context.DrawRectangle(null, ItemBrush is null ? null : new Pen(ItemBrush, 1.5), rect);
                DrawLabel(context, item.Name, rect.TopLeft + new Vector(rect.Width / 2, -10), ItemBrush, 12);
            }
            else if (ShowAbsentItems && SoftBrush is not null)
            {
                // In the room files but not placed now: already taken, or not placed until a story event.
                var taken = TakenItems?.Contains(item) == true;
                context.DrawRectangle(null, new Pen(SoftBrush, 1, new DashStyle(taken ? [1, 4] : [3, 3], 0)), rect);
                DrawLabel(context, item.Name + (taken ? " (taken)" : " (later)"), rect.TopLeft + new Vector(rect.Width / 2, -10), FaintTextBrush ?? SoftBrush, 11);
            }
        }

        foreach (var socket in filled.Where(f => !map.Items.Any(i => i.ItemId == f.ItemId && i.X == f.X && i.Z == f.Z
                     && (placed is null || placed.Any(p => p.ItemId == i.ItemId && p.X == i.X && p.Z == i.Z)))))
        {
            var rect = R(socket.X, socket.Z, socket.X + socket.Width, socket.Z + socket.Depth);
            context.DrawRectangle(null, ItemBrush is null ? null : new Pen(ItemBrush, 1.5, new DashStyle([6, 2], 0)), rect);
            DrawLabel(context, socket.Name + " (in place)", rect.TopLeft + new Vector(rect.Width / 2, -10), ItemBrush, 12);
        }

        if (placed is not null)
        {
            // Placed by the game but not in the room files at that spot (e.g. moved by a script): draw them too.
            foreach (var p in placed.Where(p => !map.Items.Any(i => i.ItemId == p.ItemId && i.X == p.X && i.Z == p.Z)))
            {
                var rect = R(p.X, p.Z, p.X + p.Width, p.Z + p.Depth);
                context.DrawRectangle(null, ItemBrush is null ? null : new Pen(ItemBrush, 1.5), rect);
                var name = map.Items.FirstOrDefault(i => i.ItemId == p.ItemId)?.Name ?? $"item {p.ItemId}";
                DrawLabel(context, name, rect.TopLeft + new Vector(rect.Width / 2, -10), ItemBrush, 12);
            }
        }

        RouteDrawing.Draw(context, Route, RouteBrush, room => room == map.RoomId ? P : null,
            HasPlayer && Route?.Legs is [{ } first, ..] && first.Room == map.RoomId ? (PlayerX, PlayerZ) : null);

        foreach (var actor in Actors ?? [])
        {
            DrawActor(context, actor, P(actor.X, actor.Z));
        }

        if (HasPlayer && PlayerBrush is not null)
        {
            var at = P(PlayerX, PlayerZ);
            var theta = PlayerAngle * Math.PI * 2 / 4096;
            // World direction (cos θ, −sin θ) in X/Z; screen Y runs opposite to Z.
            var forward = new Vector(Math.Cos(theta), Math.Sin(theta));
            var side = new Vector(-forward.Y, forward.X);
            var size = 9.0;
            var tip = at + forward * size * 1.4;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(tip, true);
                g.LineTo(at - forward * size * 0.6 + side * size * 0.8);
                g.LineTo(at - forward * size * 0.6 - side * size * 0.8);
                g.EndFigure(true);
            }

            context.DrawGeometry(PlayerBrush, null, geometry);
        }
    }

    private void DrawActor(DrawingContext context, LiveActor actor, Point at)
    {
        var (shape, color, letter, _) = ActorStyles.For(actor);
        var size = actor.Kind == ActorKind.Boss ? 11.0 : 8.0;
        var fill = new SolidColorBrush(color, 0.85);
        var pen = new Pen(new SolidColorBrush(Colors.Black, 0.8), 1.2);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            var corners = shape switch
            {
                ActorGlyph.Triangle => 3,
                ActorGlyph.Diamond or ActorGlyph.Square => 4,
                ActorGlyph.Hexagon => 6,
                ActorGlyph.Star => 10,
                _ => 0,
            };
            if (corners == 0)
            {
                g.BeginFigure(at + new Vector(size, 0), true);
                g.ArcTo(at - new Vector(size, 0), new Size(size, size), 0, false, SweepDirection.Clockwise);
                g.ArcTo(at + new Vector(size, 0), new Size(size, size), 0, false, SweepDirection.Clockwise);
            }
            else
            {
                var start = shape == ActorGlyph.Square ? Math.PI / 4 : -Math.PI / 2;
                for (var i = 0; i < corners; i++)
                {
                    var r = shape == ActorGlyph.Star && i % 2 == 1 ? size * 0.5 : size;
                    var a = start + i * 2 * Math.PI / corners;
                    var p = at + new Vector(Math.Cos(a) * r, Math.Sin(a) * r);
                    if (i == 0)
                    {
                        g.BeginFigure(p, true);
                    }
                    else
                    {
                        g.LineTo(p);
                    }
                }
            }

            g.EndFigure(true);
        }

        context.DrawGeometry(fill, pen, geometry);
        // Facing: a short tick (world direction (cos θ, −sin θ); screen Y runs opposite to Z).
        var theta = actor.Angle * Math.PI * 2 / 4096;
        var facing = new Vector(Math.Cos(theta), Math.Sin(theta));
        context.DrawLine(new Pen(fill, 2), at + facing * size, at + facing * (size + 6));
        var text = new FormattedText(letter, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, Brushes.Black);
        context.DrawText(text, at - new Vector(text.Width / 2, text.Height / 2));
        DrawLabel(context, actor.Name, at + new Vector(0, size + 9), fill, 10);
    }

    private void DrawLabel(DrawingContext context, string text, Point center, IBrush? brush, double size)
    {
        if (!ShowLabels || brush is null || string.IsNullOrEmpty(text))
        {
            return;
        }

        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);
        var step = formatted.Height + 3;
        Rect Place(Vector shift)
        {
            var o = center + shift - new Vector(formatted.Width / 2, formatted.Height / 2);
            // Keep labels inside the control (actors can stand outside the room's walls, e.g. a script-placed zombie).
            o = new Point(Math.Clamp(o.X, 4, Math.Max(4, Bounds.Width - formatted.Width - 4)),
                Math.Clamp(o.Y, 2, Math.Max(2, Bounds.Height - formatted.Height - 2)));
            return new Rect(o, new Size(formatted.Width, formatted.Height)).Inflate(new Thickness(4, 1));
        }

        Vector[] shifts =
        [
            default, new(0, -step), new(0, step), new(0, -2 * step), new(0, 2 * step),
            new(formatted.Width / 2 + 10, 0), new(-formatted.Width / 2 - 10, 0), new(0, -3 * step), new(0, 3 * step),
        ];
        var box = shifts.Select(Place).FirstOrDefault(r => !_labels.Any(r.Intersects), Place(default));
        _labels.Add(box);
        var origin = box.Deflate(new Thickness(4, 1)).TopLeft;
        if (LabelPlateBrush is { } plate)
        {
            context.DrawRectangle(plate, null, box, 3, 3);
        }

        context.DrawText(formatted, origin);
    }
}
