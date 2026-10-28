using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lockpick.App.Services;
using Lockpick.Core.Routes;

namespace Lockpick.App.ViewModels;

/// <summary>A route in the picker ("No route" has a null id).</summary>
public sealed record RouteChoice(string? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A button style in the picker.</summary>
public sealed record ButtonStyleChoice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One prompt: the buttons to press together and what they do.</summary>
public sealed record PromptRow(IReadOnlyList<ButtonGlyph> Buttons, string Text);

/// <summary>
/// The route guide for the Map page and the overlay (docs/ROUTES.md): the route chosen, the step the player is on, the
/// way there (drawn on the maps) and the buttons to press, in the style of the controller in use.
/// </summary>
public sealed partial class RouteViewModel : ObservableObject
{
    private readonly AppServices _services;
    private bool _loading;
    private bool _busyPolled;

    public RouteViewModel(AppServices services)
    {
        _services = services;
        Routes =
        [
            new RouteChoice(null, "No route"),
            new RouteChoice(RouteService.FollowSplits, "Follow my splits (work in progress: worked out for the split set on the Runs page)"),
            .. services.Route.Routes.Select(r => new RouteChoice(r.Id, r.Verified ? r.Name : $"{r.Name} (draft)")),
        ];
        Styles = [.. ButtonStyles.All.Select(s => new ButtonStyleChoice(s.Id, s.Name))];
        _loading = true;
        SelectedRoute = Routes.FirstOrDefault(r => r.Id == services.Settings.Current.RouteId) ?? Routes[0];
        SelectedStyle = Styles.FirstOrDefault(s => s.Id == services.Settings.Current.ButtonStyle) ?? Styles[0];
        LineInPicture = services.Settings.Current.RouteInPicture;
        SpotInPicture = services.Settings.Current.RouteSpotInPicture;
        PromptsInPicture = services.Settings.Current.PromptsInPicture;
        _loading = false;
        services.Route.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
        // A message or the inventory can open while nothing else changes: the busy bits are checked on every poll.
        services.Source.Polled += (_, snapshot) =>
        {
            var busy = services.Adapter.PlayerBusy(snapshot);
            if (busy != Volatile.Read(ref _busyPolled))
            {
                Volatile.Write(ref _busyPolled, busy);
                Dispatcher.UIThread.Post(Refresh);
            }
        };
        Refresh();
    }

    public IReadOnlyList<RouteChoice> Routes { get; }

    public IReadOnlyList<ButtonStyleChoice> Styles { get; }

    [ObservableProperty]
    public partial RouteChoice? SelectedRoute { get; set; }

    [ObservableProperty]
    public partial ButtonStyleChoice? SelectedStyle { get; set; }

    /// <summary>The way drawn in the game's picture (on the overlay): the line on the floor.</summary>
    [ObservableProperty]
    public partial bool LineInPicture { get; set; }

    /// <summary>Where the step is, drawn in the game's picture: the ring (on the item, the spot or the door the way leaves
    /// by) and a puzzle's numbers.</summary>
    [ObservableProperty]
    public partial bool SpotInPicture { get; set; }

    /// <summary>The buttons shown in the game's picture, beside what they're for.</summary>
    [ObservableProperty]
    public partial bool PromptsInPicture { get; set; }

    /// <summary>A route is chosen.</summary>
    [ObservableProperty]
    public partial bool IsOn { get; private set; }

    /// <summary>A step to show (a route is chosen and the player is in a game).</summary>
    [ObservableProperty]
    public partial bool HasStep { get; private set; }

    [ObservableProperty]
    public partial string Step { get; private set; } = string.Empty;

    /// <summary>"Dining room (105) · step 3 of 40".</summary>
    [ObservableProperty]
    public partial string Where { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Hint { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Next { get; private set; } = string.Empty;

    /// <summary>Why no way is shown, or a warning about the way (a door Lockpick can't tell is open).</summary>
    [ObservableProperty]
    public partial string Note { get; private set; } = string.Empty;

    /// <summary>A route is chosen but there's no step to show yet (<see cref="Status"/> says why).</summary>
    [ObservableProperty]
    public partial bool Waiting { get; private set; }

    /// <summary>What the guide is waiting for when there's no step (no game, rooms loading).</summary>
    [ObservableProperty]
    public partial string Status { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsDraft { get; private set; }

    /// <summary>The time limits running ("Richard needs the serum: 11:42 left"), one per line; empty when none.</summary>
    [ObservableProperty]
    public partial string Clocks { get; private set; } = string.Empty;

    /// <summary>The player isn't in control (a message, the inventory, a cutscene): the prompts and the way in the picture
    /// wait.</summary>
    [ObservableProperty]
    public partial bool Busy { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<PromptRow> Prompts { get; private set; } = [];

    /// <summary>The way to the step, for the maps.</summary>
    [ObservableProperty]
    public partial RoutePlan? Plan { get; private set; }

    partial void OnSelectedRouteChanged(RouteChoice? value)
    {
        if (_loading)
        {
            return;
        }

        _services.Settings.Save(_services.Settings.Current with { RouteId = value?.Id });
        _services.EnsureGamepad();
        _services.Route.Restart();
        Refresh();
    }

    partial void OnSelectedStyleChanged(ButtonStyleChoice? value)
    {
        if (_loading || value is null)
        {
            return;
        }

        _services.Settings.Save(_services.Settings.Current with { ButtonStyle = value.Id });
        _services.EnsureGamepad();
        Refresh();
    }

    partial void OnLineInPictureChanged(bool value)
    {
        if (!_loading)
        {
            _services.Settings.Save(_services.Settings.Current with { RouteInPicture = value });
        }
    }

    partial void OnSpotInPictureChanged(bool value)
    {
        if (!_loading)
        {
            _services.Settings.Save(_services.Settings.Current with { RouteSpotInPicture = value });
        }
    }

    partial void OnPromptsInPictureChanged(bool value)
    {
        if (!_loading)
        {
            _services.Settings.Save(_services.Settings.Current with { PromptsInPicture = value });
        }
    }

    [RelayCommand]
    private void SkipStep() => _services.Route.Skip(1);

    [RelayCommand]
    private void BackStep() => _services.Route.Skip(-1);

    private void Refresh()
    {
        IsOn = SelectedRoute?.Id is not null;
        var view = _services.Route.View;
        HasStep = IsOn && view is not null;
        Waiting = IsOn && !HasStep;
        Status = !IsOn ? string.Empty
            : _services.Route.Planning is { } planning ? planning
            : _services.Source.Latest?.InGameplay is not { HasValue: true, Value: true } ? "The guide starts when you're in a game."
            : "Reading the game's rooms…";
        if (view is null)
        {
            Prompts = [];
            Plan = null;
            Clocks = string.Empty;
            return;
        }

        Step = view.Text;
        Where = view.Where.Length > 0 ? $"{view.Where} · {view.Progress.ToLowerInvariant()}" : view.Progress;
        Hint = view.Hint ?? string.Empty;
        Next = view.NextText is { } next ? $"Then: {next}" : string.Empty;
        Note = view.Note.Length > 0 ? view.Note
            : view.MayBeLocked ? "The way goes through a door the story opens; it may still be locked."
            : string.Empty;
        IsDraft = !view.Route.Verified;
        Clocks = string.Join(Environment.NewLine, view.Clocks.Select(c => c.Left is { } left
            ? $"{c.Label}: {(int)left.TotalMinutes}:{left.Seconds:D2} left"
            : $"{c.Label}: running, time left unknown"));
        Plan = view.Plan;

        var kind = _services.Route.Buttons;
        var snapshot = _services.Source.Latest;
        Busy = Volatile.Read(ref _busyPolled); // from the newest poll (the latest changed snapshot can be older)
        string? Key(GameControl control) => snapshot is null ? null : _services.Adapter.KeyName(control, snapshot);
        var prompts = Busy ? [] : view.Prompts.Select(p => new PromptRow([.. p.Buttons.Select(b => ButtonGlyphs.For(kind, b, Key))], p.Text)).ToList();
        if (!prompts.Select(p => (string.Join('+', p.Buttons.Select(b => b.Label)), p.Text))
            .SequenceEqual(Prompts.Select(p => (string.Join('+', p.Buttons.Select(b => b.Label)), p.Text))))
        {
            Prompts = prompts;
        }
    }
}
