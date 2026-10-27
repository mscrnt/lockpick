# Development Guide

Everything needed to build, test, verify and release Lockpick.

## Prerequisites

| Tool | Version | Notes |
|---|---|---|
| Windows | 10/11 x64 | Process access is Win32-only |
| .NET SDK | 10.0.1xx or later | Pinned by `global.json` (`rollForward: latestFeature`) |
| Resident Evil (GOG) | build in `data/games/re1/profiles/` | Only needed for live validation |
| Rust (rustup, MSVC) | 1.96 or later (CI: 1.99.1) | Builds livesplit-core, the timer engine, from `extern/livesplit-core` (ADR-019) |
| Python 3 | optional | `tools/fixtures/trace_to_fixture.py`, static-analysis notes |

Clone with the submodule: `git clone --recursive …`, or `git submodule update --init` in an existing clone. The first
build compiles livesplit-core (about 40 s); later builds check it in about a second. `-p:SkipLiveSplitCoreBuild=true`
builds without it (Lockpick then runs with the LiveSplit timer off). After moving the submodule, regenerate the C#
bindings with `tools/livesplit/bindings.sh`.

Process access needs no native code: it is all P/Invoke. The one native component is livesplit-core (ADR-019),
added because it gives LiveSplit's own timing, files and look, which can't be had otherwise (ADR-002).

## Build, run, test

```powershell
dotnet build Lockpick.sln -c Release          # warnings are errors
dotnet test tests/Lockpick.Tests              # unit + Win32 integration tests
dotnet run --project src/Lockpick.App         # desktop app
dotnet run --project src/Lockpick.App -- --replay tests/Lockpick.Tests/Fixtures/re1-live-chris-death-and-restart.jsonl
dotnet run -c Release --project benchmarks/Lockpick.Benchmarks -- --filter "*" --job short
```

App options: `--replay <recording.jsonl>` plays a recording instead of attaching; `--verbose` enables debug logging.

Run the app from a copy (or close it) before rebuilding — a running `Lockpick.exe` locks its DLLs.

Runtime files live in `%LOCALAPPDATA%\Lockpick\`: `logs\` (JSON lines), `recordings\`, `profiles\`
(user/override profiles), `settings.json`. The portable zip (a `.portable` marker beside `Update.exe`, the app in
`current\`) keeps them in `data\` beside the marker instead (`AppPaths.DataRootFor`).

## Probe tool (headless validation)

```powershell
dotnet run --project tools/Lockpick.Probe -- identify            # fingerprint the GOG install
dotnet run --project tools/Lockpick.Probe -- identify "D:\x\ResidentEvil.exe"
dotnet run --project tools/Lockpick.Probe -- watch --seconds 60 --raw --record session.jsonl
dotnet run --project tools/Lockpick.Probe -- trace --seconds 600 > trace.txt
```

- `watch` runs the real monitor and prints status, events and normalized state.
- `trace` prints every raw profile-field change at 30 Hz (timer/position shown as context). This is the
  primary tool for verifying or refuting an address.

## Autoplay harness (developer only — never shipped)

`tools/Lockpick.Autoplay` drives the game with a **virtual Xbox 360 controller** for automated tests. It is dev
tooling: not part of the app, its installer or any release (release.yml publishes only the app and the probe) —
players play the game themselves.

Setup: install the ViGEmBus driver once (`winget install ViGEm.ViGEmBus`, admin prompt). The controller is plugged
in for a run and unplugged at the end.

```powershell
dotnet build tools/Lockpick.Autoplay -c Release
$ap = "tools/Lockpick.Autoplay/bin/Release/net10.0-windows/lockpick-autoplay.exe"
& $ap serve --launch --background  # start the game with the pad plugged in; keep it running when unfocused
& $ap send "press A; until gameplay 60000; state; shot room.png"   # commands to the running server
& $ap send --file test.txt         # a whole script
& $ap send quit                    # stop: unplugs the pad, removes the focus patch (the game keeps running)
& $ap run test.txt                 # one-shot: a script in its own process (start it with `launch`)
& $ap calibrate --launch           # press every input; print the PSX bits the game read (0xBF0A04)
```

The game finds controllers **only when it starts**, so it must be launched by the harness (`serve --launch`, a
script's `launch`, `calibrate --launch`); a pad plugged into an already running game is ignored. `serve` keeps one
pad for the game's lifetime, so tests can be sent step by step. Stop the server before rebuilding (DLL lock).

`--background` (or the script command `background`) applies the **dev-only focus patch** (`FocusPatch.cs`): the
game keeps running and reading the pad while other windows are in front, and its keyboard map is kept blank so keys
typed elsewhere don't reach it. The virtual pad is a real controller to Windows: **turn off Steam Input for Xbox
controllers** while runs go (Steam → Settings → Controller), or Steam's desktop layout turns its presses into
Enter/Escape/arrow keys in whatever window is in front. Bytes are checked before writing, read back, and restored when the harness exits; the
changes are in the game's memory only and end with the process. Screenshots use PrintWindow, so they work behind
other windows. `foreground` removes the patch again (the game pauses and blacks out when inactive, as players see
it) — used by the display research ([DISPLAY.md](DISPLAY.md), `tools/display/probe.py`).

Script commands (one per line, `#` comments): `launch`, `restart`, `background`, `foreground`, `focus`, `press A [ms]`
(combine with `+`), `wait ms`, `until room 106 [ms]` / `until gameplay` / `until field=value`, `expect …` (same
conditions), `state`, `peek addr len`, `calibrate`, `shot file.png [width]`, `look <question>`, `echo`, plus:

- `door <48 hex digits>` — enter a room through a door record, exactly as the game's doors do (`RoomWarp.cs`,
  MEMORY_MAP "Room changes"): the game plays its door animation and places the player at the door's arrival spot.
- `place <x> <z> [angle]` — stand the player at a spot in the current room, e.g. an item's pickup zone
  (`doors.items()`: the zone's centre is floor next to the item) (`PlayerPlacement.cs`). The camera re-cuts only when
  the player walks across a camera trigger, so the view may lag until they move.
- `camera <id>` — cut to a camera of the room (memory trigger; the camera must have a switch-zone group —
  `rdt_map.camera_for(x, z)` picks one) (`CameraCut.cs`).
- `freeze-enemies` — stop the room's enemies until the next room load (`EnemyFreeze.cs`); `heal`; `inv-clear <from>`
  (empty spare inventory slots); `give-box <item> [qty] [--front]` (into the first empty item-box entry, or entry 0
  where the box cursor starts); `open-box` (open the item box screen in any room: the game opens a menu by itself
  when a mode bit of the state flags' byte 1 is pending, `0x1000` = item box); `box-cursor <slot>`; raw
  `give <item> [qty]` (writes the slot and held count, but the grid icon stays whatever was loaded for that slot);
  `new-game chris|jill [s] [--skip-opening file] [--dump]` (fresh boot to a new game with no presses, right after
  `launch`: `NewGame.cs`, MEMORY_MAP "Boot and new game"); `scene-stop` (stop a playing scene: event slots idle,
  message closed, cutscene flags off); `state-apply addr:set:clear …` (set/clear bits of saved-state bytes in one
  suspension); `enemy-wake <slot> <x> <z> [facing] [behaviour hex]` (start a loaded enemy afresh at a spot, as the
  room script's `enemy_set` does: position, facing, behaviour flags, AI state 0 — e.g. Jill's dining room keeps a
  dormant zombie in slot 1 for a later scene; woken by the door pillar it walks to the player); `enemy-state <slot>
  <state>` (zombies: 4 = `zombie_no_action`, a bare RET — stands still, still drawn and hittable; 1 = back to its
  behaviour). `freeze-enemies` (enemy count 0) also stops drawing them.
- **Spawning and swapping enemies** (`spawn.py <type> [x z] [--slot 2] [--hold]`): the room reloads at the player's spot
  with one extra `enemy_set` run right after its own init script (`room-script <hex>` / `room-script off`,
  `RoomScriptHook.cs`: a jump at `0x477B29` to a block that runs the room's script, then ours), so the game loads the
  enemy's model, animations and AI like one that belongs there. Records come from a real `enemy_set` of that type in
  the install's room files; `--hold` uses `enemy-hold <slot> <state>`, sent with the door, which catches the enemy's
  first frames (do-nothing state: zombies/dogs 4, snakes 5). Enemy sounds come from a per-room table, so a type
  foreign to the room may be silent. Holds are armed before the door (`enemy-arm <slot> pin|<state>`) and caught on
  each enemy's first frames: zombies and dogs get their do-nothing state 4; crows, wasps and snakes have none, so
  every other type is pinned (`enemy-pin`: a harness thread rewrites its position, facing and AI state about every
  millisecond; health and hit state are left alone so shots still count).
- **Weapon lab** (`lab.py <out> <experiment> [--types 0,2] [--weapons 2,3] [--pairs 5:3,15:5]`): the owner's pick,
  room 108's second leg. Jill is teleported (no walking) into the foreground of camera 2 at (24000, 15500), facing the
  first corner where the enemy stands. The dogs' trigger lines only react to the player, who never crosses them.
  Experiments:
  - `hp`, `killflags`, `aim`, `attacks`, `deaths`
  - `reactions`: every weapon's hit and lethal hit per type
  - `poise [--long]`: knockdowns and getting back up
  - `special`, `falloff [--headkill]`: the shotgun/Python distance and aim rules
  - `linger`, `decap`, `wasps`, `melee`

  It is for things the code can't settle, not for what is known; results are in MEMORY_MAP ("Enemies in the weapon
  lab"). Weapons come in through the item box screen (`ap.equip_now`). The spare slots are cleared first, and
  `inv-clear` frees the icon rows it empties; otherwise the game runs out after a few changes and crashes at
  `0x402E1D`. A shot whose gun never rose (aim byte 0) reboots the game and the case is retried. Enemies that need
  effect sprites their room doesn't load (Hunter, spider) are covered by `effect-guard on`. Run one queue at a time,
  and never edit a queue script while it runs.
- **Cutscene skips** (`scenes.py`): a scene's end is recorded once by watching it — the bits it changes in the saved
  block (`0xBE9620`–`0xBE9E1F`, what a save holds) and where it leaves the player — then applied instead of playing
  it, so the game is left as if the scene had been watched. `scenes.py record-opening|check-opening jill|chris`;
  the files in `tools/autoplay/scenes/` hold addresses, bits and positions only.
- **Giving items: use `ap.give(item, qty)`**, not raw `give`. It puts the item in box entry 0, opens the box screen,
  points the cursor at the first empty slot and swaps with A, A, then closes with X, checking the screen state before
  each press. The game's own swap loads the slot's icon, so the item looks right in the grid (verified live for Jill
  and Chris, 2026-10-14). `ap.give(item, qty, to="box")` only fills a box entry. The player must be free (no menu or
  message).
- Reading the screen from memory (`ap.py`): `message_state()` / `message_text()` / `dismiss()` / `answer(yes)` use the
  game's own message state, `inventory()` gives names as the game shows them, `examine(item)` turns an item in the
  viewer until it says something, `use_item(item)` checks every menu step. Never press A on the strength of the state
  flags alone: messages don't show there.
- `speed <ms>` / `speed normal` — the game's tick interval (normal 33 ms); smaller values fast-forward cutscenes and
  scripted waits with every effect intact (`GameSpeed.cs`; restored on exit).
- `record [folder]` / `mark <note>` / `record stop` — a Lockpick capture of the run (trace, events, memory regions,
  markers), readable by `tools/bounties`.

Controller → game (default joystick table): A action, X run/cancel, Y status, Back options, RT aim, d-pad/left stick
move. Example:

```text
launch
background
wait 12000
press A          # title: press any button
shot menu.png 480
look Which menu is open, and which entry is highlighted?
```

### Python runners (`tools/autoplay`)

- `ap.py` — client: `serve()`, `send()`, `look()`, `state()`, `screen()` (vision-classified: title, newload, card,
  loadmenu, …), `new_game("jill"|"chris")` (fresh boot → new game with **no presses and no opening**: the
  harness's `new-game` hands the title task to `game_start` as the character, applies the recorded opening end
  (`scenes/opening-<character>.json`) the frame before the first room loads, and loads that room fast-forwarded;
  control is read from state — room 105, no busy flags or message, held 1.5 s; ~15 s from launch, clock ~3 s.
  `skip_opening=False` plays the opening instead, fast-forwarded),
  `in_control()` / `settle()` (state-flag busy bits; fast-forwards cutscenes until control returns).
- `doors.py` — door table from the player's install (every door's 24-byte record); `record_to(table, "105",
  "jill")` picks a real door into a room.
- `survey.py <out> <save-backup> [jill] [chris] [--rooms 105,106]` — from a new game, enters every door-reachable
  room, fast-forwards cutscenes, records state, enemy slots, a screenshot and a one-line vision description; restarts
  after deaths, redirects (scenario flag 0 sends stage 1/2 doors to 6/7) and errors; resumable.
- `library.py <out> <save-backup> <folders…> [--own]` — loads saves (the owner's slots in place; others borrowed into
  slot 1 with the owner's key settings) and checks Lockpick's live room/character against the offline decode, reads
  the status screen, records the scene; slot 1 is restored and hash-checked after every save.
- `report.py <run-dir>` — `atlas.md`: per area and room, the room files' contents, what the live run found and the
  vision description.
- `pickup.py <out> <save-backup> [jill] [chris]` — stands at every armed item (live action table) so the 600-unit
  probe lands in its zone from a spot outside collision, picks it up and checks item and flag against the room index.
- `rag.py index <run-dir>` / `ask …` — retrieval on the local stack (TEI embeddings, Qdrant collection `lockpick`,
  reranker): flag labels, names, room-script facts, pickup results, the atlas; grounded vision questions use it
  (`context.py`). Env: `LOCKPICK_EMBED_URL`, `LOCKPICK_RERANK_URL`, `LOCKPICK_QDRANT_URL`.
- `tools/routesim`: the route simulator (plays the game through from the room and event scripts; `plan.py`,
  `build_routes.py`, `check_log.py` against a live story-flag log). See its README.
- `tools/rdt`: `scd.py` (room scripts with their conditions), `events.py` (event scripts), `rdt_map.py` (geometry),
  `render_map.py` / `floor_map.py` (room and floor maps, local HTML), `tools/bounties/flag_timeline.py` (when each saved
  flag turns on across practice saves).
- `llm.py "question" files…` — digests long text (decompiled code, logs) with the local model; leads only.

Environment: `LOCKPICK_VISION_URL` / `LOCKPICK_VISION_MODEL` (OpenAI-compatible vision model; also used by
`llm.py`). Outputs stay outside the repository (`%LOCALAPPDATA%\Lockpick\research\…`): screenshots and
descriptions of the game are not committed.

Rules:
- Without `background`, every press checks the game is the foreground window and the run stops if it isn't
  (someone is using the PC). The harness never injects keys: `focus` only asks Windows, which refuses a background
  process (2026-10-14: an injected Alt opened menus in the owner's windows). WScript's `AppActivate('RESIDENT EVIL')`
  does bring the game to the front (used by the display research); the virtual pad resets Windows' idle timer, so
  "is someone using the PC" has to ignore the harness's own presses.
- Never test exclusive fullscreen at a resolution other than the desktop's without saving the display settings and
  window positions first: on 2026-10-15 it left the monitors rearranged (primary swapped) until
  `ChangeDisplaySettingsEx(NULL, NULL, …)` re-applied the saved layout.
- `calibrate` skips Start and Back (they open the game's options and key setup).
- Pressing through the title screen can load the player's saves (it did twice on 2026-10-14): `ap.wait_screen` only
  presses on screens where a press is harmless (logo, title, text, black) and backs out of the load list.
- The runners make `SAVE/*.dat` read-only for the whole run, so the game can't overwrite a save even if a stray press
  reaches a typewriter (one did on 2026-10-14; the hash check restored the file). Only the runners' own writes (the
  borrowed slot, restores) lift the flag, and set it again.
- Never save in-game during a run unless the script borrows a slot; back up `SAVE/` (with a SHA-256 manifest)
  first and restore it with a hash check afterwards. `survey.py` and `library.py` check `SAVE/` after every step and
  restore any changed file from the backup.
- `look` asks a vision model on an OpenAI-compatible server (set `LOCKPICK_VISION_URL`, e.g. `http://host:8000/v1`,
  and `LOCKPICK_VISION_MODEL`) to label a screen; pass/fail always comes from memory.

### Behaviour cloning (`tools/rl`, developer only — never shipped)

Learns from the owner's own play: a capture's memory log (10 Hz) holds both what the game saw and the game's own
held-input word `0xBE9840`, so each frame becomes a state → buttons pair. Bits, measured from 5.6 h of captures
(`inputs.py`) and checked live: forward `0x11` (walk 1,207 units/s; with run `0x8200`, 5,461), back `0x24`, turn left
`0x02` / right `0x08` (2,549 facing units/s standing), aim `0x0500` (RT), action/fire `0x00C0` (A).

```powershell
$py = "$env:LOCALAPPDATA\Lockpick\research\.venv\Scripts\python.exe"   # has torch (CUDA) and numpy
$out = "$env:LOCALAPPDATA\Lockpick\research\rl"
& $py -I tools/rl/captures.py <capture.zip>...                 # frames, rate, rooms per capture
& $py -I tools/rl/inputs.py <capture.zip>...                   # what each input bit does
& $py -I tools/rl/dataset.py $out\bc.npz <capture.zip>...      # state -> buttons rows
& $py -I tools/rl/train_bc.py $out\bc.npz $out\bc.pt --holdout <capture name>
& $py -I tools/rl/play.py $out\bc.pt <target room> [seconds]   # drive the pad (harness running)
```

The state is goal-conditioned: the door the player went through next (hindsight), 8 clearance rays from the room's
collision, the 3 nearest enemies, HP, camera and the previous frame's buttons. Models and datasets stay in the research
folder; only the owner's hand-played captures are used (not the automated runs).

## Verifying a memory address (required before marking it trusted)

1. **Source it.** Record where the lead came from (decomp, SDK layout, autosplitter, own analysis).
2. **Static check against the exact build.** Find code in the fingerprinted executable that references the
   address with the right width and plausible semantics (e.g. the room loader builds `ROOM%x%02x%x.RDT`
   from stage/room). Add a code anchor (address + instruction bytes) to the profile.
3. **Runtime check.** Run `lockpick-probe trace` while performing actions that should change the value.
   Record what was observed, with the date, in the field's `evidence`.
4. Set `verification` to `static` or `runtime`. `unverified` fields are read for diagnostics only and never
   reach the normalized state.
5. Update `docs/MEMORY_MAP.md` and, for refuted claims, `docs/RESEARCH.md`.

Never guess an offset. If a value cannot be verified, leave it unsupported.

## Adding a game build

1. `lockpick-probe identify <exe>` → SHA-256, size, PE timestamp, image size.
2. Port the verified GOG profile to it by static cross-mapping (needs `pip install capstone pefile`, e.g. in the
   research venv; `PIP_CONFIG_FILE=nul` on the dev machine):
   `python tools/re1/port_profile.py data/games/re1/profiles/re1-gog-usa-hotfix4.json <GOG ResidentEvil.exe> <new exe>
   --id <id> --name "<name>" --out data/games/re1/profiles/<id>.json`. It finds the code that uses each address
   in the new exe and keeps only what the code sites agree on (static); anchors must all be found. Check the
   "not carried over" list it prints. `python tools/re1/xmap.py A.exe B.exe targets.txt` shows the votes for any
   address list. An exe that is packed on disk (Steam's wraps GOG's) is ported from its unpacked image: start the
   game, `python tools/re1/dump_image.py <pid> image.exe` (read-only; keep the dump out of the repo), then pass
   `image.exe` as the new exe and `--identity <the shipped exe>` so the profile matches the file players have.
3. Run the build and `lockpick-probe trace` against it; raise each field to `runtime` only after it behaves as
   described in play, with the date in its evidence (and only then restore `write` bounds for the trainer).
4. Profiles are embedded at build time; users can also drop profiles in `%LOCALAPPDATA%\Lockpick\profiles\`.
   Tables, pointers and arrays the adapter reads as a whole are the profile's `addresses` (by name), not code.

## Recording fixtures

- In the app: Debug → Start capture / Stop and save (the zip in `%LOCALAPPDATA%\Lockpick\captures\` contains
  `recording-N.jsonl` and `trace-N.txt`).
- From a raw trace: `python tools/fixtures/trace_to_fixture.py trace.txt out.jsonl` (applies the same
  normalization rules as the RE1 session; keep the trace beside the fixture as provenance).
- Fixtures under `tests/Lockpick.Tests/Fixtures/` are played back through the real event detector in tests.

## Volunteer captures

Volunteers capture from the app's Debug page (see [CAPTURE.md](CAPTURE.md)) and attach the zip to a Capture
submission issue. The `capture-triage` workflow analyzes it and comments with what it shows (it never edits the repo).

Bounty data lives in `data/bounties.json`; `docs/CAPTURE_BOUNTIES.md` is generated (`render --check` runs in CI).

Captures contain `memory-N.bin` (the game-state memory, see ARCHITECTURE ADR-013); the analyzer decodes every profile
field from it — add a field to the profile and old captures can be re-analyzed for it.

1. `python tools/bounties/bounties.py analyze LockpickCapture-*.zip` — review the report (also posted on the issue).
2. `python tools/bounties/bounties.py apply LockpickCapture-*.zip --source "issue #N"` — records findings in
   `data/bounties.json` and regenerates the doc. Unambiguous results are checked off; everything else becomes
   "evidence — needs review": confirm it against the trace, then set `status` to `done` by hand (and update the
   profile evidence / MEMORY_MAP for new addresses).
3. `capture-info.json`: if `Builds[].ProfileId` is null, the SHA-256/PE fields identify a new build to profile.
   `python tools/bounties/timeline.py LockpickCapture-*.zip` prints what the player did, in order: rooms, pickups and
   documents (named via the room index), kills, enemies getting back up, unlocks, pushes, item box, item use and
   saves, and the player's markers.
4. Convert interesting sessions to fixtures: `python tools/fixtures/trace_to_fixture.py trace-1.txt tests/Lockpick.Tests/Fixtures/<name>.jsonl`
   (keep the trace beside it), then add playback assertions.
5. Add new bounties to `data/bounties.json` when a roadmap item needs data nobody has captured yet (give them a
   `detector` when the tool can recognize them).
6. Credit the volunteer in `CONTRIBUTORS.md` once they have contributed and agreed to be listed (the submission form asks).

## Project management

- **Milestones** 0.2.0 → 1.0.0 mirror docs/ROADMAP.md; every issue for a release goes in its milestone.
- **Board:** [Lockpick Roadmap](https://github.com/users/mscrnt/projects/8) (public, linked to the repo).
- **Labels:** `bounty` (capture needed), `capture`, `compatibility`, `needs-game` (needs live validation),
  `area:core|re1|app|trainer|overlay|ci`, plus the GitHub defaults.
- **Bounties:** docs/CAPTURE_BOUNTIES.md is the source of truth; open bounties for the next milestone also exist as
  `[Bnnn]` issues. When a capture verifies one, check it off in the file and close the issue.
- **Pinned:** the capture-bounties tracker issue. **Discussions** for questions, ideas and announcements (no wiki:
  documentation lives in `docs/`).

## Room index

`data/games/re1/room-index.json` maps game flags to the items, enemies and doors that use them. It is generated from
a local install (ids and numbers only; the game files themselves are never committed):

```powershell
python tools/rdt/rdt_index.py "G:\GOG Games\Resident Evil"
python tools/rdt/rdt_index.py "G:\GOG Games\Resident Evil" --dump 105   # one room
```

The two full-game routes in `data/games/re1/routes.json` (`re1-jill-full-sim`, `re1-chris-full-sim`) are written by
the route simulator from the same install (tools/routesim/README.md). After changing `tools/routesim` or its
`rules.json`, regenerate them and commit the result; CI can't (it has no game files), so run the check locally
before a release:

```powershell
python tools/routesim/build_routes.py "G:\GOG Games\Resident Evil"           # play both through, write the routes
python tools/routesim/build_routes.py "G:\GOG Games\Resident Evil" --check   # fails if routes.json is stale
python tools/routesim/plan.py "G:\GOG Games\Resident Evil" chris --verbose   # one playthrough, every move
```

## Voice transcripts (local research)

`tools/voice/transcribe.py` transcribes the game's English voice-over (`USA\Voice`, 563 WAVs) and optionally its videos
with Whisper. Output goes to `%LOCALAPPDATA%\Lockpick\research\voice\` and stays there: transcripts are the game's
script and are never committed. Set up once (ffmpeg must be on PATH; `PIP_CONFIG_FILE=nul` (lowercase) keeps machine-wide pip
settings such as extra package mirrors out of the way):

```powershell
python -m venv $env:LOCALAPPDATA\Lockpick\research\.venv
$py = "$env:LOCALAPPDATA\Lockpick\research\.venv\Scripts\python.exe"
$env:PIP_CONFIG_FILE = "nul"
& $py -m pip install torch --index-url https://download.pytorch.org/whl/cu128   # CUDA 12.8 (RTX 50xx)
& $py -m pip install openai-whisper
& $py tools/voice/transcribe.py "G:\GOG Games\Resident Evil" [--movies]
```

## Golden files

`tests/Lockpick.Tests/Fixtures/region-log-sample.bin` pins the memory-log format between the C# writer and the Python
analyzer. After an intentional format change: `LOCKPICK_UPDATE_GOLDEN=1 dotnet test --filter RegionLogTests`, then bump
the format version and keep the analyzer able to read old captures.

## Coding conventions

- C# latest, nullable enabled, file-scoped namespaces, `_camelCase` private fields, warnings as errors
  with the `latest-recommended` analyzer set (`Directory.Build.props`).
- Central package versions in `Directory.Packages.props`; pin exact versions.
- Game-specific code and offsets live only in `Lockpick.GameAdapter.<Game>` and its profile JSON.
- Memory access goes through `IProcessMemory` typed helpers (`TryReadU8/I16/U32…`) — no raw pointer
  arithmetic scattered through code; reads are confined by `ValidatedMemory`.
- Normalized state uses `Field<T>` (Valid / Stale / Unavailable / Unsupported). Events only fire on
  Valid→Valid transitions.
- Background threads never touch UI objects; the UI marshals via `LiveState`.
- No game binaries or assets in the repository (CI enforces this).

## CI and automation

| Workflow | Trigger | What it does |
|---|---|---|
| `ci.yml` | push to main, PRs | Release build (warnings as errors), all tests, test results artifact; profile JSON validation; check no game binaries are tracked |
| `codeql.yml` | push, PRs, weekly | CodeQL security analysis for C# (skipped while the repository is private) |
| `benchmarks.yml` | manual, weekly | BenchmarkDotNet short run, results artifact |
| `release.yml` | tag `v*.*.*` or manual | Test, publish the app, Velopack `pack` (Setup.exe, portable zip, update packages + feed, deltas against the previous release), `upload github` with CHANGELOG notes; attaches the probe zip |
| `dependabot.yml` | weekly | NuGet and GitHub Actions updates |
| `pr-guard.yml` | outside PRs (`pull_request_target`) | Closes and labels `spam` pull requests asking for payment; reads only the title and description, never the PR's code |

Repository protection (GitHub settings): the **Protect main** ruleset blocks deleting or force-pushing `main` and
requires a pull request with both CI jobs passing and a code-owner review (`.github/CODEOWNERS`) — repository admins
bypass it, so the maintainer can push directly. Fork pull requests need approval before CI runs (all outside
contributors). Secret scanning with push protection and Dependabot security updates are on.

## Release process

1. Confirm every acceptance criterion for the version in `docs/ROADMAP.md` (game-specific ones live).
2. Move `## [Unreleased]` entries in `CHANGELOG.md` under `## [x.y.z] - YYYY-MM-DD` (these become the release notes
   shown on GitHub).
3. Bump `<Version>` in `Directory.Build.props`.
4. Commit, then `git tag vx.y.z && git push origin vx.y.z`. `release.yml` builds and publishes.
5. Plain versions (`0.1.1`) are normal releases that installed copies update to. Versions with a suffix
   (`0.2.0-beta.1`) are GitHub pre-releases, offered only to users who enable *Include pre-release versions*.

Updating: the app uses Velopack's `GithubSource`; keep the `Velopack` package version and the `vpk` tool version
(`VPK_VERSION` in `release.yml`) identical. Test packaging locally with:

```powershell
dotnet tool install -g vpk --version 1.2.161
dotnet publish src/Lockpick.App -c Release -r win-x64 --self-contained true -p:Version=0.0.1 -o publish
vpk pack --packId Lockpick --packVersion 0.0.1 --packDir publish --mainExe Lockpick.exe -o Releases
```

The app's settings record `setupVersion`; bump `SetupViewModel.CurrentVersion` when first-time setup gains steps
that existing users should see.
