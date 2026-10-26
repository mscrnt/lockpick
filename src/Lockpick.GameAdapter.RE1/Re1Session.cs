using Lockpick.Core.Adapters;
using Lockpick.Core.Memory;
using Lockpick.Core.Profiles;
using Lockpick.Core.State;

namespace Lockpick.GameAdapter.RE1;

/// <summary>Reads RE1 profile fields and maps them onto the normalized <see cref="GameSnapshot"/>.</summary>
internal sealed class Re1Session : IGameSession
{
    internal static class Fields
    {
        public const string Stage = "stage";
        public const string Room = "room";
        public const string Camera = "camera";
        public const string Hp = "hp";
        public const string MaxHp = "maxHp";
        public const string PlayerStatus = "playerStatus";
        public const string Character = "character";
        public const string GameTimer = "gameTimer";
        public const string StateFlags = "stateFlags";
        public const string MainMenu = "mainMenu";
        public const string PositionX = "positionX";
        public const string PositionY = "positionY";
        public const string PositionZ = "positionZ";
        public const string WindowFocused = "windowFocused";

        /// <summary>The keyboard map (32 virtual-key codes at 0xAC4030 in GOG English), four to a field.</summary>
        public static readonly string[] KeyMap = [.. Enumerable.Range(0, 8).Select(i => $"keyMap{i}")];
    }

    internal const int FramesPerSecond = 30;
    internal const int StageCount = 7;
    internal const int MaxRoom = 0x1F;

    private readonly IProcessMemory _memory;
    private readonly IReadOnlyList<FieldSpec> _fields;
    private readonly HashSet<string> _trusted;
    private readonly Dictionary<string, Field<long>> _lastRaw = new(StringComparer.OrdinalIgnoreCase);
    private readonly Cluster[] _clusters;
    private GameSnapshot? _previous;
    private Field<IReadOnlyList<PlacedItem>> _placed = Field<IReadOnlyList<PlacedItem>>.Unavailable;
    private Field<IReadOnlyList<PlacedFixture>> _fixtures = Field<IReadOnlyList<PlacedFixture>>.Unavailable;
    private Field<IReadOnlyList<HeldItem>> _inventory = Field<IReadOnlyList<HeldItem>>.Unavailable;
    private Field<IReadOnlyList<HeldItem>> _box = Field<IReadOnlyList<HeldItem>>.Unavailable;

    // The tables, pointers and arrays below come from the profile's named addresses (each build has its own), and a
    // feature whose addresses a build lacks is simply unavailable. Names as in data/games/re1/profiles/*.json.
    internal static class Addresses
    {
        public const string InventoryPointer = "inventoryPointer";
        public const string Inventory = "inventory";
        public const string InventoryRebecca = "inventoryRebecca";
        public const string HeldCount = "heldCount";
        public const string ItemBox = "itemBox";
        public const string ActionTable = "actionTable";
        public const string ActionTail = "actionTail";
        public const string ActorSlots = "actorSlots";
    }

    // Inventory (docs/MEMORY_MAP.md): the game reaches the held items through the inventory pointer — Chris's/Jill's
    // inventory or Rebecca's — and treats slots at or past the held count as empty (live 2026-10-14). Item box: 48
    // entries, each low byte id, high byte quantity (live 2026-10-13).
    private readonly ulong? _inventoryPointer;
    private readonly ulong? _inventoryChrisJill;
    private readonly ulong? _inventoryRebecca;
    private readonly ulong? _heldCount;
    private readonly ulong? _itemBox;
    private const int ItemBoxCount = 48;
    private RoomLocation? _placedRoom;
    private DateTimeOffset _placedAt;

    // The room action table (static: decomp g_RoomActionTable; live 2026-10-14: the pickups it lists match the
    // room files and the pickups made). 24 entries of 12 bytes; [0] handler (4 = item pickup), +8 the record
    // (zone x/z/width/depth as i16/i16/u16/u16, item id at +8). The tail pointer marks the last entry in use.
    private readonly ulong? _actionTable;
    private readonly ulong? _actionTail;
    private const int ActionEntries = 24;
    private const int PickupHandler = 4;

    // Room action handlers 8 (item box) and 16 (typewriter): live 2026-10-14 the zones in 100/118/30E opened the item
    // box and asked to save. Room files also list handler-16 zones the game doesn't arm in every state (106, 307), so
    // the map shows the ones in the live table.
    private const int ItemBoxHandler = 8;
    private const int TypewriterHandler = 16;
    private static readonly TimeSpan PlacedRefresh = TimeSpan.FromSeconds(1);

    // Enemy/NPC slots (live 2026-10-14, the dev combat tests: +0x00 bit 0 active, +0x01 type, +0x34/+0x3C X/Z as i32,
    // +0x74 facing, +0x88 HP as i16; a dead enemy's HP goes to −1 or below).
    private readonly ulong? _actorSlots;
    internal const int ActorSize = 0x18C;
    internal const int ActorCount = 30;
    private const int FirstNpcType = 0x20;
    private readonly Func<int, (string Name, ActorKind Kind)> _actorInfo;
    private readonly byte[] _actorBuffer = new byte[ActorSize * ActorCount];

    public Re1Session(IProcessMemory memory, VersionProfile profile, Func<int, (string Name, ActorKind Kind)>? actorInfo = null)
    {
        _memory = memory;
        _actorInfo = actorInfo ?? (type => ($"type {type}", type >= FirstNpcType ? ActorKind.Npc : ActorKind.Enemy));
        Profile = profile;
        _fields = profile.Fields;
        _trusted = new HashSet<string>(profile.Fields.Where(f => f.IsTrusted).Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        _clusters = Cluster.Build(profile.Fields);
        _inventoryPointer = profile.TrustedAddress(Addresses.InventoryPointer);
        _inventoryChrisJill = profile.TrustedAddress(Addresses.Inventory);
        _inventoryRebecca = profile.TrustedAddress(Addresses.InventoryRebecca);
        _heldCount = profile.TrustedAddress(Addresses.HeldCount);
        _itemBox = profile.TrustedAddress(Addresses.ItemBox);
        _actionTable = profile.TrustedAddress(Addresses.ActionTable);
        _actionTail = profile.TrustedAddress(Addresses.ActionTail);
        _actorSlots = profile.TrustedAddress(Addresses.ActorSlots);
    }

    public VersionProfile Profile { get; }

    public bool CaptureRegions { get; set; }

    /// <summary>Process reads issued by the last <see cref="Read"/> (diagnostics and tests).</summary>
    internal int LastReadCalls { get; private set; }

    public GameSnapshot Read(long sequence, DateTimeOffset now)
    {
        var raw = new Dictionary<string, Field<long>>(_fields.Count, StringComparer.OrdinalIgnoreCase);
        var calls = 0;
        foreach (var cluster in _clusters)
        {
            // One read per cluster of nearby fields instead of one per field; if the block read fails (e.g. a
            // partially unmapped range), fall back to reading each field so one bad page can't hide the rest.
            calls++;
            var block = _memory.TryRead(cluster.Start, cluster.Buffer);
            foreach (var spec in cluster.Fields)
            {
                long v = 0;
                var ok = block;
                if (block)
                {
                    v = spec.Type.Decode(cluster.Buffer.AsSpan((int)(spec.Address - cluster.Start)));
                }
                else
                {
                    calls++;
                    ok = _memory.TryReadValue(spec.Address, spec.Type, out v);
                }

                var value = ok
                    ? Field<long>.Valid(spec.Mask is { } mask ? v & mask : v)
                    : _lastRaw.TryGetValue(spec.Name, out var last) ? last.AsFailedRead() : Field<long>.Unavailable;
                raw[spec.Name] = value;
                _lastRaw[spec.Name] = value;
            }
        }

        var snapshot = Interpret(raw, sequence, now);
        if (snapshot.Location.TryGet(out var room) && snapshot.InGameplay is { HasValue: true, Value: true })
        {
            // The inventory every poll: item splits must land on the frame the item arrives, as LiveSplit's RE1
            // autosplitter does (live 2026-10-18: with a once-a-second read Lockpick's item splits were up to 0.8 s
            // late). The item box and the room's placed items change rarely and are read once a second.
            _inventory = ReadInventory(ref calls);
            if (_placedRoom != room || now - _placedAt >= PlacedRefresh)
            {
                _placed = ReadPlacedItems(ref calls);
                _box = ReadBox(ref calls);
                _placedRoom = room;
                _placedAt = now;
            }

            calls++;
            snapshot = snapshot with
            {
                PlacedItems = _placed, PlacedFixtures = _fixtures, Actors = ReadActors(), Inventory = _inventory, ItemBox = _box,
            };
        }
        if (CaptureRegions && Profile.CaptureRegions.Count > 0)
        {
            var regions = new List<RegionData>(Profile.CaptureRegions.Count);
            foreach (var region in Profile.CaptureRegions)
            {
                calls++;
                var data = new byte[region.Length];
                if (_memory.TryRead(region.Address, data))
                {
                    regions.Add(new RegionData(region.Name, region.Address, data));
                }
            }

            snapshot = snapshot with { Regions = regions };
        }

        LastReadCalls = calls;
        _previous = snapshot;
        return snapshot;
    }

    private Field<IReadOnlyList<HeldItem>> ReadInventory(ref int calls)
    {
        var inventory = Field<IReadOnlyList<HeldItem>>.Unavailable;
        calls += 3;
        Span<byte> slots = stackalloc byte[16];
        Span<byte> count = stackalloc byte[1];
        if (_inventoryPointer is { } inventoryPointer && _heldCount is { } heldCount
            && _memory.TryReadU32(inventoryPointer, out var pointer) && (pointer == _inventoryChrisJill || pointer == _inventoryRebecca)
            && _memory.TryRead(heldCount, count) && _memory.TryRead(pointer, slots))
        {
            var held = new List<HeldItem>();
            for (var i = 0; i < Math.Min((int)count[0], 8); i++)
            {
                if (slots[i * 2] != 0)
                {
                    held.Add(new HeldItem(i, slots[i * 2], slots[i * 2 + 1]));
                }
            }

            inventory = Field<IReadOnlyList<HeldItem>>.Valid(held);
        }

        return inventory;
    }

    private Field<IReadOnlyList<HeldItem>> ReadBox(ref int calls)
    {
        var box = Field<IReadOnlyList<HeldItem>>.Unavailable;
        calls++;
        Span<byte> entries = stackalloc byte[ItemBoxCount * 2];
        if (_itemBox is { } itemBox && _memory.TryRead(itemBox, entries))
        {
            var stored = new List<HeldItem>();
            for (var i = 0; i < ItemBoxCount; i++)
            {
                if (entries[i * 2] != 0)
                {
                    stored.Add(new HeldItem(i, entries[i * 2], entries[i * 2 + 1]));
                }
            }

            box = Field<IReadOnlyList<HeldItem>>.Valid(stored);
        }

        return box;
    }

    private Field<IReadOnlyList<LiveActor>> ReadActors()
    {
        if (_actorSlots is not { } actorSlots || !_memory.TryRead(actorSlots, _actorBuffer))
        {
            return Field<IReadOnlyList<LiveActor>>.Unavailable;
        }

        var actors = new List<LiveActor>();
        for (var i = 0; i < ActorCount; i++)
        {
            var e = _actorBuffer.AsSpan(i * ActorSize, ActorSize);
            var type = e[1];
            var hp = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(e[0x88..]);
            // Inactive slots, and dead enemies (NPCs keep HP 0 or −1 and stay drawn while active).
            if ((e[0] & 1) == 0 || (type < FirstNpcType && hp <= 0))
            {
                continue;
            }

            var (name, kind) = _actorInfo(type);
            actors.Add(new LiveActor(i, type, name, kind, System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(e[0x34..]),
                System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(e[0x3C..]),
                System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(e[0x74..]), hp));
        }

        return Field<IReadOnlyList<LiveActor>>.Valid(actors);
    }

    private Field<IReadOnlyList<PlacedItem>> ReadPlacedItems(ref int calls)
    {
        Span<byte> table = stackalloc byte[ActionEntries * 12];
        calls++;
        if (_actionTable is not { } actionTable || _actionTail is not { } actionTail
            || !_memory.TryRead(actionTable, table) || !_memory.TryReadU32(actionTail, out var tailAddress))
        {
            return Field<IReadOnlyList<PlacedItem>>.Unavailable;
        }

        var used = tailAddress >= actionTable && tailAddress < actionTable + (ulong)table.Length
            ? (int)((tailAddress - actionTable) / 12) + 1
            : 0;
        var items = new List<PlacedItem>();
        var fixtures = new List<PlacedFixture>();
        Span<byte> record = stackalloc byte[12];
        for (var i = 0; i < used; i++)
        {
            var entry = table.Slice(i * 12, 12);
            if (entry[0] is ItemBoxHandler or TypewriterHandler)
            {
                calls++;
                if (_memory.TryRead(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]), record))
                {
                    fixtures.Add(new PlacedFixture(entry[0] == ItemBoxHandler ? Core.Maps.FixtureKind.ItemBox : Core.Maps.FixtureKind.Typewriter,
                        System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(record),
                        System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(record[2..]),
                        System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(record[4..]),
                        System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(record[6..])));
                }

                continue;
            }

            if (entry[0] != PickupHandler)
            {
                continue;
            }

            calls++;
            if (_memory.TryRead(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]), record))
            {
                var width = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
                var depth = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
                if (width > 0 && depth > 0)
                {
                    items.Add(new PlacedItem(record[8],
                        System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(record),
                        System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(record[2..]), width, depth));
                }
            }
        }

        _fixtures = Field<IReadOnlyList<PlacedFixture>>.Valid(fixtures);
        return Field<IReadOnlyList<PlacedItem>>.Valid(items);
    }

    /// <summary>A contiguous span covering nearby profile fields, read with a single process read.</summary>
    private sealed class Cluster(ulong start, int length, List<FieldSpec> fields)
    {
        /// <summary>Fields further apart than this start a new cluster (don't read large unrelated gaps).</summary>
        private const ulong MaxGap = 0x1000;

        /// <summary>Upper bound for one block read.</summary>
        private const ulong MaxLength = 0x10000;

        public ulong Start { get; } = start;

        public byte[] Buffer { get; } = new byte[length];

        public IReadOnlyList<FieldSpec> Fields { get; } = fields;

        public static Cluster[] Build(IEnumerable<FieldSpec> specs)
        {
            var clusters = new List<Cluster>();
            ulong start = 0, end = 0;
            var current = new List<FieldSpec>();
            foreach (var spec in specs.OrderBy(f => f.Address))
            {
                var fieldEnd = spec.Address + (ulong)spec.Type.SizeOf();
                if (current.Count > 0 && (spec.Address > end + MaxGap || fieldEnd - start > MaxLength))
                {
                    clusters.Add(new Cluster(start, (int)(end - start), current));
                    current = [];
                }

                if (current.Count == 0)
                {
                    start = spec.Address;
                    end = fieldEnd;
                }

                end = Math.Max(end, fieldEnd);
                current.Add(spec);
            }

            if (current.Count > 0)
            {
                clusters.Add(new Cluster(start, (int)(end - start), current));
            }

            return [.. clusters];
        }
    }

    private GameSnapshot Interpret(Dictionary<string, Field<long>> raw, long sequence, DateTimeOffset now)
    {
        var previous = _previous ?? new GameSnapshot();
        var gameplay = DeriveInGame(raw, previous.InGameplay);

        return new GameSnapshot
        {
            Sequence = sequence,
            CapturedAt = now,
            RawFields = raw,
            InGameplay = gameplay,
            Location = Derive(raw, gameplay, previous.Location, [Fields.Stage, Fields.Room], v =>
                v[0] < StageCount && v[1] <= MaxRoom ? new RoomLocation((int)v[0] + 1, (int)v[1]) : null),
            Camera = Derive(raw, gameplay, previous.Camera, [Fields.Camera], v => (int?)v[0]),
            Health = DeriveHealth(raw, gameplay, previous.Health),
            Character = Derive(raw, gameplay, previous.Character, [Fields.Character], v => v[0] switch
            {
                0 => PlayableCharacter.Chris,
                1 => PlayableCharacter.Jill,
                3 => PlayableCharacter.Rebecca,
                _ => PlayableCharacter.Unknown,
            }),
            Position = Derive(raw, gameplay, previous.Position, [Fields.PositionX, Fields.PositionY, Fields.PositionZ], v =>
                new Position((int)v[0], (int)v[1], (int)v[2])),
            GameTime = Derive(raw, gameplay, previous.GameTime, [Fields.GameTimer], v =>
                TimeSpan.FromSeconds(v[0] / (double)FramesPerSecond)),
            Condition = Derive(raw, gameplay, previous.Condition, [Fields.Hp, Fields.MaxHp, Fields.PlayerStatus], v =>
                Condition((int)v[0], (int)v[1], (int)v[2])),
        };
    }

    /// <summary>
    /// A game is in session when the title menu is not shown, a character has been set up (max HP
    /// is 0 from boot until New Game/Load) and the player is alive. After a death the previous
    /// game's values (e.g. HP -10) stay in memory through character select and the intro, so HP &gt; 0
    /// is required to avoid reporting them. The decomp's 0x02000000 "gameplay" flag was refuted by
    /// live traces and is deliberately not used; see docs/RESEARCH.md.
    /// </summary>
    private Field<bool> DeriveInGame(Dictionary<string, Field<long>> raw, Field<bool> previous)
    {
        if (Trusted(raw, Fields.MainMenu) is not { } menu
            || Trusted(raw, Fields.MaxHp) is not { } maxHp
            || Trusted(raw, Fields.Hp) is not { } hp)
        {
            return Field<bool>.Unsupported;
        }

        if (!menu.IsValid || !maxHp.IsValid || !hp.IsValid)
        {
            return previous.AsFailedRead();
        }

        return Field<bool>.Valid(menu.Value == 0 && maxHp.Value > 0 && hp.Value > 0);
    }

    /// <summary>
    /// The status screen's ECG band (docs/MEMORY_MAP.md, 0xAE9F2F, only written while that screen is drawn, so it is
    /// worked out here the same way): poisoned (status bit 0x02) shows Poison; otherwise (HP − 1) / (max HP ÷ 4) is
    /// 0 Danger, 1 Caution, 2–3 Fine (live: Jill 1–24 Danger, 25–48 Caution, 49–96 Fine).
    /// </summary>
    internal static HealthCondition? Condition(int hp, int maxHp, int status)
    {
        if (maxHp < 4)
        {
            return null;
        }

        if ((status & 0x02) != 0)
        {
            return HealthCondition.Poison;
        }

        return ((hp - 1) / (maxHp >> 2)) switch
        {
            <= 0 => HealthCondition.Danger,
            1 => HealthCondition.Caution,
            _ => HealthCondition.Fine,
        };
    }

    private Field<Health> DeriveHealth(Dictionary<string, Field<long>> raw, Field<bool> gameplay, Field<Health> previous)
    {
        var health = Derive(raw, gameplay, previous, [Fields.Hp], v => new Health((int)v[0], null));
        if (health.IsValid && Trusted(raw, Fields.MaxHp) is { IsValid: true } max && max.Value > 0)
        {
            return Field<Health>.Valid(health.Value with { Maximum = (int)max.Value });
        }

        return health;
    }

    /// <summary>
    /// Builds a normalized field from trusted raw inputs. Unsupported if any input lacks a trusted profile
    /// field; unavailable outside gameplay or when the value fails sanity checks; stale when a read failed.
    /// </summary>
    private Field<T> Derive<T>(
        Dictionary<string, Field<long>> raw,
        Field<bool> gameplay,
        Field<T> previous,
        string[] inputs,
        Func<long[], T?> map)
        where T : struct
    {
        var values = new long[inputs.Length];
        var readFailed = false;
        for (var i = 0; i < inputs.Length; i++)
        {
            if (Trusted(raw, inputs[i]) is not { } field)
            {
                return Field<T>.Unsupported;
            }

            if (!field.IsValid)
            {
                readFailed = true;
                continue;
            }

            values[i] = field.Value;
        }

        if (readFailed || gameplay.Status is FieldStatus.Stale or FieldStatus.Unavailable)
        {
            return previous.AsFailedRead();
        }

        // Outside gameplay these addresses hold leftovers from the previous session or attract mode.
        if (gameplay is { IsValid: true, Value: false })
        {
            return Field<T>.Unavailable;
        }

        return map(values) is { } mapped ? Field<T>.Valid(mapped) : Field<T>.Unavailable;
    }

    private Field<long>? Trusted(Dictionary<string, Field<long>> raw, string name) =>
        _trusted.Contains(name) && raw.TryGetValue(name, out var value) ? value : null;
}
