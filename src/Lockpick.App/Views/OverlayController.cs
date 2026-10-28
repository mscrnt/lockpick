using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Lockpick.App.Services;
using Lockpick.App.ViewModels;
using Lockpick.Core.Display;
using Lockpick.Core.Monitoring;
using Lockpick.Platform.Windows;
using Microsoft.Extensions.Logging;

namespace Lockpick.App.Views;

/// <summary>
/// Shows the overlay's widgets on the game's picture (docs/DISPLAY.md requirements): finds the game window, follows its
/// position, size and monitor ten times a second, renders each widget (only when its values or the scale change) and
/// shows it through its own plain layered window in its corner, kept directly above the game. Widgets sharing a corner
/// stack away from it. Hidden while the game is minimized or hidden, or not in a game.
/// </summary>
public sealed class OverlayController : IDisposable
{
    private readonly AppServices _services;
    private readonly OverlayViewModel _model;
    private readonly WidgetRenderer _renderer;
    private readonly ILogger _log;
    private readonly DispatcherTimer _timer;
    private readonly List<Widget> _widgets;
    private nint _game;
    private string? _installDirectory;
    private GameDisplaySettings? _display;

    public OverlayController(AppServices services, OverlayViewModel model, WidgetRenderer renderer)
    {
        _services = services;
        _model = model;
        _renderer = renderer;
        _log = services.LoggerFactory.CreateLogger("Overlay");
        _widgets = [.. renderer.Ids.Select(id => new Widget(id))];
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
    }

    /// <summary>The game window as last seen (null when there is none), for the Settings page.</summary>
    public GameWindowInfo? Game { get; private set; }

    /// <summary>The game window's width over its height when last seen (the layout editor draws the black bars from it).</summary>
    public static double? WindowAspect { get; private set; }

    public void Dispose()
    {
        _timer.Stop();
        _model.Map.KeepUpdating = false;
        foreach (var widget in _widgets)
        {
            widget.Window?.Dispose();
            widget.Window = null;
        }
    }

    private void Tick()
    {
        var settings = _services.Settings.Current;
        _model.UseMapSettings(settings.MapOverlay);
        _model.UseLook(settings.OverlayLook);
        var status = _services.Source.Status;
        var minimapOn = (settings.OverlayEnabled && OverlayLayout.Corner(settings, OverlayLayout.Minimap) != OverlayLayout.Off)
            || _renderer.Watched(OverlayLayout.Minimap); // a pop-out or the Stream page shows it
        // The route in the game picture follows the player and the camera through the same map model.
        var routePictureOn = settings.OverlayEnabled && OverlayLayout.Corner(settings, OverlayLayout.RoutePicture) != OverlayLayout.Off
            && settings.RouteId is not null;
        _model.Map.KeepUpdating = (minimapOn || routePictureOn) && status.State is ConnectionState.Attached or ConnectionState.Replaying;
        if (status.State != ConnectionState.Attached || status.Process is not { } process)
        {
            Game = null;
            _game = 0;
            HideAll("Resident Evil isn't running");
            return;
        }

        _game = GameWindows.Find(process.ProcessId, _services.Adapter.WindowClass, _game);
        Game = GameWindows.Read(_game);
        if (!settings.OverlayEnabled)
        {
            HideAll("Off");
            return;
        }

        if (Game is not { } game || !game.CanOverlay)
        {
            HideAll("The game window is minimized or hidden");
            return;
        }

        if (string.IsNullOrEmpty(_model.Time))
        {
            HideAll("Waiting for a game (the title screen and menus before a game show nothing)");
            return;
        }

        var directory = Path.GetDirectoryName(process.ExecutablePath);
        if (directory != _installDirectory)
        {
            _installDirectory = directory;
            _display = directory is null ? null : _services.Adapter.DisplaySettings(directory);
        }

        var picture = _display?.Picture(game.Client) ?? game.Client;
        Show(picture, game, settings);
    }

    private void Show(PixelRect picture, GameWindowInfo game, AppSettings settings)
    {
        var dpiScale = Math.Max(1, game.Dpi) / 96.0;
        _model.SetPicture(picture.Width / dpiScale, picture.Height / dpiScale);
        var margin = (int)Math.Round(OverlayViewModel.Margin * _model.Scale * dpiScale);
        var shown = 0;
        // Draw what changed, then place every widget with content (corners stack; spots placed by hand).
        List<(Widget Widget, byte[]? Pixels)> drawn = [];
        foreach (var widget in _widgets.Where(w => OverlayLayout.Corner(settings, w.Id) != OverlayLayout.Off))
        {
            if (!_renderer.HasContent(widget.Id))
            {
                widget.Window?.Hide();
                continue;
            }

            drawn.Add((widget, Render(widget, game)));
        }

        var items = drawn.Select(d => new OverlayPlacement.Item(d.Widget.Id, OverlayLayout.Corner(settings, d.Widget.Id),
            settings.OverlayWidgetSpots.TryGetValue(d.Widget.Id, out var spot) ? spot : null,
            d.Widget.Size.Width, d.Widget.Size.Height, _renderer.Full(d.Widget.Id))).ToList();
        // With the black bars on, widgets sit on the whole game window; what lines up with the picture stays on it.
        var window = settings.OverlayUseBars ? game.Client : picture;
        if (game.Client.Height > 0)
        {
            WindowAspect = (double)game.Client.Width / game.Client.Height;
        }

        var places = OverlayPlacement.Place(items.Where(i => OverlayLayout.OnPicture(i.Id, i.Full)), picture.X, picture.Y, picture.Width, picture.Height, margin);
        foreach (var (id, at) in OverlayPlacement.Place(items.Where(i => !OverlayLayout.OnPicture(i.Id, i.Full)), window.X, window.Y, window.Width, window.Height, margin))
        {
            places[id] = at;
        }
        foreach (var (widget, pixels) in drawn)
        {
            var (w, h) = widget.Size;
            var (x, y) = ((int)Math.Round(places[widget.Id].X), (int)Math.Round(places[widget.Id].Y));
            if (pixels is not null)
            {
                widget.Window!.Show(x, y, w, h, pixels);
            }
            else if (widget.Window!.Bounds is var b && (b.X != x || b.Y != y))
            {
                widget.Window.Move(x, y);
            }

            shown++;
        }

        foreach (var widget in _widgets.Where(w => OverlayLayout.Corner(settings, w.Id) == OverlayLayout.Off))
        {
            widget.Window?.Hide();
        }

        var visible = _widgets.Where(w => w.Window is { IsVisible: true }).ToList();
        var windows = visible.Select(w => w.Window!.Handle).ToList();
        foreach (var handle in windows)
        {
            GameWindows.KeepAbove(handle, game.Handle, windows);
        }

        // The other widgets go above a full-screen one (the time and HP stay readable on top of the map).
        foreach (var full in visible.Where(w => _renderer.Full(w.Id)))
        {
            foreach (var other in visible.Where(w => !_renderer.Full(w.Id)))
            {
                GameWindows.PlaceAbove(other.Window!.Handle, full.Window!.Handle);
            }
        }

        _model.Status = shown > 0 ? "Showing over the game." : "On, with every widget off or empty.";
    }

    /// <summary>Renders the widget when its look changed (or the scale or monitor); null when the last picture still holds.</summary>
    private byte[]? Render(Widget widget, GameWindowInfo game)
    {
        widget.Window ??= new LayeredWindow($"Lockpick overlay ({widget.Id})");
        var version = _renderer.Version(widget.Id);
        // Each widget's own size (Settings → Overlay) on top of the picture's scale; full-screen maps follow the picture.
        var scale = _renderer.Full(widget.Id) ? _model.Scale : _model.Scale * OverlayLayout.SizeOf(_services.Settings.Current, widget.Id);
        if (!_renderer.Live(widget.Id) && version == widget.RenderedVersion && game.Dpi == widget.RenderedDpi
            && scale == widget.RenderedScale && widget.Window.IsVisible)
        {
            return null;
        }

        if (_renderer.Render(widget.Id, scale, game.Dpi) is not var (pixels, width, height))
        {
            return null;
        }

        widget.Size = (width, height);
        widget.RenderedDpi = game.Dpi;
        widget.RenderedScale = scale;
        widget.RenderedVersion = version;
        return pixels;
    }

    private void HideAll(string reason)
    {
        foreach (var widget in _widgets)
        {
            widget.Window?.Hide();
        }

        _model.Status = reason;
    }

    /// <summary>One widget on the overlay: the layered window that shows it over the game, and what it last drew.</summary>
    private sealed class Widget(string id)
    {
        public string Id { get; } = id;

        public LayeredWindow? Window { get; set; }

        public int RenderedVersion { get; set; }

        public int RenderedDpi { get; set; }

        public double RenderedScale { get; set; }

        public (int Width, int Height) Size { get; set; }
    }
}
