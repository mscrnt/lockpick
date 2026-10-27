using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Lockpick.Core.Routes;

namespace Lockpick.App.Services;

/// <summary>A button to show: its picture, or (keyboard keys, a missing picture) its name.</summary>
public sealed record ButtonGlyph(Bitmap? Image, string Label);

/// <summary>
/// The pictures for the game's controls on each kind of controller (Assets/Buttons; sources and licences in
/// THIRD_PARTY_NOTICES.md). GOG's release maps every pad to one layout (docs/CAPTURE_BOUNTIES.md B207): on an Xbox pad
/// A is action, X run, RT aim, Y the items, Back (View) the options, checked button by button 2026-10-17. A
/// PlayStation pad's are the buttons in the same places (Cross, Square, R2, Triangle, Share), not yet checked one by one.
/// The keyboard shows the keys the player set (the adapter reads the game's key map), drawn from George Black's
/// keyboard icons where the set has the key.
/// </summary>
public static class ButtonGlyphs
{
    private static readonly Dictionary<GameControl, string> Xbox = new()
    {
        [GameControl.Action] = "a",
        [GameControl.Run] = "x",
        [GameControl.Aim] = "rt",
        [GameControl.Inventory] = "y",
        [GameControl.Options] = "view",
        [GameControl.Forward] = "dpad-up",
        [GameControl.Back] = "dpad-down",
        [GameControl.Left] = "dpad-left",
        [GameControl.Right] = "dpad-right",
    };

    private static readonly Dictionary<GameControl, string> PlayStation = new()
    {
        [GameControl.Action] = "cross",
        [GameControl.Run] = "square",
        [GameControl.Aim] = "r2",
        [GameControl.Inventory] = "triangle",
        [GameControl.Options] = "share",
        [GameControl.Forward] = "dpad-up",
        [GameControl.Back] = "dpad-down",
        [GameControl.Left] = "dpad-left",
        [GameControl.Right] = "dpad-right",
    };

    private static readonly Dictionary<string, Bitmap?> Cache = [];

    /// <summary>The button for <paramref name="control"/>; <paramref name="key"/> names the keyboard key.</summary>
    public static ButtonGlyph For(ButtonKind kind, GameControl control, Func<GameControl, string?> key) => kind switch
    {
        ButtonKind.Xbox => new ButtonGlyph(Load($"xbox/{Xbox[control]}"), Xbox[control].ToUpperInvariant()),
        ButtonKind.PlayStation => new ButtonGlyph(Load($"playstation/{PlayStation[control]}"), PlayStation[control]),
        _ => key(control) is { } name ? new ButtonGlyph(KeyIcon(name) is { } icon ? Load($"keyboard/{icon}") : null, name)
            : new ButtonGlyph(null, control.ToString()),
    };

    /// <summary>The keyboard set's picture for a key name from the adapter ("C", "↑", "Ctrl"); null for keys it has none of
    /// (Space, the F keys, the number pad), which show as text.</summary>
    private static string? KeyIcon(string name) => name switch
    {
        [var c] when char.IsAsciiLetterOrDigit(c) => char.ToLowerInvariant(c).ToString(),
        "←" => "leftarrow",
        "↑" => "uparrow",
        "→" => "rightarrow",
        "↓" => "downarrow",
        "Backspace" => "backspace",
        "Tab" => "tab",
        "Enter" => "enter",
        "Shift" => "shift",
        "Ctrl" => "control",
        "Alt" => "alt",
        "Esc" => "escape",
        _ => null,
    };

    private static Bitmap? Load(string name)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(name, out var bitmap))
            {
                var uri = new Uri($"avares://Lockpick/Assets/Buttons/{name}.png");
                bitmap = AssetLoader.Exists(uri) ? new Bitmap(AssetLoader.Open(uri)) : null;
                Cache[name] = bitmap;
            }

            return bitmap;
        }
    }
}
