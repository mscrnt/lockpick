using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Lockpick.Core.Maps;

namespace Lockpick.App.Controls;

/// <summary>
/// A whole floor: every room of the stage in place (FloorLayout), the room the player is in outlined, items still in
/// the rooms' files, and the player. Fits the floor at zoom 1; follows the player when asked (the overlay). With
/// <see cref="Interactive"/> (the Map page) the wheel zooms around the pointer, dragging pans, a double-click fits.
/// </summary>
public sealed class FloorMapControl : Control
{
    public static readonly StyledProperty<FloorMap?> FloorProperty = AvaloniaProperty.Register<FloorMapControl, FloorMap?>(nameof(Floor));
    public static readonly StyledProperty<string?> CurrentRoomProperty = AvaloniaProperty.Register<FloorMapControl, string?>(nameof(CurrentRoom));
    public static readonly StyledProperty<double> PlayerXProperty = AvaloniaProperty.Register<FloorMapControl, double>(nameof(PlayerX));
    public static readonly StyledProperty<double> PlayerZProperty = AvaloniaProperty.Register<FloorMapControl, double>(nameof(PlayerZ));
    public static readonly StyledProperty<double> PlayerAngleProperty = AvaloniaProperty.Register<FloorMapControl, double>(nameof(PlayerAngle));
    public static readonly StyledProperty<bool> HasPlayerProperty = AvaloniaProperty.Register<FloorMapControl, bool>(nameof(HasPlayer));
    public static readonly StyledProperty<IReadOnlySet<MapItem>?> TakenItemsProperty =
        AvaloniaProperty.Register<FloorMapControl, IReadOnlySet<MapItem>?>(nameof(TakenItems));
    public static readonly StyledProperty<double> ZoomProperty = AvaloniaProperty.Register<FloorMapControl, double>(nameof(Zoom), 1);
    public static readonly StyledProperty<bool> FollowPlayerProperty = AvaloniaProperty.Register<FloorMapControl, bool>(nameof(FollowPlayer));
    public static readonly StyledProperty<bool> InteractiveProperty = AvaloniaProperty.Register<FloorMapControl, bool>(nameof(Interactive));
    public static readonly StyledProperty<bool> ShowLabelsProperty = AvaloniaProperty.Register<FloorMapControl, bool>(nameof(ShowLabels), true);
    public static readonly StyledProperty<IBrush?> FloorBrushProperty = AvaloniaProperty.Register<FloorMapControl, IBrush?>(nameof(FloorBrush));
    public static readonly StyledProperty<IBrush?> WallBrushProperty = AvaloniaProperty.Register<FloorMapControl, IBrush?>(nameof(WallBrush));
    public static readonly StyledProperty<IBrush?> SoftBrushProperty = AvaloniaProperty.Register<FloorMapControl, IBrush?>(nameof(SoftBrush));
    public static readonly StyledProperty<IBrush?> DoorBrushProperty = AvaloniaProperty.Register<FloorMapControl, IBrush?>(nameof(DoorBrush));
    public static readonly StyledProperty<IBrush?> ItemBrushProperty = AvaloniaProperty.Register<FloorMapControl, IBrush?>(nameof(ItemBrush));
    public static readonly StyledProperty<IBrush?> PlayerBrushProperty = AvaloniaProperty.Register<FloorMapControl, IBrush?>(nameof(PlayerBrush));
    public static readonly StyledProperty<Lockpick.Core.Routes.RoutePlan?> RouteProperty = AvaloniaProperty.Register<FloorMapControl, Lockpick.Core.Routes.RoutePlan?>(nameof(Route));
    public static readonly StyledProperty<IBrush?> RouteBrushProperty = AvaloniaProperty.Register<FloorMapControl, IBrush?>(nameof(RouteBrush));
    public static readonly StyledProperty<IReadOnlyDictionary<MapDoor, bool>?> LockedDoorsProperty = AvaloniaProperty.Register<FloorMapControl, IReadOnlyDictionary<MapDoor, bool>?>(nameof(LockedDoors));
    public static readonly StyledProperty<IBrush?> LockedBrushProperty = AvaloniaProperty.Register<FloorMapControl, IBrush?>(nameof(LockedBrush));
    public static readonly StyledProperty<IBrush?> KeyBrushProperty = AvaloniaProperty.Register<FloorMapControl, IBrush?>(nameof(KeyBrush));
    public static readonly StyledProperty<IBrush?> LabelBrushProperty = AvaloniaProperty.Register<FloorMapControl, IBrush?>(nameof(LabelBrush));
    public static readonly StyledProperty<IBrush?> HighlightBrushProperty = AvaloniaProperty.Register<FloorMapControl, IBrush?>(nameof(HighlightBrush));

    /// <summary>Gap kept around the floor when it is fitted, in pixels.</summary>
    private const double Inset = 12;

    /// <summary>Room names show once a room is at least this wide on screen.</summary>
    private const double LabelRoomWidth = 70;

    // The Map page's own pan and zoom (on top of Zoom): the centre in floor units, and a factor.
    private Point? _center;
    private double _userZoom = 1;
    private Point? _dragFrom;

    static FloorMapControl()
    {
        AffectsRender<FloorMapControl>(FloorProperty, CurrentRoomProperty, PlayerXProperty, PlayerZProperty, PlayerAngleProperty, HasPlayerProperty,
            TakenItemsProperty, ZoomProperty, FollowPlayerProperty, ShowLabelsProperty, FloorBrushProperty, WallBrushProperty, SoftBrushProperty,
            DoorBrushProperty, ItemBrushProperty, PlayerBrushProperty, LabelBrushProperty, HighlightBrushProperty, RouteProperty, RouteBrushProperty,
            LockedDoorsProperty, LockedBrushProperty, KeyBrushProperty);
    }

    public FloorMap? Floor { get => GetValue(FloorProperty); set => SetValue(FloorProperty, value); }

    /// <summary>The room the player is in (its id), outlined.</summary>
    public string? CurrentRoom { get => GetValue(CurrentRoomProperty); set => SetValue(CurrentRoomProperty, value); }

    /// <summary>The player in the current room's own units.</summary>
    public double PlayerX { get => GetValue(PlayerXProperty); set => SetValue(PlayerXProperty, value); }

    public double PlayerZ { get => GetValue(PlayerZProperty); set => SetValue(PlayerZProperty, value); }

    public double PlayerAngle { get => GetValue(PlayerAngleProperty); set => SetValue(PlayerAngleProperty, value); }

    public bool HasPlayer { get => GetValue(HasPlayerProperty); set => SetValue(HasPlayerProperty, value); }

    /// <summary>Items already taken in this game (not drawn).</summary>
    public IReadOnlySet<MapItem>? TakenItems { get => GetValue(TakenItemsProperty); set => SetValue(TakenItemsProperty, value); }

    /// <summary>1 fits the whole floor; 2 shows half as much.</summary>
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }

    /// <summary>Keep the player in the middle (the overlay).</summary>
    public bool FollowPlayer { get => GetValue(FollowPlayerProperty); set => SetValue(FollowPlayerProperty, value); }

    /// <summary>Pan and zoom with the mouse (the Map page).</summary>
    public bool Interactive { get => GetValue(InteractiveProperty); set => SetValue(InteractiveProperty, value); }

    public bool ShowLabels { get => GetValue(ShowLabelsProperty); set => SetValue(ShowLabelsProperty, value); }

    public IBrush? FloorBrush { get => GetValue(FloorBrushProperty); set => SetValue(FloorBrushProperty, value); }

    public IBrush? WallBrush { get => GetValue(WallBrushProperty); set => SetValue(WallBrushProperty, value); }

    public IBrush? SoftBrush { get => GetValue(SoftBrushProperty); set => SetValue(SoftBrushProperty, value); }

    public IBrush? DoorBrush { get => GetValue(DoorBrushProperty); set => SetValue(DoorBrushProperty, value); }

    public IBrush? ItemBrush { get => GetValue(ItemBrushProperty); set => SetValue(ItemBrushProperty, value); }

    public IBrush? PlayerBrush { get => GetValue(PlayerBrushProperty); set => SetValue(PlayerBrushProperty, value); }

    /// <summary>The route guide's way (the parts on this floor are drawn).</summary>
    public Lockpick.Core.Routes.RoutePlan? Route { get => GetValue(RouteProperty); set => SetValue(RouteProperty, value); }

    public IBrush? RouteBrush { get => GetValue(RouteBrushProperty); set => SetValue(RouteBrushProperty, value); }

    /// <summary>Doors still locked: true when the player holds the key that opens them (drawn in the key colour), false
    /// otherwise (the locked colour). Doors not listed are open.</summary>
    public IReadOnlyDictionary<MapDoor, bool>? LockedDoors { get => GetValue(LockedDoorsProperty); set => SetValue(LockedDoorsProperty, value); }

    public IBrush? LockedBrush { get => GetValue(LockedBrushProperty); set => SetValue(LockedBrushProperty, value); }

    public IBrush? KeyBrush { get => GetValue(KeyBrushProperty); set => SetValue(KeyBrushProperty, value); }

    private IBrush? DoorColour(MapDoor door) =>
        LockedDoors is { } locked && locked.TryGetValue(door, out var haveKey) ? (haveKey ? KeyBrush : LockedBrush) ?? DoorBrush : DoorBrush;

    public IBrush? LabelBrush { get => GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

    /// <summary>Outline of the room the player is in.</summary>
    public IBrush? HighlightBrush { get => GetValue(HighlightBrushProperty); set => SetValue(HighlightBrushProperty, value); }

    /// <summary>Back to the whole floor (the Map page's Fit button).</summary>
    public void Fit()
    {
        _center = null;
        _userZoom = 1;
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FloorProperty && (change.OldValue as FloorMap)?.Stage != (change.NewValue as FloorMap)?.Stage)
        {
            Fit(); // another stage: start from the whole floor
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!Interactive || Transform() is not { } t)
        {
            return;
        }

        // Zoom around the pointer: the floor point under it stays under it.
        var at = e.GetPosition(this);
        var before = t.ToFloor(at);
        _userZoom = Math.Clamp(_userZoom * (e.Delta.Y > 0 ? 1.25 : 0.8), 0.5, 40);
        var after = Transform()!.Value.ToFloor(at);
        var center = _center ?? t.Center;
        _center = new Point(center.X + before.X - after.X, center.Y + before.Y - after.Y);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!Interactive)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            Fit();
        }
        else
        {
            _dragFrom = e.GetPosition(this);
            e.Pointer.Capture(this);
        }

        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragFrom is not { } from || Transform() is not { } t)
        {
            return;
        }

        var to = e.GetPosition(this);
        var center = _center ?? t.Center;
        _center = new Point(center.X - (to.X - from.X) / t.Scale, center.Y + (to.Y - from.Y) / t.Scale);
        _dragFrom = to;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragFrom = null;
        e.Pointer.Capture(null);
    }

    /// <summary>Floor units to the control and back: the centre (floor units, X and Z) and pixels per unit.</summary>
    private readonly record struct View(Point Center, double Scale, Size Size)
    {
        public Point ToScreen(double x, double z) => new((Size.Width / 2) + ((x - Center.X) * Scale), (Size.Height / 2) - ((z - Center.Y) * Scale));

        public Point ToFloor(Point screen) => new(Center.X + ((screen.X - (Size.Width / 2)) / Scale), Center.Y - ((screen.Y - (Size.Height / 2)) / Scale));
    }

    private View? Transform()
    {
        if (Floor is not { Rooms.Count: > 0 } floor || Bounds.Width < 40 || Bounds.Height < 40)
        {
            return null;
        }

        var (x0, z0, x1, z1) = floor.Bounds;
        var fit = Math.Min((Bounds.Width - 2 * Inset) / Math.Max(1, x1 - x0), (Bounds.Height - 2 * Inset) / Math.Max(1, z1 - z0));
        var scale = fit * Math.Max(0.1, Zoom) * _userZoom;
        Point center;
        if (FollowPlayer && HasPlayer && CurrentRoom is { } id && floor.Find(id) is { } room)
        {
            center = new Point(room.OffsetX + PlayerX, room.OffsetZ + PlayerZ);
        }
        else
        {
            center = _center ?? new Point((x0 + x1) / 2, (z0 + z1) / 2);
        }

        return new View(center, scale, Bounds.Size);
    }

    public override void Render(DrawingContext context)
    {
        // Pointer input anywhere on the control (panning starts on empty space too).
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (Transform() is not { } view || Floor is not { } floor)
        {
            return;
        }

        Rect R(FloorRoom room, double ax, double az, double bx, double bz) =>
            new(view.ToScreen(ax + room.OffsetX, bz + room.OffsetZ), view.ToScreen(bx + room.OffsetX, az + room.OffsetZ));

        foreach (var room in floor.Rooms)
        {
            var (x0, z0, x1, z1) = room.Map.Bounds;
            if (FloorBrush is not null)
            {
                context.DrawRectangle(FloorBrush, null, R(room, x0, z0, x1, z1));
            }
        }

        foreach (var room in floor.Rooms)
        {
            foreach (var s in room.Map.Shapes.Where(s => s.X1 - s.X0 < 60000 && s.Z1 - s.Z0 < 60000))
            {
                var rect = R(room, s.X0, s.Z0, s.X1, s.Z1);
                var brush = s.Blocks ? WallBrush : SoftBrush;
                if (s.Kind == MapShapeKind.Circle)
                {
                    context.DrawEllipse(brush, null, rect.Center, rect.Width / 2, rect.Height / 2);
                }
                else
                {
                    context.DrawRectangle(brush, null, rect);
                }
            }

            foreach (var d in room.Map.Doors)
            {
                var colour = DoorColour(d);
                context.DrawRectangle(null, colour is null ? null : new Pen(colour, 1.5), R(room, d.X, d.Z, d.X + d.Width, d.Z + d.Depth));
            }

            var itemPen = ItemBrush is null ? null : new Pen(ItemBrush, 1.5);
            foreach (var item in room.Map.Items.Where(i => i.Width > 16 && TakenItems?.Contains(i) != true))
            {
                context.DrawRectangle(null, itemPen, R(room, item.X, item.Z, item.X + item.Width, item.Z + item.Depth));
            }
        }

        if (CurrentRoom is { } current && floor.Find(current) is { } here && HighlightBrush is not null)
        {
            var (x0, z0, x1, z1) = here.Map.Bounds;
            context.DrawRectangle(null, new Pen(HighlightBrush, 2), R(here, x0, z0, x1, z1).Inflate(2));
        }

        if (ShowLabels && LabelBrush is not null)
        {
            // Bigger rooms first; a name that would cover one already drawn is left out (zoom in to see it).
            var placed = new List<Rect>();
            var plate = new SolidColorBrush(Color.Parse("#D0101614"));
            foreach (var room in floor.Rooms.OrderByDescending(r => (r.Bounds.X1 - r.Bounds.X0) * (r.Bounds.Z1 - r.Bounds.Z0)))
            {
                var (x0, z0, x1, z1) = room.Map.Bounds;
                var rect = R(room, x0, z0, x1, z1);
                if (rect.Width < LabelRoomWidth)
                {
                    continue;
                }

                var text = new FormattedText(room.Name is { } name ? $"{room.Map.RoomId} {name}" : room.Map.RoomId, CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, Typeface.Default, 11, LabelBrush) { MaxTextWidth = Math.Max(40, rect.Width - 4), MaxLineCount = 2, Trimming = TextTrimming.CharacterEllipsis };
                var at = rect.Center - new Vector(text.Width / 2, text.Height / 2);
                var box = new Rect(at, new Size(text.Width, text.Height)).Inflate(new Thickness(3, 1));
                if (placed.Any(p => p.Intersects(box)))
                {
                    continue;
                }

                placed.Add(box);
                context.DrawRectangle(plate, null, box, 2, 2);
                context.DrawText(text, at);
            }
        }

        RouteDrawing.Draw(context, Route, RouteBrush, id => floor.Find(id) is { } on
            ? (x, z) => view.ToScreen(on.OffsetX + x, on.OffsetZ + z)
            : null,
            HasPlayer && Route?.Legs is [{ } first, ..] && first.Room == CurrentRoom ? (PlayerX, PlayerZ) : null);

        if (HasPlayer && PlayerBrush is not null && CurrentRoom is { } id && floor.Find(id) is { } room2)
        {
            var at = view.ToScreen(room2.OffsetX + PlayerX, room2.OffsetZ + PlayerZ);
            var theta = PlayerAngle * Math.PI * 2 / 4096;
            var forward = new Vector(Math.Cos(theta), Math.Sin(theta));
            var side = new Vector(-forward.Y, forward.X);
            const double size = 8;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(at + forward * size * 1.4, true);
                g.LineTo(at - forward * size * 0.6 + side * size * 0.8);
                g.LineTo(at - forward * size * 0.6 - side * size * 0.8);
                g.EndFigure(true);
            }

            context.DrawGeometry(PlayerBrush, new Pen(Brushes.Black, 1), geometry);
        }
    }
}
