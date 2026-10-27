using System.Buffers.Binary;
using System.Collections.Concurrent;
using Lockpick.Core.Maps;
using Lockpick.Core.State;

namespace Lockpick.GameAdapter.RE1;

/// <summary>
/// Reads room layouts from the player's own RE1 room files (USA\StageN\ROOMsrrV.RDT; V = 0 Chris, 1 Jill), read-only
/// and cached per room. Layout (tools/rdt/rdt_map.py; ecruells/resident-evil-pc-decomp Types.h, checked against the
/// files): byte 1 camera count, cameras at 0x94 (0x2C each: from x/y/z at +0x08, target x/y/z at +0x14, fov at +0x28);
/// collision at the pointer at 0x4C (header: five per-quadrant counts as i32 at +4, then 0x0C-byte records xMax, zMax,
/// xMin, zMin, type — low byte 3 = circle — and flags: 0x100 or 0x200 = solid); doors (opcode 0x0C, with lock and key), item pickups (opcode
/// 0x18) and item-use spots (room action 0x0D with handler 7 and words (9, item − 0x1B): it raises the item's use bit
/// while the player stands there) from the init/main scripts at the pointers at 0x60/0x64.
/// </summary>
public sealed class Re1RoomMaps : IRoomMapSource
{
    // Bytes per script opcode including the opcode byte (IntelOrca's biohazard-utils / BioRand, MIT; tools/rdt).
    private static readonly byte[] OpcodeSizes =
    [
        2, 2, 2, 2, 4, 4, 4, 6, 4, 2, 2, 4, 26, 18, 2, 8,
        2, 2, 10, 4, 4, 2, 2, 10, 26, 4, 2, 22, 6, 2, 4, 28,
        14, 14, 4, 2, 4, 4, 0, 2, 6, 2, 12, 4, 2, 4, 0, 4,
        12, 4, 4, 2, 8, 4, 4, 4, 4, 2, 4, 6, 6, 12, 2, 6,
        16, 4, 4, 4, 2, 2, 44, 14, 2, 2, 2, 2, 4, 2, 4, 2,
        2,
    ];

    private const int HeaderSize = 0x94;

    // A collision record's flags: walls carry 0x300 and furniture 0x200 (the dining table and its chairs in 105; the
    // player can't walk through either, live 2026-10-27); only the steps and slopes (type 4) have neither: floor zones.
    private const int Solid = 0x300;
    private const int SwordKey = 51;
    private const int Lockpick = 49;
    private const int InkRibbon = 0x2F;

    // Room action handlers (opcode 0x0D +0x0A): 8 item box, 16 typewriter (live 2026-10-14: the zones in the save rooms
    // open the item box / ask to save).
    private const int ItemBoxHandler = 8;
    private const int TypewriterHandler = 16;
    private const int MaxFileSize = 4 * 1024 * 1024;

    private readonly string _stageRoot;
    private readonly Func<int, string> _itemName;
    private readonly ConcurrentDictionary<string, RoomMap?> _cache = new();
    private readonly IReadOnlyDictionary<(string Room, int Item), (string Bank, uint Mask)> _fills;

    /// <param name="fills">Confirmed story flags that mean an item is in place at its use spot (flag-labels.json "fills").</param>
    public Re1RoomMaps(string installDirectory, Func<int, string> itemName,
        IReadOnlyDictionary<(string Room, int Item), (string Bank, uint Mask)>? fills = null)
    {
        _fills = fills ?? new Dictionary<(string, int), (string, uint)>();
        // The data folder itself (Re1Install.DataDirectory: USA or JPN), or an install folder holding USA.
        var usa = Path.Combine(installDirectory, "USA");
        _stageRoot = Directory.Exists(usa) ? usa : installDirectory;
        _itemName = itemName;
    }

    public RoomMap? Load(RoomLocation room, PlayableCharacter character)
    {
        var variant = character == PlayableCharacter.Jill ? 1 : 0;
        var name = $"ROOM{room.Stage}{room.Room:X2}{variant}.RDT";
        return _cache.GetOrAdd(name, _ =>
        {
            var path = Path.Combine(_stageRoot, $"Stage{room.Stage}", name);
            try
            {
                var info = new FileInfo(path);
                var map = info.Exists && info.Length is >= HeaderSize and <= MaxFileSize
                    ? Parse(File.ReadAllBytes(path), room, _itemName)
                    : null;
                // Jill's room files list ink ribbons the game never places for her (Re1ProgressTracker.InkRibbon), and
                // her lockpick opens the doors that take Chris's sword key.
                return map is not null && variant == 1
                    ? map with
                    {
                        Items = [.. map.Items.Where(i => i.ItemId != InkRibbon)],
                        Doors = [.. map.Doors.Select(d => d.Key == SwordKey ? d with { Key = Lockpick } : d)],
                    }
                    : map;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        });
    }

    /// <summary>
    /// An item is taken once its pickup flag is clear: flagRoomItems bit n (bit 31 − n % 32 of word n / 32) is 1 while
    /// the item is still in the world (verified 2026-10-14: 295 pickups cleared exactly the indexed flag).
    /// </summary>
    public bool? IsTaken(MapItem item, GameSnapshot snapshot)
    {
        if (item.Flag < 0 || !snapshot.RawFields.TryGetValue($"flagRoomItems{item.Flag / 32}", out var word) || !word.HasValue)
        {
            return null;
        }

        return (word.Value & (1L << (31 - item.Flag % 32))) == 0;
    }

    /// <summary>Only use spots with a confirmed story flag are known; the rest stay null.</summary>
    public bool? IsFilled(RoomMap map, MapSocket socket, GameSnapshot snapshot)
    {
        if (!_fills.TryGetValue((map.RoomId, socket.ItemId), out var fill)
            || !snapshot.RawFields.TryGetValue(fill.Bank, out var word) || !word.HasValue)
        {
            return null;
        }

        return (word.Value & fill.Mask) != 0;
    }

    /// <summary>
    /// A door's lock flag is set once it has been opened (flagLocks, flag n = bit 31 − n % 32 of word n / 32; "unlocked
    /// door 20E→210" seen live 2026-10-14 when the examined Shield key opened it).
    /// </summary>
    public bool? IsUnlocked(MapDoor door, GameSnapshot snapshot)
    {
        if (door.LockFlag is not { } flag || !snapshot.RawFields.TryGetValue($"flagLocks{flag / 32}", out var word) || !word.HasValue)
        {
            return door.LockFlag is null ? true : null;
        }

        return (word.Value & (1L << (31 - flag % 32))) != 0;
    }

    /// <summary>
    /// Jill's lockpick: Barry hands it over in the main hall and it never appears in the inventory or the item box; the
    /// story flag (flagScenarioA3 bit 0x8, docs/MEMORY_MAP.md) says she has it. It opens the Sword-key doors.
    /// </summary>
    public IEnumerable<int> HeldElsewhere(GameSnapshot snapshot) =>
        snapshot.Character is { HasValue: true, Value: PlayableCharacter.Jill }
        && snapshot.RawFields.TryGetValue("flagScenarioA3", out var word) && word.HasValue && (word.Value & 0x8) != 0 ? [Lockpick] : [];

    /// <summary>Parses one room file; null when it isn't a readable room (stubs, damaged files).</summary>
    public static RoomMap? Parse(ReadOnlySpan<byte> data, RoomLocation room, Func<int, string> itemName)
    {
        if (data.Length < HeaderSize)
        {
            return null;
        }

        try
        {
            var cameras = new List<MapCamera>();
            int count = data[1];
            for (var i = 0; i < count && HeaderSize + (i + 1) * 0x2C <= data.Length; i++)
            {
                var o = HeaderSize + i * 0x2C;
                // +0x0C / +0x18: the heights; +0x28: the field of view (ecruells/resident-evil-pc-decomp RDT_Camera).
                cameras.Add(new MapCamera(i, I32(data, o + 0x08), I32(data, o + 0x10), I32(data, o + 0x14), I32(data, o + 0x1C),
                    I32(data, o + 0x0C), I32(data, o + 0x18), I32(data, o + 0x28)));
            }

            var shapes = new List<MapShape>();
            var collision = (int)U32(data, 0x4C);
            if (collision > 0 && collision + 0x18 <= data.Length)
            {
                var records = 0;
                for (var q = 0; q < 5; q++)
                {
                    var n = I32(data, collision + 4 + q * 4);
                    if (n is > 0 and < 4096)
                    {
                        records += n;
                    }
                }

                for (var i = 0; i < records; i++)
                {
                    var o = collision + 0x18 + i * 0x0C;
                    if (o + 0x0C > data.Length)
                    {
                        break;
                    }

                    var kind = U16(data, o + 8);
                    var flags = U16(data, o + 10);
                    shapes.Add(new MapShape((kind & 0xFF) == 3 ? MapShapeKind.Circle : MapShapeKind.Rectangle,
                        U16(data, o + 4), U16(data, o + 6), U16(data, o), U16(data, o + 2), (flags & Solid) != 0));
                }
            }

            var doors = new List<MapDoor>();
            var items = new List<MapItem>();
            var sockets = new List<MapSocket>();
            var fixtures = new List<MapFixture>();
            foreach (var pointerOffset in new[] { 0x60, 0x64 })
            {
                var block = (int)U32(data, pointerOffset);
                while (block > 0 && block + 2 <= data.Length)
                {
                    var length = U16(data, block);
                    if (length == 0 || block + length > data.Length)
                    {
                        break;
                    }

                    ReadScript(data.Slice(block + 2, length - 2), room, doors, items, sockets, fixtures, itemName);
                    block += length;
                }
            }

            return new RoomMap($"{room.Stage}{room.Room:X2}", shapes, doors, items, cameras, sockets, fixtures);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static void ReadScript(ReadOnlySpan<byte> script, RoomLocation room, List<MapDoor> doors, List<MapItem> items,
        List<MapSocket> sockets, List<MapFixture> fixtures, Func<int, string> itemName)
    {
        var pos = 0;
        while (pos < script.Length)
        {
            var op = script[pos];
            var size = op < OpcodeSizes.Length ? OpcodeSizes[op] : 0;
            if (size == 0 || pos + size > script.Length)
            {
                return; // unknown opcode: stop rather than misread the rest
            }

            var b = script.Slice(pos, size);
            if (op == 0x0C)
            {
                var dest = b[0x0F];
                var stage = dest >> 5 == 0 ? room.Stage : dest >> 5;
                // +0x0E: lock (bit 0x80 = starts locked, low 6 bits its flagLocks number); +0x18: the key item, or 254
                // "opens from this side" / 255 "locked from the other side" (every keyless locked door in both
                // characters' files is a 254/255 pair, one side each, except 206 → 207, which has no door back and is
                // 254), or 0: the story opens it (11A → 11B: the four crests).
                var lockByte = b[0x0E];
                var key = b[0x18];
                int? lockFlag = (lockByte & 0x80) != 0 && lockByte != 0xFF ? lockByte & 0x3F : null;
                var unlock = key switch
                {
                    254 => MapUnlock.ThisSide,
                    255 => MapUnlock.OtherSide,
                    0 => MapUnlock.Event,
                    _ => MapUnlock.Key,
                };
                // +0x0D: how the way is shown: 0x60 stairs (every mansion staircase, the lab stairway), 0x40 no door (the
                // courtyard's gates, ladders, lifts); a door model otherwise.
                var exit = b[0x0D] switch { 0x60 => MapExitKind.Stairs, 0x40 => MapExitKind.Passage, _ => MapExitKind.Door };
                // +0x10: arrival x, y, z and facing in the next room (i16 each).
                var door = new MapDoor(I16(b, 2), I16(b, 4), U16(b, 6), U16(b, 8), $"{stage}{dest & 0x1F:X2}", I16(b, 0x10), I16(b, 0x14),
                    lockFlag, unlock == MapUnlock.Key ? key : null, lockFlag is null ? MapUnlock.Key : unlock, exit);
                doors.Add(door);

                // No door leads into the mansion revisited (stages 6 and 7): the game switches to those rooms by itself
                // once the story moves on, so a door from outside into the mansion also leads to its revisited room.
                if (room.Stage >= 3 && stage is 1 or 2)
                {
                    doors.Add(door with { Target = $"{stage + 5}{dest & 0x1F:X2}" });
                }
            }
            else if (op == 0x18)
            {
                int x = I16(b, 2), z = I16(b, 4), w = U16(b, 6), d = U16(b, 8);
                var id = b[0x0A];
                // Zones of 16 or less are placed by script events, not picked up where they stand; rooms often list
                // the same pickup in both branches of a condition.
                if ((w > 16 || d > 16) && !items.Exists(i => i.ItemId == id && i.X == x && i.Z == z))
                {
                    items.Add(new MapItem(x, z, w, d, id, itemName(id), b[0x16]));
                }
            }

            else if (op == 0x0D && b[0x0A] is ItemBoxHandler or TypewriterHandler)
            {
                var kind = b[0x0A] == ItemBoxHandler ? FixtureKind.ItemBox : FixtureKind.Typewriter;
                int x = I16(b, 2), z = I16(b, 4);
                if (!fixtures.Exists(f => f.Kind == kind && f.X == x && f.Z == z))
                {
                    fixtures.Add(new MapFixture(x, z, U16(b, 6), U16(b, 8), kind));
                }
            }
            else if (op == 0x0D && b[0x0A] == 7 && U16(b, 0x0C) == 9)
            {
                int x = I16(b, 2), z = I16(b, 4), w = U16(b, 6), d = U16(b, 8), id = U16(b, 0x0E) + 0x1B;
                if (!sockets.Exists(s => s.ItemId == id && s.X == x && s.Z == z))
                {
                    sockets.Add(new MapSocket(x, z, w, d, id, itemName(id)));
                }
            }

            pos += size;
        }
    }

    private static int I32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadInt32LittleEndian(d[o..]);

    private static uint U32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d[o..]);

    private static int U16(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt16LittleEndian(d[o..]);

    private static int I16(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadInt16LittleEndian(d[o..]);
}
