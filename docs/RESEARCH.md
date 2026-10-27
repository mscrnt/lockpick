# Research Notes

Findings from the reference projects and from analysis of the GOG executable. Each item is marked
**Confirmed** (checked against the GOG build statically and/or live), **Hypothesis** (sourced but not yet
verified on this build), or **Refuted** (contradicted by observation).

Research date: 2026-10-12. No third-party code was copied into Lockpick.

## Sources and licenses

| Project | What it is | Target build | License | Use in Lockpick |
|---|---|---|---|---|
| [ecruells/resident-evil-pc-decomp](https://github.com/ecruells/resident-evil-pc-decomp) | Decompilation of the USA PC release | **GOG USA `ResidentEvil.exe` — the same build** (its globals are referenced by this exe's code) | GPL-3.0 | Addresses/offsets used as facts with credit; no code |
| [Gemini-Loboto3/RE1-Mod-SDK](https://github.com/Gemini-Loboto3/RE1-Mod-SDK) | Mod SDK for Classic REbirth | Classic REbirth `Bio.exe` (Japanese-based); 0 references in the GOG exe | CC BY-NC-ND 4.0 | Struct layouts cross-checked only |
| [shadwork/Windows-ResidentEvil1-MODS](https://github.com/shadwork/Windows-ResidentEvil1-MODS) | REbirth mods (HUD, health bars, door skip) | REbirth `Bio.exe` | MIT | Layout cross-check only |
| [deserteagle417/RE1-Autosplitter](https://github.com/deserteagle417/RE1-Autosplitter) | LiveSplit ASL | JP REbirth, JP GOG, English GOG (earlier revision) | none (all rights reserved) | Addresses used as leads with credit; no code |
| [biorand/classic](https://github.com/biorand/classic) | Randomizer for RE1–3 | RE1: Sourcenext/Mediakite JP exe + Classic REbirth | MIT | RDT format notes, item/enemy ids (future, with attribution) |
| [IntelOrca/biorand](https://github.com/IntelOrca/biorand), [IntelOrca/biohazard-utils](https://github.com/IntelOrca/biohazard-utils) | Randomizer / RE file library | RE1 PC data files | MIT | Item and enemy id names (`data/games/re1/names.json`), RDT script format for `tools/rdt` (credited in THIRD_PARTY_NOTICES) |
| [pmandin/reevengi](https://github.com/pmandin/reevengi) | RE engine re-implementation | PC data files | GPL-2.0 | Opcode list cross-check only (it differs on 0x17/0x28/0x33; the fixed sizes parse every retail room) |
| [speedrun.com guides](https://www.speedrun.com/residentevil/guides) | Speedrun community guides | JP PC / PS1 mostly | none stated | Leads: green herb heals 32 (Jill) / 46 (Chris), G+G 64 / 93; zombie HP rerolled 18–99 on room entry; Richard's 421 s timer. SHiiDO's damage chart matches the GOG build live (zombie bite 10 / ankle bite 6, crow 6 + 3s, plant slap 2, green herb +32 for Jill) |

## 0. Areas, rooms and routes

- **Stage areas** (deserteagle417's "Autosplitter Room ID Maps", speedrun.com resources; floor plans with room numbers,
  no names, no license — not committed): stage index 0 = mansion 1F (rooms 1xx), 1 = mansion 2F (2xx),
  2 = courtyard & underground (3xx), 3 = guardhouse (4xx), 4 = laboratory (5xx), 5/6 = mansion 1F/2F on the return
  (6xx/7xx). The plans agree with the door links in `room-index.json` (e.g. 2F room 2, the balcony around the dining
  room, is where the statue falls from). `data/games/re1/rooms.json` holds the areas, and names only for rooms whose
  identity live captures or the player confirmed.
- **Key-item routes** (owner-supplied split package, July 2024: RE1aio.asl + LiveSplit files): Jill and Chris Any% NMG
  split on each key item in route order — Jill: Emblem, Chemical, Armor Key, Music Notes, Gold Emblem, Blue Jewel,
  Shield Key, Wind Crest, Serum, Moon/Sun/Star Crests, Square Crank, Control Room Key, Red Book, 002/003 Keys, Helmet
  Key, Doom Books, Batteries, Hex Crank, Power Room Key, Master Key, Flare, End (the Jill file carries a 49:01 personal
  best with per-segment times). Facts used as leads for route guidance and autosplits; the files and their icons are
  not included. The autosplitter also gives `characterID` 0xBE9829 (verified in saves), previous room 0xAC9CF4 and a
  new-game byte 0xC26E2F (unverified).
- **Speedrun segments** (LiveSplit templates from speedrun.com, no times): Chris/Jill × good/bad ending × MG/NMG share
  Yawn 1 › Mansion 1 › Courtyard 1 › Boxes › Guardhouse › Courtyard 2 › Yawn 2 › Basement › Mansion 2 › Courtyard 3 ›
  Black Tiger › Underground › Loud Room › (Lab) Tyrant › [Orgy Room, good ending] › End; the first split is
  "King Henry" (Jill NMG), "Rebecca Piano" (Chris NMG) or "Chemical Skip" (MG). A lead for route/split features (0.8+).

## 0a. Voice and video

`USA\Voice` holds the English voice-over as 563 plain WAV files (40.7 min, 22 kHz mono 16-bit; two 44.1 kHz stereo),
named `vSSS_LL.wav` (31 groups; the group looks like a story scene, LL a line, hex). `USA\Movie` holds 27 AVI videos
(Capcom logo, intro, endings). Only one room script plays a voice directly (`xa_on` in 704); cutscene voices come from
the event scripts (`offsets[8]`, not parsed yet) — or can be identified in captures if the playing sound shows in the
system block's 0x24-stride arrays. Transcribed 2026-10-13 (`tools/voice/`: Whisper large-v3 greedy + beam search with
character names as a prompt, NVIDIA Canary-Qwen 2.5B under WSL, merged by `compare.py`): 540 dialogue lines on which
at least two passes agree, 9 screams, the rest without speech (the `vb00_*` sounds and all 27 videos). Canary
misspells names (Whisker, Jail) but returns nothing on silence where Whisper invents text — together they catch
both failure modes. The dialogue is Capcom's, so it is never committed here: the subtitles are published separately as
[lockpick-subtitles](https://github.com/mscrnt/lockpick-subtitles) (fan transcription for accessibility, no license
granted, removed on request) by `tools/voice/export_pack.py`; CI rejects audio/video files in this repository.

## 0b. Room files (RDT) — what each game flag means

The game's room files (`USA\Stage1..7\ROOMsrrv.RDT`, s = stage, rr = room, v = 0 Chris / 1 Jill) contain init and
main scripts with `item_aot_set` (item id, quantity, pickup flag), `em_set` (enemy type, kill flag) and
`door_aot_set` (target room, lock flag, key item) opcodes. `tools/rdt/rdt_index.py` walks every retail room file
(320 files, no desync) and writes `data/games/re1/room-index.json` — ids and flag numbers only.

**Confirmed (live, 2026-10-13):** flag *n* of a bank is bit `31 − n % 32` of word `n / 32` (bit 31 of the first word
is flag 0). The ITEM group is the `flagRoomItems` bank (a set bit = still in the world), ENEMY is `flagEnemies`
(set = killed), LOCK is `flagLocks` (set = unlocked). Checked against captures: blue jewel flag 44, 1F map 145, wind
crest 174, the two dogs in 108 (0, 1), the door 206→207 (lock 21), the document in 206 (247).

## 1. Identifying the running game

- **Confirmed:** GOG installs register under `HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\1580232252`
  (`path`, `exe`, `ver` = "1.0 hotfix4"). Executable `ResidentEvil.exe`, 32-bit, no ASLR.
- **Confirmed:** The autosplitter identifies builds by `ModuleMemorySize` (EnglishGOG = 77279232 /
  `0x49B3000`). This GOG revision's SizeOfImage is `0x49B2000`, so that check fails on it — an earlier
  GOG revision existed. Lockpick identifies by SHA-256 instead.
- **Confirmed:** GOG ships its own DirectDraw wrapper (`ddraw.dll`, "GOG.com DirectX 1-7 wrapper"
  v1.1.0.5574, configured by `dxcfg.ini`) and input wrapper (`dinput.dll`). Classic REbirth is not present.
- **Confirmed:** When the GOG installer launches the game it runs elevated; a non-elevated Lockpick then
  gets `ERROR_ACCESS_DENIED` for `PROCESS_VM_READ`. Lockpick reports this explicitly.
- **Confirmed:** The game is single-instance; a second launch exits immediately.

## 1a. Versions runners use, and Classic REbirth (2026-10-17)

- **Confirmed (speedrun.com API, game `residentevil`):** the PC board recommends the Japanese MediaKite version (or a
  reprint) with Classic REbirth (or the 1.7/1.8 patch); GOG and Steam are allowed and may use REbirth. LiveSplit's IGT
  script must be shown. Banned: REbirth's debug menu, quickturn and tactical reload; mods; tabbing out. Separate boards:
  EMU (DuckStation with fixed settings, or PSXjin / BizHawk; Director's Cut non-DualShock is the fast version), PlayStation,
  PSN, Saturn (real hardware), plus `re1ce` (extensions) and `re123`.
- **Confirmed (LiveSplit.AutoSplitters.xml):** the only RE1 autosplitter is deserteagle417's `RE1aio.asl`; PC only
  (JapaneseREbirth 10027008, JapaneseGOG 77144064, EnglishGOG 77279232 by ModuleMemorySize). GOG hotfix4's SizeOfImage
  is 77275136 (`0x49B2000`), so the script's English GOG check doesn't match this build.
- **Confirmed (classicrebirth.com, files downloaded 2026-10-17):** REbirth's install is the official 1.01 `Biohazard.exe`
  (962,560 bytes) plus its `ddraw.dll` (1.1.4 also `SDL3.dll`, `gamecontrollerdb.txt`, and a Steam launcher,
  `4249100_Launcher.exe`, with the game in `japanese\`). Its DLL holds the size `0xEB000` and SizeOfImage `0x990000` of the
  1.01 exe and none of the GOG English exe's values. Version table in 1.1.4: European 1.00c, Japanese 1.01, Asia 1.00,
  UK/USA 1.00 (+alt), German, French, PowerVR UK/US and Japanese 1.00, PC Gamer demo.
- **Confirmed (strings in REbirth's DLL):** `config.ini` beside the game, `[RE1]` (… `Save_folder = %s`, default
  `savedata`) and `[DLL]` (`QuickTurn`, `TactReload`, `CutSkip`, `FocusIgnore`, `EnableDebug`, `BootConfig`, `Steam`, …),
  `[Mods]` / `[Mod_Preset_n]`; mods are `mod_*` folders. Saves: `<save folder>\savedatN.dat`.

## 2. Accessing memory

- **Confirmed:** All relevant state is in static globals of the main image (no pointer chains needed for
  0.1). Absolute addresses are stable because the image is not relocatable.
- Autosplitter offsets are module-relative (`0x400000 +`). The decomp lists absolute addresses.

## 3. Player and enemy structures

- **Confirmed (live):** HP i16 at player+0x88 (`0xBE636C`), max HP u8 at +0x175, position int32 at
  +0x34/+0x3C, character at +0x01 (Chris = 0).
- **Confirmed (live):** Jill = 1 with max HP 96 (decomp: `maxHealth = (id & 1) * -44 + 140`); a Jill game sets
  `stateFlags & 0x00800000`. **Hypothesis:** Rebecca = 3.
- **Hypothesis:** Enemy array of 30 × 0x18C at `0xBE6464` (decomp; the Mod SDK declares 32 but the USA
  code clamps to 30). The autosplitter's EnglishGOG enemy addresses are JP values copied unchanged and are
  wrong for GOG (no code references).

## 4. Room and camera

- **Confirmed (static + live):** stage/room/camera bytes at `0xBE9820/21/22`, derived from the RDT and
  background filename builders (see MEMORY_MAP.md). Room files: `USA\StageN\ROOMsRRp.RDT`, `p` = 0 Chris / 1 Jill.
- **Confirmed (live):** at a door the room id changes first; the camera id keeps the previous room's
  value for several seconds (4.7 s at 106→105; ~17 s at 105→104 during the Kenneth cutscene) while the
  game timer is frozen.

## 5. RDT files (for 0.4)

From biorand / biohazard-utils (MIT), not yet verified against the USA files:
header bytes `nSprite, nCut, nOmodel, nItem, nDoor, nRoom_at`; 19 int32 offsets at `0x48`
(cameras, collision, models, init/main/event SCD, messages, …); camera array at `0x94`.
SCD opcodes: `0x0C door_aot_set` (26 bytes; target = stage<<5 | room), `0x18 item_aot_set` (26 bytes),
`0x1B` enemy set (22 bytes). Some stage-1 rooms are read from stage+5 (`MissingRooms`).

Room-script facts settled while building the route simulator (`tools/routesim`, 2026-10-27; static, from every
retail room file, checked against two live story-flag logs of Jill's opening):
- **Event inline blocks** (event opcode `0x06`): the payload starts with a u16 block size (counting itself) before the
  first command (the decomp's `tools/evt_disasm.py`); reading the size as an opcode desynchronised most event scripts.
  A single command (`0x07`) is read at its own size, which some length bytes understate.
- **`0x12` re-arms a room action** (handler, probe flags, words) and leaves the zone alone; `0x13` writes the handler
  and probe flags only. Scripts turn doors into "it's locked" messages or cutscene triggers this way, and arm items
  (the Wind crest in 10D, the Helmet key at Plant 42's fireplace, the lab key behind the panel).
- **Probe flags**: a door record's `+0x19` and an item record's `+0x17` are the entry's probe byte; without `0x01`
  the entry is dead until a script arms it (`0x81` = the player's action key). Lifts are doors with a dead probe that
  an event uses with a room-action call (`0x24`, handler 1 `door_try_enter`).
- **State byte 3** holds the room the player came from: scripts compare it with the neighbours' room numbers (305:
  15/16 for 30F/310; 302: 0/1 for 300/301, where the lifts arrive). The decomp names it attractMode_RoomCameraId.
- **Item use**: `g_itemUseFlags` bit `item − 0x1B` is armed by a zone's `flag_bank_set`; the main script tests
  "item used is" (`0x10`) or state byte 18 (the used item), state byte 19 (the item just picked up), state byte 16
  (the room action the player stands at).
- A door's target stage (`+0x0F >> 5`) is 1-based, 0 = this stage.

## 6. Overlays / HUD

- REbirth mods draw in-process through the REbirth SDK. Not applicable to GOG. The GOG wrapper renders via
  Direct3D 9; an external transparent window is the planned first approach (0.3).
- **Confirmed (user report):** the game stops rendering (black) when it loses focus — a property of the
  original window procedure; fixing it needs an in-process patch.

## 7. Gameplay events

- **Refuted:** "`stateFlags & 0x02000000` = gameplay active" (decomp `g_main_state_flags`). Live: set only
  for single polls at New Game and during door transitions; clear during all free movement.
- **Confirmed (live):** `mainMenu` (`0xD22777`) is 1 at the title menu (also after dying) and 0 in game
  (autosplitter uses it for reset).
- **Confirmed (live):** `stateFlags & 0x01000000` set at death; HP overshoots below zero (−10).
- **Confirmed (live):** after death, choosing New Game keeps the dead game's room/HP in memory through
  character select and the intro (~23 s) until the new game initializes.
- **Confirmed (live):** loading a save from the title: `mainMenu` 1 → 0 on entering the load screen, then the
  timer jumps to the save's value (backwards, `0x64AE → 0x6253`), the position resets to the saved spot and the
  timer resumes. During the load-file screen (~7 s) the previous game's values remain in memory with
  `mainMenu == 0`, so they briefly count as "in game".
- **Confirmed (live, 2026-10-13):** attract mode (title screen left idle) replays the same demo each cycle on this
  build: Chris in room 107 with a preset 8-slot inventory and item box, `mainMenu = 0`, max HP 140 but **HP 0**.
  The session rule (`hp > 0`) therefore treats demos as "not in game", so they produce no state or events.
- **Confirmed (live, 2026-10-13):** in **Jill's** game, saving at a typewriter on the GOG PC build consumed **no ink
  ribbons** (owner saved three times with no ribbon in the inventory and no quantity change; `SAVE\savedat1–3.dat`
  were written at the save times). **Chris's** game uses them: each of his saves consumed one ink ribbon (id `0x2F`,
  BioRand id 47) — ribbon use depends on the scenario, not the platform. During a save the in-game timer freezes (~7 s) and no profile field or state-flag bit changes,
  so saves are detected from the save files, not memory.
- **Lead (2026-10-17, owner's Knife only session, Jill):** entering 107 cleared its ink ribbon's item flag (39) with
  nothing reaching the inventory, 3 s before the lockpick opened the door to 108. Jill's rooms seem to mark their
  ribbon spots taken as she enters, which fits her game saving without ribbons. The owner's idea: the game hands Jill
  an "invisible" ribbon supply around then (she usually can't save before that point). To check: which rooms clear
  their ribbon flags for Jill, and when her typewriter starts allowing saves. The event feed no longer calls it a pickup.
- **From the leaderboard's rules (speedrun.com, read 2026-10-16):** opening the options menu pauses the in-game timer
  (the PC rules forbid it as a pause for that reason). One answer for B206; the status screen, files, doors,
  cutscenes and the item box are still to be measured.
- **Refuted for this build:** the autosplitter's `time` (`0x4D46D4`) is not the live timer — it is a
  copy written at one code site (`0x4298AB`). The live timer is `0xD22730`.

## 8. Pickups and interactables

- **Confirmed (static):** inventory at `0xBE9944`, 2 bytes per slot (`mov byte [eax+0xBE9944], id` /
  `mov byte [eax+0xBE9945], 1`). Item ids (biorand, matches autosplitter lists): 0 none, 1 knife,
  2 Beretta, 3 shotgun, … 47 ink ribbon, 61 small key, 66 serum, 67–75 herbs, 0x6F Uzi, 0x70 minigun.
- Room items come from RDT `item_aot_set`; per-room pickup flags live in a flag bank at `0xBE989C` (hypothesis).

## Methods

- Static analysis: Python + Capstone linear sweep over `.text`, searching absolute operands, plus string
  cross-references (`./usa/stage /room    .rdt`, `rcSRRC.pak`, `savedat%d.dat`). Scripts were run on the
  local install only; nothing from the game is committed.
- Runtime: `lockpick-probe trace` (read-only) during normal play. Raw traces are kept next to fixtures.
