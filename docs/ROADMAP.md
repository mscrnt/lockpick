# Lockpick Roadmap

Each release must be functional, tested and a meaningful improvement. A release is **not** advanced
because its code compiles: game-specific acceptance criteria require validation against the real GOG
executable, and unverified work stays marked as such.

Status legend: ✅ done and verified · 🟡 done, partially verified · ⬜ not started · ⛔ blocked

| Version | Milestone | Status |
|---|---|---|
| 0.1.0 | Game detection, memory reading, application foundation | ✅ released 2026-10-12 |
| 0.1.1 | Installer + auto-update, first-time setup, in-app capture (Debug page) | ✅ released 2026-10-13 |
| 0.1.2 | In-app credits, capture bounties, community setup | ✅ released 2026-10-13 (first update path 0.1.1 → 0.1.2) |
| 0.1.3 | Bounty automation, capture fixes from live testing (compact recordings, mid-session start) | ✅ released 2026-10-13 |
| 0.1.4 | Captures record the game-state memory; enemy and flag detection; grouped reads | ✅ released 2026-10-13 |
| 0.1.5 | Research tooling: capture markers, ask mode, room index, capture timeline, subtitles pack | ✅ released 2026-10-13 |
| 0.1.6 | Progress tracker (read-only), Items and Map pages, controller capture markers | ✅ released 2026-10-15 |
| 0.2.0 | Core trainer (Trainer Mode, HP/ammo/inventory, hotkeys), Challenges tab, API for other tools, branding | ✅ released 2026-10-15 |
| 0.3.0 | Play and stream with one app: overlay, runs (timer, splits, LiveSplit), OBS sources, training captures | 🟡 released 2026-10-16 (live checks pending) |
| 0.3.1 | Every PC version runners use: the Japanese game with Classic REbirth, GOG's Japanese release (Steam and emulators to follow) | ✅ released 2026-10-17 (Steam and EMU next) |
| 0.3.2 | OBS WebSocket automation and keep-running (and Steam's English and Japanese releases) | ✅ released 2026-10-26 (keep-running on GOG English; other builds after their live checks) |
| 0.4.0 | Room, camera, item and enemy database | ⬜ |
| 0.5.0 | Object highlighting and ESP | ⬜ |
| 0.6.0 | Advanced trainer and gameplay manipulation | ⬜ |
| 0.7.0 | Contextual warnings and route guidance | ⬜ |
| 0.8.0 | Speedrun suite: PB tracking, run review, community segment templates | ⬜ |
| 0.9.0 | Optional AI, optimization, compatibility testing | ⬜ |
| 1.0.0 | Stable RE1 release | ⬜ |

Game-specific work depends on live captures; what each release still needs captured is tracked in
[CAPTURE_BOUNTIES.md](CAPTURE_BOUNTIES.md).

RE2 and RE3 adapters are deliberately out of scope until RE1 reaches 1.0, but the adapter contract
(`IGameAdapter`, profile JSON) is designed so they can be added without restructuring.

---

## 0.1.0 — Foundation (done)

Goal: identify Resident Evil 1 (GOG), attach safely and read-only, expose live verified game state.

### Definition of done

| # | Criterion | Status | Evidence |
|---|---|---|---|
| 1 | The application builds successfully | ✅ | `dotnet build -c Release` with warnings-as-errors; CI workflow `ci.yml` |
| 2 | The desktop interface launches | ✅ | Launched and screenshotted on the dev machine (wide and portrait layouts) |
| 3 | Game detection works | ✅ | Running `ResidentEvil.exe` found by name and full path; GOG install located via registry |
| 4 | Executable fingerprinting works | ✅ | SHA-256 + PE header of the real GOG exe matched profile `re1-gog-usa-hotfix4` |
| 5 | Process attachment is safe | ✅ | Read-only handle (`PROCESS_VM_READ`, query, synchronize); in-memory PE header and 9 code anchors verified before any read; reads confined to the image range |
| 6 | Verified memory values are readable and correctly displayed | ✅ | Stage, room, camera, HP, max HP, character, timer, position, main-menu flag observed live in Chris and Jill games (Jill: id 1, max HP 96, Jill RDT bit). Rebecca live 2026-10-15 (B007: id 3, max HP 88, inventory pointer 0xBE9950, shown as Rebecca 88/88 by the probe). |
| 7 | Game-state changes update the interface | ✅ | Live: camera cuts, door transitions, damage, death, return to title, restart |
| 8 | The application safely handles game termination | ✅ | Live: game closed → `Detached: Game process exited`; relaunch → re-attached automatically |
| 9 | Unsupported builds are rejected | ✅ | Hash mismatch never opens the process (unit tests); elevated game reports a clear access message (observed live) |
| 10 | Mock adapter tests pass | ✅ | 70+ tests incl. mock adapter, fake memory, live-trace fixture playback, Win32 integration |
| 11 | Known limitations are documented | ✅ | README, COMPATIBILITY, this file |

**Release decision:** all criteria met; tagged `v0.1.0` on 2026-10-12 after a live Jill session confirmed
character id 1, max HP 96 and the Jill RDT bit.

### Follow-ups (0.1.x, non-blocking)

- [x] Live-verify Jill (character id 1, max HP 96, Jill RDT bit).
- [x] Live-verify the Rebecca partner swap (character id 3) — 2026-10-15, B007 (from a Chris save at Plant 42).
- [x] Live-verify loading a save from the title screen (session rule holds; see RESEARCH.md §7).
- [x] Push to GitHub so `release.yml` publishes the tagged build (0.1.0–0.1.5 published by `release.yml`).
- [x] All 0.1.x capture bounties done (B001–B008; the last, Rebecca, 2026-10-15).

---

## 0.1.5 — Research tooling (released)

Built and used in live sessions 6–8 (2026-10-13); see CHANGELOG `[0.1.5]`.

- Capture markers: Ctrl+Shift+M (configurable) or the Debug page; notes saved in the capture.
- Ask mode: pauses the game and asks about events Lockpick can't explain; predicts rooms, maps, documents,
  room-script events and action-driven events, so it asks only about cutscenes, surprises and switch puzzles
  (session 8: 10 questions in ~90 minutes). ~85 story/puzzle events named so far (`data/games/re1/flag-labels.json`).
- Room index from the game's room files (`tools/rdt`): items, enemies, doors and story flags per room — the core of
  the 0.4.0 world database, as a research tool.
- Capture timeline (`tools/bounties/timeline.py`): rooms, pickups, documents, kills, attacks by name, pushes, item
  use, saves, markers and answers.
- Voice-over transcription (`tools/voice`) and the separate subtitles pack (data for 0.3.0).

---

## 0.1.6 — Progress tracker (released)

Pulled forward from 0.7.0 (owner's decision, 2026-10-13): read-only and Challenge-safe, and it needs no new memory
research — the room index, the flag banks and the named flags already hold everything.

Acceptance criteria:
- A Progress page: per area and per room, the items still there vs taken (room index + pickup flags), documents and
  maps collected, rooms explored (the map's own flags, all five stages), enemies killed for good (kill flags).
- Story progress from the named flags (what has happened, in game order), unnamed flags shown as "unnamed event".
- Character-aware (Jill's and Chris's room files differ) and correct for the mansion revisited (6xx/7xx).
- Updates live while playing; works from a loaded save (flags, not history).
- Verified live against a playthrough: counts match what the player saw; no writes, no extra process access.

**Live check 2026-10-15** (the owner's saves loaded from the title screen: Jill in the mansion revisited, 66 rooms
explored; Chris in the main hall): every area's items, documents and maps, rooms and kills matched an independent
reading of the same state (`tools/bounties/progress_preview.py` on a capture), and so did the story list (61 named,
11 unnamed). The page updated live when Jill entered a new room (courtyard rooms explored 4 → 5) and re-attached after
the game restarted. Found and fixed: items a story event places later counted as taken before their room was visited.
Read-only throughout (the app's handle has no write rights).

---

## 0.2.0 — Core trainer (released 2026-10-15)

Goal: deliberate, validated memory modification in Trainer Mode, with Challenge Mode enforcing no writes.

Status (2026-10-15): every criterion below is built and live-verified on the GOG build (issues #8–#12, #14, #16,
#17, #37), on top of the 0.2.0 capture bounties (B101–B114). The app also gained the Challenges tab (guided
challenges that fail on a hit or a non-knife shot), an API for other tools, branding, and layouts for wide,
ultrawide and portrait windows. The save tools moved to 0.6.0.

Acceptance criteria:
- **Done:** separate write-capable handle requested only when Trainer Mode is enabled (`PROCESS_VM_WRITE | PROCESS_VM_OPERATION`); Challenge Mode never opens one (live: the handle closes when a challenge starts).
- **Done:** every write goes through a typed, profile-declared writable field with bounds (e.g. HP 1..maxHp) and is read back to confirm.
- **Done:** trainer action log (persisted, `trainer/actions.jsonl`) with timestamp, field, old → new value and source.
- **Done (live 2026-10-15):** set/lock HP, infinite ammo, inventory editor — counts of what you carry, and any item
  put into the item box (the game loads it like a pickup when taken out; box entries 0–47 runtime-verified). Background
  for the design: inventory slots (8 slots, id + quantity at `0xBE9944`; the held-item count
  `0xBE9827` must follow, and grid icons load only on pickup, load, combine or an item-box swap — MEMORY_MAP).
  **Done (read side), 2026-10-14:** the Items page shows the inventory grid (8 or 6 slots, equipped weapon) and the
  item box (48 entries at `0xBE98E4`, runtime-verified).
- **Done 2026-10-14:** item id table (from biorand, MIT, with attribution) checked against the game's own item-name
  table for ids 1–77 (`data/games/re1/names.json`; mismatches corrected, generic names before examining noted).
- **Done (owner-verified live 2026-10-15):** hotkeys (global, configurable: fill HP, lock HP, infinite ammo) with no
  effect unless Trainer Mode has write access; also a GameSaved event from the save folder (#37, owner save to slot 4).
- **Done:** run record notes whether any write occurred (`RunInvalidated` event; a challenge fails if cheats go on).
- **Done:** all writable addresses runtime-verified on the GOG build; tests with fake memory for every write path.
- Moved to 0.6.0 (2026-10-15): the save tools (practice mode, save editor).

## 0.3.0 — Play and stream with one app

**Released 2026-10-16** (owner's call, for a tester). Still to confirm live after the release: chapter markers and a
replay-buffer save with the OBS script (#66), the new feed lines in play (#43), a training capture through
tools/rl/dataset.py (#61), frame pacing with OBS (B209), and code signing (#64, the owner's decision).

Owner's direction (2026-10-15): the first release for other players. A runner doing a speedrun, a no-damage run, a
knife-only run or a regular playthrough needs only Lockpick open — no second app for timing, splits or the stream —
and their sessions can be captured for behaviour-cloning research (they will be playing 0.3.0, so training captures
ship in it).

**What we know going in (2026-10-15):**
- Display research ([DISPLAY.md](DISPLAY.md)): Lockpick reads the game in every display mode; the game is
  per-monitor DPI aware and opens on the primary monitor; windowed/borderless overlays work and never take focus;
  exclusive fullscreen minimizes and stops on any focus change (an overlay there still needs an owner check); a
  vanilla game pauses and goes black when inactive, which blanks every OBS capture; Discord's toast covers the
  top-left corner.
- OBS 32.2.2 / WebSocket 5.7.4 on the owner's PC supports everything planned below (browser sources, recording,
  `CreateRecordChapter`, `SaveReplayBuffer`, scene and source switching, creating inputs).
- Already in the app: challenges (no damage, knife only, speedrun) with live rule checks, the API with a live event
  stream (`/api/v1/events/stream`), controller markers, captures, the room map, progress and the layouts.
- speedrun.com's PC rules (checked 2026-10-15 through its API): runs are timed by **in-game time**, start at the
  title screen and end on the end screen showing the final IGT; the options menu pauses IGT (so using it as a pause
  is banned); **tabbing out or clicking off the game is not allowed during a run**; no mods; LiveSplit with its IGT
  script must be used and IGT shown (autosplits optional). Categories: Jill / Chris, Bad / Best ending, No Major /
  Major Glitches, No Restriction / **Knife Only**.
- OBS's browser source exposes `window.obsstudio` (obs-browser 2.26.9 in OBS 32.2.2, verified 2026-10-15): a page
  with `webpage_control_level` 4 read the status and current scene, got scene-change events and a live event
  stream; level 4 allows switching scenes and saving the replay buffer, 5 starting/stopping recording.
- Known in memory: in-game clock `0xD22730` (frames at 30/s; zeroed on New Game, restored on load — the docs disagree
  whether it runs in menus: bounty B206), player dead (`0x01000000` in the state flags, HP below 0), the results
  screen (reads the saved clock), named story flags, item pickups and rooms (split triggers), the input words
  `0xBE9840`/`0xBE9842` and the raw pad word `0xBF0A04`.

**Owner decisions (2026-10-15):** challenges are made for streaming and recording — nothing during a run needs a
click outside the game (speedrun.com bans it anyway): hotkeys and the controller for Lockpick, OBS reacting by
itself through an **OBS script shipped with Lockpick**; the timer defaults to the game's clock (matches
speedrun.com); Lockpick may switch the game to borderless by editing `dxcfg.ini` on the player's click; code signing
is decided later. Keep-running stays out of challenges (it patches the game, and runners can't click off anyway).

**Sprints.** 0.3.0 ships after sprints 1–6; the keep-running option and OBS WebSocket control follow as 0.3.2 (0.3.1 became the PC versions release).

1. **Responsive app — done 2026-10-15:** every page at ultrawide (full, half, third, quarter), 1080p, 720p, 1024×768,
   800×600, portrait (720×1280 at 150 %) and the 150 % 1080p monitor (full to quarter); content scrolls instead of
   clipping; the map keeps the space. **Pages in sections (owner, 2026-10-16: the pages were getting cluttered):** each page's sections
   under it in the navigation, one shown at a time (a row above the page in the compact layout).
2. **Overlay** (spec: DISPLAY.md requirements). **Foundation done 2026-10-15 (#54):** follows the game window
   (100 % and 150 % monitors, moved, minimized), sits on the 4:3 picture from `dxcfg.ini`, click-through and
   never activated (verified: a click on the panel reaches the game, the game keeps focus), directly above the game
   (other windows cover it), owner-verified on the 150 % monitor and over exclusive fullscreen (B202); drawn by a
   plain layered window the size of the panel; widgets: in-game time, HP bar, room, challenge state (clean / failed
   with the reason); corner setting.
   Show/hide hotkey (Settings → Hotkeys). Frame pacing measured: the game's clock ran 30.25 ticks/s with the
   overlay up and with Lockpick closed (no cost). **Widgets and the map done 2026-10-15:** each widget is its own
   layered window with its own corner (or off); widgets sharing a corner stack. The map widget is a framed,
   north-up minimap around the player; its hotkey cycles through the views the player ticks (minimap, the whole room
   over the picture on a dark backdrop, the same see-through, hidden), with the minimap's reach and size, the
   see-through strength and names on the full-screen map as options; the time panel stays above a full-screen map
   (verified live in 105). **Floor map done 2026-10-16:** rooms placed from their doors (FloorLayout, the
   tools/rdt/floor_map.py method: door zone centre = arrival spot, least squares), on the Map page with pan and zoom
   and as overlay views (whole floor, see-through; a zoom key follows the player); stage 1 checked by eye against
   the game's own layout. HP band, poison and enemy HP done (#42). Event feed (#43) first pass done: the change
   explainer's plain words in the event log and an overlay widget (attackers by name and zombies getting back up
   still to come). Big messages (#59, first part): challenge failed with the reason, a death, a run finished or a
   new personal best, centred at the top. **Layout editor done 2026-10-16 (#55):** drag widgets on a preview of the
   picture to any spot (kept as a share of the picture); the overlay and the preview place widgets with the same
   code. Lockpick no longer takes the focus over exclusive fullscreen (the ask-mode question shows without it).
   - **Leaderboard runs — done 2026-10-16:** trainer locked read-only, ask mode off, the install hashed against
     GOG's release (no mods), the rules only the player can keep as reminders, the result on each attempt's record;
   - **Widgets off the game too — done 2026-10-16** (owner: the overlays start to cover the picture): one splits
     widget, drawn by livesplit-core (LiveSplit's look) with the world record, instead of two; every widget also as
     a **pop-out window** (movable, resizable, on any monitor, plain or green background for OBS's window capture,
     always on top if wanted, place and open state remembered) and on a **Stream page** in the app — a dashboard of
     the widgets the player picks, sizes and orders; the overlay, pop-outs and Stream page all draw the same widgets
     (one renderer), so each can be used alone. Checked in a replay: map, splits and status in pop-outs and tiles,
     reopened after a restart;
   - follow the game window (PID → class `RESIDENT EVIL`): client rectangle in physical pixels, monitor, DPI,
     minimized, exclusive fullscreen; move/size events;
   - a click-through, topmost, non-activating overlay window over the 4:3 picture (computed from `dxcfg.ini`'s
     `aspect`/`scaling`); hidden while the game is minimized; Lockpick never activates its own windows while the
     game is exclusive fullscreen (the ask-mode question waits or shows without activation);
   - widgets: run timer, splits, HP band and poison, challenge status (a fail banner with the reason), room name,
     enemy HP, the event feed (#43); positioned against the picture, presets for each corner (not top-left by
     default), a layout editor on a preview, toggled by a hotkey or a controller button;
   - exclusive fullscreen: works (owner-verified 2026-10-15 at desktop resolution);
   - game display helper (#20) — **done 2026-10-15:** Settings → Game display: windowed / borderless / fullscreen,
     resolution (the main monitor's own list), picture scaling, vsync, gamma, filtering, antialiasing (every dxcfg
     option, values as GOG's dxcfg.exe writes them); a two-click Restart Resident Evil next to Apply; `dxcfg.ini`
     written only on Apply, the original kept
     in Lockpick's data folder and restorable; a warning when fullscreen would change the monitor's resolution; move
     the game window to another monitor (verified live both ways);
   - budget: the overlay costs < 2 % of the game's frame pacing (measured from the in-game clock).
3. **Runs:** **First pass done 2026-10-16 (#56, #57; docs/RUNS.md):** run sessions (new game → ending, death, title,
   game closed, reset; optional start on a save load), in-game time from the game's clock and real time, split
   triggers (room from a room or another stage, item obtained, the ending, split key), the community's 8 segment
   sets and 2 key-item routes built in (unverified: B701/B702/B703), `.lss` import (triggers matched by name against
   the best-fitting built-in set) and export with the player's times and history, records per attempt, PB and best
   segments, a Runs page, run hotkeys and a splits overlay widget — live: a new Jill game started an attempt, the
   Emblem split at 14.9 s, reset kept the record. **Then:** the run API and OBS splits page (#60), LiveSplit
   follow and drive modes over LiveSplit's TCP server (#58; tested against a stand-in server, LiveSplit itself is
   B208). **Split editor, runs from a save at a chosen split, the wrong-splits warning — done 2026-10-16.** Next: `.lss` import of the community files checked by a player.
   - run sessions: start on New Game (or a loaded save, marked as such), end on the ending's results screen
     (B703), a death / Game Over (B205), quitting to the title, or the game closing; a local record per attempt
     (category, character, in-game time, splits, deaths, damage taken, result, whether cheats were on);
   - a "Playthrough" mode (no rules: timer, splits and progress only) next to the challenges;
   - timer from the game's own clock (the speedrun.com timing; how it behaves in menus — B206), real time as an
     option; a run starts at the title screen, as on the leaderboard;
   - splits: built-in RE1 sets — the community segments (Yawn 1 › Mansion 1 › … › End) and the key-item routes
     (Jill / Chris NMG) — triggered by named story flags, item pickups and rooms (B701, B702, B706); a split editor;
     comparison against the player's best (local);
   - challenge feedback: the fail reason in the overlay and an optional sound; a death counter; retry from a
     controller button — **done 2026-10-16 (#59)**, owner-tested live: no-damage fails on the first hit, the
     death counter, the fail message and the sound (a death doesn't end a speedrun challenge, the owner's call);
     sounds from Windows or the player's own .wav, one for a fail and one for a death;
   - **LiveSplit:** leaderboard runs must use LiveSplit's own IGT script, so by default Lockpick *follows* the
     runner's LiveSplit through LiveSplit Server (split index, times — for the overlay, markers and run records)
     and never splits twice; only with no autosplitter does Lockpick drive it (start, split, reset, set game
     time). Import a `.lss` file's segment names onto Lockpick's split triggers; a capture marker at each split
     (B208);
   - nothing during a run needs a click: retry, marker and overlay toggle from hotkeys or the controller; Lockpick
     shows no dialogs and never takes focus while a run is on (update prompts wait).
   - **Speedrun suite — Lockpick as the one stop from practice to submission** (owner's direction 2026-10-16):
     1. *speedrun.com* — **leaderboard and account done 2026-10-16** (board per split set, WR / top / your place,
        the API key kept with DPAPI and only sent to speedrun.com); **submission done 2026-10-16** (a finished clean
        attempt, board, IGT, date, video, the summary as comment; two presses; tested against a stand-in — the first
        real submission is the owner's). Next: splits.io upload with a submission.
     2. *livesplit-core* — **done 2026-10-16 (ADR-019, docs/LIVESPLIT_CORE.md):** built from a pinned submodule
        with Rust (6.3 MB), following Lockpick's run, kept as a LiveSplit file per split set, drawn as an overlay
        widget and an OBS page; CI builds it. **Comparisons done 2026-10-16:** LiveSplit's comparisons on the Runs
        page, the overlay and its layout; where the PB loses time against the best segments; practice and trainer
        attempts kept out of its history. Next: saving through it.
     3. *An RE1 autosplitter in livesplit-core's WebAssembly format* from Lockpick's verified addresses, run by
        Lockpick and offered to the community (LiveSplit loads it too) — alongside the community `.asl`.
     4. *Managed LiveSplit* for leaderboard runs (the rules require LiveSplit's `.asl` IGT script): Lockpick finds
        or (on request) downloads LiveSplit, starts its server, follows it, and matches its splits by
        `getcategoryname` / `getsplitname`.
     5. *LiveSplit One* driven by Lockpick through its server protocol (a WebSocket on Lockpick's API) for a
        browser or OBS timer with LiveSplit's look.
     6. *Training*: practice from saves (segment practice with per-segment bests), where time was lost per segment,
        the floor map and feed, captures; splits uploaded to splits.io with a submission.
4. **OBS (stream- and record-friendly challenges):** **Done 2026-10-16 except a check in OBS itself (#60, #66):**
   every widget as a browser source with Copy OBS link buttons (Stream → OBS), pop-out windows for Window Capture,
   and `lockpick.lua` (below).
   - the API serves browser-source pages — timer, splits, HP, challenge status, event feed, room map —
     transparent, live through the event stream, options in the URL; "Copy OBS link" buttons; a pop-out HUD
     window for window capture; our in-game overlay is not part of any capture, so these pages are the stream's
     HUD;
   - **`lockpick.lua`, an OBS script shipped with Lockpick — written 2026-10-16 (#66; checked against a stand-in for
     OBS's Lua API, not yet in OBS itself)** (Tools → Scripts; Lua needs nothing installed): finds
     Lockpick's API address and token itself, adds the Lockpick sources (and a Game Capture of the game) to a
     scene, and automates runs through OBS's own frontend API — start/stop recording with a run, chapter markers
     at splits, fails and deaths, save the replay buffer on a death, a fail or a new best, switch to a "paused"
     scene while the game is inactive or minimized and back. It reads Lockpick's events from a local file
     (OBS Lua has no network), so no WebSocket password is involved; browser pages that need OBS access get the
     lowest control level that works (4 for scenes and replays);
   - the docs recommend Game Capture (works in every mode, 640×480 native, no pillarbox).
5. **Training captures — built 2026-10-16 (#61; a live capture and the dataset check to go):** opt-in, every game frame (the in-game clock's ticks): state, the held and new input words
   and the raw pad word, control type, room and camera, enemies; a consent screen (what is recorded, nothing
   personal); submitted like play captures; checked by `tools/rl/dataset.py` and a small behaviour-cloning model
   (dev). Optional pairing with an OBS recording (its start time and chapters) for models that learn from video.
   Needs every input setup recorded (B207).
6. **Ready for other players:** **Done 2026-10-16 (#62, #63):** the player guide (docs/GUIDE.md), streamer mode, the
   support bundle and error reports, the window remembered per monitor setup. **Still open:** code signing (owner's
   decision) and the frame-pacing check with OBS (B209). The plan: a player guide (install, challenges, cheats, overlay, OBS, LiveSplit, captures);
   streamer mode (hides profile paths, process ids and the API token on screen); a one-click support bundle;
   unexpected errors logged with an offer to send the bundle; the Lockpick window remembers its size per monitor;
   code signing so Windows stops warning about the installer (owner decision: certificate or Azure Trusted Signing);
   OBS + Lockpick + the game together checked for frame pacing (B209).

### 0.3.1 — Every speedrun.com RE1 run

Owner's direction (2026-10-17): "one tool for all speedrun.com RE1 runs". The PC board recommends the Japanese
MediaKite game with Classic REbirth; GOG and Steam are allowed (docs/COMPATIBILITY.md has every board). Order:

1. ✅ **Japanese PC 1.01 (`Biohazard.exe`), with or without REbirth** (live-verified under REbirth 1.1.4 2026-10-17) — profile `re1-jpn-101` ported from the GOG one by
   static cross-mapping (`tools/re1/xmap.py`, `tools/re1/port_profile.py`; every address and anchor found in the
   1.01 exe's own code, MEMORY_MAP "Japanese PC 1.01"). Profiles now name their tables and pointers (`addresses`), so
   the session has no build's addresses in code. REbirth installs: saves in its save folder, room files in `JPN\`,
   display settings left to REbirth's setup. **Needs:** the Japanese data (GOG's Japanese installer, owner's
   account) to run it, then a probe trace to raise the fields to runtime and the trainer's writes back on; check that
   REbirth's in-memory patches leave the anchors alone.
2. 🟡 **Leaderboard checks for REbirth** — the exe and every REbirth release (1.0.0–1.1.4) by hash, its banned options
   (quickturn, tactical reload, debug) and cutscene skip from `config.ini`, `mod_*` folders. Done 2026-10-17: the
   Japanese file list (`gog-jpn-hotfix4.sha256`), the check following the game being played (shared folders), and the
   three real setups (GOG English, GOG Japanese, REbirth) checked against it.
3. 🟡 **GOG Japanese and Steam** — GOG Japanese hotfix4 (`re1-gog-jpn-hotfix4`): ported and live-verified 2026-10-17
   (a Chris game, probe trace; `port_profile.py --live` keeps the live evidence when it's regenerated); the trainer
   writes what the trace confirmed. Steam (app 4249100) English (`re1-steam-usa`): GOG's English exe in a wrapper,
   ported from the image read from the running game 2026-10-26 (`tools/re1/dump_image.py`; code identical to GOG's,
   every address the same), install found from Steam's libraries, leaderboard check (`steam.json`), display settings
   in the launcher's ini. Steam Japanese (`re1-steam-jpn`): GOG's Japanese exe wrapped, the same way.
4. ⬜ **EMU board** — read the PS1 game inside DuckStation (and BizHawk / PSXjin): find the console RAM in the
   emulator, identify the disc, a profile per PS1 version (Director's Cut first, the board's fast version). Needs a PS1
   disc image the owner owns for the research; console boards (PS, PSN, Saturn) stay out of reach.

### 0.3.2 — OBS automation and keep-running

- **OBS WebSocket (optional, for setups the script can't cover, e.g. OBS on another PC; the password kept with
  Windows DPAPI):** the same automation as `lockpick.lua` from Lockpick's side — scene setup, recording with runs,
  chapters (`CreateRecordChapter`), replay buffer, the "paused" scene. **Done 2026-10-26 (#65):** `ObsWebSocket`
  (obs-websocket 5 client: Hello / Identify with the challenge answer / requests) and `ObsLink` (the script's
  on_event, plus "Set up OBS": a Lockpick scene with the widgets and, for OBS on this PC, a Game Capture; for OBS on
  another PC the widgets use this PC's address and need the API open to the network). Stream → OBS → OBS WebSocket;
  "Fill in from OBS on this PC" reads OBS's own port and password. While it drives this PC's OBS, the script stands
  aside (`websocket` in connection.json) so nothing happens twice. Tests against a stand-in server (handshake,
  wrong password, recording, chapters, replay buffer, paused scene, set-up). **Live 2026-10-26 against OBS 32.2.2 /
  WebSocket 5.7.4 on the owner's PC** (ObsLink driven with the run's moments by hand): the wrong password refused,
  "Set up OBS" made the Lockpick scene from the script's sources, the recording started with the run and stopped 2 s
  after it ended, the "paused" scene and back, and the Hybrid MP4 held the chapters (Run, split, Death, end; ffprobe).
  Found there: StartRecord answers before the recording runs, so the run's chapter was refused — both Lockpick and
  the script now wait for it. **OBS on another PC, live 2026-10-26:** OBS 29.1.3 / WebSocket 5.2.3 in a container on
  the owner's server: seen as another PC, set-up refused while the API is this-PC-only and otherwise made the scene
  without a Game Capture, the widgets at this PC's LAN address; recording and the paused scene worked; that OBS has
  no chapters (no CreateRecordChapter before WebSocket 5.5) and no replay buffer, and each is noted once in the card
  while the rest goes on. Replay buffer, live here 2026-10-26: a death, a failed challenge and a new best each saved
  a replay (three files); a buffer turned on but not started is noted once in the card (the script stays silent).
  In the app, 2026-10-26: the owner connected from Stream → OBS to the local OBS and to the server's (password saved
  by Connect), and Set up OBS completed the scene on both; the server's browser sources reached Lockpick's API over
  the network (firewall allowed). **Not yet seen:** the widgets drawn in the other PC's OBS during gameplay, and the
  card's automation with real game events (a run, a split, a death) rather than moments sent by hand.
- **Keep the game running when it isn't the active window** (opt-in, Trainer Mode only, never in challenges; chosen
  by the owner 2026-10-13) — the alternative to the OBS "paused" scene for people who aren't running a challenge.
  **Done 2026-10-26 (#21), data-only instead of the code patch:** a trainer rule holds the game's focus flag at 1 and
  blanks its keyboard map while it is in the background, and puts the map back when it's in front and whenever the
  rule ends (switched off, a challenge or leaderboard run, Lockpick closing); rules can now restore and run outside
  gameplay (`anyTime` write bounds). Live on GOG English with the owner (MEMORY_MAP "Focus and input"): the game ran
  in the background with the picture moving and no sound, and A typed in Notepad didn't reach it. Not yet checked:
  a controller in the background through the app (the dev harness showed it 2026-10-14), and the other builds — the
  fields reached every port (static) and the switch says "not checked" there until each gets a live check.

Known HUD data (already understood, read-only) for sprint 2 and #42:
- HP status as the game rates it — Jill Fine 73–96 / Caution 25–48 / Danger 0–24, Chris 106 / 36 / 0
  thresholds (speedrun damage chart, matches the GOG build);
- poisoned indicator (status byte `0xBE63C0` bit `0x02`, Yawn's poison `0x20`; −2 HP each time the timer
  `0xBE6458` runs out, ~4 s) and the status screen's own HP band (`0xAE9F2F`) — runtime-verified 2026-10-14;
- enemy health in the current room (enemy slots: type, HP, active);
- what just happened: an event feed in plain words (picked up the Emblem, Zombie ankle bite (6), first visit to
  room 11D, Barry gives Jill the lockpick) — the capture timeline's logic in the app (#43);
- current room and area by name (areas from the game's room table; names as they are confirmed);
- room map — in the app since 2026-10-14 (`Re1RoomMaps`, `RoomMapControl`); next: script-placed objects (the
  dining-room clock) and the whole-floor view (tools/rdt/floor_map.py), then an overlay / browser-source version.

## Developer tooling (never shipped)

Automated play is for the owner's development and testing only — never in the app, its installer or a release:
players play the game themselves; Lockpick enhances their play.

- **Automated testing harness** (`tools/Lockpick.Autoplay` + `tools/autoplay`, 2026-10-14) — **working:**
  - virtual Xbox 360 controller (ViGEmBus); menus driven by a local vision model's screen labels; new games for
    either character;
  - **enter any room** through a real door record (the game's own door handoff, fade and arrival spot);
  - **fast-forward cutscenes** (tick interval; every effect kept); control detected from the state flags;
  - **stand at a spot** (e.g. an item's pickup zone) and **heal**; read-only `peek`; captures of whole runs;
  - `scene.py <character> <room>`: load a practice save at the right story stage, then enter the room — any part of
    the game with either character.
- **First overnight run (2026-10-14):** every door-reachable room entered from a new game for both characters (162
  of 165 door targets each; the other 3 are stub room files that crash the game), phase surveys from practice saves
  (guardhouse 35, return mansion 58, caves 17, lab 22 rooms — all entered, both characters; 588 room visits in all),
  and 75 saves loaded with Lockpick's live room/character matching the offline save decode (73 of 75; the other two,
  a pair of practice saves at the same spot in 513 with camera 5, crash the game on load even at normal speed). Results, captures and an atlas stay local (`%LOCALAPPDATA%\Lockpick\research\night-20261014`).
- **Item pickup verification (2026-10-14):** every armed item zone of a new game, both characters: 295 items picked up (Jill 138, Chris 157), each giving the item and clearing the flag the room index names; the only differences are 104/604's two clips stacked on one spot and 719's battery hand-over spot next to its rounds — both in the room scripts. Not-yet-placed items (story-gated), event hand-overs and unreachable spots are listed per room.
- Next: regression scripts (expected flags/items per scene, run after changes to addresses or event rules), item
  pickups at their zones (camera re-cut after placement), capture bounties that need precise actions, why the first
  door handoff after a save load is ignored.
- Unattended runs — **done 2026-10-14:** `--background` applies the focus patch inside the harness (dev runs only)
  and blanks the keyboard map; the game ran, read the virtual pad and rendered behind other windows. Memory input
  (`0x497BF7`) isn't needed for testing. During runs `SAVE/*.dat` are read-only and hash-checked after every step.
- Vision labels — **working 2026-10-14:** describes screenshots and labels menus; not reliable for reading the
  status screen (invents item lists), so item names were checked against the game's own name table instead.

## 0.4.0 — World database

- RDT parser for the user's own installation (no assets redistributed): cameras, doors (`door_aot_set`),
  items (`item_aot_set`), enemies (`SceEmSet`). **Done as a research tool** (`tools/rdt`, every retail room file,
  committed as `room-index.json` with ids and numbers only); still to do: port to C# for in-app use, and cameras.
- **Event-script parser** (RDT `offsets[8]`): cutscene scripts set the story flags room scripts don't, hand over items
  (Barry's lockpick, Richard's radio) and play voice lines — parsing them names events without asking and tells the
  subtitles which line plays when.
- Room/camera names database in `data/games/re1/` (JSON), keyed by room id — areas done (`rooms.json`), names so far
  for rooms identified in play; the rest via ask mode / community.
- Rooms-visited and map/document flags: done (the game's table at `0x4D31E0`; maps = item + 46, documents = item + 35).
- Enemy array reader (30 × 0x18C at `0xBE6464`) runtime-verified — types, HP and active flag behave as expected in
  every capture (static anchor still needed); most enemy types identified in play.
- On-screen text: which message is shown (pointer at `0xD226A4`, text buffer recorded in captures) and the room files'
  MSG section decoded locally — for a text log and examine/pickup messages.

## 0.5.0 — Highlighting and ESP

- World-to-screen projection using RDT camera data; overlay markers for items, doors, enemies.

## 0.6.0 — Advanced trainer

- Teleport, movement speed, enemy freeze/health, event-flag editor, game speed.
- The event-flag editor shows the named flags (story, puzzles, locks, items, rooms) instead of raw bits, with
  story-state presets ("start at the courtyard").
- Give or remove any item; put pickups back into the world (set their pickup bit), reset puzzles (clear their named
  flags), respawn enemies (clear kill flags), reset the map's explored rooms; teleport (room + position).
- Challenge setups (knife-only, no herbs, start with nothing) and a light item randomizer, built on the same writes.
- **Save tools** (moved from 0.2.0; write only save files, never the process; always a backup first and an exact
  restore, never in Challenge Mode): save reader (done as `tools/bounties/saves.py`: a save is a copy of the state
  block — offset + `0xBE9620`), a **scene library / practice mode** (put any practice save into a spare slot: Plant
  42, Lab Tyrant, Rebecca…), and a **save editor** (inventory, item box, story flags) once it is known whether saves
  carry a checksum.

## 0.7.0 — Contextual guidance

- Missed-item warnings, puzzle-state tracking, route guidance, warnings under 100 ms.
- **Progress tracker** (read-only, Challenge-safe — can ship before 0.7, it needs no new memory research): per room
  and area, items still there vs taken (room index + item flags), documents and maps collected, rooms explored (the
  map's own flags), enemies killed for good, and story/puzzle progress from the named flags.
- Timed events: countdowns the game keeps silently (Richard's serum errand, 15 min; later the self-destruct).
- Missed-item warning when leaving a room or area with items left (from the same data).

## Event candidates found in live traces

- ~~`GameLoaded` / new game~~ — implemented (timer discontinuities), tested against both live fixtures.
- Worked out in the research tools (timeline / ask mode), to move into the app's `EventDetector`: item picked up,
  put back, used, received, combined, loaded; document/map taken; first visit to a room; door/desk unlocked; enemy
  killed (kill flags) and getting back up; attack by name (damage per hit); poison ticks; object pushed; named story
  and puzzle events.
- `PlayerDied`: `stateFlags & 0x01000000` with HP ≤ 0 — needs a gated raw-field path.
- `GameSaved`: from the save files (#37); ink ribbon use is visible in the inventory (Chris).

## 0.8.0 — Speedrun and challenges

- Run timer, LiveSplit, autosplits, run records, deaths and the no-damage / knife-only rules move to 0.3.0 (their
  capture bounties B701–B704, B706 too). Here: PB history and run review, more challenge rules (no heal, no saves,
  deathless…), community segment templates, trainer-modification records (records, not anti-cheat claims).
- Voice-over subtitles (moved from 0.3.0): optional download of the separate
  [lockpick-subtitles](https://github.com/mscrnt/lockpick-subtitles) pack (off by default; checked against the
  player's voice files by SHA-256), shown in the overlay while a line plays. Needs: which voice file is playing.
- Autosplits from the named story flags and room transitions, matching the community's segment templates (Yawn 1,
  Mansion 1, Courtyard 1, … End — speedrun.com); in-game time from the game timer; the capture timeline as a run
  review (where time went, damage taken, items used).

## 0.9.0 — Optional AI, performance, compatibility

- Optional ONNX inference (< 200 ms, CPU fallback) only where deterministic data is unavailable.
- Additional builds (other GOG revisions, languages) as fingerprinted profiles.
- Community labelling: ask-mode answers and `auto` markers from submitted captures become suggested flag labels
  (reviewed before merging), like the bounty automation does for evidence.

## 1.0.0 — Stable RE1

- All features verified on supported builds; documentation complete; signed releases.

---

## Backlog / ideas

- "Compact" always-on-top mode for a side screen (remembering the window size per monitor is in 0.3.0 sprint 6).
- ~~Music track lead (`0xD226A0`)~~ — ruled out 2026-10-14 (a sound-state bitfield, not a track id; MEMORY_MAP Leads).
- Ask mode with a controller button instead of the keyboard (the owner plays with a DualSense).
- Investigate why the game window cannot be dragged (window style `0x14C80000` has a caption; the 1997 window procedure likely swallows the move).
