using Lockpick.App.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lockpick.Tests.App;

public sealed class OverlayLayoutTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "lockpick-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Widgets_start_in_their_default_corners() =>
        Assert.Equal(OverlayCorners.BottomRight, OverlayLayout.Corner(new AppSettings(), OverlayLayout.Minimap));

    [Fact]
    public void The_time_panel_keeps_the_corner_chosen_before_widgets() =>
        Assert.Equal(OverlayCorners.BottomLeft, OverlayLayout.Corner(new AppSettings { OverlayCorner = OverlayCorners.BottomLeft }, OverlayLayout.Status));

    [Fact]
    public void A_widget_s_own_corner_wins_and_off_is_kept()
    {
        var settings = new AppSettings
        {
            OverlayCorner = OverlayCorners.BottomLeft,
            OverlayWidgetCorners = new Dictionary<string, string> { [OverlayLayout.Status] = OverlayCorners.TopLeft, [OverlayLayout.Minimap] = OverlayLayout.Off },
        };
        Assert.Equal(OverlayCorners.TopLeft, OverlayLayout.Corner(settings, OverlayLayout.Status));
        Assert.Equal(OverlayLayout.Off, OverlayLayout.Corner(settings, OverlayLayout.Minimap));
    }

    [Fact]
    public void An_unknown_corner_falls_back_to_the_default()
    {
        var settings = new AppSettings { OverlayWidgetCorners = new Dictionary<string, string> { [OverlayLayout.Minimap] = "Middle" } };
        Assert.Equal(OverlayCorners.BottomRight, OverlayLayout.Corner(settings, OverlayLayout.Minimap));
    }

    [Fact]
    public void Map_options_survive_a_save_and_load()
    {
        var path = Path.Combine(_folder, "settings.json");
        var options = new MapOverlaySettings { Views = [MapViews.Hidden, MapViews.RoomSeeThrough], Reach = 9000, Size = 170, SeeThroughOpacity = 40, Names = false };
        new SettingsStore(path, NullLogger.Instance).Save(new AppSettings { MapOverlay = options });

        var loaded = new SettingsStore(path, NullLogger.Instance).Load().MapOverlay;
        Assert.Equal(options.Views, loaded.Views);
        Assert.Equal((9000, 170, 40, false), (loaded.Reach, loaded.Size, loaded.SeeThroughOpacity, loaded.Names));
    }

    [Fact]
    public void Settings_from_before_the_map_options_get_the_defaults()
    {
        var path = Path.Combine(_folder, "settings.json");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(path, "{\"overlayEnabled\": true, \"overlayCorner\": \"Top left\"}");
        var loaded = new SettingsStore(path, NullLogger.Instance).Load();
        Assert.Equal([MapViews.Around, MapViews.Room, MapViews.RoomSeeThrough, MapViews.Floor], loaded.MapOverlay.Views);
        Assert.Equal(OverlayCorners.TopLeft, OverlayLayout.Corner(loaded, OverlayLayout.Status));
    }

    [Fact]
    public void Pop_out_windows_and_the_stream_page_survive_a_save_and_load()
    {
        var path = Path.Combine(_folder, "settings.json");
        var popOuts = new Dictionary<string, PopOutOptions>
        {
            [OverlayLayout.Splits] = new() { Open = true, X = -1900, Y = 40, Width = 300, Height = 560, Green = true, Topmost = true },
            [OverlayLayout.Minimap] = new() { MapView = PopOutMapViews.Floor, MapZoom = 2, MapReach = 9000, MapNames = false },
        };
        new SettingsStore(path, NullLogger.Instance).Save(new AppSettings { PopOuts = popOuts, StreamWidgets = ["minimap:Large", "splits:Small"] });

        var loaded = new SettingsStore(path, NullLogger.Instance).Load();
        Assert.Equal(popOuts[OverlayLayout.Splits], loaded.PopOuts[OverlayLayout.Splits]);
        Assert.Equal(popOuts[OverlayLayout.Minimap], loaded.PopOuts[OverlayLayout.Minimap]);
        Assert.Equal(["minimap:Large", "splits:Small"], loaded.StreamWidgets);
        Assert.Null(new AppSettings().StreamWidgets); // none saved: the page's default layout
    }

    [Fact]
    public void Alert_sounds_are_windows_own_or_a_wave_file()
    {
        var settings = new AppSettings();
        Assert.Equal("critical", settings.FailSound);
        Assert.Contains(Lockpick.Platform.Windows.AlertSound.BuiltIn, b => b.Id == settings.DeathSound);
        Assert.True(Lockpick.Platform.Windows.AlertSound.IsWave(@"C:\sounds\Fail.WAV"));
        Assert.False(Lockpick.Platform.Windows.AlertSound.IsWave(@"C:\sounds\fail.mp3"));
        Assert.False(Lockpick.Platform.Windows.AlertSound.Play(@"C:\no such folder\fail.wav")); // a missing file plays nothing
        Assert.False(Lockpick.Platform.Windows.AlertSound.Play(Lockpick.Platform.Windows.AlertSound.None));
    }

    [Fact]
    public void Widget_windows_report_each_change_once()
    {
        var windows = new WidgetWindows();
        var changes = 0;
        string? asked = null;
        windows.Changed += (_, _) => changes++;
        windows.ToggleRequested += (_, id) => asked = id;

        windows.Toggle(OverlayLayout.Minimap);
        windows.SetOpen(OverlayLayout.Minimap, true);
        windows.SetOpen(OverlayLayout.Minimap, true);
        Assert.Equal(OverlayLayout.Minimap, asked);
        Assert.True(windows.IsOpen(OverlayLayout.Minimap));
        windows.SetOpen(OverlayLayout.Minimap, false);
        Assert.False(windows.IsOpen(OverlayLayout.Minimap));
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Widgets_in_a_corner_stack_and_a_spot_placed_by_hand_stays_inside_the_picture()
    {
        OverlayPlacement.Item Item(string id, string corner, double w, double h, WidgetSpot? spot = null, bool full = false) => new(id, corner, spot, w, h, full);
        var places = OverlayPlacement.Place(
        [
            Item("a", OverlayCorners.TopRight, 100, 50),
            Item("b", OverlayCorners.TopRight, 80, 40),
            Item("c", OverlayCorners.BottomLeft, 60, 30),
            Item("d", OverlayCorners.Custom, 100, 100, new WidgetSpot(0.5, 1)),
            Item("e", OverlayCorners.Custom, 100, 100, new WidgetSpot(7, -3)), // out of range: clamped to the edges
            Item("m", OverlayCorners.BottomRight, 0, 0, full: true),
        ], 10, 20, 640, 480, 6);

        Assert.Equal((544, 26), places["a"]);         // top right, inside the margin
        Assert.Equal((564, 82), places["b"]);         // under it
        Assert.Equal((16, 464), places["c"]);         // bottom left
        Assert.Equal((280, 394), places["d"]);        // centred along the width, at the bottom
        Assert.Equal((544, 26), places["e"]);
        Assert.Equal((16, 26), places["m"]);          // a full-screen map covers the picture

        var spot = OverlayPlacement.SpotFor(280, 394, 100, 100, 10, 20, 640, 480, 6);
        Assert.Equal(new WidgetSpot(0.5, 1), spot);   // the way back from a drop
    }

    [Fact]
    public void Spots_survive_a_save_and_load()
    {
        var path = Path.Combine(_folder, "settings.json");
        var spots = new Dictionary<string, WidgetSpot> { [OverlayLayout.Minimap] = new(0.25, 0.75) };
        new SettingsStore(path, NullLogger.Instance).Save(new AppSettings
        {
            OverlayWidgetSpots = spots,
            OverlayWidgetCorners = new Dictionary<string, string> { [OverlayLayout.Minimap] = OverlayCorners.Custom },
        });

        var loaded = new SettingsStore(path, NullLogger.Instance).Load();
        Assert.Equal(new WidgetSpot(0.25, 0.75), loaded.OverlayWidgetSpots[OverlayLayout.Minimap]);
        Assert.Equal(OverlayCorners.Custom, OverlayLayout.Corner(loaded, OverlayLayout.Minimap));
    }

    [Fact]
    public void Support_bundles_leave_out_the_windows_user_folder()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var escaped = profile.Replace("\\", "\\\\", StringComparison.Ordinal); // as JSON logs write it
        var log = "{\"message\":\"Saved to " + escaped + "\\\\x.json\"} and " + profile + "\\y and " + profile.Replace('\\', '/') + "/z";
        var clean = SupportBundle.Anonymize(log);
        Assert.DoesNotContain(profile, clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(escaped, clean, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, clean.Split("%USERPROFILE%").Length - 1);
    }

    [Fact]
    public void A_layout_file_carries_the_overlay_setup_and_nothing_else()
    {
        var path = Path.Combine(_folder, "mine" + OverlayLayoutFile.Extension);
        Directory.CreateDirectory(_folder);
        var mine = new AppSettings
        {
            ApiToken = "secret",
            OverlayWidgetCorners = new Dictionary<string, string> { [OverlayLayout.Minimap] = OverlayCorners.Custom },
            OverlayWidgetSpots = new Dictionary<string, WidgetSpot> { [OverlayLayout.Minimap] = new(0.3, 0.9) },
            OverlayWidgetSizes = new Dictionary<string, int> { [OverlayLayout.Minimap] = 150, [OverlayLayout.Status] = 75 },
            OverlayLook = new OverlayLook { BackgroundOpacity = 20, TextColor = "amber", Font = "mono", ShowRoom = false },
            MapOverlay = new MapOverlaySettings { Views = [MapViews.Floor], Reach = 9000 },
        };
        OverlayLayoutFile.Write(path, mine);
        Assert.DoesNotContain("secret", File.ReadAllText(path), StringComparison.Ordinal);

        var theirs = OverlayLayoutFile.Apply(path, new AppSettings { ApiToken = "theirs" });
        Assert.Equal("theirs", theirs.ApiToken); // only the overlay changes
        Assert.Equal(OverlayCorners.Custom, OverlayLayout.Corner(theirs, OverlayLayout.Minimap));
        Assert.Equal(new WidgetSpot(0.3, 0.9), theirs.OverlayWidgetSpots[OverlayLayout.Minimap]);
        Assert.Equal(1.5, OverlayLayout.SizeOf(theirs, OverlayLayout.Minimap));
        Assert.Equal(mine.OverlayLook, theirs.OverlayLook);
        Assert.Equal([MapViews.Floor], theirs.MapOverlay.Views);
    }

    [Fact]
    public void A_layout_file_s_values_are_checked()
    {
        var path = Path.Combine(_folder, "odd.json");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(path, """
            {"version": 1, "corners": {"minimap": "On the ceiling", "nope": "Top left", "status": "Top left"},
             "spots": {"feed": {"x": 7, "y": -2}}, "sizes": {"status": 5000},
             "look": {"backgroundOpacity": 300, "textColor": "plaid", "font": "comic"}}
            """);
        var loaded = OverlayLayoutFile.Apply(path, new AppSettings());
        Assert.Equal(OverlayCorners.TopLeft, OverlayLayout.Corner(loaded, OverlayLayout.Status));
        Assert.Equal(OverlayCorners.BottomRight, OverlayLayout.Corner(loaded, OverlayLayout.Minimap)); // unknown corner: the default
        Assert.Equal(new WidgetSpot(1, 0), loaded.OverlayWidgetSpots[OverlayLayout.Feed]);
        Assert.Equal(4.0, OverlayLayout.SizeOf(loaded, OverlayLayout.Status));
        Assert.Equal((100, "default", "default"), (loaded.OverlayLook.BackgroundOpacity, loaded.OverlayLook.TextColor, loaded.OverlayLook.Font));

        File.WriteAllText(path, "{\"version\": 9}");
        Assert.Throws<InvalidDataException>(() => OverlayLayoutFile.Apply(path, new AppSettings()));
        File.WriteAllText(path, "not json");
        Assert.Throws<InvalidDataException>(() => OverlayLayoutFile.Apply(path, new AppSettings()));
    }

    [Fact]
    public void Only_the_room_views_cover_the_picture()
    {
        Assert.True(MapViews.IsFull(MapViews.Room));
        Assert.True(MapViews.IsFull(MapViews.RoomSeeThrough));
        Assert.False(MapViews.IsFull(MapViews.Around));
        Assert.False(MapViews.IsFull(MapViews.Hidden));
    }

    [Fact]
    public void The_stream_layout_has_ticked_widgets_in_corners_or_at_spots_placed_by_hand()
    {
        var settings = new AppSettings
        {
            StreamLayout = new Dictionary<string, StreamWidgetChoice>
            {
                [OverlayLayout.Splits] = new(true, OverlayCorners.Custom, 100, 0.5, 2), // a spot (kept on the canvas)
                [OverlayLayout.Minimap] = new(true, OverlayCorners.Custom, 900),        // "your spot" with no spot: its corner
                [OverlayLayout.Feed] = new(false, OverlayCorners.BottomLeft, 150),      // not ticked
            },
        };

        var placements = StreamLayout.Placements(settings);
        var splits = Assert.Single(placements, p => p.Id == OverlayLayout.Splits);
        Assert.Equal(("spot", 0.5, 1.0, 1.0), (splits.Corner, splits.X, splits.Y, splits.Scale));
        var minimap = Assert.Single(placements, p => p.Id == OverlayLayout.Minimap);
        Assert.Equal(("bottom-right", (double?)null, 6.0), (minimap.Corner, minimap.X, minimap.Scale));
        Assert.DoesNotContain(placements, p => p.Id == OverlayLayout.Feed);
        Assert.Contains(placements, p => p.Id == OverlayLayout.Status && p.Corner == "top-right"); // the default
    }
}
