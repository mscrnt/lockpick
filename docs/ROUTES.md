# Routes and the route guide

The route guide follows a route as the game is played: the step the player is on, the way there and the buttons to
press. Read-only, like the rest of Lockpick's reading: it never changes the game.

## Routes

Routes live in `data/games/re1/routes.json`, a list of steps per character:

| Step | Done when |
|---|---|
| `{"get": item, "in": room}` | the item is held, or gone from where it was (its pickup flag) |
| `{"use": item, "in": room}` | its use spot is filled or the door it opens is unlocked; when the game doesn't say, when it leaves the inventory (not into the item box) while that step is shown |
| `{"go": room}` | its `done` flag is set; without one, the player has been in the room and left it (or arrived, when the next step is in the same room) |
| `{"end": true}` | never (the route's end) |

`hint` says what the room data can't (how a puzzle opens); hints are only written where the puzzle is certain. Any step
can carry `"done": "field:0xMASK"`, the game flag set once it has happened (story flags from flag-labels.json and the
room scripts); a step with a readable flag is done exactly when it's set. A step the game gives no sign of (a lever, a
crank turned and kept) is done once the player has been in its room and left. The step the player is on is the first
one the flags don't show as done; a step they can't tell about counts as done when a later one is, so steps done out of
order (a crest placed early) neither hold the guide back nor skip what's left. A use repeated at the same spot (the
square crank, twice at the water gate) isn't judged by the spot. `"optional": true` marks a step the game doesn't
need (the guardhouse statue over the hole: a door goes the other way round); it's passed once a later step is done,
and so is a step that gets an item from the inventory (a Doom book's medal) once a later one is.

**Sources for the steps:** the key-item order of the community split set; the room files (items, use spots, doors,
flags, scripts); and the events and puzzles of a full-game Jill walkthrough (GameFAQs, PlayStation Director's Cut),
facts only, each matched to the room files (where the item or spot is in Jill's PC room file, and the room ids). Two
things it settled: the square crank in 11B is armed only while the player stands on the stepladder (the room's main
script, `stateFlags[24]`), and the 15 minutes for Richard's serum. Where it differs from the PC game, the PC game wins:
it has Barry hand over the lockpick in the passage behind the main hall's stairs, but on PC (live 2026-10-27, the story
flags logged as the owner played) the search upstairs comes first: 2F search, the stairs cutscene on coming down
(`flagScenarioB0` 0x04000000), then the lockpick (`flagScenarioA3` 0x8). The same log confirmed the done flags for the
herbicide, the piano, both emblems and the lion head.

**Jill Any% NMG (draft):** the key-item order of the Jill Any% NMG split set, with the use spots read from the room
files (the room scripts' use actions: emblems and music notes in 105/10F, the herbicide in 10C, the four crests in 11A,
the cranks and batteries in the courtyard and the lab). Checked by walking it through the room files (every step
reachable from the last with only the keys collected so far, scratch `walk.cs`, 2026-10-27); **not yet followed
through a run**. Items placed by scripts (the serum in 100, the battery in 719) have no zone in the room file, so the
guide leads to the room.

**Full-game routes (simulated): Jill and Chris.** `re1-jill-full-sim` and `re1-chris-full-sim` are written by the route
simulator (`tools/routesim`, its README): it plays the game through from a new game using the room and event
scripts of the player's own room files, taking the nearest thing that moves the story on, and keeps the steps a
player needs, each with the story flag that says it's done, the spot the line points at and the flag's label as the
hint. Both reach the flare at the heliport (Jill 110 steps, Chris 102). Jill's covers every get and use step of the
hand-made route above, in a different order (nearest first, not the fastest). What the scripts don't say (walls,
water, the lab computer, Plant 42's death, the Doom books) is in `tools/routesim/rules.json`, each rule with its
source. Regenerate with `python tools/routesim/build_routes.py <install>` after changing the tools or the rules
(`--check` compares). **Not followed in a real game yet.**

**Routes for a split set: "Follow my splits".** For every built-in split set (`splits.json`) the simulator also
writes a route, `<set id>-sim`: it heads for each split in turn (the room transitions and the items), from wherever the
player is, and makes the nearest progress whenever the next split can't be reached yet (the key, the scene that opens
the way). Each split is a step of its own ("Split: Yawn 1"); a key-item split got on the way to an earlier one is the
step that took it. The route picker's "Follow my splits" uses the route for the split set chosen on the Runs page,
and the character's full-game route for a set of the player's own (those can't be simulated in the app yet).
Glitched sets are reached the ordinary way.

**Time limits:** a route's `timers` count down in game time from when a start flag goes on until a stop flag is set:
Richard's serum, 15 minutes from "serum errand starts" (`flagScenarioA2` 0x40000) to "serum given" (`flagScenarioB3`
0x04000000). The 15 minutes come from the owner's answer in ask mode and a walkthrough; the game's own deadline isn't
found yet (no value runs with the clock in its data, scanned live 2026-10-27, B604). When the start flag is already on
the first time Lockpick looks (it was started late, or a save was loaded), the time left is shown as unknown.

**Spots the room file doesn't place:** `"at": [x, z]` gives a step's spot in its room. The serum in 100 is placed at
(0, 0) by the room file; the shelf is an event zone (7400, 3880, 600 × 3370: event 2 takes the serum and clears
"Richard is waiting for the serum").

**Puzzles:** a step can list `spots`, the zones to press in order, numbered on the maps; standing at one shows "Push the
switch: n of N, …". The gallery (117): each switch under a painting runs its own event script (room 117's events 14–19,
`tools/rdt/events.py`), which counts only if the one before set its `sys` flag: 134 → 136 → 137 → 135 → 133 → 138
(the plaques, room messages 128 + n: newborn baby, infant, lively boy, young man, tired middle-aged man, old man), then
the switch under the mourners (event 12, message 139), where the Star crest appears. The game keeps only the last
switch pressed, in script flag bank 4 (`sys`, `flagSys0` at 0xBE41C8: the script engine's bank switch at 0x460590;
live 2026-10-27: 27 after the young man, then 25 after the middle-aged man), so the map ticks off the spots up to it:
pressed ones dim with a tick, the next one filled. The bank is in the GOG English profile so far.

## The way there

`RoutePlanner` (Core) searches the doors (Dijkstra over door arrival points: straight-line distance plus a cost per
door) through the doors the player can open: no lock, unlocked (`flagLocks`), or the key held. Doors locked with no key
open from the other side or by an event; when only such a door leads on, the way is shown through it, marked. Each
room's line is a grid search (`RoomPath`, 250-unit cells, walls grown by 350) round the room's blocking shapes, then
straightened. Collision records with flag 0x300 (walls) or 0x200 (furniture: the dining table and chairs in 105) are
solid; only the steps and slopes (type 4, flags 0) are floor. A path end that falls inside a wall (doors sit in walls)
snaps to the nearest open cell in the same open area as the start, not to one past the room's outer wall (the open
space outside can even be the biggest, when a long record far off stretches the grid: 10D). Still drawn straight: 19 of
the 724 door-to-door pairs in Jill's rooms, mostly in staircase rooms, where 0x200 records (railings) also cover the
stairs' upper end; the records carry no height, and which floor level each bit means is still to work out.

RE1 specifics (`Re1RoomMaps`): door records carry the lock (+0x0E, bit 0x80 = starts locked, low 6 bits the flag) and
the key (+0x18): an item, or 254 "opens from this side" / 255 "locked from the other side" (every keyless locked door in
both characters' room files is a 254/255 pair, one per side; 206 → 207 has no door back and is 254), or 0, a door the
story opens (11A → 11B: the four crests; the guide shows the way through such a door, marked, when there's no other).
Jill's lockpick opens the doors that take Chris's Sword key; no door leads into the mansion revisited
(stages 6/7), so a door from outside into the mansion (300 → 11B) also leads to its revisited room (61B), the way the
game switches by itself.

## In the game picture

The overlay widget *Route guide in the game picture* (`route-picture`, a full-picture layer) draws the way on the floor
of the room the player is in, a ring at the step's spot (or the door the way leaves by), a puzzle's numbered switches,
and the prompts at the bottom middle of the picture; the Route guide card switches the way and the buttons on and off. Points go
through the current camera's projection (`CameraProjection`): RE1 room cameras (`RDT_Camera`, 0x2C bytes: position
+0x08, target +0x14, field of view +0x28, ecruells/resident-evil-pc-decomp) are pinhole cameras in the 320×240 frame,
basis n = target − position, r = (dz, 0, −dx), u = n × r, the room's −Y up. Checked live 2026-10-27: Jill's projected
feet landed on her boots in room 115 (camera 2), and the room's wall outlines followed its floor. The way is drawn at
the player's height, cut into 250-unit pieces so a stretch behind the camera drops out cleanly.

## Buttons

Prompts name the game's controls (`GameControl`: action, run, aim, items, options, directions) and the app draws them
for the controller in use (`ButtonGlyphs`): an Xbox pad's A, X, RT, Y, View (checked button by button, B207); a
PlayStation pad's buttons in the same places (Cross, Square, R2, Triangle, Share; not yet checked one by one); the
keyboard keys from the game's key map (`Re1Controls`, keyMap0–7). "The controller in use" is the kind of pad pressed
last (XInput: Xbox; Raw Input: PlayStation), the keyboard until one is. Pictures and their licences:
`THIRD_PARTY_NOTICES.md`.
