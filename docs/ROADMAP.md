# Lockpick Roadmap

Each release must be functional, tested and a meaningful improvement. A release is **not** advanced
because its code compiles: game-specific acceptance criteria require validation against the real GOG
executable, and unverified work stays marked as such.

Status legend: ✅ done and verified · 🟡 done, partially verified · ⬜ not started · ⛔ blocked

| Version | Milestone | Status |
|---|---|---|
| 0.1.0 | Game detection, memory reading, application foundation | ✅ released 2026-10-12 |
| 0.1.1 | Installer + auto-update, first-time setup, in-app capture (Debug page) | 🟡 built and tested; first Velopack release |
| 0.2.0 | Core trainer functionality and memory modification | ⬜ |
| 0.3.0 | Real-time overlay and customizable HUD | ⬜ |
| 0.4.0 | Room, camera, item and enemy database | ⬜ |
| 0.5.0 | Object highlighting and ESP | ⬜ |
| 0.6.0 | Advanced trainer and gameplay manipulation | ⬜ |
| 0.7.0 | Contextual warnings and route guidance | ⬜ |
| 0.8.0 | Speedrunning and challenge-run suite | ⬜ |
| 0.9.0 | Optional AI, optimization, compatibility testing | ⬜ |
| 1.0.0 | Stable RE1 release | ⬜ |

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
| 6 | Verified memory values are readable and correctly displayed | ✅ | Stage, room, camera, HP, max HP, character, timer, position, main-menu flag observed live in Chris and Jill games (Jill: id 1, max HP 96, Jill RDT bit). Rebecca id remains static-only (documented). |
| 7 | Game-state changes update the interface | ✅ | Live: camera cuts, door transitions, damage, death, return to title, restart |
| 8 | The application safely handles game termination | ✅ | Live: game closed → `Detached: Game process exited`; relaunch → re-attached automatically |
| 9 | Unsupported builds are rejected | ✅ | Hash mismatch never opens the process (unit tests); elevated game reports a clear access message (observed live) |
| 10 | Mock adapter tests pass | ✅ | 70+ tests incl. mock adapter, fake memory, live-trace fixture playback, Win32 integration |
| 11 | Known limitations are documented | ✅ | README, COMPATIBILITY, this file |

**Release decision:** all criteria met; tagged `v0.1.0` on 2026-10-12 after a live Jill session confirmed
character id 1, max HP 96 and the Jill RDT bit.

### Follow-ups (0.1.x, non-blocking)

- [x] Live-verify Jill (character id 1, max HP 96, Jill RDT bit).
- [ ] Live-verify the Rebecca partner swap (character id 3).
- [x] Live-verify loading a save from the title screen (session rule holds; see RESEARCH.md §7).
- [ ] Push to GitHub so `release.yml` publishes the tagged build.

---

## 0.2.0 — Core trainer (next)

Goal: deliberate, validated memory modification in Trainer Mode, with Challenge Mode enforcing no writes.

Acceptance criteria:
- Separate write-capable handle requested only when Trainer Mode is enabled (`PROCESS_VM_WRITE | PROCESS_VM_OPERATION`); Challenge Mode never opens one.
- Every write goes through a typed, profile-declared writable field with bounds (e.g. HP 1..maxHp) and is read back to confirm.
- Trainer action log (persisted) with timestamp, field, old → new value.
- Features: set/lock HP, infinite ammo, inventory viewer/editor (8 slots, id + quantity at `0xBE9944`), item box viewer.
  Inventory layout, ammo semantics and equipped slot are already runtime-verified (read side); the item box is not.
- Item id table (from biorand, MIT, with attribution) verified against the in-game inventory.
- Hotkeys (global, configurable) with no effect when the game is not attached.
- Run record notes whether any write occurred (`RunInvalidated` event).
- All writable addresses runtime-verified on the GOG build; tests with fake memory for every write path.

## 0.3.0 — Overlay and HUD

- Transparent, click-through overlay window tracking the game window (position, size, DPI).
- Game-window placement helper (move/resize the game window to a chosen monitor/position).
- Configurable HUD widgets (HP, room, timer, events) with < 2% game frame-time impact (measured).
- Investigate rendering while the game is unfocused (the game blacks out when inactive): requires an
  opt-in in-process patch; must be isolated, reversible and never used in Challenge Mode.

## 0.4.0 — World database

- RDT parser for the user's own installation (no assets redistributed): cameras, doors (`door_aot_set`),
  items (`item_aot_set`), enemies (`SceEmSet`).
- Room/camera names database in `data/games/re1/` (JSON), keyed by room id.
- Enemy array reader (30 × 0x18C at `0xBE6464`) runtime-verified.

## 0.5.0 — Highlighting and ESP

- World-to-screen projection using RDT camera data; overlay markers for items, doors, enemies.

## 0.6.0 — Advanced trainer

- Teleport, movement speed, enemy freeze/health, event-flag editor, game speed.

## 0.7.0 — Contextual guidance

- Missed-item warnings, puzzle-state tracking, route guidance, warnings under 100 ms.

## Event candidates found in live traces

- ~~`GameLoaded` / new game~~ — implemented (timer discontinuities), tested against both live fixtures.
- `ItemCollected` / `ItemUsed`: inventory slot diffs (runtime-verified layout) — 0.2.
- `PlayerDied`: `stateFlags & 0x01000000` with HP ≤ 0 — needs a gated raw-field path.

## 0.8.0 — Speedrun and challenges

- Run timer, LiveSplit integration, autosplits, no-damage/no-heal/knife-only challenges, PB tracking,
  trainer-modification detection (records, not anti-cheat claims).

## 0.9.0 — Optional AI, performance, compatibility

- Optional ONNX inference (< 200 ms, CPU fallback) only where deterministic data is unavailable.
- Additional builds (other GOG revisions, languages) as fingerprinted profiles.

## 1.0.0 — Stable RE1

- All features verified on supported builds; documentation complete; signed releases.

---

## Backlog / ideas

- Lockpick window: remember size/position per monitor; "compact" always-on-top mode for a side screen.
- Diagnostics: one-click "export support bundle" (log + recording + fingerprint, no game files).
- Investigate why the game window cannot be dragged (window style `0x14C80000` has a caption; the 1997 window procedure likely swallows the move).
