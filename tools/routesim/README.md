# Route simulator (`tools/routesim`)

Plays RE1 through from a new game, as Jill or Chris, using the game's own room files from your installation
(read-only; nothing from them is copied into the repository). It's how the full-game routes in
`data/games/re1/routes.json` are made: no one has to write the steps by hand, and each step knows the story flag
that says it's done.

```
python tools/routesim/plan.py "G:\GOG Games\Resident Evil" jill [--steps 600] [--json route.json] [--verbose]
python tools/routesim/build_routes.py "G:\GOG Games\Resident Evil" [--check]
```

`plan.py --splits <set id>` plays toward a split set's splits in turn instead (`data/games/re1/splits.json`): each
round it first looks for the way to the next split, and makes the nearest progress when there's none yet; the moves
that reach a split carry its name. `build_routes.py` writes those routes too (`<set id>-sim`).

`plan.py` prints the playthrough, move by move, with what each move changed (`+1:172` = flag 172 of bank 1 set,
`-7:42` = room item 42 taken). `build_routes.py` plays both characters and writes the two routes
(`re1-jill-full-sim`, `re1-chris-full-sim`); `--check` only compares (run it after changing the tools or the rules).

## How it works

- **`world.py`** reads every room file: the init and main scripts as condition trees (if/else kept, so a command's
  guards are judged when the script reaches it, as the game does) and the event scripts (RDT `+0x68`) as step lists.
- **`sim.py`** holds the state the scripts read and write (the flag banks, the inventory, the room, the state bytes)
  and runs the scripts on it. Moves: go through a door, take an item, use an item at a spot, step on a spot, push
  something onto one, examine an item, kill what's in the room (the route may cheat). Entering a room runs its init
  script; after every move the main script and the running events run until nothing changes.
- **`plan.py`** searches, breadth-first over rooms, for the nearest move that changes something the playthrough hasn't
  seen before (a story flag, a lock, an item), takes it, and repeats until the flare at the heliport.
- **`export.py`** keeps what a player needs (items used later, every use, scenes that open the way somewhere else)
  and writes route steps: the done flag, the spot the guide's line points at, and the flag's label as the hint.

- **`reach.py`** says where the player can walk: the room file's collision records with the scripts' changes to
  them (command `0x30`), the same grid as the app's route lines. A zone is out of reach only when the scripts' changes
  are what cut it off, or when a record a script made solid covers a zone the player must stand in (the alley lift in
  300 is closed off while the lift is down at the falls). The base geometry alone isn't trusted to cut a room in two
  (stairs split rooms it shouldn't). The player's position comes from the door they came in by (its arrival point),
  a script that moves them, or the spot they last stood at.

What the scripts can't say is assumed in the player's favour: distance and facing tests pass, random rolls come up 0,
and on entering a room every camera test passes (the player can walk to any camera); standing at a spot, the camera
is the one that shows it (`tools/rdt/rdt_map.py`).

## Rules (`data/games/re1/route-rules.json`)

What the room scripts don't say lives in `data/games/re1/route-rules.json`, each entry with its reason and source (a labelled flag, the
decomp's notes, a live flag log, or a walkthrough's facts in our own words):

- **gates**: an item lies behind a wall, a plant or water until a flag says it's open (the piano's secret room, the
  plant in 10C).
- **ways**: a door or spot that can't be reached until a flag is set or clear (the pool in 301, the ladder at the
  falls, the fountain's steps).
- **use / spots / kills / examine**: what the game's own code does (the lab computer's passwords, Plant 42's death,
  the Doom books' medals) — the parts that happen in the exe, not in the scripts.
- **keep / keyAlias / newGame**: tools that aren't used up, Jill's lockpick for the Sword-key doors, the flags a new
  game starts with.

## Checking it against real play

- **Live, in the game** (dev harness, never shipped): `tools/autoplay/simplay.py <out> <save-backup> jill` plays the
  simulated playthrough in the real game, the needed moves and every door, and compares the story, lock and item
  flags with the simulator's after every move (doors opened with the action button, items taken, cutscenes waited
  out, a room walked camera by camera when a scene needs the player to get somewhere; kills: HP 1 and one shot).
  2026-10-27, Jill: from the main hall through the 2F Wesker search, 107 and 108 every move matched (the zombie past
  Kenneth got up a few seconds after the harness's walk of 104 moved on, so Barry's scene came late); it found three things
  the simulator now knows: the new game's real state (control in the dining room, the flags and inventory read live,
  `data/games/re1/route-rules.json` `newGame`), Jill's missing ink ribbons, and the Shield key behind the clock. Saves stay read-only and
  are checked against a fresh backup.
- **Shuffled orders**: `plan.py --seed N` breaks ties between equally near moves another way. 24 seeds (both
  characters) all reach the heliport; before the collision changes were read, one Chris order rode the alley lift
  without power and was stuck below. Dropping a rule and checking every guarded move over 8 seeds shows which rules
  the game data doesn't cover: all of `ways` and the gates for the piano's secret room and (for Jill) the plant.

- `check_log.py <install> jill <log.txt>` replays a story-flag log of a real playthrough (a room per line and the flags
  that changed) and compares, per visit, the flags the simulator sets on entering with the ones the game set. Two Jill
  logs from 2026-10-27: 39 of 51 and 19 of 22 visits agree; the rest are the player's own actions (pushes, uses),
  which a room entry can't know, and no flag the simulator set that the game didn't once the new-game flags were in.
- `check_capture.py` does the same with capture recordings, once they record the flag banks (the current ones
  don't).
- `plan.py --trace trace.jsonl` writes the game state after every move (room, position, inventory, flag words) and
  `dotnet run tools/routesim/GuideCheck.cs -- <route id> trace.jsonl` feeds it to the app's own route guide: it
  must end on the route's last step without going back. All three routes follow a full playthrough to the end
  (2026-10-27); this found two guide bugs (an optional step and a medal got from a Doom book used to hold it for ever)
  and filled the hand-made route's missing done flags from the simulated ones (the 10 it had already matched).
- `export.py --compare re1-jill-any-nmg` checks a simulated route against the hand-made one: Jill's covers all 47 of its
  get and use steps (in a different order: nearest first, not the fastest).

## Limits

- Not a speedrun order: the nearest next thing, which wanders. The steps are right; the order can be improved.
- Chris's start flags aren't logged yet, and his Rebecca stretch isn't modelled (the character byte stays his).
- The falls' two levels and lifts are rules, not geometry; physical blocks (walls, water, plants) are rules until the
  collision changes (`0x30` boundary records) are read.
- Not followed in a real game yet: the routes say so in the app ("draft").
