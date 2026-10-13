# LOCKPICK

*Master of Unlocking.*

Lockpick Trainer is a trainer and gameplay-intelligence platform for the original Resident Evil trilogy
on PC, starting with **Resident Evil (1996), GOG release**. It reads live game state, verifies exactly
which build it is talking to, and is designed to grow into a trainer, overlay, world explorer and
speedrun/challenge suite.

> **Status: 0.1.0 (foundation).** Detection, fingerprinting, safe read-only attachment and live state
> are working and validated against the real GOG game (Chris and Jill games). See [docs/ROADMAP.md](docs/ROADMAP.md).

## What works in 0.1

- Finds the running game and locates the GOG install via the registry.
- Fingerprints the executable (SHA-256 + PE header) and matches it to a version profile; unknown builds
  are rejected without opening the process.
- Attaches with a read-only handle after verifying the in-memory image and code anchors.
- Live values (30 Hz): session state, character, HP / max HP, stage + room, camera, position, in-game time.
- Typed events: attached/detached, room changed, camera changed, HP changed, damaged.
- Survives the game closing and restarting; explains access problems (e.g. elevated game).
- Diagnostics: raw profile fields with verification level, poll statistics, live log, session recording.
- Replay mode for recordings (`--replay`), and a headless probe tool for validation.
- Responsive UI with a compact layout for portrait / side-by-side windows.

Not yet: any memory writes (trainer features arrive in 0.2), overlay (0.3), world data (0.4).

## Supported builds

| Game | Build | Status |
|---|---|---|
| Resident Evil | GOG `ResidentEvil.exe` 1.0 hotfix4, SHA-256 `205BD6FE…4A78` | Supported |

Other builds are refused until a verified profile exists — see [docs/COMPATIBILITY.md](docs/COMPATIBILITY.md).

## Quick start

Requirements: Windows 10/11 x64, [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
git clone <repo-url> lockpick; cd lockpick
dotnet run --project src/Lockpick.App
```

Start the game normally (not from the GOG installer's elevated "Launch" button). Lockpick attaches
automatically.

```powershell
dotnet test tests/Lockpick.Tests                         # tests
dotnet run --project tools/Lockpick.Probe -- identify    # check your installed build
```

## Help out

Lockpick verifies everything against the real game. Recording a play session (any kind; speedruns are great)
helps the most: grab the **[capture kit](docs/CAPTURE.md)**, play, send the zip. Helpers are credited in
[CONTRIBUTORS.md](CONTRIBUTORS.md).

## Repository layout

```text
src/
  Lockpick.Core/               state model, events, adapter contract, profiles, monitor, recording, logging
  Lockpick.Platform.Windows/   Win32 process discovery and read-only memory access
  Lockpick.GameAdapter.RE1/    RE1 adapter (the only code that knows RE1 addresses)
  Lockpick.App/                Avalonia desktop app
data/games/re1/profiles/       version profiles (addresses, anchors, evidence) — source of truth
tools/Lockpick.Probe/          headless identify / watch / trace / record
tools/fixtures/                trace → recording fixture converter
tools/capture-kit/             volunteer capture kit (build script + README)
tests/Lockpick.Tests/          unit, fixture playback and Win32 integration tests
benchmarks/                    BenchmarkDotNet suites
docs/                          architecture, roadmap, research, compatibility, memory map, performance, development
```

## Documentation

- [Roadmap and acceptance criteria](docs/ROADMAP.md)
- [Development guide](docs/DEVELOPMENT.md) — build, test, verify addresses, release
- [Architecture and decisions](docs/ARCHITECTURE.md)
- [Memory map](docs/MEMORY_MAP.md) · [Research](docs/RESEARCH.md) · [Compatibility](docs/COMPATIBILITY.md) · [Performance](docs/PERFORMANCE.md)

## Safety and integrity

- Read-only in 0.1. No game files are modified, no game binaries or assets are distributed.
- Exact build identification and runtime image verification before any read; reads are confined to the
  verified module image.
- Future Trainer Mode writes will be explicit, validated and logged; Challenge Mode will disable writes.
  Challenge Mode records what Lockpick did — it is not an anti-cheat system.

## Known limitations

- Only the GOG USA 1.0 hotfix4 build is supported.
- The Rebecca character id (partner swap) is implemented from static analysis but not yet observed live.
- While choosing a save file on the load screen, the previous game's values are still shown for a few seconds.
- After a death, values are hidden until the next game initializes (the dead game's data lingers in memory).
- The game stops rendering when unfocused, and its window can't be dragged — original game behavior.
- If the game runs elevated, Lockpick must also run elevated to read it.

## Credits

Contributors and capture volunteers are listed in [CONTRIBUTORS.md](CONTRIBUTORS.md).

Research builds on the work of the RE1 decompilation (ecruells), the RE1 Mod SDK and Classic REbirth
(Gemini-Loboto3), the RE1 autosplitter (deserteagle417 / Dchaps), BioRand (Ted John) and
Windows-ResidentEvil1-MODS (shadwork). No code from these projects is included; see
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Resident Evil is a trademark of Capcom; this project is
not affiliated with or endorsed by Capcom or GOG.

## License

[MIT](LICENSE)
