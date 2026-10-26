# Compatibility

Lockpick reads memory only from builds it can identify **exactly**. Anything else is reported as
unsupported and never opened.

## Supported

| Game | Store / build | Executable | Size | SHA-256 | Profile | Status |
|---|---|---|---|---|---|---|
| Resident Evil (1996) | GOG, USA data, "1.0 hotfix4" (GOG build 59172465945261369) | `ResidentEvil.exe` | 1,004,112 | `205BD6FE…CAEEE4A78` | `re1-gog-usa-hotfix4` | Live-verified (Chris and Jill games, Rebecca swap) |
| Biohazard (1996) | GOG, Japanese data, "1.0 hotfix4" (the Japanese installer; it can share the English install's folder) | `Biohazard.exe` | 972,880 | `9F38420B…7168E8012` | `re1-gog-jpn-hotfix4` | Live-verified 2026-10-17 (a Chris game: rooms, HP, timer, inventory, enemies, pickups); the rest static. Same addresses as 1.01 |
| Resident Evil (1996) | Steam (app 4249100, Steam build 21744136), English: GOG's English hotfix4 exe inside Steam's wrapper, in `english\` | `ResidentEvil.exe` | 3,198,880 | `184D0858…C93AEF59A` | `re1-steam-usa` | Live-verified 2026-10-26 (a Jill game, 15 min on 1F and 2F: rooms, HP, timer, inventory incl. Jill's 8 slots, item box, enemies, pickups); the rest static. Same addresses as GOG English |
| Biohazard (1996) | Steam (app 4249100), Japanese: GOG's Japanese hotfix4 exe inside Steam's wrapper, in `japanese\` | `Biohazard.exe` | 3,191,712 | `E6984B58…5646D2F6C` | `re1-steam-jpn` | Live-verified 2026-10-26 (a Jill game, 10 min on 1F and 2F: rooms, HP, timer, inventory incl. combining and loading, item box, enemies, pickups); the rest static. Same addresses as GOG Japanese |
| Biohazard (1996) | Japanese PC 1.01: MediaKite/Sourcenext patched to 1.01, or GOG/Steam's Japanese version with the 1.01 exe, usually with **Classic REbirth** | `Biohazard.exe` | 962,560 | `25BE34D4…D23A85302` | `re1-jpn-101` | Live-verified 2026-10-17 under Classic REbirth 1.1.4 (a Chris game to a death: rooms, HP, timer, inventory, enemies, pickups); the rest static |

The Japanese 1.01 profile was made by `tools/re1/port_profile.py` from the GOG one: for each address Lockpick reads,
the code that uses it in the GOG exe was found again in the 1.01 exe (`tools/re1/xmap.py`), and the sites agree on the
new address (e.g. stage 114 of 122 sites, HP 150 of 154, the timer 6 of 7, no disagreement). The game-state block moved
by `+0x4EED0`, the system globals by `−0x279920`, the room action table by `−0x258F00` — the same layout. The RE1
autosplitter's "JapaneseREbirth" addresses (module size 10027008 = `0x990000`) agree. Classic REbirth rewrites one
instruction in memory (the frame loop's timer tick becomes a call to its own tick, which increments the same timer), so
this profile's timer anchor is the New Game reset instead. What a probe trace showed in play is runtime
(`data/games/re1/profiles/live/*.json`, applied by `port_profile.py --live`); the trainer writes only those fields.

Steam's release (Capcom with GOG.com, 2026-04-01) is GOG's: each language folder holds GOG's executable for that
language inside a wrapper that unpacks it when the game starts, and GOG's data. The image read from the running game
(`tools/re1/dump_image.py`) matches GOG English's code and constants byte for byte (`.text`, `.rdata`, `.reloc`,
`.data2`), and 2,938 of the English folder's 2,951 GOG files are identical (the rest are GOG's own extras). So
`re1-steam-usa` is `re1-gog-usa-hotfix4` under Steam's identity (`port_profile.py` on the dump, `--identity` the
shipped exe). Steam's launcher (`4249100_Launcher.exe`) picks the language and keeps the DirectDraw wrapper settings in
`4249100_Launcher.ini`, which it copies into every language's `dxcfg.ini` at each start, so Lockpick's display
settings edit the launcher's file there. Steam's Japanese folder is the same with GOG's Japanese release: GOG's
Japanese data (2,988 of 2,990 files) and GOG's `Biohazard.exe` wrapped (code identical in memory), so `re1-steam-jpn`
is `re1-gog-jpn-hotfix4` under Steam's identity. French and German
(`FRA\`, `GER\`) have data GOG's releases don't, so they are new builds.

## speedrun.com boards (Resident Evil, `residentevil`, read 2026-10-17)

| Board | What the rules allow | Lockpick |
|---|---|---|
| PC | Japanese MediaKite (or a reprint) with Classic REbirth (recommended) or the 1.7/1.8 patch; GOG and Steam allowed, may use REbirth. LiveSplit's IGT script on screen, no REbirth quickturn / tactical reload / debug menu, no mods | Supported: GOG English and Japanese, Steam English and Japanese, and Japanese 1.01 with Classic REbirth (MediaKite, or GOG's or Steam's Japanese data) |
| EMU | PS1 versions on DuckStation (fixed settings) or PSXjin / BizHawk; Director's Cut is the fast version, PAL banned | Not yet: needs emulator support (reading the console's RAM inside the emulator) and a PS1 disc image to research |
| PlayStation, PSN, Sega Saturn | Real consoles (PS1–PS5, PSP/Vita, Saturn) | Out of reach: nothing runs on the PC to read |
| `re1ce` (category extensions), `re123` | Same versions; REbirth required on PC there | As PC |

The leaderboard-run checks (Runs → Leaderboard run) know every supported PC build: on GOG every file is compared with
the release; on Steam the data is compared with GOG's and the exe by Steam's hash (`data/games/re1/install/steam.json`);
on the Japanese game the exe and REbirth are checked by hash (every REbirth release 1.0.0–1.1.4,
`data/games/re1/install/rebirth.json`) and REbirth's banned options are read from its `config.ini`.

## Known other builds (not supported yet)

| Build | How to recognize | Notes |
|---|---|---|
| Steam French / German (app 4249100) | `french\` / `german\ResidentEvil.exe` | Their own data (`FRA\`, `GER\`); not researched |
| Earlier GOG English revision | SizeOfImage `0x49B3000` (from the RE1 autosplitter) | Addresses differ slightly; needs its own profile |
| Other versions REbirth 1.1.4 lists | European 1.00c, Asia 1.00, UK/USA 1.00, German, French, PowerVR UK/US and Japanese 1.00, PC Gamer demo | Not used on the leaderboard as far as known |
| HD Remaster (2015), REmake | — | Different games; out of scope |

To request support, run `lockpick-probe identify "<path to exe>"` and open an "Unsupported game build" issue.

## Environment notes

- **Privileges:** Lockpick must run at the same privilege level as the game. If the game was started
  elevated (e.g. by the GOG installer's "Launch" button), restart the game normally or run Lockpick as
  administrator. Lockpick itself never requests elevation.
- **GOG DirectX wrapper:** windowed size is controlled by `dxcfg.ini` (`display=WIDTHxHEIGHT@HZ`,
  `presentation=windowed`, `aspect=enabled` keeps 4:3). Example for a 16:9 window on an ultrawide:
  `display=1920x1080@60`. Lockpick changes it only from its display settings (backing the file up first). On Steam
  the launcher's `4249100_Launcher.ini` holds these settings and overwrites `dxcfg.ini` at every start.
- **Focus:** the game stops rendering when it is not the active window (original behavior).
- **Multiple monitors / DPI:** Lockpick is per-monitor DPI aware and has a compact layout for narrow or
  portrait windows (< 760 px wide).
- **Classic REbirth:** replaces the DirectDraw wrapper (`ddraw.dll`) and keeps its own settings (`config.ini`) and
  saves (`savedata\`); Lockpick reads both and leaves the display settings to REbirth's own setup. It runs only on
  the Japanese game (on GOG: the Japanese version with the 1.01 exe).

## Platform

Windows 10 (1803+) or 11, x64. The app is 64-bit and reads the 32-bit game through WOW64.
High-resolution polling uses `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION` (falls back to the default timer).
