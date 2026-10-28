# RE1 Memory Map — GOG `ResidentEvil.exe` (USA, 1.0 hotfix4)

Build: 1,004,112 bytes, SHA-256 `205BD6FED6E080A38823958E5C3306CACC86A18FF9A2758179E8D33CAEEE4A78`,
x86, PE timestamp `0x33FD5643` (1997-08-22), image base `0x400000`, SizeOfImage `0x49B2000`,
DllCharacteristics `0` (no ASLR). All addresses are absolute virtual addresses for **this build only**.

The machine-readable source of truth is `data/games/re1/profiles/re1-gog-usa-hotfix4.json`.

Verification: **runtime** = observed behaving as described in the live game (2026-10-12 sessions) and
confirmed statically · **static** = confirmed by disassembling this executable · **unverified** = lead
from a third-party source, diagnostics only.

## Globals used by Lockpick

| Address | Type | Name | Meaning | Verification |
|---|---|---|---|---|
| `0xBE9820` | u8 | stage | Stage, 0-based (0 = STAGE1) | runtime |
| `0xBE9821` | u8 | room | Room within stage (two hex digits of `ROOMsRRp.RDT`) | runtime |
| `0xBE9822` | u8 | camera | Current camera (cut) | runtime |
| `0xBE636C` | i16 | hp | Player HP (player entity +0x88); goes negative at death | runtime |
| `0xBE6459` | u8 | maxHp | Player max HP (entity +0x175); 0 until a game starts; Chris 140, Jill 96 | runtime |
| `0xBE63C0` | u8 | playerStatus | Player status flags (entity +0xDC): `0x02` poisoned | runtime (2026-10-14 lab; static test/and/or byte refs) |
| `0xBE64EC + 0x18C·i` | i16 | enemy0..7Hp | Enemy slot HP (+0x88): below 0 once dead (zombies −1) | runtime (static: `sub`/`cmp word [0xBE64EC]`; 2026-10-14 lab) |
| `0xBE62E5` | u8 & 3 | character | 0 Chris, 1 Jill, 3 Rebecca (entity +0x01; bit 0x08 = alt costume) | runtime (Chris, Jill; Rebecca live 2026-10-15) |
| `0xD22730` | u32 | gameTimer | In-game time, frames at 30/s; runs in the status screen, the item box, messages and documents; stops in door transitions, the options screen and at a death (B206, 2026-10-17) | runtime |
| `0xD22777` | u8 | mainMenu | 1 while the title/main menu is shown | runtime |
| `0xBE41C0` | u32 | stateFlags | Main state bitfield (diagnostics only, see below) | static |
| `0xBE6318` | i32 | positionX | Player X (entity +0x34) | runtime |
| `0xBE631C` | i32 | positionY | Player Y (entity +0x38) | static (0 on flat floors) |
| `0xBE6320` | i32 | positionZ | Player Z (entity +0x3C) | runtime |
| `0xBE6358` | i16 | yaw | Facing (entity +0x74), units unconfirmed | unverified |
| `0xBE9944` + 2·i | u16 | inv0..inv7 | Inventory slot: low byte item id, high byte quantity (weapons: loaded rounds). 6 slots (Chris/Rebecca), 8 (Jill); compacts when an item is used | runtime |
| `0xBE9849` | u8 | equippedSlot | Equipped slot, 1-based (0 = none) | runtime |
| `0xD22768` | u32 | inventoryPtr | Active inventory (`0xBE9944`; `0xBE9950` for Rebecca) | runtime (Jill; Rebecca live 2026-10-15) |
| `0xBE98E4` + 2·i | u16 | box0..box3 | Item box entries: low byte id, high byte quantity (48 entries total; first 4 read) | runtime |
| `0xBE41E2` | u8 | enemyCount | Active enemy count | unverified (changed 2→0→2 across rooms 106/105/104 live) |

### Code anchors (checked in the live process before reading)

| Address | Bytes | Instruction | Purpose |
|---|---|---|---|
| `0x477DA4` | `A0 20 98 BE 00` | `mov al,[0xBE9820]` | room loader reads stage |
| `0x477DBB` | `A0 21 98 BE 00` | `mov al,[0xBE9821]` | room loader reads room |
| `0x462A4C` | `A0 22 98 BE 00` | `mov al,[0xBE9822]` | background loader reads camera |
| `0x4810D1` | `FF 05 30 27 D2 00` | `inc dword [0xD22730]` | frame loop increments timer |
| `0x40615E` | `66 83 3D 6C 63 BE 00 00` | `cmp word [0xBE636C],0` | death check reads HP |
| `0x401285` | `66 0F B6 35 59 64 BE 00` | `movzx si,byte [0xBE6459]` | heal clamp reads max HP |
| `0x4817B1` | `88 15 E5 62 BE 00` | `mov [0xBE62E5],dl` | new game stores character |
| `0x480B53` | `81 0D C0 41 BE 00 00 00 00 02` | `or dword [0xBE41C0],0x2000000` | game start sets a state flag |
| `0x40582E` | `A1 18 63 BE 00` | `mov eax,[0xBE6318]` | player X read |

### Key static evidence

- `0x4C44F8` holds the template `./usa/stage /room    .rdt`. The room loader at `0x477D90` indexes the
  hex table `0123456789abcdef` (`0x4C44E0`) with `stage+1`, the high and low nibble of `room`, and writes a
  `0`/`1` suffix from `stateFlags & 0x00800000` (Jill's RDT variant). This confirms stage/room semantics
  and the 0-based stage.
- `.\usa\stageS\rcSRRC.pak` (`0x4C2078`) is filled at `0x462A06` from stage, room and **camera**
  (`0xBE9822`); the install has `RC1000..RC1005.pak` for room 00, cameras 0–5.
- The timer is zeroed on New Game (`0x48090A`), restored from the save copy `0xBE9844` on load
  (`0x430695`), and the results screen divides the saved copy by `0x1A5E0` (1 h at 30/s) and `0x708` (1 min).

### Item ids observed live (behavioral identification)

| Id | Behavior observed | Likely item |
|---|---|---|
| `0x01` | Starting item, slot 1, equipped as melee | Knife |
| `0x02` | Quantity drops by 1 per shot, refilled to 15 on reload | Beretta |
| `0x0B` | Consumed by Beretta reloads; item box starts with two stacks of 15 | Handgun bullets |
| `0x41` | Consumed while HP went 46 → 96 (full heal) | First aid spray |
| `0x03` | Quantity drops by 1 per shot (7 → 0), equipped as a weapon | Shotgun |
| `0x0C` | Loaded into the shotgun: a reload takes exactly the missing rounds (capacity 7) | **Shotgun shells** |
| `0x44` | Consumed while HP rose 54 → 86 (+32, Jill) | Green herb? (BioRand id 68) |
| `0x07` | Picked up with 6 rounds by Forest, −1 per shot, 201 damage per hit | **Bazooka, explosive rounds** (BioRand says acid: swapped) |
| `0x11` | Loaded into an empty `0x07` (×6), which became `0x08` ×6 — not shotgun shells | **Acid rounds** |
| `0x08` | `0x07` after loading `0x11` rounds; 95 damage per hit (the speedrun chart's acid grenade) | **Bazooka, acid rounds** |
| `0x47` | Made from two `0x44`; consumed while HP went 71 → 96 (max) | Mixed herb (green + green)? |
| `0x21` | Dropped by the statue pushed in 202, picked up in 105, placed in the lion head in 10D (scenario flag A0 `0x10000`) | **Blue gem** (owner-confirmed) |
| `0x29` | Received from the lion head in 10D for the blue gem | **Wind crest** (owner-confirmed) |
| `0x1F`, `0x23`, `0x26` | Picked up / stored in the item box (0x23 has quantity 0: a key item?) | unknown |
| `0x2D` | Received in the gallery (117) after a puzzle flag, used in 11A like the wind crest | A crest? |
| `0x05` | In the attract-mode demo inventory, quantity 5 | unknown |
| `0x0E` | In the attract-mode demo inventory, quantity 144 | unknown (ammo-like quantity) |
| `0x2F` | Chris picked up 3; each typewriter save used one (3 → 2, 5 → 4) | **Ink ribbon** (owner + capture) |

Names are not shown in the UI until an item table is verified (0.2/0.4).

Stage/room note: walking up the main-hall stairs goes to `stage=1, room=0x03` (room 203): stage 2 = mansion 2F.

## `stateFlags` (`0xBE41C0`) observed values

| Situation | Value(s) |
|---|---|
| Fresh boot / title | `0x00000000`, `0x40000000`, `0x80000000` |
| Free movement, room 106 | `0x8011000C`, `0x8013000C` |
| Free movement, room 105 | `0x80000000` |
| Menu open/close | `0x80400000 → 0x60400000 → 0x80008000 → 0x60008000 → 0x40008000 → 0x40000000` |
| Pushing an object | `0x40` set while pushing (statue in 202) |
| Examining / picking up | `0x800` while interacting, plus `0x8000` while the message shows; a pickup refused with a full inventory shows the same bits with no inventory change |
| Door transition | `0x8201000C → 0x6601000C → 0x46010000 → 0x46000000 → 0x42000000 → 0x82000000` |
| Death | `0x8100000C` (`0x01000000` set when HP reached −10) |
| Jill game, main hall | `0x4080000C` (`0x00800000` = Jill RDT variant) |
| Player in control (live 2026-10-14, dev harness: still unless pushed, moves when pushed) | `0x80800000` (Jill), `0x80800004` in 104 (its script leaves `0x4` set) |
| Opening cutscene, Jill (same run: moves on its own) | `0x8091000C`, `0x8093000C` |

The "free movement, room 106" row above was most likely recorded during the opening cutscene: in the 2026-10-14
run the character moved on its own while `0x00100000` (camera lock), `0x00010000` and `0x00020000` (voice) were set,
and control came back with all three clear. The dev harness treats any of `0x80 | 0xFF00 | 0x10000 | 0x20000 |
0x40000 | 0x100000 | 0x01000000 | 0x04000000 | 0x20000000` as "player not in control" (bit names from the
ecruells/resident-evil-pc-decomp reference; `0x4`/`0x8` are set by room scripts and are not a busy signal).

Bits with support: `0x00800000` Jill RDT variant (static + live), `0x01000000` player dead (static + live),
`0x04000000` door transition (live), `0x00008000` menu (live). `0x02000000` is **not** a gameplay flag.

## Capture regions (recorded verbatim by captures)

| Region | Address | Length | Contents |
|---|---|---|---|
| `gameState` | `0xBE41C0` | `0x587C` | State flags, player entity, 30 enemy slots, save card (stage/room/camera, inventory, item box, flag banks) |
| `system` | `0xD21380` | `0x1400` | Item-use flags, game timer, inventory pointer, main-menu flag and nearby menu state |

## Diagnostic fields (unverified — decoded from captures to verify them)

| Fields | Address | Meaning (decomp / Mod SDK layout) |
|---|---|---|
| `enemy0..7Flags/Type` | `0xBE6464 + 0x18C·i` (+0, +1) | Enemy slot: active bit, type id (the HP at +0x88 is verified: `enemy0..7Hp` above) |
| `flagScenarioB0..7` | `0xBE9854` (32 B) | Scenario flag bank 1 |
| `flagLocks0..1` | `0xBE9874` (8 B) | Door lock flags |
| `flagEnemies0..7` | `0xBE987C` (32 B) | Enemy flags |
| `flagRoomItems0..7` | `0xBE989C` (32 B) | Per-room item pickup flags |
| `flagScenarioA0..3` | `0xBE98C0` (16 B) | Scenario flag bank 0 |
| `flagRoom0..4` | `0xBE98D0` (20 B) | Room flags |
| `box4..47` | `0xBE98EC`… | Remaining item-box entries (layout verified on entries 1–4) |

The flag banks tile `0xBE9854`–`0xBE98E4` exactly and end where the item box starts — consistent with the layout.

### Observed in live session 4 (2026-10-13, Jill, memory log) — behavior only, no code anchors yet

- **Enemy slots** decode consistently: zombies type 0 and 17, crows type 5; HP drops 12 per Beretta round and 201 per
  bazooka hit, and a dead enemy's HP is set to −1 (crows −50). For about one frame after a room loads the slots still
  hold the previous room's values (types 34, 37, 38 appeared only then) — readers must let the room settle.
  Type 15 is the mansion plant: six of them in 10C (HP 1 each), slapping for 2 HP — the damage chart's value.
- **`flagEnemies`**: each of 13 zombie kills set exactly one new bit; killing crows set none.
- **`flagRoomItems`**: each pickup cleared exactly one bit as the item entered the inventory (1 = still in the world).
- **`flagRoom` bank** (the map's flags, flag *n* = bit 31 − *n* % 32 of word *n* / 32):
  - flags 0–123: rooms visited, one per room: flag = base[stage] + room. **Static:** the table is in the exe at
    `0x4D31E0` (bases `00 20 3F 52 64` = 0, 32, 63, 82, 100) followed at `0x4D31E8` by the rooms per stage
    (`20 1F 13 12 18` = 32, 31, 19, 18, 24 — 124 in all), read by the map code (`mov cl,[eax+0x4D31E0]` at
    `0x487755`, `cmp [eax+0x4D31E8],bl` at `0x48773B`). Confirmed live for stages 1–4; stages 6/7 (the mansion
    revisited) use 1/2. The map's own room numbers count: 107's back room is map room 11D (flag 29), the
    piano room's secret room 11E (30), the room behind the 2F bookcase 21D (61);
  - 124–129 maps taken: flag = map item id + 46 (124 = 1F map, item 78; 125 = 2F map, 79; 126 = garden map, 80);
  - 130+ documents taken: flag = document item id + 35 (130 researcher's will, item 95; 132 keeper's diary, 97;
    145 the book about mixing herbs, 110).
- **`flagLocks0`**: one bit set per door unlocked (4 doors).
- **Statue push (202)**, seen three times (session 4 and both new games in session 5): `stateFlags` bit `0x40` is set
  while pushing; when the statue falls, `flagScenarioA0` bit `0x100000` and `stateFlags` `0x100000` (camera cut) are set.
  It drops the blue gem in the dining room.
- **Stairs push (107)** to reach the map, by both characters: push bit `0x40` per push; taking the map cleared
  `flagRoomItems4` bit `0x4000` and set `flagRoom3` bit `0x8` (maps don't enter the inventory). No word in the
  recorded regions moves with a pushed object, so object positions live elsewhere.
- **Damage per hit** matches SHiiDO's RE1 damage chart (speedrun.com, JP PC new game) in every case seen: zombie
  bite 10, ankle bite 6 (also from a zombie that got back up with 1 HP), crow peck 6 then 3 per hit, mansion plant
  slap 2. The timeline names attacks from it.
- **Zombie getting up:** HP ≤ 0, kill flag set, then active again with HP 1 (204, both sessions).
- **Room 206** (Jill): the timer paused 4.5 s, `flagRoom4` bit `0x4000` set and `flagRoomItems7` bit `0x100` cleared, with
  no inventory change — probably reading a document (not identified).
- **Enemy types confirmed by behavior:** 0 and 17 zombies, 2 dogs (Cerberus), 5 crows. Dog kills set `flagEnemies` bits
  like zombie kills; a new game clears every flag bank.

## Not yet accounted for (sessions 4–5, 2026-10-13)

What changes during play but isn't explained yet — candidates for markers in future captures.

- **Scenario flags** (`flagScenarioA/B`): two ask-mode sessions (Jill, new game → the guardhouse save room) named
  about 70 story and puzzle events — Barry's scenes, the ceiling trap, the gallery, crests, the piano and emblems,
  Richard and the serum, Yawn, the aquarium, the water gate, the guardhouse statue — in
  `data/games/re1/flag-labels.json` with their source. Later areas are still unknown; ask-mode captures name them.
- **Room scripts set story flags:** `set` group 0 writes bank A (`flagScenarioA`), group 1 bank B (`flagScenarioB`)
  — about 35 confirmed labels match a `set` in the room where the player was. `tools/rdt` records them
  (`room-index.json` → `story`). Cutscene/event scripts (`offsets[8]`) set the rest and are not parsed yet.
- **`flagLocks`** also covers locked desks (flags 30, 31: desks opened with Jill's lockpick in 102 and 111).
- **Kill flag 255** (`flagEnemies7` bit `0x1`) is set when an enemy whose room script says "not persisted" dies.
- **Jill's lockpick** is given by Barry in the main hall (`flagScenarioA3` bit `0x8`) and never appears in the
  inventory or item box; it opens the Sword-key doors (107→108, 103→10E).
- **Unidentified memory** (changes during play): `0xBE41C6–0xBE41DC` (state block next to `stateFlags`; `0xBE41DC`
  toggles 0/2 at every room load), `0xBE5684–0xBE62DE` (arrays with a 0x84-byte stride — objects/effects, mostly
  updated at room loads; the pushed stairs/statue positions were not found in the recorded regions),
  `0xBE92CC–0xBE9318`, `0xBE9616` (counts 0→1→2→3→0 through each door transition), `0xBE9682–0xBE981F` (written
  once at new game), `0xBE9823–0xBE9852` (save-card fields after stage/room/camera, very active), `0xBE995C–0xBE9A3B`
  (224-byte table, single bytes change at a few moments — door/map state?), and the system block `0xD213A1–0xD21503`
  (arrays with a 0x24 stride).

## Save files (`SAVE\savedatN.dat`)

2690 bytes (`0xA82`), starting `53 43 13 01`. **Static (2026-10-13):** written by `0x493926`–`0x493A06`
(`fwrite 0xA82`), read by `0x493D04`; **no checksum** or transform (the loader only checks section sizes). Layout:
`0x000`–`0x7FF` = memory `0xBE9620`–`0xBE9E1F` verbatim; `0x800` key map (32 VK codes); `0x820` keyboard→pad table;
`0x8A0` SideWinder table; `0x920` copy of `0xBE995C` (0xE0 bytes); `0xA00`/`0xA01` two bytes; `0xA02` joystick
table. Inside the block: save count `0x228` (`0xBE9848`), character also at `0x22B` (`0xBE984B`). After a load the
game ORs `0xBE41C0 |= 0x10000000` (and `0x800000` for a non-Chris character). Save offset + `0xBE9620` = memory address (found 2026-10-13 by matching a save written during a live capture against the memory
recorded 48 ms earlier) — so stage/room/camera, every flag bank, the item box and the inventory decode with the
profile's addresses. The character is the low bit of `0xBE9829` (save offset `0x209`; field `saveCharacter`):
0 Chris, 1 Jill in all 75 saves checked; bit `0x4` is set only in the underground caves and the lab (the autosplitter's
"Jill's character ID changes in a few spots"). The byte is the decomp's `characterModelId`: room scripts set it (state byte 9, opcode `0x08`) and the next room's
init swaps the player to it. Chris's Plant 42 room (40C) event 0 sets 3, and on the next load the game makes the player
Rebecca: HP 88/88 (Chris's HP kept aside), the equipped weapon cleared, the inventory pointer `0xD22768` → `0xBE9950`.
Live 2026-10-15 (B007): the byte set to 3 in a Chris game, then a door, gave exactly that. The caves and lab scripts
(302/305/310) set 4/5, the bit-`0x4` saves. Byte `0x212` agrees (`0x2F`, the ink ribbon a save costs, for Chris). `tools/bounties/saves.py` reads saves offline (character, room, inventory, box, progress, story); it
decoded 67 community practice saves and the owner's 8 into the rooms their names describe.

## Focus and input (static analysis 2026-10-13; live-tested 2026-10-14 with the dev harness)

| Address | What | Evidence |
|---|---|---|
| `0x441170` | Window procedure (class registered at `0x441CBB`) | `C7 44 24 70 70 11 44 00` at `0x441C71` |
| `0x4BCB2C` (u32) | The only "focused" flag (decomp `g_bWindowFocused`); image value 1 | written only by `WM_ACTIVATE` at `0x44126B` (0) / `0x4412A0` (1); `WM_ACTIVATE` also pauses sounds (`0x480640`) |
| `0x441E9D`–`0x441EA3` | Main loop skips `main_loop` (`0x428EB0`: input, game, render) while the flag is 0 — the focus-loss pause | `39 1D 2C CB 4B 00 0F 84 C8 FE FF FF` |
| `0x497BA0` | `ReadPadBoth`: keyboard (+ joystick #0) → PSX button word; returns 0 if `[0x4D460C]` | ends `A1 18 40 AC 00 C3 CC×8` at `0x497BF2` |
| `0x4202F0` | Keyboard poll: `GetAsyncKeyState` over the 32-key map at `0xAC4030` (copied from `0x4D4730`); GOG edits left only map indices 0–3, 5, 10, 11, 13, 27–31 active | `GetAsyncKeyState` works unfocused |
| `0xBF0A04` / `0xBF0A08` (u32) | Raw held / newly-pressed PSX word per tick (`0x44E000`) | rewritten every tick; **live 2026-10-14:** `0xBF0A04` showed the bit of every virtual-controller input pressed, on the title screen and in gameplay |
| `0xBE9840` / `0xBE9842` (u16) | `key_on` / `key_trg` (remapped per control type `[0xBE9A5C]&3`) | rewritten every tick |

PSX button word: `0x1000` up, `0x4000` down, `0x8000` left, `0x2000` right, `0x80` action/confirm, `0x40`
cancel/run, `0x08` aim, `0x800` status, `0x900` options. Default keys (`0x4D4730`): arrows, V, X (aim), C, Z (status),
A (options), Ctrl, Enter, Space, Esc. The owner's machine has a remapped table (registry `Key Def` and every save's
`0x800`): A = up, Up arrow = down, Down arrow = options — why simulated arrow keys did nothing useful.

Controller (live 2026-10-14, virtual Xbox 360 pad, default joystick table): A action `0x80`, X cancel/run `0x40`,
Y status `0x800`, Back options `0x900`, RT aim `0x08`, d-pad and left stick movement; B, Start, LB, RB, LT and the
stick clicks unmapped. **The game finds controllers only at startup** — a pad plugged in later is ignored. The keyboard
map at `0xAC4030` is rewritten ~2 s after launch with the player's table (and presumably on save load).

**Keep-running in the app (#21, 2026-10-26) is data-only, no code patch:** while the game isn't the active window,
Lockpick holds `windowFocused` (`0x4BCB2C`) at 1 and blanks the keyboard map (`keyMap0`-`keyMap7`, `0xAC4030`, 32
bytes); the player's map goes back when the game is in front and whenever the feature ends (Trainer → Cheats; both are
profile fields with `anyTime` write bounds, so it works at the title too). The main loop polls messages with
`PeekMessageA` and never waits (0x441D71-0x441EA3), so the flag alone restarts it. **Live 2026-10-26 (GOG English,
owner):** in the background the flag read 0 and the timer stood still; after 1 was written the timer ran at 30 ticks a
second, the picture moving, no sound (WM_ACTIVATE pauses the game's sounds; DirectSound mutes unfocused buffers). With
the map intact the game read A typed in Notepad (input word `0x1000`, Jill moved); blanked, nothing. In the app the
switch held the flag within half a second of each focus loss, put the map back on each return and when switched off,
and A in Notepad did nothing. The Japanese builds have the fields at `0x4B3274` / `0x4CFD40` (cross-mapped, static).

**Controller and sound in the background (the app, 0.5.1):** two code patches in the profiles (`patches`:
`backgroundFocus`, `backgroundSound`), applied by keep-running when the player asks, only over the expected bytes, read
back and put back. `backgroundFocus` is the `WM_ACTIVATE` jump below (English `0x44126B`, Japanese `0x448795`): the
focus-loss branch also calls `0x480640`, which stops every playing looping sound (`0xAC9940` and the table
`0xAC99D0`-`0xAC99E8`; `0x4806B0` plays them again on activation), and sets `0x4C39CC` when `[0x4C39C8]` is 1.
`backgroundSound` adds `DSBCAPS_GLOBALFOCUS` (0x8000) to the only secondary-buffer flags, `0xE2` at English `0x41EBC1`
(`mov [esp+0x24],0xE2` at `0x41EBBD`, CreateSoundBuffer at `0x41EBCC`) / Japanese `0x48B6F1`. **Live 2026-10-28 (GOG
Japanese, owner's controller):** keep-running alone kept the game running but silent and deaf to the controller; with
both patches the controller worked and the sound played with another window in front. English: the jump was live with
the dev harness (2026-10-14); the sound patch waits for its own check.

Planned patches (Trainer Mode, opt-in, reversible, read back, never in Challenge Mode):
- **Keep running unfocused (the code-patch variant; not used, the app's is data-only above):** `0x44126B` `C7 05 2C CB 4B 00 00 00 00 00` → `E9 C6 00 00 00 90 90 90 90 90`
  (`WM_ACTIVATE` goes straight to `DefWindowProcA`; the flag stays 1, sounds aren't paused). Data-only alternative:
  write 1 to `0x4BCB2C` after each deactivation. **Live 2026-10-14 (dev harness `--background`):** with the patch,
  the flag set to 1 and the key map blanked, the game ran with another window in front, read every controller input
  and rendered normally (PrintWindow screenshots of gameplay); restoring the bytes on exit worked. Risks: keys typed in other windows reach the game
  (`GetAsyncKeyState`) — zero the key map at `0xAC4030` while unfocused or set `0x4D460C`; DirectSound mutes unfocused
  buffers (no `GLOBALFOCUS`, `0x41EBBD`); GOG's ddraw wrapper may still black out the window (unanalysed).
- **Input from Lockpick:** `0x497BF7` `C3 CC CC CC CC CC` → `0D 00 00 00 00 C3` (`or eax, imm32; ret`): the u32 at
  `0x497BF8` becomes a button mask OR-ed into every read — triggers, menus and movement follow naturally. Hold ≥ 2
  ticks, release ≥ 2 ticks.

## Room changes, game speed (static analysis + live, 2026-10-14 — dev harness only)

Used by the developer-only autoplay harness (`tools/Lockpick.Autoplay`), never by the app.

| Address | What | Evidence |
|---|---|---|
| `0x41B59C` | `door_begin_transition`: `[0xBEBCBC] = record`, `[0xBE41C0] \|= 0x02000000`, `[0xBEBCC0] = 0` (u16), `[0xBE961B] = 1` (u8), then a black rect | disassembly of this build; the decomp's port names `0xD22760` for the last byte, the original writes `0xBE961B` |
| `0x4810DE` | `game_loop` reads `[0xBE961B]`: 1 → room-change path (`room_transition_load` `0x4813C0`) | disassembly |
| door record (24 bytes) | the RDT `door_aot_set` opcode without its first two bytes: `+0x0A` door type, `+0x0B` entry camera (`&0x3F`), `+0x0C` lock, `+0x0D` destination (`< 0x20` same stage, else `(stage digit << 5) \| room`), `+0x0E` arrival x, y, z, angle (i16), `+0x16` key | live: handing a 106 → 105 record entered 105 with the door animation at (30900, 8300); the 2026-10-14 surveys entered 162 of 165 door targets per character from a new game (the 3 others are stub room files that crash the game) and every room of four later story phases from practice saves |
| scenario flag 0 (bank `0xBE98C0`) | while set, a destination in stage 1/2 loads the stage 6/7 version (`room_transition_load`) | decomp + live: a warp to 104 arrived in 604 after the flag was set during play |
| `0x441F0C`–`0x441F75` | main-loop limiter: when `[0x4D46D8]` != 0 and `[0x4BCB48]` == 1, `main_loop` runs once per `[0x4BCB54]` ms (33 = 30 ticks/s) | disassembly; live: `[0x4BCB54]` read 33 with mode 1, writing a smaller interval fast-forwards cutscenes (measured from a capture of the 2026-10-14 run: with 4 ms the game clock ran up to 8.2× real time in the opening — the full 33/4 — and 3–6× in rooms, presumably render-bound) |
| `0xBF0A04` | raw pad word — see "Focus and input" | live |

Item names (static, decoded 2026-10-14): `0x4BF0A0` is the item-name pointer table, entry *i* = item id *i* + 1
(index 0 = combat knife); strings use the game's text encoding (`0x00` space, `0x0C`–`0x15` digits, `0x1D`–`0x36`
A–Z, `0x3D`–`0x56` a–z, `0x79` `.`, `0x01`/`0x07` end). `data/games/re1/names.json` was checked against it.

After loading a save, the game ignores the **first** door handoff (the byte is consumed, no room change); the next
one works (live 2026-10-14: every phase survey's first targets failed until a second handoff, e.g. 600 took 2, 601
took 1). Cause not analysed — likely a first-frame state after the load path. The harness hands the door over again.

Event-script slots (lead): `0xBF084C`, 8 × `0x34` bytes, `+0x00` state, `+0x02` active — one slot stays active
during free movement, several during the opening cutscene.

Save files, leads (not yet live-checked): offset `0x220` (u16) holds the HP (Jill saves 96 = `0x60`, Chris 140),
`0x22C`/`0x22E` look like X/Z and `0x230` like the facing (always < 4096). A save resumes in its save room; rooms
are entered with door records instead.

## Interaction, menus and cameras (static + live, 2026-10-14 — dev harness)

| Address / rule | What | Evidence |
|---|---|---|
| `0xD91AA0` (288 B), tail ptr `0xD91BC0` | Room action table: 12-byte entries `[0]` handler (1 door, 2 message, 3 key prompt, 4 item pickup, 7 flag set, 9 event…), `[1]` probe flags (`0x80` action key only, `0x40` test own position), `+6` item flag, `+8` record pointer (zone x/z/w/d, item id at +8) | decomp; live: armed pickups listed per room match the room files and the pickups made |
| action probe | On A, the point **600 units ahead** of the player is tested against the zones (`check_action_object` `0x41C150`); facing 0 = +X, 1024 = −Z | live: Emblem picked up from 600 back facing in |
| item pickup | flag `0x800` (item viewer) + `0x8000` message; "Will you take …?" defaults to Yes; inventory slot gets `item \| qty << 8`, item flag cleared | live: 138 of 138 Jill pickups gave the item and cleared the flag the room index names |
| documents | pickup screen `0x100` (+ camera lock + ramp); some loop their pages on A — cancel (X) closes them | live: researcher's will (20A) |
| `0xBE9822` + state bit `0x20` | Camera id + "camera redraw": the next frame runs `display_room_camera_bg` (`0x462D50`) — a camera cut on demand; the game only tests the current camera's switch zones, so it never re-cuts after a teleport by itself; the camera must have a group in the room's switch table | decomp + live (105 table and fireplace cameras) |
| `0xBE41E2` (u8) | Enemy count; `update_entities` (`0x48F0F0`) returns when 0 → enemies stop and aren't drawn until the next room load | disassembly + live (101's zombies) |
| `0xD22774` / `0xD22775` / `0xD22777` | Title: cursor (1 NEW GAME, 2 LOAD GAME, preset to 2 when saves exist), menu shown, title loop | live trace |
| `0xAC9880` / `0xAC93F0` | Character select: state (1 = waiting for input, 6 = start), selection (0 Chris, 1 Jill) | decomp + live (new games for both) |
| `0xAE9F20` / `0xAE9F23` / `0xAE9F1B` | Inventory menu: grid takes input when 1 (submenu 2); cursor (8 + 2·slot); selected item id — follows the cursor a few frames late, 0 on a slot at or past the held count | decomp + live (USE and CHECK) |
| `0xAE9F24` | Item submenu cursor: 0 USE, 1 CHECK, 2 COMBN | live diff |
| `0xBE41C0` bit `0x1000` | Item box screen mode: set while the player is free and the game opens the item box screen by itself, in any room (the box lid animation sets it the same way); cleared when the screen closes. On that screen `0xAE9F20` is 1 on the inventory side, 2 on the box side; `0xAE9F24` is the box entry under the cursor (0 when it opens); A on a box entry swaps it with the chosen slot and the game loads the slot's icon (decomp game_loop / MainMenu item box) | decomp + live 2026-10-14 (Jill, Chris) |
| `0xD21CD0` / `0xD22734` | Grid icon row per inventory slot / bitmask of rows in use: an icon is loaded only on pickup, game load, combine or an item box swap, so writing a slot directly keeps the old picture | decomp + live |
| `0xBE9827` (u8) | Held-item count (`g_TotalHeldItems`): the game ignores slots at or past it — the menu shows them empty and doors don't see a key there; recounted from the slots only on game start/load (Jill 8 slots max, Chris 6) | decomp + live: a Shield key written to slot 3 with the count left at 3 was invisible ("It's locked.") |
| `0xD21CD0` (8 B) / `0xD22734` | Per-slot icon index / icon bitmask: the grid icon comes from the slot's index; icons load on pickup, game start/load and item-box/combine swaps — not on room changes | decomp + live (an item added by memory shows the knife icon until then) |
| `0xBE9825` (u8) | `g_menu_choice_id`: bit `0x80` = a message is up; bit 0 = yes/no choice (0 Yes, 1 No) | decomp + live ("It's locked." reads 0x80; the Discard prompt read 0, answered Yes) |
| `0xBF0A16` (u8) | Message state: 1 typing, 2 waiting on a page (▼), 3 after a line break, 4 yes/no prompt, 5 waiting to close, 6 closing by itself. **The state flags don't show messages**: "It's locked." leaves `0xBE41C0` at plain control `0x80800000` | decomp + live |
| `0xBF0A1C` / `0xBF0A20` | Message text: start pointer / revealed-so-far pointer (into the exe's text tables or the room file); text encoding in the decomp's docs/TEXT_ENCODING.md (`0x01` end, `0x02`/`0x03` page/line, `0x05 n` colour, `0x06 n` item name, `0x08` yes/no) | decomp + live ("Nothing important.", "A carving of a shield.", "This key is useless now. Discard?") |
| `0xBE98BC` (4 B, MSB-first) | Items examined: keys, cranks, the herbicide and the books show a generic name ("MANSION KEY", "CRANK", "CHEMICAL", "RED BOOK"…) until examined; flag index = byte at `0x4BD81D + id·4 + 2` (none when bit 7 set), generic names at `0x4BF260[index]` | decomp + live: CHECK, turn the Shield key until "A carving of a shield." → bit 5 set, name SHIELD KEY |
| `0xD213A0` | Item-use flags (bank 9): a room's "flag set" action raises the bit (item − 0x1B) while the player stands at the spot; USE checks it | decomp + room scripts (105 fireplace: items 31/32) |

## Boot and new game (static + live, 2026-10-14 — dev harness only)

The game runs its screens as cooperative tasks (decomp TaskScheduler). A screen hands over with `Task_chain`
(`0x420230`): the next entry goes to `g_TasksEIP[id]` and the task's state to START; the scheduler enters it fresh.

| Address | Meaning | Evidence |
|---|---|---|
| `0xD91A80` (3 × u32) / `0xD91A70` | Task entry points / saved stacks; task 0 is the main one (`0x480710` `game_start` in a game) | live read in a game; code bytes at `0x480710` match the decomp |
| `0xD1FDE4` (3 × 0x7C) | Task blocks: i16 state at +0 (0 dead, 1 sleeping, 2 START), i16 sleep counter at +2 | decomp + live |
| `0xD91A7C` (u32) | Task being run by the scheduler (3 = none: between passes) | live |
| `0x442BB0` / `0x430470` / `0x492340` / `0x420060` / `0x480710` | `logos_state` / `title_state` / character select / intro (sounds_reset, intro movie, then `game_start`) / `game_start` | disassembly of this build |
| `0xBE984B` (u8) | Character for a new game (0 Chris, 1 Jill), written by the character card; `InitializeGame` copies it to the player id, then overwrites it from `bio_card.dat` | decomp + live |
| `0xD22777` (u8) | Title menu shown: the title sets it on entry (`0x4304E8`), its exit clears it (`0x430E10`) | disassembly + live |
| `0xD1FDC4` | Room-music table pointer, set to `0x4D0C30` by `sounds_reset` (`0x47EB70` → `0x47EA10`); null until the title's start runs it, and `update_room_bgm` (`0x47F6B7`) crashes on null | disassembly + two live crashes |

The title's start runs `sounds_reset`, loads sound bank 12, sets up the screen and sleeps one frame (`0x43056C`). When
the title ends with no input the game chains from it straight to `game_start` (the attract demo). The dev harness
(`NewGame.cs`) does the same once task 0 is in `title_state` and asleep, between scheduler passes, with the game
suspended: character byte, the Jill room-variant bit `0x800000`, title-menu byte 0, `game_start`, START. With the
attract bit clear, `InitializeGame` builds a real new game (HP, starting items, clock 0) and the opening plays by
itself (voiced lines close on their own) until control in the dining room (105). Handing over at the title's START,
before its setup, crashed in `update_room_bgm` both times.

`InitializeGame` builds the new game (`0x481880` sets the spawn spot: i16 x `0xBE6350` = 17000, z `0xBE6354` = 5000,
facing `0xBE6358` = 0xC00), sleeps one frame (call `0x480A44`) and then loads the first room (`0x480AB6`), which
places the player from the spawn spot. A sleeping task's saved stack (`0xD91A70[id]`) holds the return address 40
bytes up (`pushal`, `pushfd`, `call` in the yield at `0x47577C`), so `0x480A49` there marks that frame. Writing the
opening's end there (its saved-state bits, room `0xBE9821`, camera `0xBE9822`, spawn spot) loads straight into the
dining room with the opening never started. Room files then load a chunk per frame (`0x411FDE`, ~7 s at 30 ticks/s);
the clock `0xD22730` counts only in the game loop (`0x4810D1`).

| Scene | Changes in the saved block (besides room/camera) | Control at |
|---|---|---|
| Opening, Jill | `0xBE9834` word `0xEFFF`, bank B bits at `0xBE9856`/`57`/`6A`/`6B`, `0xBE98D3` `\|= 0x06`, `0xBE996B` `\|= 0x01` | 105, (30700, 7200), facing 1900, camera 0 |
| Opening, Chris | 6 bytes (`tools/autoplay/scenes/opening-chris.json`) | 105, (30900, 8300), facing 2048, camera 0 |

The game loop takes no room change while a message is up (`0x4810F7` tests `0xBE9825 & 0x80`), which is why a door
handed over during a scene waits; stopping a scene = event slots `0xBF084C` (8 × 0x34; state +0, active +2) idle,
message closed, state-flag bits `0x100000` (camera lock), `0x20000` (voice), `0x10000` (intensity ramp) cleared.

## Player status, healing, combat (decomp facts checked live, 2026-10-14 — dev test suite)

| Address / rule | What | Evidence |
|---|---|---|
| `0xD22730` (u32) | In-game timer, frames at 30 fps (counts in menus too; settled by B206 2026-10-17: runs in the status screen, item box, item viewer and messages, stops in door transitions and the options screen) | live: 153 frames in 5.07 s |
| `0xAE9F2F` (u8) | Status-screen ECG band, only updated while the screen is drawn: `(HP − 1) / (maxHP >> 2)` → 0 Danger, 1 Caution, 2–3 Fine; 4 while poisoned | live: Jill at HP 1, 24, 25, 48, 49, 72, 73, 96 gave 0,0,1,1,2,2,3,3; poisoned 4. The screen's word, seen 2026-10-15: HP 60 (band 2) "Fine" with a yellow trace, HP 30 (band 1) "Caution" in orange |
| `0xBE63C0` (u8, player +0xDC; profile field `playerStatus`) | Status: `0x02` poisoned (`0x20` Yawn poison per the decomp; `0x10`/`0x80` are other bits) | live: wasp stings (408) set `0x02`; the blue herb cleared it; the serum said "You don't need to use this item at the moment." |
| `0xBE6458` (u8, player +0x174) | Poison timer: counts down per frame; at 0 it reloads to ~120 and HP drops 2 | live: −2 HP at frames 7988, 8108, 8230 |
| healing | Green herb +⅓ max HP, green+green and green+green+blue +⅔, three greens / green+red / green+red+blue / first aid spray full, green+blue +⅓ and cures; red or blue alone: "You don't need to use this item at the moment." | live (Jill, from HP 10): +32, +64, +86 (full) … |
| herb mixing | G+G 0x47, R+G 0x46, G+B 0x48, GG+G 0x4A, GG+B 0x4B, GR+B 0x49, GB+R 0x49, GB+G 0x4B; three greens + green: "Mixing these does not seem to work." — each asks "Will you mix the herbs?" | live, all 8 + the refusal |
| full inventory | `0xBE9827` at the slot limit (Jill 8, Chris 6): "You can't carry any more items." and the item stays; ammo into a matching stack is still taken | live (Jill): the Emblem refused; a clip taken into the clip stack (flag 50 cleared) |
| `0x4BB648` | Weapon hit size, u32 per weapon (id − 1), Chris row then Jill row: knife 400/500, Beretta 1200/1300, shotgun 2600, magnum 800, flamethrower 400, bazooka 900, rocket 1100; the enemy's collision radius is added. Knife, flamethrower, bazooka and rocket: a radius around the knife or the projectile. **Guns: the half-width of the aim cone's far end, not a reach** — a gun shot has no distance limit | exe + decomp (`weapon_hit_detect_gun` 0x43D410 / `_projectile` 0x43D810) + live: bazooka fired only inside 900; the weapon lab (2026-10-15) hit zombies with the shotgun at 9500 and the Python at 5000 |
| `0x4BB698` | Weapon damage, first run: 12-byte records, i16 at +6, index (weapon − 1) + enemy type × 10. The damage is fixed per weapon and type (no random roll, no distance falloff except the shotgun's). On top: **Jill's handgun +3 on zombies, +7 on dogs** (none on spiders, Black Tiger, Hunters, Chimeras); the shotgun gives 10 back beyond 9000 units; a shotgun or magnum hit at close range kills a zombie outright (HP → −300) whatever its HP | live, one-room trap (a 20E zombie standing in for each type, 2026-10-14): zombie, naked zombie, dog, spider and Black Tiger — every weapon equal to the table plus the rules above (knife 8, flamethrower 20, bazooka 201/95/95 on zombies; 60/60/205 on Black Tiger; rocket 900; Beretta 12 = 9 + 3, 27 = 20 + 7); Hunter 14, Chimera 20, crow 26 (Beretta) |
| enemy `+0x00` & `0xE0` | Aim heights a shot can take the enemy at (must overlap the player's aim): idle zombies 0x41, once they notice the player 0xF1 — so an enemy that hasn't noticed the player can't be shot at most heights | live (202, 20E) |
| enemy `+0x8A` | Hit state: a shot only takes an enemy whose hit state is 0; its AI clears it after reacting | decomp + live |
| `0xBE41E2` = 0 (frozen) | Shots don't register either: the weapon's hit check loops over the counted enemies | live |
| deaths | A killing hit leaves HP = before − damage, except zombie head kills (−300) and rocket kills of zombies (−1) | live |
| `0xBE9849` | Equipped slot, 1-based (0 = none) — not the item id the decomp calls it | live (B114) + harness |
| `0xBE9840` / `0xBE9842` | The game's held / newly pressed input word: forward `0x11`, back `0x24`, turn `0x02`/`0x08`, run `0x8200`, aim `0x0500` (RT), action `0x00C0` (A) | 5.6 h of the owner's captures + live (holding RT reads `0x0500`) |
| movement | Walk 1,207 units/s, run 5,461, turn 2,549 facing units/s standing (about half while running) | the owner's captures |

Keys: a door that takes a key opens only once the key has been **examined** — holding an unexamined Shield key gave
"It's locked. / A carving of a shield."; examined, the door unlocked (lock bit 7 in `flagLocks0`), the game asked
"This key is useless now. Discard?" in the status screen, and the next press went through (20E → 210, live
2026-10-14).

Room scripts (`tools/rdt/scd.py`) and event scripts (`tools/rdt/events.py`) read every room's conditions: e.g. 105
places the blue jewel only once "the statue falls" (A11), always arms the shield key (only the clock — a
script-placed object — stands in front), and using the gold emblem at the fireplace sets B171 and starts the clock
events; 106 runs its "go back" scene (event 4) after the opening and before the Kenneth scene.

## Enemies in the weapon lab (live, 2026-10-14/15 — dev harness only)

`tools/autoplay/lab.py`: a fresh boot to room 108 (the L-corridor, camera 2). Each normal enemy type is added to the
room's own script and the room reloaded (`spawn.py`), then held still, pinned, or let loose in front of Jill. Jill
only. Bosses aren't covered. Raw results: `%LOCALAPPDATA%\Lockpick\research\lab-20261014\final-*.json`.

| Address / rule | What | Evidence |
|---|---|---|
| enemy `+0x88` at spawn | Starting HP, rolled by each type's init: zombie 26–98 (`0x4BB288` base 39/59/79/99 minus `rand % 22`), naked 24–71, researcher 30–78, dog 60–122, spider 99/119, crow 18–38, Hunter 79/95, wasp 30–56 (big wasp, behaviour bit 2: 50–72), Chimera 88–110, snake 20–40; mansion plant 1 (its HP is never used) | 16 spawns per type (wasps: 16 each, normal `0x20` and big `0x22`) |
| enemy `+0x00` & `0xE0` | The aim heights it can be hit at, once it has noticed the player (`0x80` up, `0x40` level, `0x20` down). Zombies, dogs, spider, Hunter, Chimera `0x61`: level and down hit, up misses; crow `0x81` up only; wasp `0x41` level only; snake `0x21` down only. Jill's aim byte (player `+0x176`, `0xBE645A`) is `0x40` level, `0x80` up, `0x20` down | handgun shot at each height per type |
| enemy `+0x163` | Kill flag (`death_event_id`): the `flagEnemies` bit the enemy sets when it dies. Flag *n* is bit `31 − n % 32` of the little-endian word `n / 32`. An enemy whose flag is set is not placed again. `+0x161` = slot \| `0x80` | flags 17 and 250: set on the kill, the enemy not placed again |
| damage to the player | Zombies 10 (a grab can take 20), dog 12, spider 10, Hunter swipes 10 and 15, Chimera 15, crow 6 then 3, wasp 4 (big 8), snake 6 plus poison, plant 2 per tick of its hold (all first-run values; the decomp's second-run values are higher) | Jill and Chris, 6 hits each |
| who can kill the player | Zombies, dog, Hunter, Chimera, crow and snake kill outright (HP below 0). Below 13 HP (17 on a second run) the dog's finisher sets HP to −1 when the player isn't facing it. The spider's hits clamp HP at 1 and it never kills (3 minutes at 1 HP). A normal wasp's sting clamps at 1 (HP 0 is still alive); a big wasp's 8-HP sting kills (24 → 16 → 8 → 0 → −1, 3 of 3). Poison ticks 2 HP down to 0, then holds at 1, so it never kills | deaths and linger runs + decomp |
| handgun, shotgun, Python | Damage per hit is fixed (table plus Jill's handgun bonus): handgun 12 on zombies, 27 dogs, 20 spider and snake, 26 crow, 14 Hunter, 30 wasp, 20 Chimera. Shotgun 53 zombies, 40 dog, spider and snake, 50 crow, 32 Hunter, 60 wasp, 30 Chimera. Python: aimed level it head-kills zombies (HP → −300) from 2500 to 5000 units; 130 on everything else | reactions + special runs |
| shotgun rules | 10 less damage far away: 53 at 8142 units centre to centre, 43 at 8639 (the code compares its own distance measure with 9000). Aimed up it head-kills a zombie (HP → −300) from 1674 to 2506 units centre to centre and misses from 2779 out; aimed level or down it never head-kills | special + falloff runs, player standing still (inside ~1500 a zombie grabs the player before the shot) |
| heavy weapons | Flamethrower 20 a tick (snake 30). Grenade launcher explosive 201 zombies, 200 dog, 100 spider, 200 crow, 100 Hunter, 200 wasp, Chimera and snake; acid 95 zombies, 100 dog and spider, 60 crow, 200 Hunter, 80 wasp, 60 Chimera and snake; flame 95, 100, 200 spider, 60 crow, 100 Hunter, 80 wasp, 60 Chimera and snake. Rocket 900 on everything it hit: a loose crow was killed (fired aimed down as it swooped), but no rocket ever hit a snake (30 at a loose one) | reactions runs |
| knife | Zombies 8, dog 30 (aimed down), spider 10, crow 50, Hunter 16, wasp 20, Chimera 17, snake 20 — every one killed (a knifed wasp respawned with fresh HP within a second). Held level, the knife reads aim byte 0. Its reach is measured from the knife joint on x/z only, and it skips crows, wasps and the Chimera above Y −4800, so fliers are cut as they swoop in | knife at arm's length with the enemy held; crow and wasp let loose (`melee`) + decomp (`weapon_hit_detect_knife`) |
| kills | A lethal hit sets the enemy's kill flag (when it has one) and leaves HP below 0: zombies and Hunters end at −1 after their death animation, others keep their overshoot. Every type was killed with every weapon except the rocket on the snake (never hit) and the plant (can't be killed) | every type × weapon (reactions + loose `melee` runs) |
| wasps and snakes respawn | A wasp killed by a weapon up to id 6 (the flamethrower) leaves a corpse that crawls for 20 frames, then respawns in the nest with fresh HP; every tenth respawn comes back as kind `0x12`. A big wasp, or one killed by a grenade round or rocket, is removed for good and sets its kill flag. A shot snake sinks under the floor and comes back with fresh HP 210–241 frames later; the explosive round removed it for good | decomp (`wasp_state_death`, `adder_death_retreat`) + live: the handgun, shotgun and Python wasp/snake kills came back (HP −29 → 26), the grenade rounds set the wasp's flag |
| zombies getting back up | When its poise runs out (3–5 handgun hits here; a per-zombie roll from `0x4BB2A8`), a zombie bends down (state 1, action 7) and is walking again 5.7–9.8 s later. In the AI state, action 4 is a lunge, not a knockdown. A shot that blows a leg off (damage action 5) clamps HP at 1 rather than killing | poise runs: 14 falls over 3 types, every one back up within 10 s (30 s watch) + decomp (`zombie_damaged`, `explode_leg_and_drop`) |
| mansion plant | Weapons can't kill it. Its HP (1) is never used: each hit sends it to state 2, which counts it at `+0x188` (a shotgun shot can count 3) and, past 3 hits, sets scenario flag `0x5B`. It dies only when its room script sets behaviour bit `0x40`. It never turns, and faces the usual way. Variant 1 strikes when the player is within 3000 (Manhattan; 2500 once it has struck) and inside ±90° of its facing; its slap takes 2 HP. Its hold drains 2 HP every 12 frames, and only variant 5 stops at 0 (decomp; no hold was seen in the lab) | decomp (`MonsterPlant.cpp`) + live: with yaw 3072 and the player teleported to 8 sides 900 out, it struck from sides 2048–3584 and never from 0–1536, matching the decomp's facing check; hits counted for every gun and heavy weapon, flag `0x5B` set, HP unchanged |
| Hunter's leap (decapitation) | Kills outright whatever the player's HP: the bite puts the player in the bitten pose (animation 7, frame 6) and HP goes straight to −1, with `0x4BD2B0` = 1 (image value `0x6C6C`) for the death screen. It is only tried when the player is under 105 HP (Jill; Chris 72) and the Hunter itself under 79 (a 4-in-16 roll per decision); while the player has more than 15 HP, only 5500–6400 units away | decomp (`hunter_*`, `0x4B4860`) + live: Jill at 12 HP and the Hunter at 50 HP, leapt and killed in 3 of 3 tries (1.7–9.7 s); at 60 HP from 6000 it closed in and swiped instead |

## Leads (unverified)

| Address | Observation (live, 2026-10-13) | Lead |
|---|---|---|
| `0xD226A0` (u8) | Changes on room entry and when enemies appear (e.g. `0x08 → 0x20` as the dogs burst into 108, back after the kill). **2026-10-14, 588 automated room visits:** `0x08` in most rooms with or without enemies, `0xFF` around transitions, `0x40` set in a fixed group of rooms (10D, 10F, 115, 119, 203, 205, 20B, 20D, 210, 310, 405, 409, 60A, 60D, 615, 700, 705, 70B, 718) | Not a track number — a sound/BGM state bitfield; no danger cue |
| `0xD226A4` (u32) | Set to `0xD91AF4` from the first message shown onwards | Pointer to the on-screen text buffer — recorded as capture region `textBuffer` (`0xD91A00`, 1 KB) to find the message shown |

Room files keep their text in the MSG section (`offsets[11]`: u16 offset table, then text in the game's own encoding
with control codes), so a message number plus the player's install is enough to show the text without recording it.

## Structures (leads for later milestones)

From the decomp (GPL-3.0; facts only, no code used) and the RE1 Mod SDK layout, consistent with each other:

| Address | Description | Verification |
|---|---|---|
| `0xBE62E4` | Player entity (0x180 bytes) | partially (fields above) |
| `0xBE6464` | Enemy array, 30 × 0x18C; slot active when `+0x00 & 1` | unverified |
| `0xBE9950` | Rebecca's inventory (6 slots; a new game puts the Beretta with 15 rounds in it) | live 2026-10-15: the inventory pointer while playing Rebecca; it held the Beretta ×15, as her status screen showed (her portrait, nothing equipped, ECG Fine at 88/88); the held count `0xBE9827` was 1 and Chris's list at `0xBE9944` stayed as it was |
| `0xBE98E4` | Item box, 48 × `{id, qty}` | unverified |
| `0xBE9844` | Saved copy of the game timer | static |
| `0xBE9620` | Save block ("bio card"), 0x41C bytes | unverified |
| `0xBE98C0`… | Scenario/flag banks (see RESEARCH.md) | unverified |

Entity layout (shared player/enemy up to +0x8D): `+0x00` flags, `+0x01` id, `+0x34/38/3C` int32 X/Y/Z,
`+0x74` i16 yaw, `+0x88` i16 HP, `+0xDC` status flags (poison), `+0x175` max HP (player).

## Japanese PC 1.01 `Biohazard.exe` (static, 2026-10-17)

Build: 962,560 bytes, SHA-256 `25BE34D4F4586E9EC84E39412915AE1DE38AF1FC5924C02824D38E15C8406458`, x86, PE timestamp
`0x3642D468` (1998-11-06), image base `0x400000`, SizeOfImage `0x990000` (the RE1 autosplitter's "JapaneseREbirth"
module size). The official MediaKite patch 1.01 executable, which Classic REbirth installs (on GOG's Japanese version
too). Source of truth: `data/games/re1/profiles/re1-jpn-101.json`, generated by `tools/re1/port_profile.py`.

Every address above was found again in this exe's own code by `tools/re1/xmap.py`: each instruction that uses it in
the GOG exe, with its neighbours (absolute addresses and branch offsets masked), occurs exactly once here, and the
address it uses here is recorded. The votes agree block by block:

| GOG block | Moves by | Examples (GOG → 1.01, sites agreeing / sites) |
|---|---|---|
| Game state `0xBE41C0` (+0x587C): player, enemies, save card, inventory, box, flag banks, input | `+0x4EED0` | stage `0xBE9820 → 0xC386F0` (114/122), HP `0xBE636C → 0xC3523C` (150/154), character `0xBE62E5 → 0xC351B5` (117/122), position X (408/439), inventory `0xBE9944 → 0xC38814` (17/18), input `0xBF0A00 → 0xC3F8D0` (73/82) |
| System `0xD21380` (+0x1400) | `−0x279920` | game timer `0xD22730 → 0xAA8E10` (6/7), main menu `0xD22777 → 0xAA8E57` (3/4), inventory pointer `0xD22768 → 0xAA8E48` (73/85) |
| Room action table | `−0x258F00` | `0xD91AA0 → 0xB38BA0` (11/12), tail `0xD91BC0 → 0xB38CC0` (10/13) |
| Rooms-visited table (data) | `−0x21618` | `0x4D31E0 → 0x4B1BC8`; the same 16 bytes in both exes |

No site disagreed except one each for `stateFlags` and `flagLocks0`, where a neighbouring global shares the window.
All nine code anchors are the same instructions (e.g. the frame loop's `inc dword [0xAA8E10]` at `0x48D36E`, the death
check's `cmp word [0xC3523C],0` at `0x45B7EE`). The autosplitter's leads for this build (time `0x6A8E10`, stage
`0x8386F0`, HP `0x83523C`, character `0x8386F9`, module-relative) match. Room files: `JPN\StageN\ROOMsRRp.RDT` (the
exe's template is `d:/horr/jpn/stage /room    .rdt`).

**GOG's Japanese `Biohazard.exe`** (972,880 bytes, SHA-256 `9F38420B5B608CBB12ED38A1DBFAD690BB79D7407856EE0BADC76DC7168E8012`,
the same link timestamp `0x3642D468`, SizeOfImage `0x4991000` = the autosplitter's "JapaneseGOG") is the 1.01 code
with GOG's changes: the port gives every address and anchor exactly as in 1.01. **Live 2026-10-17** (probe trace, the
owner playing Chris for 13 min; `data/games/re1/profiles/live/re1-gog-jpn-hotfix4.json`): stage, room, camera, HP and
max HP, character, timer, main menu, position, the inventory pointer (it holds `0xC38814`) and slots 0–3, the equipped
slot, enemy slot 0, the room-item flags and the item box behaved as on GOG English, so those are runtime there.

**Classic REbirth 1.1.4 (live 2026-10-17)** rewrites the frame loop's `inc dword [0xAA8E10]` at `0x48D36E` (and the
one at `0x40B5B4`) into `call` + `nop` to its own tick: `cmp byte [0xC330AC],0xFF / je skip / inc dword [0xAA8E10] /
ret` — the same timer, skipped in one game state. The other `0xAA8E10` references (`0x484AE3`, `0x48CA0F`, the New Game
reset `0x48CBAA`, `0x48CBCC`, `0x48DE45`) are untouched, so the 1.01 profile anchors on the reset. A trace under REbirth
(Chris from the title to a death) showed the rooms, HP (to −10 at death), the timer (frozen from the death to the
title), the main menu, position, the inventory pointer and slots, enemy slot 0, a room-item flag and the item box as
on GOG (`data/games/re1/profiles/live/re1-jpn-101.json`).

**Room files, JPN vs USA (2026-10-17, `tools/rdt/rdt_index.py` on both):** the doors (target, lock flag, key), the
enemies (type, kill flag), the story flags and every item's pickup flag are identical. The only difference is ink
ribbons: 3 per pickup in Japanese where English gives 2 (14 spots), and the main hall's ribbon (flag 52) is one entry
for both characters. The map reads the player's own room files, so it shows each version's amounts; progress, the
feed and splits go by flags, which match.
