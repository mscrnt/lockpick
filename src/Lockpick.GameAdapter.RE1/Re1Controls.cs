using System.Globalization;
using Lockpick.Core.Routes;
using Lockpick.Core.State;

namespace Lockpick.GameAdapter.RE1;

/// <summary>
/// The keyboard keys the player has set in OPTION MODE, from the game's key map (keyMap0–7, 0xAC4030: 32 bytes, a
/// Windows virtual-key code per function). Which byte is which function comes from the GOG English defaults: the map
/// read live 2026-10-26 held exactly the keys OPTION MODE's keyboard page lists (C action, V dash, X get ready, A
/// forward, ↑ back, ← / → turn, Z sub screen, ↓ option), each at its own byte. When the map can't be read (another
/// build, or blanked while the game is in the background) the defaults are shown.
/// </summary>
internal static class Re1Controls
{
    private static readonly Dictionary<GameControl, (int Slot, int Default)> Slots = new()
    {
        [GameControl.Forward] = (0, 'A'),
        [GameControl.Back] = (1, 0x26),
        [GameControl.Left] = (2, 0x25),
        [GameControl.Right] = (3, 0x27),
        [GameControl.Run] = (5, 'V'),
        [GameControl.Aim] = (10, 'X'),
        [GameControl.Action] = (11, 'C'),
        [GameControl.Inventory] = (13, 'Z'),
        [GameControl.Options] = (27, 0x28),
    };

    public static string KeyName(GameControl control, GameSnapshot snapshot)
    {
        var (slot, key) = Slots[control];
        if (snapshot.RawFields.TryGetValue(Re1Session.Fields.KeyMap[slot / 4], out var word) && word.HasValue
            && (byte)(word.Value >> (slot % 4 * 8)) is var set and not 0)
        {
            key = set;
        }

        return Name(key);
    }

    private static string Name(int vk) => vk switch
    {
        >= '0' and <= '9' or >= 'A' and <= 'Z' => ((char)vk).ToString(),
        0x25 => "←",
        0x26 => "↑",
        0x27 => "→",
        0x28 => "↓",
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x10 => "Shift",
        0x11 => "Ctrl",
        0x12 => "Alt",
        0x1B => "Esc",
        0x20 => "Space",
        >= 0x60 and <= 0x69 => $"Num {vk - 0x60}",
        >= 0x70 and <= 0x7B => $"F{vk - 0x6F}",
        _ => string.Create(CultureInfo.InvariantCulture, $"Key {vk:X2}"),
    };
}
