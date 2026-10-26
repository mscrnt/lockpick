using Lockpick.Core.Adapters;
using Lockpick.Core.Memory;
using Lockpick.Core.Processes;
using Lockpick.Core.Profiles;
using Lockpick.Core.State;
using Lockpick.GameAdapter.RE1;
using Lockpick.Tests.Fakes;

namespace Lockpick.Tests.RE1;

/// <summary>Exercises the real RE1 adapter against fake process memory laid out like the GOG build.</summary>
public class Re1AdapterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);

    private readonly Re1Adapter _adapter = new();
    private readonly VersionProfile _profile;
    private readonly FakeMemory _memory;
    private readonly IGameSession _session;

    public Re1AdapterTests()
    {
        _profile = _adapter.Profiles.Single(p => p.Id == "re1-gog-usa-hotfix4");
        _memory = new FakeMemory().WithVerifiedImage(_profile);
        _session = _adapter.Attach(new FakeAttachedProcess(1, _memory, new ModuleImage(0x400000, 0x49B2000, null)), _profile);
    }

    private ulong Addr(string field) => _profile.FindField(field)!.Address;

    private ulong Address(string name) => _profile.TrustedAddress(name)!.Value;

    /// <summary>Values observed live in a new Chris game in the main hall (2026-10-12 trace).</summary>
    private void NewChrisGameInMainHall()
    {
        _memory.WriteU8(Addr("stage"), 0)
            .WriteU8(Addr("room"), 0x06)
            .WriteU8(Addr("camera"), 1)
            .WriteI16(Addr("hp"), 140)
            .WriteU8(Addr("maxHp"), 140)
            .WriteU8(Addr("character"), 0)
            .WriteU32(Addr("gameTimer"), 0x142)
            .WriteU32(Addr("stateFlags"), 0x8013000C)
            .WriteI32(Addr("positionX"), 0x3908)
            .WriteI32(Addr("positionZ"), 0x1388)
            .WriteU8(Addr("mainMenu"), 0);
    }

    /// <summary>The live room action table: a door, an item pickup and an event-placed item (zero-size zone).</summary>
    private void ActionTable(ushort pickupItemX)
    {
        const ulong record = 0xC4EE00;
        var table = Address(Re1Session.Addresses.ActionTable);
        var bytes = new byte[288]; // the whole table is read; entries past the tail are ignored
        bytes[0] = 1;   // door
        bytes[12] = 4;  // pickup → record
        BitConverter.GetBytes((uint)record).CopyTo(bytes, 20);
        bytes[24] = 4;  // pickup with a zero-size zone (handed over by an event)
        BitConverter.GetBytes((uint)(record + 0x20)).CopyTo(bytes, 32);
        bytes[36] = 16; // typewriter → record + 0x40
        BitConverter.GetBytes((uint)(record + 0x40)).CopyTo(bytes, 44);
        _memory.Write(table, bytes).Write(Address(Re1Session.Addresses.ActionTail), BitConverter.GetBytes((uint)(table + 36)));
        var typewriter = new byte[12];
        BitConverter.GetBytes((short)2300).CopyTo(typewriter, 0);
        BitConverter.GetBytes((short)8750).CopyTo(typewriter, 2);
        BitConverter.GetBytes((ushort)1600).CopyTo(typewriter, 4);
        BitConverter.GetBytes((ushort)2000).CopyTo(typewriter, 6);
        _memory.Write(record + 0x40, typewriter);
        var item = new byte[12];
        BitConverter.GetBytes((short)pickupItemX).CopyTo(item, 0);
        BitConverter.GetBytes((short)16000).CopyTo(item, 2);
        BitConverter.GetBytes((ushort)2000).CopyTo(item, 4);
        BitConverter.GetBytes((ushort)2000).CopyTo(item, 6);
        item[8] = 47;
        _memory.Write(record, item).Write(record + 0x20, new byte[12]);
    }

    [Fact]
    public void Placed_items_come_from_the_live_action_table()
    {
        NewChrisGameInMainHall();
        ActionTable(13000);

        var placed = _session.Read(1, T0).PlacedItems;

        Assert.Equal(FieldStatus.Valid, placed.Status);
        Assert.Equal(new PlacedItem(47, 13000, 16000, 2000, 2000), Assert.Single(placed.Value));
    }

    [Fact]
    public void Inventory_follows_the_pointer_and_held_count_and_the_box_lists_filled_entries()
    {
        NewChrisGameInMainHall();
        _memory.Write(Address(Re1Session.Addresses.InventoryPointer), BitConverter.GetBytes(0xBE9944u));
        _memory.WriteU8(Address(Re1Session.Addresses.HeldCount), 2);
        _memory.Write(0xBE9944, [0x01, 0x00, 0x02, 0x0F, 0x35, 0x01]); // knife, Beretta 15, a Shield key past the count
        var box = new byte[96];
        box[0] = 0x0B; box[1] = 15;   // clip ×15
        box[10] = 0x44; box[11] = 1;  // green herb (entry 5)
        _memory.Write(Address(Re1Session.Addresses.ItemBox), box);

        var s = _session.Read(1, T0);

        Assert.Equal([new HeldItem(0, 1, 0), new HeldItem(1, 2, 15)], s.Inventory.Value);
        Assert.Equal([new HeldItem(0, 0x0B, 15), new HeldItem(5, 0x44, 1)], s.ItemBox.Value);
        Assert.Equal("Green herb", new Re1Adapter().ItemName(0x44));
    }

    [Fact]
    public void The_inventory_is_read_every_poll_so_item_splits_land_on_time()
    {
        // Live 2026-10-18: read once a second, Lockpick's item splits came up to 0.8 s after LiveSplit's.
        NewChrisGameInMainHall();
        _memory.Write(Address(Re1Session.Addresses.InventoryPointer), BitConverter.GetBytes(0xBE9944u));
        _memory.WriteU8(Address(Re1Session.Addresses.HeldCount), 6);
        _memory.Write(0xBE9944, [0x01, 0x00]);
        Assert.Single(_session.Read(1, T0).Inventory.Value);

        _memory.Write(0xBE9946, [0x03, 0x02]); // the Shotgun arrives 33 ms later
        Assert.Contains(_session.Read(2, T0.AddMilliseconds(33)).Inventory.Value, i => i.ItemId == 3);
    }

    [Fact]
    public void Active_typewriters_and_item_boxes_come_from_the_live_action_table()
    {
        NewChrisGameInMainHall();
        ActionTable(13000);

        var fixtures = _session.Read(1, T0).PlacedFixtures;

        Assert.Equal(new PlacedFixture(Lockpick.Core.Maps.FixtureKind.Typewriter, 2300, 8750, 1600, 2000), Assert.Single(fixtures.Value));
    }

    [Fact]
    public void Active_enemies_and_npcs_are_read_and_dead_or_inactive_slots_skipped()
    {
        NewChrisGameInMainHall();
        void Slot(int i, byte flags, byte type, int x, int z, short hp)
        {
            var at = Address(Re1Session.Addresses.ActorSlots) + (ulong)(i * Re1Session.ActorSize);
            var bytes = new byte[Re1Session.ActorSize];
            bytes[0] = flags;
            bytes[1] = type;
            BitConverter.GetBytes(x).CopyTo(bytes, 0x34);
            BitConverter.GetBytes(z).CopyTo(bytes, 0x3C);
            BitConverter.GetBytes(hp).CopyTo(bytes, 0x88);
            _memory.Write(at, bytes);
        }

        _memory.Write(Address(Re1Session.Addresses.ActorSlots), new byte[Re1Session.ActorSize * Re1Session.ActorCount]);
        Slot(0, 1, 0, 5300, 5800, 63);    // a zombie
        Slot(1, 1, 13, 100, 200, 3050);   // Yawn (boss)
        Slot(2, 1, 0, 0, 0, -1);          // a dead zombie
        Slot(3, 0, 2, 0, 0, 60);          // an inactive slot
        Slot(4, 1, 34, 900, 900, 0);      // Barry (NPC, HP 0)
        var session = new Re1Session(new ValidatedMemory(_memory, [_profile.ImageRange]), _profile, Re1Adapter.ActorInfo);

        var actors = session.Read(1, T0).Actors.Value;

        Assert.Equal([0, 1, 4], actors.Select(a => a.Slot));
        Assert.Equal(new LiveActor(0, 0, "Zombie", ActorKind.Enemy, 5300, 5800, 0, 63), actors[0]);
        Assert.Equal(ActorKind.Boss, actors[1].Kind);
        Assert.Equal(("Barry", ActorKind.Npc), (actors[2].Name, actors[2].Kind));
    }

    [Fact]
    public void Placed_items_are_reread_once_a_second_and_on_room_change()
    {
        NewChrisGameInMainHall();
        ActionTable(13000);
        _session.Read(1, T0);

        ActionTable(14000);
        Assert.Equal(13000, Assert.Single(_session.Read(2, T0.AddMilliseconds(500)).PlacedItems.Value).X);
        Assert.Equal(14000, Assert.Single(_session.Read(3, T0.AddMilliseconds(1100)).PlacedItems.Value).X);

        ActionTable(15000);
        _memory.WriteU8(Addr("room"), 0x05);
        Assert.Equal(15000, Assert.Single(_session.Read(4, T0.AddMilliseconds(1200)).PlacedItems.Value).X);
    }

    [Fact]
    public void Reads_and_normalizes_a_live_like_game_state()
    {
        NewChrisGameInMainHall();

        var s = _session.Read(1, T0);

        Assert.Equal(Field<bool>.Valid(true), s.InGameplay);
        Assert.Equal(new RoomLocation(1, 0x06), s.Location.Value);
        Assert.Equal("106", s.Location.Value.Id);
        Assert.Equal(1, s.Camera.Value);
        Assert.Equal(new Health(140, 140), s.Health.Value);
        Assert.Equal(PlayableCharacter.Chris, s.Character.Value);
        Assert.Equal(new Position(0x3908, 0, 0x1388), s.Position.Value);
        Assert.Equal(TimeSpan.FromSeconds(0x142 / 30.0), s.GameTime.Value);
        Assert.All(new[] { s.Location.Status, s.Camera.Status, s.Health.Status, s.Character.Status, s.GameTime.Status }, st => Assert.Equal(FieldStatus.Valid, st));
    }

    [Fact]
    public void Live_jill_game_start_is_read_correctly()
    {
        // Raw values observed live at the start of a Jill game (2026-10-12).
        _memory.WriteU8(Addr("stage"), 0)
            .WriteU8(Addr("room"), 0x06)
            .WriteU8(Addr("camera"), 0)
            .WriteI16(Addr("hp"), 0x60)
            .WriteU8(Addr("maxHp"), 0x60)
            .WriteU8(Addr("character"), 0x01)
            .WriteU32(Addr("gameTimer"), 0)
            .WriteU32(Addr("stateFlags"), 0x4080000C)
            .WriteI32(Addr("positionX"), 0x4844)
            .WriteI32(Addr("positionZ"), 0x9C4)
            .WriteU8(Addr("mainMenu"), 0);

        var s = _session.Read(1, T0);

        Assert.True(s.InGameplay.Value);
        Assert.Equal(PlayableCharacter.Jill, s.Character.Value);
        Assert.Equal(new Health(96, 96), s.Health.Value);
        Assert.Equal("106", s.Location.Value.Id);
        Assert.Equal(new Position(18500, 0, 2500), s.Position.Value);
        Assert.NotEqual(0, s.RawFields["stateFlags"].Value & 0x00800000); // Jill RDT variant bit
    }

    [Theory]
    // The status screen's band, read live for Jill (max 96) on 2026-10-14: 1–24 Danger, 25–48 Caution, 49–96 Fine.
    [InlineData(1, 0, HealthCondition.Danger)]
    [InlineData(24, 0, HealthCondition.Danger)]
    [InlineData(25, 0, HealthCondition.Caution)]
    [InlineData(48, 0, HealthCondition.Caution)]
    [InlineData(49, 0, HealthCondition.Fine)]
    [InlineData(96, 0, HealthCondition.Fine)]
    [InlineData(96, 0x02, HealthCondition.Poison)] // wasp stings set status bit 0x02
    [InlineData(30, 0x10, HealthCondition.Caution)] // other status bits aren't poison
    public void Condition_matches_the_status_screen(short hp, byte status, HealthCondition expected)
    {
        NewChrisGameInMainHall();
        _memory.WriteI16(Addr("hp"), hp).WriteU8(Addr("maxHp"), 96).WriteU8(Addr("playerStatus"), status);
        Assert.Equal(expected, _session.Read(1, T0).Condition.Value);
    }

    [Theory]
    [InlineData(0x00, PlayableCharacter.Chris)]
    [InlineData(0x01, PlayableCharacter.Jill)]
    [InlineData(0x03, PlayableCharacter.Rebecca)]
    [InlineData(0x09, PlayableCharacter.Jill)] // alternate costume bit is masked off
    [InlineData(0x02, PlayableCharacter.Unknown)]
    public void Character_id_is_masked_and_mapped(byte raw, PlayableCharacter expected)
    {
        NewChrisGameInMainHall();
        _memory.WriteU8(Addr("character"), raw);
        Assert.Equal(expected, _session.Read(1, T0).Character.Value);
    }

    [Fact]
    public void Title_screen_marks_player_fields_unavailable()
    {
        NewChrisGameInMainHall();
        _memory.WriteU8(Addr("mainMenu"), 1);

        var s = _session.Read(1, T0);

        Assert.Equal(Field<bool>.Valid(false), s.InGameplay);
        Assert.Equal(FieldStatus.Unavailable, s.Health.Status);
        Assert.Equal(FieldStatus.Unavailable, s.Location.Status);
        Assert.Equal(FieldStatus.Unavailable, s.GameTime.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-10)] // observed live: the final bite overshoots to -10 and stays there through the next New Game intro
    public void Dead_player_is_not_in_game(short hp)
    {
        NewChrisGameInMainHall();
        _memory.WriteI16(Addr("hp"), hp).WriteU8(Addr("room"), 0x1B);

        var s = _session.Read(1, T0);
        Assert.Equal(Field<bool>.Valid(false), s.InGameplay);
        Assert.Equal(FieldStatus.Unavailable, s.Location.Status);
    }

    [Fact]
    public void Fresh_boot_with_zero_max_hp_is_not_in_game()
    {
        // Observed live: every field is zero before the first New Game.
        var s = _session.Read(1, T0);
        Assert.Equal(Field<bool>.Valid(false), s.InGameplay);
        Assert.Equal(FieldStatus.Unavailable, s.Location.Status);
    }

    [Fact]
    public void State_flag_0x2000000_does_not_affect_session_state()
    {
        NewChrisGameInMainHall();
        _memory.WriteU32(Addr("stateFlags"), 0x80000000); // free movement in room 105 had no 0x2000000 bit
        Assert.True(_session.Read(1, T0).InGameplay.Value);
    }

    [Fact]
    public void Out_of_range_stage_is_rejected_not_displayed()
    {
        NewChrisGameInMainHall();
        _memory.WriteU8(Addr("stage"), 9);
        Assert.Equal(FieldStatus.Unavailable, _session.Read(1, T0).Location.Status);
    }

    [Fact]
    public void Failed_reads_turn_previous_values_stale_then_recover()
    {
        NewChrisGameInMainHall();
        Assert.True(_session.Read(1, T0).Camera.IsValid);

        _memory.FailingAddresses.Add(Addr("camera"));
        var failed = _session.Read(2, T0);
        Assert.Equal(FieldStatus.Stale, failed.Camera.Status);
        Assert.Equal(1, failed.Camera.Value);
        Assert.True(failed.Location.IsValid); // other fields unaffected

        _memory.FailingAddresses.Clear();
        _memory.WriteU8(Addr("camera"), 4);
        Assert.Equal(Field<int>.Valid(4), _session.Read(3, T0).Camera);
    }

    [Fact]
    public void Session_gate_read_failure_makes_dependent_fields_stale()
    {
        NewChrisGameInMainHall();
        _session.Read(1, T0);
        _memory.FailingAddresses.Add(Addr("hp")); // hp, maxHp and mainMenu all gate the session

        var s = _session.Read(2, T0);
        Assert.Equal(FieldStatus.Stale, s.InGameplay.Status);
        Assert.Equal(FieldStatus.Stale, s.Location.Status);
    }

    [Fact]
    public void Unverified_fields_are_read_for_diagnostics_only()
    {
        NewChrisGameInMainHall();
        _memory.WriteI16(Addr("yaw"), 0x800);
        var s = _session.Read(1, T0);
        Assert.Equal(0x800, s.RawFields["yaw"].Value);
        Assert.Equal(FieldStatus.Valid, s.RawFields["enemyCount"].Status);
    }

    [Fact]
    public void Reads_stay_inside_the_verified_image_including_capture_regions()
    {
        var reads = new List<(ulong Address, int Length)>();
        NewChrisGameInMainHall();

        // A spy under the validation layer sees every request that reaches the process.
        var validated = new ValidatedMemory(new SpyMemory(_memory, reads), [_profile.ImageRange]);
        var direct = new Re1Session(validated, _profile) { CaptureRegions = true };
        direct.Read(1, T0);

        Assert.NotEmpty(reads);
        Assert.All(reads, r => Assert.True(_profile.ImageRange.Contains(r.Address, r.Length), $"0x{r.Address:X}+{r.Length}"));
    }

    [Fact]
    public void Fields_are_read_in_a_few_block_reads_when_memory_is_mapped()
    {
        // Real process memory is fully mapped; make the fake contiguous over the field clusters.
        foreach (var (start, end) in new (ulong, ulong)[] { (0x4BCB2C, 0x4BCB30), (0xAC4030, 0xAC4050), (0xBE41C0, 0xBE41F0), (0xBE62E0, 0xBE9960), (0xD22730, 0xD22780) })
        {
            _memory.Write(start, new byte[end - start]);
        }

        NewChrisGameInMainHall();
        _memory.WriteI16(Addr("inv1"), 0x0F02); // Beretta, 15 rounds
        var session = new Re1Session(new ValidatedMemory(_memory, [_profile.ImageRange]), _profile);
        var s = session.Read(1, T0);

        // A few block reads for the fields, plus - once a second - the room's placed items (the action table; the fake
        // has no entries, so no record reads), the inventory (pointer, held count, slots) and the item box, and one
        // block for the enemy/NPC slots. Two of the field blocks are keep-running's: the window's focus flag and the
        // keyboard map, each far from the other fields.
        Assert.InRange(session.LastReadCalls, 1, 12);
        Assert.Equal(new Health(140, 140), s.Health.Value);
        Assert.Equal("106", s.Location.Value.Id);
        Assert.Equal(0x0F02, s.RawFields["inv1"].Value);
    }

    [Fact]
    public void Capture_regions_are_copied_only_when_requested()
    {
        foreach (var region in _profile.CaptureRegions)
        {
            _memory.Write(region.Address, new byte[region.Length]);
        }

        NewChrisGameInMainHall();
        var session = new Re1Session(new ValidatedMemory(_memory, [_profile.ImageRange]), _profile);
        Assert.Empty(session.Read(1, T0).Regions);

        session.CaptureRegions = true;
        var regions = session.Read(2, T0).Regions;
        Assert.Equal(_profile.CaptureRegions.Select(r => r.Name), regions.Select(r => r.Name));
        var game = regions.Single(r => r.Name == "gameState");
        Assert.Equal(140, BitConverter.ToInt16(game.Data, (int)(0xBE636C - game.Address))); // HP inside the block
    }

    private sealed class SpyMemory(FakeMemory inner, List<(ulong, int)> reads) : IProcessMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            reads.Add((address, destination.Length));
            return inner.TryRead(address, destination);
        }
    }

    [Fact]
    public void Attach_rejects_patched_code()
    {
        var memory = new FakeMemory().WithVerifiedImage(_profile);
        memory.Write(_profile.Anchors[0].Address, [0x90, 0x90, 0x90, 0x90, 0x90]);
        var process = new FakeAttachedProcess(2, memory, new ModuleImage(0x400000, 0x49B2000, null));
        var ex = Assert.Throws<AttachRejectedException>(() => _adapter.Attach(process, _profile));
        Assert.Contains(_profile.Anchors[0].Name, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void User_profiles_override_built_in_profiles_by_id()
    {
        var custom = _profile with { DisplayName = "Custom" };
        var other = _profile with { Id = "other-game", GameId = "re2" };
        var adapter = new Re1Adapter([custom, other]);
        Assert.Equal("Custom", Assert.Single(adapter.Profiles, p => p.Id == _profile.Id).DisplayName);
        Assert.DoesNotContain(adapter.Profiles, p => p.Id == "other-game");
    }
}
