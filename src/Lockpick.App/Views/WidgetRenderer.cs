using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Lockpick.App.Services;
using Lockpick.App.ViewModels;

namespace Lockpick.App.Views;

/// <summary>
/// The overlay's widgets, drawn for whoever shows them — the overlay on the game, pop-out windows, the Stream page
/// (roadmap: widgets off the game too). Each widget is a panel of the hidden host (OverlayWindow) or draws itself
/// (the splits: LiveSplit's layout by livesplit-core); a version number per widget says when it last changed, so each
/// place showing it draws again only then. UI thread only.
/// </summary>
public sealed class WidgetRenderer : IDisposable
{
    /// <summary>The splits widget's size in the overlay's own units (LiveSplit's layout scales to fit it).</summary>
    private const double SplitsWidth = 170;
    private const double SplitsHeight = 300;

    private readonly AppServices _services;
    private readonly OverlayViewModel _model;
    private readonly OverlayWindow _host;
    private readonly Dictionary<string, Entry> _entries;
    private readonly Dictionary<string, int> _watchers = [];
    private readonly Dictionary<string, long> _requested = [];

    public WidgetRenderer(AppServices services, OverlayViewModel model)
    {
        _services = services;
        _model = model;
        _host = new OverlayWindow { DataContext = model };
        Entry[] entries =
        [
            // The time and HP only in a game, as on the overlay (not the last game's at the title).
            new(OverlayLayout.Status, _host.StatusPanel, () => !string.IsNullOrEmpty(model.Time), () => false),
            new(OverlayLayout.Minimap, _host.MinimapPanel, () => model.HasMinimap, () => model.MinimapFull),
            new(OverlayLayout.Enemies, _host.EnemiesPanel, () => model.Enemies.Count > 0, () => false),
            // One splits widget: LiveSplit's layout drawn by livesplit-core, or Lockpick's own panel without it.
            services.LiveSplitEngine.Available
                ? new(OverlayLayout.Splits, null, () => services.Runs.Status.Set is not null, () => false)
                {
                    Draw = (width, height) => services.LiveSplitEngine.Render(width, height),
                    Base = (SplitsWidth, SplitsHeight),
                }
                : new(OverlayLayout.Splits, _host.SplitsPanel, () => model.SplitRows.Count > 0, () => false),
            new(OverlayLayout.Feed, _host.FeedPanel, () => model.FeedLines.Count > 0, () => false),
            new(OverlayLayout.Banner, _host.BannerPanel, () => model.HasBanner, () => false),
            new(OverlayLayout.Route, _host.RoutePanel, () => model.Map.Guide.HasStep, () => false),
            new(OverlayLayout.RoutePicture, _host.RoutePicturePanel, () => model.HasRoutePicture, () => true),
        ];
        _entries = entries.ToDictionary(e => e.Id);
        _host.ShowEnemies(model.Enemies);
        _host.ShowSplits(model);
        _host.ShowRoute(model.Map.Guide);
        model.PropertyChanged += OnModelChanged;
        model.Map.Guide.PropertyChanged += OnRouteChanged;
        model.Map.PropertyChanged += OnMapChanged;
        services.LiveSplitEngine.Changed += OnEngineChanged;
    }

    /// <summary>The app's renderer, for controls created by XAML (the Stream page, pop-outs).</summary>
    public static WidgetRenderer? Current { get; set; }

    public IEnumerable<string> Ids => _entries.Keys;

    /// <summary>The live map (the map's own window draws it itself, at its own view).</summary>
    public MapViewModel Map => _model.Map;

    /// <summary>The widget has something to show now.</summary>
    public bool HasContent(string id) => _entries.TryGetValue(id, out var e) && e.HasContent();

    /// <summary>On the overlay, the widget covers the game's picture (the full-screen map views).</summary>
    public bool Full(string id) => _entries.TryGetValue(id, out var e) && e.Full();

    /// <summary>Changes each time the widget's look may have changed.</summary>
    public int Version(string id) => _entries.TryGetValue(id, out var e) ? e.Version : 0;

    /// <summary>The widget changes every tick (LiveSplit's timer while it runs): draw it each time.</summary>
    public bool Live(string id) => _entries.TryGetValue(id, out var e) && e.Draw is not null && _services.LiveSplitEngine.Running;

    /// <summary>A place other than the overlay shows the widget (so its data keeps updating, e.g. the map).</summary>
    public void Watch(string id, bool watching)
    {
        _watchers[id] = Math.Max(0, _watchers.GetValueOrDefault(id) + (watching ? 1 : -1));
    }

    /// <summary>Shown somewhere other than the overlay: a pop-out, a Stream page tile, or a browser source asking
    /// for it in the last few seconds.</summary>
    public bool Watched(string id) => _watchers.GetValueOrDefault(id) > 0 || Environment.TickCount64 - _requested.GetValueOrDefault(id, long.MinValue / 2) < 3000;

    /// <summary>The widget for a browser source (the API): as large as fits, or null while it has nothing to show.</summary>
    public Lockpick.Api.WidgetPicture? ForBrowser(string id, int width, int height)
    {
        if (!_entries.ContainsKey(id))
        {
            return null;
        }

        _requested[id] = Environment.TickCount64; // keeps its data updating (e.g. the map) while OBS shows it
        return HasContent(id) && RenderToFit(id, width, height, 96) is var (pixels, w, h) ? new Lockpick.Api.WidgetPicture(pixels, w, h) : null;
    }

    /// <summary>The widget at the overlay's <paramref name="scale"/> for a monitor at <paramref name="dpi"/> (premultiplied BGRA).</summary>
    public (byte[] Pixels, int Width, int Height)? Render(string id, double scale, int dpi)
    {
        if (!_entries.TryGetValue(id, out var entry))
        {
            return null;
        }

        if (entry.Draw is { } draw)
        {
            var dpiScale = Math.Max(1, dpi) / 96.0;
            var width = Math.Max(1, (int)Math.Round(entry.Base.Width * scale * dpiScale));
            var height = Math.Max(1, (int)Math.Round(entry.Base.Height * scale * dpiScale));
            return draw(width, height) is { } pixels ? (pixels, width, height) : null;
        }

        return OverlayWindow.Render(entry.Panel!, scale, dpi);
    }

    /// <summary>The widget as large as fits <paramref name="width"/> × <paramref name="height"/> pixels (pop-outs, the Stream page).</summary>
    public (byte[] Pixels, int Width, int Height)? RenderToFit(string id, int width, int height, int dpi)
    {
        if (!_entries.TryGetValue(id, out var entry) || width < 8 || height < 8)
        {
            return null;
        }

        if (entry.Draw is { } draw)
        {
            return draw(width, height) is { } pixels ? (pixels, width, height) : null; // LiveSplit's layout fills the box
        }

        var natural = OverlayWindow.Measure(entry.Panel!);
        var dpiScale = Math.Max(1, dpi) / 96.0;
        var scale = Math.Min(width / dpiScale / Math.Max(1, natural.Width), height / dpiScale / Math.Max(1, natural.Height));
        return OverlayWindow.Render(entry.Panel!, Math.Clamp(scale, 0.25, 8), dpi);
    }

    public void Dispose()
    {
        _model.PropertyChanged -= OnModelChanged;
        _model.Map.PropertyChanged -= OnMapChanged;
        _model.Map.Guide.PropertyChanged -= OnRouteChanged;
        _services.LiveSplitEngine.Changed -= OnEngineChanged;
        _host.Close();
    }

    private void Bump(string id)
    {
        if (_entries.TryGetValue(id, out var entry))
        {
            entry.Version++;
        }
    }

    private void OnEngineChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => Bump(OverlayLayout.Splits));

    private void OnMapChanged(object? sender, PropertyChangedEventArgs e)
    {
        Bump(OverlayLayout.Minimap);
        if (e.PropertyName is nameof(MapViewModel.PlayerX) or nameof(MapViewModel.PlayerY) or nameof(MapViewModel.PlayerZ)
            or nameof(MapViewModel.CameraId) or nameof(MapViewModel.Map) or nameof(MapViewModel.HasPlayer) or nameof(MapViewModel.InGame))
        {
            Bump(OverlayLayout.RoutePicture);
        }
    }

    private void OnRouteChanged(object? sender, PropertyChangedEventArgs e)
    {
        Bump(OverlayLayout.RoutePicture);
        if (e.PropertyName == nameof(RouteViewModel.Plan))
        {
            Bump(OverlayLayout.Minimap);
            return;
        }

        _host.ShowRoute(_model.Map.Guide);
        Bump(OverlayLayout.Route);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(OverlayViewModel.Status):
                return;
            case OverlayViewModel.LookChanged:
                // The look changed: every panel draws again (the rows built in code take the new colours too).
                _host.ShowSplits(_model);
                _host.ShowFeed(_model.FeedLines);
                _host.ShowEnemies(_model.Enemies);
                _host.ShowRoute(_model.Map.Guide);
                foreach (var entry in _entries.Values)
                {
                    entry.Version++;
                }

                return;
            case nameof(OverlayViewModel.PanelBackground) or nameof(OverlayViewModel.TextBrush) or nameof(OverlayViewModel.PanelFont)
                or nameof(OverlayViewModel.ShowTime) or nameof(OverlayViewModel.ShowHealth) or nameof(OverlayViewModel.ShowRoom)
                or nameof(OverlayViewModel.ShowChallengeLine):
                return; // told once by LookChanged
            case nameof(OverlayViewModel.SplitRows) or nameof(OverlayViewModel.RunClock) or nameof(OverlayViewModel.RunTitle)
                or nameof(OverlayViewModel.RunComparison) or nameof(OverlayViewModel.RunBehind):
                _host.ShowSplits(_model);
                Bump(OverlayLayout.Splits);
                return;
            case nameof(OverlayViewModel.FeedLines):
                _host.ShowFeed(_model.FeedLines);
                Bump(OverlayLayout.Feed);
                return;
            case nameof(OverlayViewModel.Enemies):
                _host.ShowEnemies(_model.Enemies);
                Bump(OverlayLayout.Enemies);
                return;
            case nameof(OverlayViewModel.HasBanner):
                Bump(OverlayLayout.Banner);
                return;
        }

        Bump(e.PropertyName?.StartsWith("Banner", StringComparison.Ordinal) == true ? OverlayLayout.Banner
            : e.PropertyName?.StartsWith("Minimap", StringComparison.Ordinal) == true ? OverlayLayout.Minimap
            : OverlayLayout.Status);
    }

    private sealed class Entry(string id, LayoutTransformControl? panel, Func<bool> hasContent, Func<bool> full)
    {
        public string Id { get; } = id;

        public LayoutTransformControl? Panel { get; } = panel;

        public Func<bool> HasContent { get; } = hasContent;

        public Func<bool> Full { get; } = full;

        /// <summary>Draws the widget itself (premultiplied BGRA at the size asked), instead of the host's panel.</summary>
        public Func<int, int, byte[]?>? Draw { get; init; }

        /// <summary>A drawn widget's size in the overlay's own units.</summary>
        public (double Width, double Height) Base { get; init; }

        public int Version { get; set; } = 1;
    }
}
