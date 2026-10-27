using Lockpick.Core.State;

namespace Lockpick.Core.Routes;

public enum RouteStepKind
{
    /// <summary>Pick up <see cref="RouteStep.Item"/> (in <see cref="RouteStep.Room"/> when given).</summary>
    Get,

    /// <summary>Use <see cref="RouteStep.Item"/> where it goes (a use spot, or a door it opens).</summary>
    Use,

    /// <summary>Go to <see cref="RouteStep.Room"/> (something happens there, see the hint).</summary>
    Go,

    /// <summary>The route's end.</summary>
    End,
}

/// <summary>A game flag: the bits <paramref name="Mask"/> of the raw field <paramref name="Field"/> (e.g. a story flag).</summary>
public sealed record RouteFlag(string Field, long Mask);

/// <summary>
/// A spot to press in a puzzle, in order (a switch under a painting): its zone in the room, what it is, and the game
/// flag set once it has been pressed (when the game keeps one: the gallery keeps only the last switch pressed).
/// </summary>
public sealed record RouteSpot(int X, int Z, int Width, int Depth, string Label, RouteFlag? Pressed = null)
{
    public RoutePoint Centre => new(X + (Width / 2.0), Z + (Depth / 2.0));
}

/// <summary>
/// One step of a route; <paramref name="Hint"/> says what the room data can't (how a puzzle opens), and
/// <paramref name="Done"/> is a game flag that is set once the step has happened (a story event), when there is one.
/// </summary>
public sealed record RouteStep(RouteStepKind Kind, int? Item = null, string? Room = null, string? Hint = null, RouteFlag? Done = null)
{
    /// <summary>A puzzle's spots in <see cref="Room"/>, in the order to press them (numbered on the map).</summary>
    public IReadOnlyList<RouteSpot> Spots { get; init; } = [];

    /// <summary>Where in <see cref="Room"/> the step happens, when the room file doesn't place it (an item handed over by a
    /// script, like the serum on the shelf).</summary>
    public RoutePoint? At { get; init; }
}

/// <summary>
/// A time limit the game keeps (Richard's serum): it runs from when <paramref name="Start"/> is set until
/// <paramref name="Stop"/> is, <paramref name="Minutes"/> of game time.
/// </summary>
public sealed record RouteTimer(string Label, RouteFlag Start, RouteFlag Stop, double Minutes);

/// <summary>
/// A route through the game: what to get and use, in order, for one character. The guide finds the way to each step
/// from the room data; <paramref name="Verified"/> is false until someone has followed it through a whole run.
/// </summary>
public sealed record Route(
    string Id,
    string Name,
    string Category,
    PlayableCharacter Character,
    IReadOnlyList<RouteStep> Steps,
    bool Verified,
    string Source)
{
    /// <summary>Time limits to count down while the route runs through them.</summary>
    public IReadOnlyList<RouteTimer> Timers { get; init; } = [];
}

/// <summary>What the player presses, in the game's own terms; the app shows the button for the input in use.</summary>
public enum GameControl
{
    Action,
    Run,
    Aim,
    Inventory,
    Options,
    Forward,
    Back,
    Left,
    Right,
}

/// <summary>A button prompt: press <paramref name="Buttons"/> together to <paramref name="Text"/>.</summary>
public sealed record RoutePrompt(IReadOnlyList<GameControl> Buttons, string Text);
