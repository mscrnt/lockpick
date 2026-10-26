# Lockpick player guide

Lockpick sits next to the original **Resident Evil** on PC (GOG's English or Japanese release, or the Japanese
version with Classic REbirth) and reads the game as you play: your health, the room
you're in, a live map, items, story progress, the enemies around you. On top of that it times runs the way the
leaderboard does, runs challenges, puts it all on your stream, and — only if you turn them on — has cheats for
practice. It never changes your game files.

- [Install and first start](#install-and-first-start)
- [Finding your way around](#finding-your-way-around)
- [Challenges](#challenges)
- [Runs, splits and the leaderboard](#runs-splits-and-the-leaderboard)
- [Cheats for practice](#cheats-for-practice)
- [The overlay and its widgets](#the-overlay-and-its-widgets)
- [Streaming and recording (OBS)](#streaming-and-recording-obs)
- [LiveSplit](#livesplit)
- [Keys and controller buttons](#keys-and-controller-buttons)
- [Display modes](#display-modes)
- [Helping out: captures](#helping-out-captures)
- [Troubleshooting](#troubleshooting)

## Install and first start

1. Download **[Lockpick-win-Setup.exe](https://github.com/mscrnt/lockpick/releases/latest/download/Lockpick-win-Setup.exe)**
   and run it. It installs for your Windows user only (no administrator rights needed). Windows SmartScreen may warn
   because Lockpick isn't code-signed yet: *More info → Run anyway*.
2. The first start finds your game and checks its build. Lockpick supports GOG's English and Japanese 1.0 hotfix4
   releases and the Japanese 1.01 version with Classic REbirth; other builds are refused rather than guessed at.
   With REbirth or a MediaKite copy, point Lockpick at your `Biohazard.exe` (Settings → General → Game installation).
3. Start Resident Evil from its own `ResidentEvil.exe` or `Biohazard.exe` (not the GOG installer's *Launch* button,
   which starts it as administrator). Lockpick connects by itself — the badge at the top right turns green.

**Classic REbirth:** keep it in its own folder (it replaces `ddraw.dll`, which GOG's English game needs). Lockpick
reads REbirth's save folder and settings; change the window size in REbirth's own setup window.

Updates are checked in the background; when one is ready the version at the bottom of the sidebar turns green.
Nothing installs without your click.

## Finding your way around

The sidebar lists the pages; a page with several parts unfolds its **sections** under it when you click it (in a
narrow window they appear as a row above the page instead). Each page remembers the section you were on.

| Page | What it's for |
|---|---|
| Dashboard | Whether Lockpick sees the game, the game's state, and the event log |
| Runs | Everything for a run on one page: the timer, splits and LiveSplit (*Splits*), challenges and their sounds (*Challenges*), your own splits (*Edit splits*), where your time goes (*Training*), speedrun.com and leaderboard runs (*Leaderboard*). The challenge you're on and whether a leaderboard run is ready show above the timer on every tab |
| Trainer | Cheats (off until you turn them on): health, ammo, inventory, and what was changed |
| Progress | Items, documents, maps and rooms of each area, from the game's own flags (works from a save too) |
| Map | The room you're in, live, or the whole floor |
| Items | Your inventory and the item box |
| Stream | Your widgets as a dashboard inside Lockpick (*Widgets*) and everything for OBS (*OBS*) |
| Debug | Captures for research, and the log |
| Settings | General, game display, the overlay, hotkeys, speedrun.com, the API |

## Challenges

Runs → *Challenges*: pick one, add rules if you like, press **Start challenge**: Lockpick switches to the splits, with
the challenge above the timer. Then start a new game or load a save — the attempt begins when you're in the game.

- **Speedrun:** finish as fast as you can. Lockpick can't change your game while it runs, so the run is clean.
- **No damage:** one hit (or poison) and the attempt is over.
- **Knife only:** firing any other weapon ends the attempt (carrying one is fine).

Rules can be combined (a knife-only speedrun, a no-damage knife run). When a challenge fails you see why and when (in
in-game time) above the run's timer and as a big message on the overlay. **Try again** starts over — also from a key or
a controller button (Settings → Hotkeys → *Challenge: try again*). A new game or a loaded save starts a new attempt;
closing the game ends one. Deaths are counted for the whole session.

Runs → *Challenges*, under the steps: a sound when a challenge fails or you die — one of Windows' sounds or a `.wav` of your own,
each with a Play button.

## Runs, splits and the leaderboard

Runs → *Splits*: choose your splits — the community's segments (Jill or Chris, either ending, NMG or MG), a key-item
route, or a LiveSplit `.lss` file you import. From then on every **new game** is timed on the game's own clock (the
in-game time the leaderboard uses) and splits by itself on rooms, key items and the ending. A death, the title screen,
the game closing or **Reset** ends the attempt; every attempt is kept.

- **This run** shows each split against your personal best — or pick another comparison: best segments, average,
  median, your latest run, balanced PB… Green is ahead, red behind, gold a best-ever segment.
- Runs → *Training*: **Where the time is** lists the segments where your personal best loses most against your best
  segments — practice those first. Below it, your attempts.
- *Also start when I load a save* times practice from a save on the game's own clock (or from zero at the load); it
  never counts as a personal best, and splits it didn't see stay empty.
- If your splits are for the other character (Jill's splits in a Chris game), Lockpick tells you when the run starts.
- Attempts with the trainer on never count as a personal best.

Runs → *Leaderboard*:

- **speedrun.com leaderboard** (turn it on — Lockpick only goes online while it's on): the world record, the top
  runs, and where your best would place.
- **Submit to speedrun.com:** a finished clean attempt, with its in-game time, the date and your video link, in two
  presses. It needs your speedrun.com API key (Settings → speedrun.com): the key is kept encrypted for your Windows
  account and only ever sent to speedrun.com.
- **Leaderboard run:** for runs you'll submit. Lockpick locks its cheats read-only, keeps ask mode off, checks the
  game files of the version you're playing (no mods; with Classic REbirth also that quickturn, tactical reload and its
  debug menu are off) and lists the rules only you can keep. Each attempt's record notes
  whether every check passed.

The leaderboard's rules for PC require LiveSplit's in-game time script on screen — see [LiveSplit](#livesplit).

## Cheats for practice

Trainer: **Turn cheats on** to change your game — set or fill your health, lock it, infinite ammo, and edit your
inventory. Cheats are off every time Lockpick starts, every change is checked and logged (*Action log*), a run with
changes is marked as such, and nothing can be changed while a challenge runs. Keys and controller buttons for the
common cheats are in Settings → Hotkeys.

**Keep the game running when it isn't in front** (*In the background*): the game normally pauses, and goes black in
OBS, when you click another window. With this on it keeps playing behind other windows, and keys you type there don't
reach it; your controller still does. The game is silent in the background (it mutes itself). It goes off for a
challenge or a leaderboard run and when Lockpick closes, and your keyboard is put back each time the game comes to
the front. So far it's checked on GOG's English release; on other versions the switch says it isn't checked yet.

## The overlay and its widgets

Settings → *Overlay*: **Overlay on** shows widgets on top of the game's picture. Clicks go through them and they never
take the focus, so the game keeps running; they work windowed, borderless and in fullscreen.

- **Widgets:** time, HP, room and challenge · the map · enemies in the room with their HP · your splits (LiveSplit's
  look, with the world record) · what just happened (pickups, kills, story events) · big messages (challenge failed,
  you died, run finished, new personal best).
- **Where:** each widget goes in a corner (widgets in the same corner stack), or **anywhere**: drag it on the
  *Layout* preview and it keeps that spot at any window size. *Back to the corners* undoes it.
- **Size and look:** each widget has its own size (50–200%), also while you play with the keys *Overlay: next widget to
  size*, *bigger* and *smaller* (Settings → Hotkeys); *Look* sets the panels' transparency, text colour and font and
  which lines the time/HP widget shows. **Save layout…** keeps the whole setup in a file to share; **Load layout…** uses
  one (yours or someone else's).
- **The map's views** (*Map views*): the map key moves through the ones you tick — a minimap around you, the whole room
  over the picture (solid or see-through), the whole floor. Reach, size, see-through strength and names are there too.
- **Keep the picture clean:** every widget can **Pop out** into a window of its own instead — move it to another
  monitor, resize it, right-click it for a green background or always on top. The map's window has its own view
  (around you, the whole room, the whole floor; right-click). The **Stream** page shows the widgets inside Lockpick,
  as large as you like (*Customize* picks which, their order and sizes).

## Streaming and recording (OBS)

The overlay is drawn over the game on your screen, so OBS doesn't see it. For your stream, use one of these:

- **Browser sources:** Stream → *OBS* lists every widget's address; press **Copy OBS link**, then in OBS add a
  *Browser* source, paste it and set the size shown. They're transparent and live. They need Lockpick's API on
  (Settings → *API and profiles*; it only listens on your PC), and the address holds your API token — keep it to
  yourself.
- **Lockpick's OBS script:** in OBS, *Tools → Scripts → +* and pick `lockpick.lua` (Stream → *OBS* shows where it
  is). **Add Lockpick to this scene** adds all the browser sources and a Game Capture of the game. Its options, each
  optional: record each run, chapter markers at splits, deaths and fails, save the replay buffer on a death, a fail
  or a new best, and switch to a "paused" scene while the game isn't in front. It also adds **OBS hotkeys** for
  Lockpick (OBS Settings → Hotkeys, "Lockpick: …"): split, undo, skip, reset, try again, the overlay and the map.
  For chapter markers, set OBS *Settings → Output → Recording → Recording Format* to **Hybrid MP4**: plain MP4 and
  MKV recordings get no chapters.
- **Lockpick's own OBS connection** (Stream → *OBS* → *OBS WebSocket*, optional): the same as the script, done from
  Lockpick through OBS's WebSocket server, so it also works with OBS on another PC (a streaming PC). In OBS, turn on
  *Tools → WebSocket Server Settings → Enable WebSocket server*; in Lockpick, tick **Connect to OBS** and press
  **Fill in from OBS on this PC** (or type the other PC's address, the port and the password from *Show Connect
  Info*). Pick what Lockpick does (the same options as the script), and **Set up OBS** makes a "Lockpick" scene with
  the widgets (and a Game Capture of the game when OBS is on this PC). For OBS on another PC, allow other devices on
  your network in Lockpick's API settings so it can show the widgets. The password is kept encrypted for your Windows
  account. If you also use the script on this PC, it leaves these to Lockpick while Lockpick is connected; its
  hotkeys keep working.
- **Window Capture** of a popped-out widget window works too (use its green background with a chroma key).
- **Streamer mode** (Settings → General) blurs file paths, process ids and the API token in Lockpick's window, if you
  show the app on stream.

For the game itself, **Game Capture** works in every display mode.

## LiveSplit

Lockpick has **LiveSplit's own timer built in**: your runs are also kept as LiveSplit files with every attempt, the
splits widget has LiveSplit's look, and the comparisons are LiveSplit's — no LiveSplit needed for practice.

For leaderboard submissions the rules require LiveSplit with its RE1 in-game time script. Runs → *LiveSplit*:

- **Follow** (with LiveSplit's autosplitter): Lockpick moves with LiveSplit's splits instead of splitting itself, so
  nothing splits twice.
- **Drive** (no autosplitter): Lockpick starts, splits and resets LiveSplit and sets its game time.

Both need LiveSplit's server on (in LiveSplit: *Control → Start TCP Server*).

## Keys and controller buttons

Settings → *Hotkeys*: keys that work while you play (Lockpick doesn't need the focus) for the overlay, the map's views
and zoom, the run (split, undo, skip, reset), *Challenge: try again*, capture markers and the common cheats. Any of
them can also be a **controller button** (*Set button…*), so you never need the keyboard.

## Display modes

Settings → *Game display* changes the game's own display settings (they apply when the game starts; your original
settings are kept and can be put back):

- **Windowed:** a normal window. Everything works.
- **Borderless (desktop):** fills the screen but is still a window. Everything works — the best choice with the
  overlay and OBS.
- **Fullscreen (exclusive):** the overlay works, but the game minimizes as soon as anything else takes the focus, and
  a resolution other than your desktop's changes your monitor's mode. Lockpick never takes the focus while the game is
  fullscreen.

Resident Evil pauses and goes black whenever it isn't the active window — that's the original game. Keep it in front
while you play (and in OBS, expect a black picture while you click elsewhere).

## Helping out: captures

Debug → *Capture*: **Start capture**, play, **Stop and save** — the zip helps Lockpick learn the parts of the game it
doesn't know yet. It holds the game's memory and Lockpick's readings, never screenshots, audio, files or anything
about you. Press the marker key (or a controller button) at moments worth a look and describe them later; runs and
challenge fails are marked by themselves. See [CAPTURE.md](CAPTURE.md) and the open
[capture bounties](CAPTURE_BOUNTIES.md).

## Troubleshooting

- **Lockpick never connects:** the game was probably started as administrator (e.g. from the GOG installer's
  *Launch* button), which Lockpick can't read. Close it and start it from `ResidentEvil.exe` or `Biohazard.exe`.
- **"Unsupported build":** Lockpick reads GOG's English and Japanese 1.0 hotfix4 and the Japanese 1.01 version
  (Classic REbirth); other builds are refused until they're verified ([COMPATIBILITY.md](COMPATIBILITY.md)). Run
  `lockpick-probe identify` on yours and open an issue to ask for it.
- **The overlay doesn't show in OBS:** that's by design — use the browser sources or a popped-out window.
- **The overlay doesn't show at all:** check Settings → Overlay (on, and the widgets aren't all *Off*); its status line
  says why it's hidden.
- **Something went wrong:** Settings → General → **Make a support bundle** zips the last few days' logs, your
  settings without the API token, and versions (your Windows user name is left out). Attach it to a
  [GitHub issue](https://github.com/mscrnt/lockpick/issues). After an unexpected error Lockpick offers it by itself.
