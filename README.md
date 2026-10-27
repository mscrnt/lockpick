<h1 align="center"><img src="docs/lockpick-banner.png" alt="Lockpick — Master of Unlocking." width="640"></h1>

[![Latest release](https://img.shields.io/github/v/release/mscrnt/lockpick?label=release)](https://github.com/mscrnt/lockpick/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/mscrnt/lockpick/total)](https://github.com/mscrnt/lockpick/releases)
[![CI](https://github.com/mscrnt/lockpick/actions/workflows/ci.yml/badge.svg)](https://github.com/mscrnt/lockpick/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)
[![Discussions](https://img.shields.io/github/discussions/mscrnt/lockpick)](https://github.com/mscrnt/lockpick/discussions)

**Lockpick** is a free companion app for the original **Resident Evil (1996)** on PC: GOG's English or Japanese
release, or the Japanese version with Classic REbirth that most speedrunners use. Run it next to
the game and it shows what the game is doing as you play: your health, the room you're in, a live map, your items,
your progress and the enemies around you. It also times speedruns the way the leaderboard does, runs challenges such
as "no damage" or "knife only", puts all of it on your stream, and has optional cheats for practice.

It never changes your game files. It only changes the running game if you switch cheats on.

> **Status:** version **0.3**, "play and stream with one app", is out (see [what's new in 0.3](#new-in-03)). What's
> next is on the [roadmap](docs/ROADMAP.md).

## Screenshots

<p align="center">
  <img src="docs/screenshots/stream.png" alt="Stream page: the time and HP, the splits in LiveSplit's look with the world record, and the minimap" width="49%">
  <img src="docs/screenshots/runs.png" alt="Runs: the community's splits, practice from a save and this run's splits" width="49%">
</p>
<p align="center"><em>Your widgets inside Lockpick, as big as you like (left) and runs timed on the game's own clock (right).</em></p>

<p align="center">
  <img src="docs/screenshots/overlay-settings.png" alt="Settings, Overlay: each widget's place and size, the look, and the map's views" width="49%">
  <img src="docs/screenshots/leaderboard.png" alt="Runs, Leaderboard: the speedrun.com board for the chosen category" width="49%">
</p>
<p align="center"><em>Make the overlay yours (left) and the speedrun.com leaderboard for your category (right).</em></p>

<p align="center">
  <img src="docs/screenshots/challenges.png" alt="Challenges tab: a no-damage attempt running, still clean" width="49%">
  <img src="docs/screenshots/trainer.png" alt="Trainer tab with cheats on: health, infinite ammo, inventory counts and the item box" width="49%">
</p>
<p align="center"><em>Challenges, step by step (left) and cheats in the Trainer, every change checked and logged (right).</em></p>

<p align="center">
  <img src="docs/screenshots/progress.png" alt="Progress: items, documents, rooms and enemies per area, read from the game's flags" width="49%">
  <img src="docs/screenshots/map.png" alt="Map: the current room from the install's room files, with doors, the item box, the typewriter and the player" width="49%">
</p>
<p align="center"><em>Progress per area from the game's own flags (left) and the live room map (right).</em></p>

<p align="center">
  <img src="docs/screenshots/items.png" alt="Items: what you carry and what is in the item box" width="49%">
  <img src="docs/screenshots/dashboard.png" alt="Dashboard: connection, verified build fingerprint, live game state and event log" width="49%">
</p>
<p align="center"><em>Inventory and item box (left) and the dashboard with the verified build and live state (right).</em></p>

## What it does

- **Watches the game for you:** your character, health, the room you're in, the in-game time and what just happened,
  updated 30 times a second.
- **Map, progress and items:** the room you're in, drawn from your own game files with you in it; what's left in each
  area (items, documents, rooms, enemies); and what you carry and what's in the item box.
- **Challenges:** *Speedrun*, *No damage* and *Knife only*, which you can combine. Lockpick tells you the moment a rule
  breaks and why. While a challenge runs, Lockpick can't change your game at all.
- **Cheats for practice** (always off when Lockpick starts): set, fill or lock your health, infinite ammo, change what
  you carry or what's in the item box, with hotkeys. Every change is checked, logged, and the run is marked as changed.
- **Captures** of play sessions, which help Lockpick learn more of the game (see [Help out](#help-out)).
- **An API for other tools** (off unless you turn it on), for scripts, OBS, a Stream Deck or macros
  ([docs/API.md](docs/API.md)).
- **Installs and updates itself**, with a short first-time setup, and fits any window: ultrawide, 1080p, side by side
  with the game, or portrait.

## New in 0.3

**Speedrunning and practice**
- **Run timer on the game's own clock** (the in-game time the leaderboard uses), with the community's splits for Jill
  and Chris built in. Lockpick splits by itself on rooms, key items and the ending.
- **LiveSplit built in:** your splits look like LiveSplit, compare against your best, your average or your best
  segments, and save as LiveSplit files. If you already use LiveSplit, Lockpick can follow it or drive it.
- **Training help:** "where the time is" shows the segments where your best run loses the most time.
- **Your own splits:** rename splits, choose what triggers each one, and save them as your own set.
- **Practice from a save:** start timing when you load a save, on the game's clock, from the split you choose. These
  runs never count as a personal best.
- **Leaderboard runs:** one switch keeps Lockpick hands-off (cheats locked, nothing that pauses the game), checks your
  game files for mods (and, with Classic REbirth, that quickturn, tactical reload and its debug menu are off), and
  lists the rules only you can keep.
- **speedrun.com:** see the leaderboard and where your time would place, and submit a finished run from the app.
- **Warnings** when your splits are for the other character.

**On screen and on stream**
- **Overlay on top of the game:** time and HP, a minimap (or the whole room, or the whole floor), the enemies in the
  room with their health, your splits, what just happened, and big messages ("Challenge failed", "New personal best").
  Clicks go straight through it and it never steals focus from the game.
- **Make it yours:** drag any widget anywhere, change each one's size (even while playing, with a key or a controller
  button), choose what the time/HP panel shows, and pick the colours, transparency and font. Save your layout to a
  file and share it.
- **Keep the game clean:** pop any widget out into its own window, or show them all on the Stream page instead.
- **OBS:** every widget can be its own OBS source. Lockpick's OBS script adds them with one click, records each run,
  adds chapter markers at splits and deaths, saves your replay buffer on a death or a new best, switches to a
  "paused" scene when the game isn't in front, and gives you OBS hotkeys for Lockpick. Or Lockpick does the same
  through OBS's WebSocket server, for OBS on another PC too.
- **Streamer mode** blurs file paths and other private details if you show Lockpick's window on stream.

**Also new:** a death counter, sounds when you fail a challenge or die (Windows sounds or your own), every page
split into sections in the sidebar, a support bundle for bug reports, and a [player guide](docs/GUIDE.md).

## Install

1. Download **[Lockpick-win-Setup.exe](https://github.com/mscrnt/lockpick/releases/latest/download/Lockpick-win-Setup.exe)**
   and run it. It installs for your Windows user only (no administrator rights needed). Windows may warn that it
   doesn't know the app, because Lockpick isn't code-signed yet: click *More info → Run anyway*.
2. The first start helps you find your game and checks that it's a supported version.
3. Start Resident Evil from its own `ResidentEvil.exe` (or `Biohazard.exe` for the Japanese version), not the GOG
   installer's *Launch* button, which starts it as administrator. Lockpick connects by itself.

**Updates:** Lockpick checks for new versions in the background. When one is ready, the version number at the bottom
of the sidebar turns green; nothing installs until you click.

**Portable:** each release also has **Lockpick-win-Portable.zip**. Unzip it anywhere and run `Lockpick Trainer.exe`.
It keeps its settings, logs and runs in a `data` folder beside it, so you can keep several copies in different
folders. Neither the zip nor the app writes to the registry, and the portable copy updates itself the same way. The
installer adds only what any per-user install adds: desktop and Start menu shortcuts and an entry in *Installed apps*,
so you can uninstall it.

**Which game version?** Lockpick supports:

- **GOG's English release**, 1.0 hotfix4 (`ResidentEvil.exe`);
- **GOG's Japanese release**, 1.0 hotfix4 (`Biohazard.exe`; GOG offers it as a separate download of the same game);
- **the Japanese 1.01 version with Classic REbirth**, the version the speedrun.com PC board recommends (MediaKite, or
  GOG's Japanese release with REbirth in its own folder).

It refuses other versions instead of guessing; see [docs/COMPATIBILITY.md](docs/COMPATIBILITY.md). The PlayStation,
Saturn and emulator boards aren't supported yet.

## Safe by design

- **Read-only unless you turn cheats on.** Lockpick never changes the game's files, and doesn't include any of the
  game's files.
- **It checks before it reads.** Lockpick only works with a game version it knows exactly, and checks the running game
  before reading anything.
- **Cheats are opt-in and careful.** They're off every time Lockpick starts. Every change is checked against the game's
  limits, read back and logged, and a run with changes is marked. During a challenge, Lockpick has no way to change the
  game at all.
- **The one exception:** an optional research mode (*Ask me about unknown events*, on the Debug page) can pause the
  game while it asks you what just happened. It never changes the game, and the game carries on as soon as you answer.
- **Your data stays yours.** Your speedrun.com key is stored encrypted for your Windows account and only ever sent to
  speedrun.com. Captures hold only game data. Support bundles leave out your API token and your Windows user name, and
  you choose whether to send one.

Challenge mode records what Lockpick did. It is not an anti-cheat system.

## Known limitations

- Supported: GOG's and Steam's English and Japanese releases, and the Japanese 1.01 version with Classic REbirth. Not
  yet: emulators. On the Japanese versions, cheats only cover what has been checked in play so far: health, and ammo in
  the first four slots on GOG's Japanese release. The rest follows as it's tested.
- Resident Evil pauses and goes black whenever it isn't the active window (that's the original game). An OBS capture
  shows black while you click elsewhere, and in exclusive fullscreen the game minimizes instead.
- The built-in split points come from the community's autosplitter and haven't all been checked in full runs yet.
- On the load screen, the last game's values show for a few seconds; after a death, values are hidden until the next
  game starts.
- If the game runs as administrator, Lockpick has to as well.

## Help out

The most helpful thing is recording a play session: open **Debug → Capture**, press *Start capture*, play (any kind
of play; speedruns are great), then *Stop and save* and send the zip ([how](docs/CAPTURE.md)). The
**[bounty list](docs/CAPTURE_BOUNTIES.md)** shows what's still needed. Bounties are unpaid; your name goes in the
app's credits.

Ideas and questions go to [Discussions](https://github.com/mscrnt/lockpick/discussions); plans are on the
[roadmap board](https://github.com/users/mscrnt/projects/8). See [CONTRIBUTING.md](CONTRIBUTING.md); helpers are
credited in [CONTRIBUTORS.md](CONTRIBUTORS.md).

Lockpick is free. If you'd like to support it, you can [buy me a coffee](https://buymeacoffee.com/mscrnt). It's
entirely optional and doesn't buy features or priority.

## Documentation

- **[Player guide](docs/GUIDE.md)**: challenges, runs, the overlay, OBS, LiveSplit, troubleshooting
- [Runs and splits](docs/RUNS.md): the run timer, splits, LiveSplit and speedrun.com
- [API and OBS](docs/API.md): the API, the OBS pages and the OBS script
- [Captures](docs/CAPTURE.md): recording play sessions for research
- [Roadmap](docs/ROADMAP.md) · [Display modes](docs/DISPLAY.md) · [Compatibility](docs/COMPATIBILITY.md)
- For developers: [development guide](docs/DEVELOPMENT.md) · [architecture](docs/ARCHITECTURE.md) ·
  [memory map](docs/MEMORY_MAP.md) · [research](docs/RESEARCH.md) · [performance](docs/PERFORMANCE.md) ·
  [LiveSplit engine](docs/LIVESPLIT_CORE.md)

## Build from source

You need Windows 10 or 11 (64-bit), the [.NET 10 SDK](https://dotnet.microsoft.com/download) and
[Rust](https://rustup.rs) (for the built-in LiveSplit timer).

```powershell
git clone --recursive https://github.com/mscrnt/lockpick; cd lockpick
dotnet run --project src/Lockpick.App
dotnet test tests/Lockpick.Tests                         # tests
dotnet run --project tools/Lockpick.Probe -- identify    # check your installed game version
```

More in the [development guide](docs/DEVELOPMENT.md).

<details>
<summary>Repository layout</summary>

```text
src/
  Lockpick.Core/               game state, events, monitor, trainer, challenges, runs, recording
  Lockpick.Platform.Windows/   Windows: finding the game, reading it (writing only with cheats on), hotkeys, controllers
  Lockpick.GameAdapter.RE1/    everything specific to Resident Evil (the only code that knows its addresses)
  Lockpick.LiveSplit/          the built-in LiveSplit timer (livesplit-core)
  Lockpick.Api/                the local API and OBS pages (off by default)
  Lockpick.App/                the desktop app (Avalonia), with the OBS script in Obs/
data/games/re1/                game versions (addresses with their evidence), names, labels, split data
extern/livesplit-core/         LiveSplit's engine (git submodule)
tools/                         developer tools: probe, capture analysis, research harnesses (not part of the app)
tests/Lockpick.Tests/          tests
benchmarks/                    performance benchmarks
docs/                          guides and design documents
```

</details>

## Credits

Contributors and capture volunteers are listed in [CONTRIBUTORS.md](CONTRIBUTORS.md).

Lockpick's timer is [livesplit-core](https://github.com/LiveSplit/livesplit-core) (MIT / Apache 2.0). Leaderboard data
comes from [speedrun.com](https://www.speedrun.com) (CC BY-NC 4.0). Research builds on the RE1 decompilation
(ecruells), the RE1 Mod SDK and Classic REbirth (Gemini-Loboto3), the RE1 autosplitter (deserteagle417 / Dchaps),
BioRand (Ted John), Windows-ResidentEvil1-MODS (shadwork) and the community's speedrun guides. No code from those
research projects is included; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

Resident Evil is a trademark of Capcom. This project is not affiliated with or endorsed by Capcom or GOG.

## License

[MIT](LICENSE)
