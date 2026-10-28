using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Lockpick.App.Controls;
using Lockpick.App.Services;
using Lockpick.App.ViewModels;

namespace Lockpick.App.Views;

/// <summary>
/// Overlay widgets in their own windows (roadmap: widgets off the game too): movable, resizable, on any monitor, on a
/// plain dark or a green (chroma key) background for OBS's window capture, always on top if the player wants. Each
/// window's place and options are remembered, and the ones open when Lockpick closed open again.
/// </summary>
public sealed class PopOutManager : IDisposable
{
    public static readonly IBrush Dark = new SolidColorBrush(Color.Parse("#0B0F0E"));
    public static readonly IBrush Green = new SolidColorBrush(Color.Parse("#00B140"));

    private readonly AppServices _services;
    private readonly WidgetRenderer _renderer;
    private readonly Dictionary<string, Window> _open = [];
    private bool _closing;

    public PopOutManager(AppServices services, WidgetRenderer renderer)
    {
        _services = services;
        _renderer = renderer;
        services.WidgetWindows.ToggleRequested += OnToggleRequested;
    }

    private void OnToggleRequested(object? sender, string id) => Toggle(id);

    public void Toggle(string id)
    {
        if (_open.TryGetValue(id, out var window))
        {
            window.Close();
        }
        else
        {
            Open(id, activate: false); // the game keeps focus (it pauses when it loses it)
        }
    }

    /// <summary>Opens the pop-outs that were open when Lockpick last closed (without taking focus).</summary>
    public void RestoreOpen()
    {
        foreach (var (id, options) in _services.Settings.Current.PopOuts)
        {
            if (options.Open && _renderer.Ids.Contains(id))
            {
                Open(id, activate: false);
            }
        }
    }

    public void Dispose()
    {
        _services.WidgetWindows.ToggleRequested -= OnToggleRequested;
        _closing = true; // closing with Lockpick: they open again next time
        foreach (var window in _open.Values.ToList())
        {
            window.Close();
        }

        _open.Clear();
    }

    private void Open(string id, bool activate)
    {
        var options = _services.Settings.Current.PopOuts.GetValueOrDefault(id) ?? new PopOutOptions();
        var name = OverlayLayout.Widgets.FirstOrDefault(w => w.Id == id).Name ?? id;
        var window = new Window
        {
            Title = $"Lockpick — {name}",
            Width = options.Width,
            Height = options.Height,
            MinWidth = 120,
            MinHeight = 80,
            Background = options.Green ? Green : Dark,
            Topmost = options.Topmost,
            ShowActivated = activate,
            ShowInTaskbar = true,
        };
        if (options.X is { } x && options.Y is { } y)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(x, y);
        }

        List<object> menu = [];
        if (id == OverlayLayout.Minimap)
        {
            // The map draws itself live at the window's size, with its own view (not the overlay's).
            var map = new MapPopOutViewModel(_renderer.Map, options);
            window.Content = new MapPopOutView { DataContext = map };
            _renderer.Watch(id, true); // keeps the map updating with the overlay off
            window.Closed += (_, _) => _renderer.Watch(id, false);
            menu.AddRange(MapMenu(id, window, map));
            menu.Add(new Separator());
        }
        else
        {
            window.Content = new WidgetView { WidgetId = id, Margin = new Thickness(6), EmptyBrush = new SolidColorBrush(Color.Parse("#8FA39B")) };
        }

        var green = new MenuItem { Header = "Green background (for OBS's chroma key)", ToggleType = MenuItemToggleType.CheckBox, IsChecked = options.Green };
        green.Click += (_, _) =>
        {
            window.Background = green.IsChecked ? Green : Dark;
            Save(id, window, o => o with { Open = true, Green = green.IsChecked });
        };
        var top = new MenuItem { Header = "Always on top", ToggleType = MenuItemToggleType.CheckBox, IsChecked = options.Topmost };
        top.Click += (_, _) =>
        {
            window.Topmost = top.IsChecked;
            Save(id, window, o => o with { Open = true, Topmost = top.IsChecked });
        };
        var close = new MenuItem { Header = "Close" };
        close.Click += (_, _) => window.Close();
        menu.AddRange([green, top, new Separator(), close]);
        window.ContextMenu = new ContextMenu { ItemsSource = menu };
        window.Closing += (_, _) => Save(id, window, o => o with { Open = _closing });
        window.Closed += (_, _) =>
        {
            _open.Remove(id);
            _services.WidgetWindows.SetOpen(id, false);
        };
        _open[id] = window;
        window.Show();
        Save(id, window, o => o with { Open = true });
        _services.WidgetWindows.SetOpen(id, true);
    }

    /// <summary>The map window's menu: its view, how far or close, and names.</summary>
    private List<object> MapMenu(string id, Window window, MapPopOutViewModel map)
    {
        List<object> items = [];
        var reach = MapOverlaySettings.ReachChoices.Select(c => Choice($"Reach: {c.Name}", "reach", map.Reach == c.Value, () =>
        {
            map.Reach = c.Value;
            Save(id, window, o => o with { MapReach = c.Value });
        })).ToList();
        var zoom = MapPopOutViewModel.FloorZooms.Select((z, i) => Choice($"Floor: {z.Name}", "zoom", map.ZoomLevel == i, () =>
        {
            map.ZoomLevel = i;
            Save(id, window, o => o with { MapZoom = i });
        })).ToList();
        void ShowFor(string view)
        {
            reach.ForEach(r => r.IsVisible = view == PopOutMapViews.Around);
            zoom.ForEach(z => z.IsVisible = view == PopOutMapViews.Floor);
        }

        foreach (var (view, name) in MapPopOutViewModel.Views)
        {
            items.Add(Choice(name, "view", map.View == view, () =>
            {
                map.View = view;
                ShowFor(view);
                Save(id, window, o => o with { MapView = view });
            }));
        }

        items.Add(new Separator());
        items.AddRange(reach);
        items.AddRange(zoom);
        ShowFor(map.View);
        var names = new MenuItem { Header = "Names of doors, items and people", ToggleType = MenuItemToggleType.CheckBox, IsChecked = map.Names };
        names.Click += (_, _) =>
        {
            map.Names = names.IsChecked;
            Save(id, window, o => o with { MapNames = names.IsChecked });
        };
        items.Add(names);
        return items;
    }

    private static MenuItem Choice(string header, string group, bool chosen, Action chose)
    {
        var item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.Radio, GroupName = group, IsChecked = chosen };
        item.Click += (_, _) => chose();
        return item;
    }

    /// <summary>Keeps the window's place and size with <paramref name="change"/> applied to what was saved for it.</summary>
    private void Save(string id, Window window, Func<PopOutOptions, PopOutOptions> change)
    {
        var saved = _services.Settings.Current.PopOuts.GetValueOrDefault(id) ?? new PopOutOptions();
        var popOuts = new Dictionary<string, PopOutOptions>(_services.Settings.Current.PopOuts)
        {
            [id] = change(saved with
            {
                X = window.Position.X,
                Y = window.Position.Y,
                Width = window.Width is > 0 and var w ? w : window.Bounds.Width,
                Height = window.Height is > 0 and var h ? h : window.Bounds.Height,
            }),
        };
        try
        {
            _services.Settings.Save(_services.Settings.Current with { PopOuts = popOuts });
        }
        catch (IOException)
        {
            // Remembering the window is a convenience; a locked settings file must not break it.
        }
    }
}
