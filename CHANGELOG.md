# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.4.1] - 2026-10-27

### Fixed
- **Lockpick's window on stream:** OBS's Window Capture showed it blank (its usual capture method can't see windows
  drawn through DirectComposition). Lockpick now draws into a plain window surface, so any capture method works.
- **Streamer mode** smeared the whole window on some PCs and left trails behind the mouse (a blur effect). It now
  shows the API token as dots and leaves the rest readable: paths and process ids are no longer hidden.

## [0.4.0] - 2026-10-27

### Added
- **Full-game routes for Jill and Chris** (Map page → Route guide): from a new game to the heliport, every step with
  where to go, what to do and the story flag that ticks it off. Made by a simulator that plays the game through from
  the room files' own scripts (`tools/routesim`): it isn't a speedrun order (it takes the nearest next thing), and
  neither route has been followed in a real game yet, so both show as drafts.
- **Route guide** (Map page → Route guide, and an overlay widget): pick a route and Lockpick shows the next step, the
  way there drawn on the room and floor maps (round the furniture, through the doors you can open), and the buttons to
  press as you get near: pick up, open the door, use the item, push a puzzle's switches in order (the gallery's
  paintings, numbered on the map from the room's own scripts). The buttons follow
  the controller you're using (Xbox, PlayStation, or your own keyboard keys from the game's settings), or one kind you
  choose. Steps tick off by themselves from the game's flags, so a loaded save starts at the right step; Back and Skip
  move by hand. The first route is a draft of Jill's any% (the key-item order, the game's own use spots and story
  flags, and the forced events and puzzles of a full walkthrough, matched to the room files); it hasn't been followed
  through a whole run yet.
- **Time limits** on the route: Richard's serum counts down from when he asks for it (15 minutes, in the route widget,
  the Map page and the top of the game picture).
- **The route in the game's own picture** (overlay): the way drawn on the floor of the room you're in, a ring where
  the step is, a puzzle's switches numbered (ticked off as you press them), and the buttons at the bottom middle of the
  picture. Each part can be switched off in the Route guide card; the layer is an overlay widget
  (Settings → Overlay). Drawn with each room camera's own projection from the room files.
- **Locked doors on the map**: red while locked, yellow when you can open them (you hold the key, or it opens from
  your side).

### Changed
- The event feed names the enemy behind the game's shared "not remembered" kill flag ("Zombie died") instead of "an
  enemy the room doesn't remember died", and no longer says people appear (Barry is with you in those scenes).
- Jill's any% route: the search upstairs for Wesker ends at the top of the stairs, so the way back down is drawn.

## [0.3.3] - 2026-10-26

The portable zip keeps its own data, so you can keep several copies.

### Changed
- **The portable zip keeps everything in its own folder**: settings, logs, runs, captures and secrets now go in a
  `data` folder beside `Lockpick Trainer.exe` instead of `%LOCALAPPDATA%\Lockpick`, so several unzipped copies stay
  separate. Neither the zip nor the app writes to the registry. Installed copies are unchanged. If you already use the
  portable zip, copy what's in `%LOCALAPPDATA%\Lockpick` into the new `data` folder to keep your settings and runs
  (saved passwords only work for the same Windows user).

## [0.3.2] - 2026-10-26

Steam's Resident Evil, OBS from Lockpick's side, and the game kept running when you click away.

### Added
- **Keep the game running when it isn't in front** (Trainer → Cheats, opt-in, #21): Resident Evil normally pauses, and
  goes black in OBS, when you click another window. With this on it keeps playing, and keys you type in other windows
  don't reach it (your controller still does). It's silent in the background (the game mutes itself), goes off for
  a challenge or a leaderboard run and when Lockpick closes, and puts your keyboard back every time. Checked live on
  GOG's English release; the other versions follow once each is checked.
- **Steam's Resident Evil (1996)**, English and Japanese: each is GOG's game inside Steam's wrapper, so everything that
  works on GOG works on Steam. Lockpick finds the Steam install, its leaderboard checks compare Steam's files, and its display
  settings change Steam's launcher settings (the launcher rewrites the game's own file at every start).
- **OBS through its WebSocket server** (Stream → OBS, optional, #65): Lockpick records each run, adds chapter markers,
  saves the replay buffer and switches to a "paused" scene itself, the same as the OBS script, so it works with OBS on
  another PC (a streaming PC) or without installing the script. **Set up OBS** makes a "Lockpick" scene with the
  widgets (and a Game Capture when OBS is on this PC). **Fill in from OBS on this PC** takes OBS's port and password;
  the password is kept encrypted for your Windows account and never sent, only the answer to OBS's challenge. While
  Lockpick drives this PC's OBS, the script leaves those parts to it (its hotkeys keep working).

### Changed
- **One page for running:** Challenges is now a tab on the Runs page. Starting a challenge switches to the splits, with
  the challenge (and Try again / Stop) above the timer; whether a leaderboard run is ready shows there too. LiveSplit's
  settings sit with the splits.
- **Splits like LiveSplit:** item splits land on the frame the item arrives (they were up to a second late); an item
  picked up before its split is due splits as soon as it comes up; times show hundredths, updated every game tick.
- A LiveSplit file you import takes its splits' rules from your own sets with the same names, then the built-in ones,
  then item names; splits left without a rule are listed above the timer with a way to fix them.

### Fixed
- The OBS script no longer stops a recording you started yourself when a run ends, or the next attempt's recording
  when you try again within the stop delay, and a run's first chapter marker is no longer lost when the script
  starts the recording; its Game Capture finds the Japanese and Steam versions' executable too.
- The game's own demo (left at the title) no longer starts an attempt, or an OBS recording.
- The overlay shows one attempt number, the run's.

## [0.3.1] - 2026-10-17

Lockpick now works with every PC version the speedrun.com leaderboard allows.

### Added
- **The Japanese version with Classic REbirth**, the one most PC runners use (MediaKite, or GOG's Japanese release
  with REbirth in its own folder), and **GOG's Japanese release** (`Biohazard.exe`). Both checked in play: rooms, HP,
  the timer, inventory, enemies, pickups, the map and the overlay. Cheats cover what has been checked so far (health,
  and ammo in the first slots on GOG's Japanese release).
- **Leaderboard run on the Japanese versions:** checks the game files, the REbirth version, and that quickturn,
  tactical reload and REbirth's debug menu are off.

### Fixed
- The leaderboard check and the map follow the game you're playing, so the English and Japanese releases can share
  a folder.

Developers: profiles for other builds are ported by finding the same code in them (`tools/re1/port_profile.py`), and
name their tables and pointers instead of keeping addresses in code. Details in docs/COMPATIBILITY.md.

## [0.3.0] - 2026-10-16

### Added
- **Overlay** (Settings → Overlay, off until you turn it on): the in-game time, HP, the room and your challenge
  (clean, or failed with the reason) in a small panel on top of the game's picture, in the corner you pick. Clicks
  go through it and it never takes focus, so the game keeps running; it follows the game window across monitors and
  scaling, sits on the 4:3 picture (from the game's own display settings) right above the game window (other
  windows cover it with the game), works windowed, borderless and in fullscreen, and hides while the game is
  minimized. A hotkey shows or hides it without leaving the game (Settings → Hotkeys); it costs the
  game no frames (30 per second with and without it).
- **Overlay widgets and the map:** each overlay widget has its own corner (or is off), and widgets in the same
  corner stack. The new map widget is a minimap around you (north-up, framed). Its hotkey (Settings → Hotkeys,
  "Map: next view") cycles through the views you tick in Settings → Overlay: the minimap, the whole room over the
  game's picture on a dark backdrop, the same see-through, or hidden. Options: how far the minimap reaches, its
  size, how strong the see-through map is, and names of doors, items and people on the full-screen map.
- **Controller buttons for actions** (Settings → Hotkeys → Set button…): any key action — the map's views and zoom,
  the run keys, the overlay — can also be a controller button, so a player on a pad never needs the keyboard.
- **Big messages on the overlay:** "Challenge failed" with the reason, "You died" and "Run finished" / "New personal
  best!" centred at the top of the picture for a few seconds (any widget can now sit top centre).
- **What just happened** (#43): items picked up, used, received or combined, documents and maps taken, first visits,
  unlocks, kills, enemies appearing, damage and named story events in plain words — in the event log and as an
  overlay widget (the last few, for 20 seconds), from the same knowledge as captures and the Progress page.
- **Whole-floor map:** every room of the floor you're on, put together from the doors between them (Map → Whole
  floor: scroll to zoom, drag to move, double-click or Fit to see it all), with the room you're in outlined, you on
  it, and the items you haven't taken yet. On the overlay it is two more map views (whole floor, also see-through):
  the whole floor, then closer around you with the map's zoom key.
- **Condition on the overlay:** Fine, Caution, Danger or Poison next to your HP, worked out the way the game's
  status screen does it (poison read from the player's status), with the HP bar in the same colour; also in the API
  (`health.condition`) and on the OBS HUD page.
- **Leaderboard run** (Runs page): for runs you'll submit, Lockpick locks its trainer read-only, keeps ask mode off,
  checks every game file against GOG's release (no mods) and lists the rules only you can keep; each attempt's
  record says whether every check passed.
- **LiveSplit's engine inside Lockpick** (livesplit-core, docs/LIVESPLIT_CORE.md): Lockpick's runs are also timed by
  LiveSplit's own engine, so no LiveSplit is needed — its layout is the overlay's one splits widget (*Run splits*,
  with the world record from speedrun.com when the leaderboard is on) and an OBS page (`/overlay/livesplit`), and
  each split set is kept as a LiveSplit file with every attempt.
- **Pages in sections:** Settings, Runs, Challenges, Trainer and Debug are split into sections listed under the page
  in the navigation (Settings → General, Game display, Overlay, Hotkeys, speedrun.com, API and profiles; Runs → Splits,
  Training, Leaderboard, LiveSplit; …), so each shows only what that part needs; in a narrow window the sections are a
  row above the page. The overlay's map options are a card of their own.
- **Challenge feedback** (#59): a death counter (deaths this session, any play; on the Challenges page and the
  overlay's challenge line), optional sounds when a challenge fails or you die — each its own, from Windows' sounds
  or a .wav file of yours (Challenges → Feedback, with a Play button) — and **Challenge: try again** as a key or
  controller button (Settings → Hotkeys). Closing the game ends a challenge's attempt.
- **Support bundle and error reports** (#63): Settings → General → *Make a support bundle* zips the last few days'
  logs, the settings without the API token and the versions (the Windows user folder replaced by `%USERPROFILE%`; no
  game files, saves or captures) to attach to an issue. An unexpected error is logged and shown above the page with
  the same offer; after a crash, the next start offers it.
- **Player guide** (#62, [docs/GUIDE.md](docs/GUIDE.md)): install, finding your way, challenges, runs and the
  leaderboard, cheats, the overlay, OBS, LiveSplit, keys and buttons, display modes, captures, troubleshooting.
- **The window comes back where you left it** (#63): Lockpick's window reopens at its place and size (or maximized),
  remembered for each monitor setup; a place that's no longer on a screen falls back to centred.
- **Streamer mode** (#63, Settings → General): file paths, process ids and the API token are blurred wherever Lockpick
  shows them, so its window can be on stream (copy buttons still work).
- **OBS hotkeys for Lockpick** (the OBS script): split, undo, skip, reset, try a challenge again, the overlay, the map's
  view and zoom, and capture markers from OBS's own hotkeys — no cheats.
- **Lockpick's OBS script** (#66, `obs/lockpick.lua` next to Lockpick.exe; Stream → OBS shows where): one button
  adds Lockpick's sources and a Game Capture of the game to the scene, and, each optional, records each run, puts
  chapter markers at splits, deaths and fails, saves the replay buffer on a death, a fail or a new best, and switches to
  a "paused" scene while the game isn't in front. It reads files Lockpick writes (no WebSocket password).
- **Overlay layout editor** (#55, Settings → Overlay → Layout): the game's picture in small with every widget that's
  on, drawn as it looks; drag one anywhere and it keeps that spot (as a share of the picture, so on any size). Its
  place then reads *Your spot*; choosing a corner, or *Back to the corners*, puts it back.
- **Every widget in OBS** (#60): any overlay widget — the map, the enemies, the status, big messages — as its own
  Browser Source (`/overlay/widget?id=…`, drawn live as large as fits), and **Stream → OBS** lists each widget's
  address with a **Copy OBS link** button.
- **Captures mark the run by themselves** (#58): a running capture gets a marker at each run start, split, run end and
  challenge fail. Captures also record the game window as it changes (#18): place, size, monitor, DPI, mode, and
  whether it's in front or minimized.
- **No focus taken over exclusive fullscreen** (#54): the ask-mode question shows without taking the focus while the
  game is exclusive fullscreen and in front (the game would minimize).
- **Wrong-splits warning:** a run started with splits for the other character (e.g. Jill's in Chris's game) says so
  at once — a big message and a note on the Runs page.
- **A quieter event log:** it reads like the feed — rooms and damage in plain words (with the attack), no camera cuts
  or duplicate HP lines. Captures still record every raw event.
- **Your overlay, your way** (Settings → Overlay):
  - **Size per widget** (50–200%), and **live while you play**: keys or controller buttons for *Overlay: next widget to
    size*, *bigger* and *smaller* (Settings → Hotkeys; OBS hotkeys too) — a big message says which widget and its size.
  - **What the time/HP widget shows:** the time, HP, the room and the challenge line, each on or off.
  - **Look:** the panels' background transparency, the text colour and the font.
  - **Layouts to keep and share:** *Save layout…* / *Load layout…* — corners and spots, sizes, the look and the map's
    options in one file (nothing else from your settings); a loaded file's values are checked.
- **Training captures** (#61, Debug → Capture, opt-in each time): every game frame with the buttons the game read,
  for research on AI that learns to play from people — the same game data as a capture, more often, plus the inputs;
  nothing about you.
- **More in "what just happened"** (#43): hits named by the damage chart ("took damage (96 → 90, Zombie ankle bite
  (6))"), poison ticks, zombies getting back up, and pushes told once with their length ("pushed something (5.7 s)").
- **Split editor** (#57, Runs → *Edit splits*): rename splits and choose what triggers each — entering a room (from
  a given room, or from another area), getting an item (anywhere or in a room), the ending, or the split key — add,
  remove and reorder them, and save them as your own splits. Built-in and imported splits stay as they are: editing
  one saves your copy.
- **Runs from a save start at your split:** Runs → Splits → *Start at* picks where your save is in the route; the
  splits before it stay empty (nothing is filled in), and LiveSplit — built in or driven — starts there too.
- **Runs from a save on the game's clock:** practice from a save is now timed on the game's own clock, so the splits
  match the game (an option times it from zero at the load instead). Splits it didn't see are never filled in.
- **Story events in the right character's words:** a story-flag label is shown only in the games it was confirmed in
  (most were confirmed in Jill's): in Chris's game Lockpick no longer says "rescued after fainting" when he meets
  Rebecca in the save room, or announces the Emblem slot at the start (it's his starting state). Chris's own labels from
  a live run were added, and the research notes that were showing in two labels were taken out.
- **Widgets only in a game:** the map (on the overlay, in OBS and in its own window) and the time/HP widget in OBS and
  pop-outs hide after a death and at the title, instead of showing the last game's room and values.
- **The map's own window:** the popped-out map is drawn live at the window's size with its own view (right-click):
  around you (close, medium or far), the whole room, or the whole floor you're on (whole, closer or closest,
  following you), names on or off — remembered with the window, whatever the overlay's map shows.
- **Comparisons and where the time is** (Runs page): compare your splits against your personal best or any of
  LiveSplit's comparisons (best segments, average, median, latest run, balanced PB, …) on the page, the overlay and
  LiveSplit's layout; a new card lists the segments where your PB loses most against your best segments. Practice and
  trainer attempts no longer reach the LiveSplit file's history (they could have set a PB or a gold there), and a
  finished attempt is kept when you switch splits right after it.
- **Widgets off the game:** every overlay widget can also **pop out** into a window of its own (Settings → Overlay →
  Pop out): move it to any monitor, resize it, and right-click it for a green background (OBS's chroma key) or to
  keep it on top; windows remember their place and reopen with Lockpick. The new **Stream page** shows the widgets
  inside Lockpick as large as you like — Customize picks which, their order and sizes — so the game's picture can
  stay clean.
- **Runs and splits** (new Runs page; [docs/RUNS.md](docs/RUNS.md)): pick the community's splits (Jill or Chris,
  either ending, NMG or MG), a key-item route, or import a LiveSplit file (the community's from speedrun.com or your
  own), and every new game is timed on the game's own clock — the in-game time the leaderboard uses — splitting by
  itself on rooms, key items and the ending. Each split shows against your best (green ahead, red behind, gold for a
  best segment); a death, the title menu or the game closing ends the attempt, and every attempt is kept. Save the
  splits with your times as a LiveSplit file (e.g. for a submission). Keys for split, undo, skip and reset (Settings
  → Hotkeys, including LiveSplit's number-pad keys), and a **Run splits** overlay widget. The built-in split points
  come from the community autosplitter and are marked as not yet checked in a full run.
- **Enemies widget:** the enemies and bosses in the room with their HP and a bar (against the most HP each has had
  since you came in), bosses first; hidden when the room is clear.
- **Game display** (Settings → Game display): switch Resident Evil between windowed, borderless (fills the screen —
  best for the overlay and streaming) and fullscreen, pick a resolution your main monitor supports, how the picture
  is scaled (4:3, whole multiples, stretched), vertical sync, gamma, texture filtering and antialiasing — every
  setting of the game's display file, all on one screen. Lockpick changes them only when you press Apply, keeps your
  original (Restore my original puts it back), warns when fullscreen would change your monitor's resolution, and
  offers Restart Resident Evil right there (two clicks: anything since your last save is lost). It can also move
  the game window to another monitor, which the game itself doesn't allow.
- **speedrun.com leaderboard** (Runs → Leaderboard, off until you turn it on): the board for your splits, the world
  record, the top runs, where your best would place and your own run; the overlay shows the world record too.
- **Submit runs to speedrun.com** (Runs → Submit, with your API key saved): a finished attempt with its in-game
  time, the board, the date, your video link and the run summary, sent only after you press Submit twice; the
  moderators verify it as usual. Runs with the trainer on or from a save can't be sent.
- **speedrun.com account** (Settings → speedrun.com): your name for the boards, and an optional API key for
  submitting runs — checked with speedrun.com, kept encrypted for your Windows account (never in settings or
  logs), sent only to speedrun.com, never shown again, removable any time.
- **Run summary and record for submissions:** Runs → Attempts → *Copy run summary* (category, result and in-game
  time, every split, the game build and its executable's SHA-256, trainer off) and *Save run record…* (the same as
  JSON). Attempts are numbered across sessions, and closing Lockpick keeps the attempt that was running.
- **LiveSplit** (Runs → LiveSplit, through LiveSplit's TCP server): *Follow* moves Lockpick's run with your
  LiveSplit and its autosplitter (as leaderboard runs require) without splitting twice; *Drive* lets Lockpick start,
  split and reset LiveSplit and set its game time from the game's clock.
- **OBS splits and feed pages, run API:** `/overlay/splits` shows the run's splits and `/overlay/feed` what just
  happened, each in an OBS Browser Source, and
  `GET /api/v1/run` (plus `POST /api/v1/run/split`, `/undo`, `/skip`, `/reset`) serves the run to other tools.
- **OBS HUD page:** with the API on, `http://127.0.0.1:1996/overlay/hud?token=…` is a transparent page for an OBS
  Browser Source with the same time, HP, room and challenge state, in the corner and size you choose
  ([docs/API.md](docs/API.md)).

### Fixed
- Closing Lockpick could leave it running in the background (its shutdown waited on itself).

## [0.2.0] - 2026-10-15

The core trainer: cheats you switch on deliberately (health, ammo, inventory, hotkeys), every change checked, read
back and logged, and guided challenges that Lockpick can't touch. Also an API for other tools, a new look, and
layouts for wide, ultrawide and portrait windows.

### Added
- **Challenges** tab, step by step: choose a challenge (Speedrun, No damage, Knife only), add rules to it (a no-damage
  knife run…), start it. While it runs Lockpick can't change your game. The tab shows whether the attempt is still
  clean or why it failed (a hit, a shot from anything but the knife, cheats switched on) with the in-game time; a new
  game or a loaded save starts a new attempt. Overlays for a failed challenge come in 0.3.0.
- **Trainer** tab for cheats. Lockpick starts with cheats off every time and keeps them off while a challenge runs.
  With cheats on: set HP, fill it, or lock it (put back whenever the game lowers it), and infinite ammo (the equipped
  weapon's rounds never go down; a reload raises the count). Every change is checked against the game's limits (HP up
  to max HP), read back and logged with the value before and after (`trainer/actions.jsonl`); a run the trainer
  changed is marked as such until the next new game or load.
- Inventory editor (Trainer page, Trainer Mode): change the count of what you carry (a weapon's loaded rounds, ammo,
  ink ribbons), and put any item into the item box — take it out at a box and the game loads it like a pickup.
- Trainer hotkeys (Settings → Trainer hotkeys, off until you pick keys): fill HP, lock / unlock HP, infinite ammo
  on / off — while the game has focus. They do nothing unless Trainer Mode is on with write access, and a key can't
  be given to two actions.
- An API for other tools ([docs/API.md](docs/API.md)), off until you turn it on in Settings: scripts, OBS, LiveSplit, a
  Stream Deck or macro tools can read the game state and a live event stream, start and stop captures and add
  markers, and, if you allow it, use trainer commands (only in Trainer Mode, checked and logged like the buttons).
  It listens on this PC only unless you allow other devices on your network, and every request needs a token.
- Saves show up in the event log ("Game saved to slot 3", with the in-game time) and in captures. Lockpick watches the
  game's save folder for this and never opens the files.
- Branding: an app icon (a pick set: hook, rake and tension wrench), a typewriter wordmark (Special Elite, Apache 2.0)
  and colours taken from the game's status screen.

### Changed
- Pages use the width of wide and ultrawide windows: cards sit side by side in as many columns as fit, long lists
  (the story so far, the item box) flow into columns, and the dashboard gives the event log its own column. The map
  page keeps the map as large as the window allows; its legend moves beside the map on wide windows, under it on tall
  ones and behind a button on small ones. The Debug page scrolls in a short window instead of squeezing its lists.

### Fixed
- The header's status badge is green when connected, red for an unsupported build and amber in replay (it was always
  grey), and shortens itself instead of running into the wordmark in a narrow window.
- Trainer inventory rows line up, and keep the count and Set on the item's line in a narrow window.
- The map's room line says "1 door" / "1 camera" instead of "1 doors".

## [0.1.6] - 2026-10-15

Read-only pages for what you carry, where you are and how far you've got, and capture markers from the controller.

### Added
- Capture markers from the controller: press **L2** (LT on an Xbox-style pad) while a capture is running to mark the
  moment, without letting go of the pad. It works with Xbox-style pads and with PlayStation pads and other USB pads
  read directly; a pad whose buttons are numbered differently (a PS1 USB adapter) can be bound by pressing the button
  (Settings → Capture markers → Bind…), or the button turned off. Only button presses are read, never the keyboard,
  and the game still gets every press.
- Items page (read-only): what you carry (with the equipped weapon and the rounds loaded) and everything in the
  item box.
- Map page (read-only): the room you're in, drawn from your own install's room files — walls and furniture, doors with the room they lead to, item spots for your character — with your position and facing live. Items show as in the room now, taken,
  or coming later in the story, and items put in place (the gold emblem in its slot) show where they sit.
  Enemies and people in the room are drawn live (a shape and colour per kind, bosses as stars, people as blue
  squares), and rooms show their names next to the number on the map and the dashboard. Jill's map and progress
  no longer list ink ribbons: the game never places them in her game. Item boxes and typewriters are marked on the map. The map shows only what is in the room now (a switch adds the items you've taken or that
  come later), with a legend beside it (under it on a narrow window) listing just what this room holds.
- Progress page (read-only): per area, items taken vs still there, documents and maps, rooms explored (the game's own
  room table) and enemies defeated for good, with what's left and where; plus the story so far from the named events.
  Read from the game's flags, so it works from a loaded save. `tools/bounties/progress_preview.py` shows the same for a
  capture.

### Fixed
- Progress: an item counts as taken only from a room you have been in. Items a story event puts in place later
  (Chris's flamethrower) read as taken from the start of a game.
- Screen readers name the navigation pages, the dashboard's event log and the Debug log (they read internal type
  names before).

### Developer tooling (not shipped)
- `tools/Lockpick.Autoplay`: a test harness for the maintainer — virtual Xbox 360 controller (ViGEmBus), game-state
  checks, background screenshots and an optional focus patch for runs behind other windows (no key is ever
  injected). Never part of the app or its releases.

## [0.1.5] - 2026-10-13

Research tooling, used across three long live sessions (Jill from a new game to the mansion revisited).

### Added
- Capture markers: press **Ctrl+Shift+M** while playing (configurable in Settings) or use **Add marker** on the Debug
  page to mark a moment, then describe it. Markers record the time, room and game time, are saved in the capture
  (`markers.jsonl`) and are listed in the bounty analysis report.
- Ask mode for captures (Debug page, off by default): when the game does something Lockpick can't explain — usually
  a cutscene or scripted event — it pauses the game and asks what happened. Answers are saved in the capture with the
  changes they explain. Pausing suspends the game process only while the question is open (opt-in; never writes).
  It works out what it can without asking — rooms visited (the game's own table), maps and documents, events a room's
  own script triggers, cleared story flags, and story changes within 5 s of something the player did (an item used,
  received or picked up, a kill, a door, damage, pushing, an enemy appearing; saved as "auto" notes) — so it asks only
  about cutscenes, surprises and switch puzzles. The question says when the change happened and what came just before.
  About 85 story and puzzle events are named so far (`data/games/re1/flag-labels.json`).
- Capture timeline (`tools/bounties/timeline.py`): a readable account of a capture — rooms, pickups, documents and
  maps by name, kills, enemies getting back up, unlocked doors, pushes, item box, item use, saves and markers.
- Room index (`tools/rdt/rdt_index.py` → `data/games/re1/room-index.json`): which item, enemy or door each game flag
  belongs to, read from the game's own room files (ids and numbers only); item and enemy names from BioRand (MIT).
- Item and enemy names confirmed in play: about 60 item ids (the bazooka ids 7/8 are the other way round from
  BioRand's list: 7 explosive, 8 acid) and most enemy types (zombies, dogs, crows, plants, Yawn, Hunters, giant spiders,
  wasps, Neptune, Plant 42); attacks named by their damage (speedrun damage chart, matches the GOG build).
- Research tools for the voice-over: local transcription (`tools/voice`) and the separate, optional
  [lockpick-subtitles](https://github.com/mscrnt/lockpick-subtitles) pack (fan transcription; the dialogue is Capcom's).
- `tools/bounties/ask_preview.py`: replays captures through ask mode's rules to see what would be asked.
- Captures also record the game's text buffer (research for on-screen text).
- A "buy me a coffee" link in the README and the repository's Sponsor button (optional; it doesn't buy features or
  bounties).

### Fixed
- Bounty analyzer: enemy encounters and kills are only counted once a room has settled; for about a frame after a
  door the enemy slots still hold the previous room's values, which were reported as phantom enemies and kills.

## [0.1.4] - 2026-10-13

### Added
- Captures record the game's state memory (`memory-N.bin`: the main game-state block and a system block, 10 Hz,
  keyframes plus changed bytes), so every bounty that lives in game memory can be checked from a capture — including
  values Lockpick hasn't identified yet. The bounty analyzer decodes all profile fields from it.
- Diagnostic (unverified) fields for 8 enemy slots, all event-flag banks and the full 48-entry item box; analyzer
  detectors for enemy types, kills, door locks, puzzle/scenario flags, room flags and room item pickups.

### Changed
- Field reads are grouped into a few block reads per poll instead of one read per field.

### Fixed
- The app shows its version consistently as the release version (e.g. `0.1.3`, keeping any pre-release suffix);
  the startup log and capture info no longer show the four-part `0.1.3.0`.

## [0.1.3] - 2026-10-13

### Added
- Bounty automation: `data/bounties.json` is the source of truth and `docs/CAPTURE_BOUNTIES.md` is generated;
  `tools/bounties/bounties.py` analyzes capture zips and records findings (only unambiguous ones verify a bounty
  automatically; captures with trainer writes never do); the `capture-triage` workflow comments on Capture
  submission issues with the analysis.

### Fixed
- Capture recordings stay compact: raw values are left to the trace and timer-only changes are written at most
  once per second (a 20-minute live session had produced a 64 MB recording).
- A capture started mid-session now records the current state first (a quiet session left the recording empty).
- Bounty analysis ignores changes outside gameplay (attract-mode demos, loads, title screen).

### Changed
- Bounty rules: unpaid, completed only by captures (never code changes), no automated or AI-generated replies;
  blank issues disabled so reports go through the forms.

## [0.1.2] - 2026-10-13

### Added
- Capture bounty list (docs/CAPTURE_BOUNTIES.md) covering everything to capture through 1.0.0, and a
  Capture submission issue form.
- CONTRIBUTING.md, README badges, milestones, labels, bounty issues, a public roadmap board and Discussions.
- In-app credits (Settings → Credits), generated from CONTRIBUTORS.md; bounties documented as unpaid (credit only).

### Changed
- CI actions updated (checkout v7, setup-dotnet v6, upload-artifact v7, codeql-action v4); xunit.runner.visualstudio 4.0.1.

## [0.1.1] - 2026-10-13

### Added
- Installer and automatic updates (Velopack): `Lockpick-win-Setup.exe` per-user install, update checks 20 s after start
  and every 6 hours (optional; pre-release channel optional), *Update and restart* from Settings, and a sidebar version
  label that highlights available updates.
- First-time setup: welcome and safety notes, game location with build check and file browser, update preferences;
  re-runnable from Settings.
- Debug page (replaces Diagnostics) with one-click session capture: raw field traces, recordings, events and build
  fingerprints per game attachment, zipped on stop or on exit. No personal information recorded.
- `GameLoaded` event (new game / save loaded), derived from in-game timer discontinuities and tested against two live
  recordings; replay now detects events on recorded timestamps so playback speed can't fake a load.
- Second live fixture: Jill session with death, New Game, a route through 1F/2F, healing and loading a save.
- Profile: runtime-verified inventory slots, equipped slot and inventory pointer (read-only diagnostics).
- CONTRIBUTORS.md and docs/CAPTURE.md.

### Changed
- Releases are built with Velopack and are normal (non-pre-release) GitHub releases unless the version has a suffix.

## [0.1.0] - 2026-10-12

**Foundation.** Verified live against GOG `ResidentEvil.exe` 1.0 hotfix4 in Chris and Jill games.

### Added
- Game-agnostic core: `GameSnapshot` with quality-tagged `Field<T>` values (valid / stale / unavailable / unsupported),
  typed events and an event detector, adapter contract, version profiles with validation, exact build matching and
  runtime image verification (PE header + code anchors), `GameMonitor` with process lifecycle handling, session
  recording and replay, non-blocking JSON-lines logging.
- Windows platform layer: process discovery by executable name and full path, read-only process handles,
  `ReadProcessMemory` typed reads, high-resolution waitable timer for 30 Hz polling, GOG install lookup.
- Resident Evil adapter and profile for GOG `ResidentEvil.exe` 1.0 hotfix4 (USA): stage, room, camera, HP, max HP,
  character, in-game timer, position and session state, live-verified on 2026-10-12.
- Desktop app (Avalonia 12): dashboard (connection, fingerprint, game state, event log), diagnostics (raw fields with
  verification level, poll statistics, live log, recording), settings (install path override, build check, profiles).
  Responsive compact layout for portrait windows; Lucide icons.
- `lockpick-probe` tool: `identify`, `watch`, `trace`, recording.
- Tests: mock adapter, fake process memory, RE1 adapter, profile validation, monitor lifecycle and failure handling,
  fixture playback from a live game trace, Win32 integration tests. Benchmarks with BenchmarkDotNet.
- CI (build + test + data hygiene), CodeQL, scheduled benchmarks, tag-driven releases, Dependabot, issue templates.
- Documentation: architecture with decision records, roadmap with acceptance criteria, research, compatibility,
  memory map, performance, development guide.
