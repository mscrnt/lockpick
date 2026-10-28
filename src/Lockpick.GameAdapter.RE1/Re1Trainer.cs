using Lockpick.Core.State;
using Lockpick.Core.Trainer;

namespace Lockpick.GameAdapter.RE1;

/// <summary>
/// RE1's trainer features as profile fields: HP (bounded by max HP); for infinite ammo, the quantity byte of the
/// equipped slot in the inventory the game is using (Chris's and Jill's at 0xBE9944, Rebecca's at 0xBE9950); and for
/// keep-running, the window's focus flag and the keyboard map (docs/MEMORY_MAP.md "Focus and input").
/// </summary>
internal sealed class Re1Trainer(Func<int, string?> itemName) : IGameTrainer, Lockpick.Core.Challenges.IChallengeRules
{
    private const int ItemBoxEntries = 48;
    private const int LastItem = 0x4D;      // radio; 0x4E and up are documents and maps
    private const int InkRibbon = 0x2F;
    private const int LastAmmo = 0x12;      // clip 0x0B … flame rounds 0x12
    private const int Knife = 0x01;
    private const int LastWeapon = 0x0A; // Beretta 2 … rocket launcher 10 (B101–B107)
    private const int Rebecca = 3;

    public string HealthField => Re1Session.Fields.Hp;

    public string MaxHealthField => Re1Session.Fields.MaxHp;

    public Func<GameSnapshot, IReadOnlyList<FieldWrite>> CreateInfiniteAmmoRule()
    {
        // The most rounds seen in each slot for the weapon now in it: a shot that lowers the count is put back, a reload
        // raises the mark. A different weapon in the slot starts over from what it holds.
        var highest = new Dictionary<int, (int Item, int Rounds)>();
        return snapshot =>
        {
            if (snapshot.Inventory is not { HasValue: true, Value: { } held }
                || Raw(snapshot, "equippedSlot") is not { } equipped || equipped < 1)
            {
                return [];
            }

            var slot = (int)equipped - 1;
            var weapon = held.FirstOrDefault(h => h.Slot == slot);
            if (weapon is null || weapon.ItemId <= Knife || weapon.ItemId > LastWeapon)
            {
                return [];
            }

            // The item list is refreshed less often than every poll; the slot's quantity byte is read every poll.
            var rebecca = Raw(snapshot, Re1Session.Fields.Character) is { } c && (c & 3) == Rebecca;
            var field = $"{(rebecca ? "rebInv" : "inv")}{slot}Qty";
            if (Raw(snapshot, field) is { } rounds)
            {
                weapon = weapon with { Quantity = (int)rounds };
            }

            if (!highest.TryGetValue(slot, out var mark) || mark.Item != weapon.ItemId || weapon.Quantity > mark.Rounds)
            {
                highest[slot] = (weapon.ItemId, weapon.Quantity);
                return [];
            }

            if (weapon.Quantity >= mark.Rounds)
            {
                return [];
            }

            return [new FieldWrite(field, mark.Rounds)];
        };
    }

    public IReadOnlyList<string> KeepRunningFields { get; } = [Re1Session.Fields.WindowFocused, .. Re1Session.Fields.KeyMap];

    /// <summary>
    /// The window procedure's WM_ACTIVATE handling skipped (the game then never stops its looping sounds or lets go of the
    /// controller when another window is clicked), and the sound buffers made with DSBCAPS_GLOBALFOCUS (Windows doesn't
    /// mute them in the background). Profiles: backgroundFocus, backgroundSound.
    /// </summary>
    public IReadOnlyList<string> BackgroundPatches { get; } = ["backgroundFocus", "backgroundSound"];

    public TrainerRule CreateKeepRunningRule(Func<bool> inFront)
    {
        // The game pauses while its focus flag is 0 (its WM_ACTIVATE clears it); held at 1, it runs on in the background.
        // It reads the keyboard with GetAsyncKeyState, which sees keys typed in any window, so the keyboard map is
        // blanked while the game is in the background and the player's map put back when it's in front again (live
        // 2026-10-26: blanked, the game read no keys typed in Notepad; the controller isn't in the map). The game
        // rewrites the map when it loads its settings or a save: the latest one seen is the one put back.
        long[]? keys = null;
        IReadOnlyList<FieldWrite> PutBack()
        {
            var back = keys;
            keys = null;
            return back is null ? [] : [.. Re1Session.Fields.KeyMap.Select((f, i) => new FieldWrite(f, back[i]))];
        }

        return new TrainerRule("Keep running in the background", snapshot =>
        {
            var map = Re1Session.Fields.KeyMap.Select(f => Raw(snapshot, f)).ToArray();
            if (map.Any(v => v is null) || Raw(snapshot, Re1Session.Fields.WindowFocused) is not { } focused)
            {
                return [];
            }

            var blank = map.All(v => v == 0);
            if (inFront())
            {
                return blank ? PutBack() : [];
            }

            var writes = new List<FieldWrite>();
            if (focused == 0)
            {
                writes.Add(new FieldWrite(Re1Session.Fields.WindowFocused, 1));
            }

            if (!blank)
            {
                keys = [.. map.Select(v => v!.Value)];
                writes.AddRange(Re1Session.Fields.KeyMap.Select(f => new FieldWrite(f, 0)));
            }

            return writes;
        })
        {
            OutsideGameplay = true, // the flag and the map mean the same at the title: it keeps running there too
            Restore = PutBack,
        };
    }

    public IReadOnlyList<GivableItem> GivableItems => field ??=
        [.. Enumerable.Range(1, LastItem).Select(id => (id, name: itemName(id))).Where(i => i.name is not null)
            .Select(i => new GivableItem(i.id, i.name!, HasQuantity(i.id)))];

    /// <summary>Weapons (loaded rounds), ammo and ink ribbons have a count; other items are single.</summary>
    public bool HasQuantity(int itemId) => itemId is > Knife and <= LastAmmo or InkRibbon;

    public FieldWrite? SetQuantity(GameSnapshot snapshot, int slot, int quantity, out string? refusal)
    {
        refusal = null;
        if (snapshot.Inventory is not { HasValue: true, Value: { } held } || held.FirstOrDefault(h => h.Slot == slot) is not { } item)
        {
            refusal = $"slot {slot + 1} is empty";
            return null;
        }

        if (!HasQuantity(item.ItemId))
        {
            refusal = $"{itemName(item.ItemId) ?? "this item"} has no count to change";
            return null;
        }

        // A weapon may be empty; ammo and ribbons need at least one (an item with no count left is removed by the game).
        var lowest = item.ItemId <= LastWeapon ? 0 : 1;
        if (quantity < lowest || quantity > byte.MaxValue)
        {
            refusal = $"{quantity} is outside {lowest}..{byte.MaxValue}";
            return null;
        }

        var rebecca = Raw(snapshot, Re1Session.Fields.Character) is { } c && (c & 3) == Rebecca;
        return new FieldWrite($"{(rebecca ? "rebInv" : "inv")}{slot}Qty", quantity);
    }

    public FieldWrite? AddToStorage(GameSnapshot snapshot, int itemId, int quantity, out string? refusal)
    {
        refusal = null;
        if (itemId is < 1 or > LastItem || itemName(itemId) is null)
        {
            refusal = $"item {itemId} isn't one the game knows";
            return null;
        }

        if (snapshot.ItemBox is not { HasValue: true, Value: { } box })
        {
            refusal = "the item box can't be read right now";
            return null;
        }

        quantity = HasQuantity(itemId) ? quantity : 1;
        if (quantity is < 1 or > byte.MaxValue)
        {
            refusal = $"{quantity} is outside 1..{byte.MaxValue}";
            return null;
        }

        var used = box.Select(b => b.Slot).ToHashSet();
        var free = Enumerable.Range(0, ItemBoxEntries).FirstOrDefault(i => !used.Contains(i), -1);
        if (free < 0)
        {
            refusal = "the item box is full";
            return null;
        }

        return new FieldWrite($"box{free}", itemId | (quantity << 8));
    }

    /// <summary>
    /// A shot: the same gun stays equipped in the same slot and its loaded rounds drop (the knife has none; a reload or a
    /// bazooka round change raises the count or changes the item, so neither reads as a shot).
    /// </summary>
    public string? NonMeleeShot(GameSnapshot previous, GameSnapshot current)
    {
        if (Equipped(previous) is not { } before || Equipped(current) is not { } now
            || before.Slot != now.Slot || before.ItemId != now.ItemId || now.ItemId <= Knife || now.ItemId > LastWeapon)
        {
            return null;
        }

        return now.Rounds < before.Rounds ? itemName(now.ItemId) ?? $"weapon {now.ItemId}" : null;
    }

    private static (int Slot, int ItemId, int Rounds)? Equipped(GameSnapshot snapshot)
    {
        if (snapshot.Inventory is not { HasValue: true, Value: { } held } || Raw(snapshot, "equippedSlot") is not { } equipped || equipped < 1)
        {
            return null;
        }

        var slot = (int)equipped - 1;
        if (held.FirstOrDefault(h => h.Slot == slot) is not { } item)
        {
            return null;
        }

        // The slot's quantity byte is read every poll; the item list less often.
        var rebecca = Raw(snapshot, Re1Session.Fields.Character) is { } c && (c & 3) == Rebecca;
        var rounds = Raw(snapshot, $"{(rebecca ? "rebInv" : "inv")}{slot}Qty") is { } q ? (int)q : item.Quantity;
        return (slot, item.ItemId, rounds);
    }

    private static long? Raw(GameSnapshot snapshot, string field) =>
        snapshot.RawFields.TryGetValue(field, out var value) && value.HasValue ? value.Value : null;
}
