# Architecture

## Overview

```text
                      ┌──────────────────────────── Lockpick.App (Avalonia) ───────────────────────────┐
                      │  MainWindow ─ Dashboard / Debug / Settings / Setup    LiveState (UI-thread view) │
                      └──────────────▲───────────────────────────────────────────────▲─────────────────┘
                                     │ StatusChanged / SnapshotChanged / EventRaised  │ (Dispatcher.Post, coalesced)
                      ┌──────────────┴──────────── IGameStateSource ─────────────────┴─────────────────┐
                      │  GameMonitor (live, background thread)          ReplaySource (recordings)        │
                      └───────┬──────────────────────┬───────────────────────┬─────────────────────────┘
         IProcessHost         │   IFingerprintProvider│          IGameAdapter │  EventDetector
 ┌────────────────────────────┴───┐ ┌─────────────────┴──┐ ┌──────────────────┴───────────────────────┐
 │ Lockpick.Platform.Windows       │ │ FileFingerprint-   │ │ Lockpick.GameAdapter.RE1                 │
 │  Win32ProcessHost (discovery)   │ │ Provider (SHA-256  │ │  Re1Adapter: profiles, Identify, Attach  │
 │  Win32AttachedProcess (RPM)     │ │ + PE header, cache)│ │  Re1Session: raw fields → GameSnapshot   │
 │  HighResolutionWaitStrategy     │ └────────────────────┘ │  profiles/*.json (embedded from data/)   │
 │  GogInstallLocator              │                        └──────────────────────────────────────────┘
 └─────────────────────────────────┘
                     Lockpick.Core: state model, events, adapter contract, profiles, memory, monitor, recording, logging
```

Projects:

| Project | Responsibility | Depends on |
|---|---|---|
| `Lockpick.Core` | Game-agnostic model (`GameSnapshot`, `Field<T>`), events + detector, adapter contract, version profiles (load/validate/match/verify), memory abstractions, `GameMonitor`, recording/replay, JSON-lines logging | M.E.Logging.Abstractions |
| `Lockpick.Platform.Windows` | Win32 process discovery, read-only handles, `ReadProcessMemory`, high-resolution waits, GOG registry lookup | Core |
| `Lockpick.GameAdapter.RE1` | RE1 build profiles and interpretation of raw values. **The only place that knows RE1 addresses.** | Core |
| `Lockpick.App` | Avalonia desktop UI and composition root; first-time setup; Velopack self-update; Debug page with capture | all of the above |
| `tools/Lockpick.Probe` | Headless identify / watch / trace / record | Core, Platform, RE1 |
| `tests/Lockpick.Tests` | Unit, fixture playback, Win32 integration | Core, Platform, RE1 |
| `benchmarks/Lockpick.Benchmarks` | BenchmarkDotNet suites | Core, Platform, RE1 |

Projects from the original proposal that have no concrete responsibility yet (Trainer, Overlay, World,
Routes, Challenges, AI, Native) are intentionally **not** created; they arrive with their milestone.

## Data flow

1. **Scan** (1 Hz while detached): `IProcessHost.FindProcesses(adapter.ExecutableNames)` → for each
   candidate, fingerprint the executable on disk (SHA-256 + PE header, cached by path/size/mtime) →
   `adapter.Identify` (exact SHA-256 + size match against profiles).
2. **Unsupported** builds stop here: the process is never opened. Status explains why (unknown hash,
   modified file with a known timestamp, wrong architecture, access denied/elevated).
3. **Attach**: open a read-only handle → `ImageVerifier` checks the module base, size, the in-memory PE
   timestamp/size and every code anchor (instruction bytes that reference the profile's globals) →
   `IGameSession` created over `ValidatedMemory` (reads outside the image are rejected).
4. **Poll** (30 Hz, high-resolution waitable timer): `session.Read` → `GameSnapshot` → `EventDetector`
   → events. `SnapshotChanged` is raised only when game-facing state changes.
5. **Detach** on process exit (`WaitForSingleObject(handle, 0)`), on 30 consecutive fully failed polls,
   or on an unexpected exception; scanning resumes, so a restarted game is re-attached automatically.

## State model

`GameSnapshot` holds `Field<T>` values: `InGameplay` (game in session), `Character`, `Health` (current
and max), `Location` (stage + room), `Camera`, `Position`, `GameTime`, plus `RawFields` for diagnostics.

`FieldStatus`:
- **Valid** — read this poll and passed sanity checks.
- **Stale** — the read failed; the value is the last good one.
- **Unavailable** — supported but not meaningful now (title screen, dead, out-of-range value, failed read with no history).
- **Unsupported** — the active profile has no trusted source for it.

## Events

`GameStarted`/`GameStopped` (attach/detach), `GameLoaded` (new game or save loaded), `RoomChanged`,
`CameraChanged`, `PlayerHealthChanged`, `PlayerDamaged`. Rules (all derived from live RE1 observations):
- Only Valid→Valid transitions produce events.
- `CameraChanged` is suppressed after a room change until the in-game timer advances again: RE1 updates
  the room id before the camera id and assigns the new camera during the door animation with the timer frozen.
- `PlayerDamaged` requires the same room in both snapshots (a load is not damage).
- `GameLoaded` fires when the in-game timer restarts: below 2 s = new game, otherwise a save. Backwards jumps and
  forward jumps larger than elapsed real time count; the last timer is kept across title gaps so the load-file
  screen (still showing the old timer) does not fire. Attaching mid-game never fires.

Events listed in the product brief but not yet reliably derivable (`ItemCollected`,
`ItemUsed`, `EnemySpawned`, `EnemyDied`, `DoorEntered`, `TrainerActionExecuted`, `RunInvalidated`) are
not emitted yet; they arrive with the milestone that verifies their data.

## Threading

- One dedicated background thread per monitor (`AboveNormal` priority) runs scan/poll.
- Events are raised on that thread. `LiveState` queues them and applies them in one dispatcher pass;
  snapshots are coalesced (latest wins), so a slow UI never backs up the monitor.
- The logger never blocks callers: entries go to a bounded queue drained by a writer thread.

## Safety

- Read-only access rights only (`PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE`).
- Exact build identification before opening the process; runtime image verification before reading.
- Reads confined to the verified image range; profile validation rejects addresses outside the image.
- Lockpick never modifies game files and runs `asInvoker` (no elevation).

## Architecture decision records

**ADR-001 — .NET 10 + Avalonia 12.** The brief's preferred stack. .NET 10 SDK was installed for this
project (it initially had only .NET 6/8). Avalonia 12.1.4 supports net8+/net10.

**ADR-002 — No native component in 0.1.** Reading state needs only `ReadProcessMemory`, which P/Invoke
(`LibraryImport`, source-generated) handles with no measurable overhead (≈0.36 µs per read). CMake/C++
will be introduced only for in-process features (e.g. render hooks for the overlay or background-render
patch) where an external process cannot do the job.

**ADR-003 — Profiles are data.** Build-specific addresses, anchors and evidence live in JSON
(`data/games/<game>/profiles/`), embedded at build time and overridable from `%LOCALAPPDATA%`. Every
trusted field must carry evidence; CI and tests validate profiles.

**ADR-004 — Exact fingerprinting.** Builds are identified by SHA-256 + size. The RE1 autosplitter's
module-size check would have mis-identified this GOG revision (0x49B3000 vs 0x49B2000), which confirms
size checks are insufficient.

**ADR-005 — Code anchors.** Each profile lists instruction bytes that reference its globals. Matching
them in the live process proves the running code uses the addresses we read (guards against patched or
wrapped executables with identical file hashes, e.g. runtime patchers).

**ADR-006 — Session gate.** The decomp labels `stateFlags & 0x02000000` as "gameplay active"; live traces
refuted it (it is set only around game start and door transitions). RE1 "in game" is instead
`mainMenu == 0 && maxHp > 0 && hp > 0`, each observed live. Values outside a session are Unavailable.

**ADR-007 — High-resolution timer.** `Task.Delay(33 ms)` polls at ~21 Hz on Windows (15.6 ms
quantization). A `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` timer gives 29.9 Hz measured without changing
the global timer resolution.

**ADR-008 — Minimal logging dependency.** A ~150-line JSON-lines provider for Microsoft.Extensions.Logging
instead of Serilog: buffered, size-capped, non-blocking, with an in-memory ring for the Debug page.

**ADR-010 — Velopack for installs and updates.** Matches the owner's other app (Tauri updater: GitHub-release
feed, signed packages, passive install, restart). Velopack gives the same for .NET: per-user `Setup.exe`, delta
updates, a `releases.win.json` feed on GitHub releases, and `UpdateManager` for check/download/apply-and-restart.
Checks run 20 s after start and every 6 h, failures are shown (not swallowed), and updates install only on click.
Not yet code-signed (SmartScreen warns); `vpk pack --signParams` or Azure Trusted Signing can be added later.

**ADR-011 — Explicit first-time setup.** Setup completion is stored as `setupVersion` in settings (not inferred
from data), so it never re-triggers by accident and new steps can be shown to existing users by bumping the version.

**ADR-012 — Capture is part of the app.** Research capture runs on the same `IGameStateSource` as the UI through a
per-poll `Polled` event (raw fields change without normalized state changing, e.g. ammo). `CaptureSession` writes
per-attachment trace/recording/event files and zips them; there is no separate tool.

**ADR-009 — Responsive UI.** The window switches to an icon-rail + single-column layout below 760 px
(window) / 780 px (dashboard content), for portrait monitors and side-by-side use with the game.
Icons are Lucide (ISC) path data rendered with Lucide's stroke style.
