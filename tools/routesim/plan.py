r"""Plays RE1 through in the simulator: from a new game, always the nearest move that makes progress.

    python tools/routesim/plan.py "G:\GOG Games\Resident Evil" jill [--steps 400] [--verbose]

Progress = a story flag, a door unlocked, an item taken or placed, a new item in the inventory. The nearest is the one
the fewest doors away (breadth-first over rooms, each room entered through the simulator, so cutscenes on entering
happen on the way). The moves taken are printed as a route.
"""
from __future__ import annotations

import json
import os
import sys
from collections import deque
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import world as world_mod  # noqa: E402
from sim import ITEM_USE_BASE, Sim, State, target_of  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
NAMES = json.loads((ROOT / "data" / "games" / "re1" / "names.json").read_text(encoding="utf-8"))
RULES = Path(os.environ.get("ROUTESIM_RULES") or ROOT / "data" / "games" / "re1" / "route-rules.json")


def item_name(i: int) -> str:
    return NAMES["items"].get(str(i), f"item {i}")


def progress(a: State, b: State, ever: set) -> list[str]:
    """What the move from a to b changed that this playthrough hasn't seen before (flag set/cleared, item held).
    Clearing a flag the playthrough set itself (the crank turned back) isn't progress on its own: see undone()."""
    out = []
    for bank in (0, 1, 2, 7):
        out += [f"+{bank}:{f}" for f in sorted(b.flags[bank] - a.flags[bank]) if ("+", bank, f) not in ever]
        out += [f"-{bank}:{f}" for f in sorted(a.flags[bank] - b.flags[bank])
                if ("-", bank, f) not in ever and (bank == 7 or ("+", bank, f) not in ever)]
    out += [f"got {item_name(i)}" for i in b.inventory if i not in a.inventory and ("item", i) not in ever]
    return out


def armed(s: State) -> tuple:
    return tuple(sorted((slot, a.get("handler"), a.get("probe")) for slot, a in s.actions.items()))


def undone(a: State, b: State) -> list[str]:
    return [f"-{bank}:{f}" for bank in (0, 1) for f in sorted(a.flags[bank] - b.flags[bank])]


def remember(a: State, b: State, ever: set) -> None:
    for bank in (0, 1, 2, 7):
        ever.update(("+", bank, f) for f in b.flags[bank] - a.flags[bank])
        ever.update(("-", bank, f) for f in a.flags[bank] - b.flags[bank])
    ever.update(("item", i) for i in b.inventory)


def moves(sim: Sim, s: State):
    """Every move in the current room: (description, kind, function on a state, details). A move in a room whose
    yes/no question is answered No (sim.answers) says so."""
    for text, kind, fn, meta in plain_moves(sim, s):
        target = meta.get("to_room") if kind == "door" else s.room
        if sim.answers.get(s.room) or (target and sim.answers.get(target)):
            meta = {**meta, "answer": "no"}
        yield text, kind, fn, meta


def nearest_object(s: State, zone: list[int]) -> list[int] | None:
    """The script-placed object nearest a push spot: the thing to push onto it (a statue, a crate)."""
    if not s.objects:
        return None
    cx, cz = zone[0] + zone[2] / 2, zone[1] + zone[3] / 2
    x, z = min(s.objects.values(), key=lambda p: (p[0] - cx) ** 2 + (p[1] - cz) ** 2)
    return [x, z]


def plain_moves(sim: Sim, s: State):
    """Every move in the current room: (description, kind, function on a state, details)."""
    for slot, a in sorted(s.actions.items()):
        if a["kind"] == "door" and a.get("handler", 1) in (7, 9):
            yield f"step up to door {slot}", "spot", lambda t, slot=slot: sim.at(t, slot), {"zone": a["zone"]}
        elif a["kind"] == "door":
            yield f"door {slot}", "door", lambda t, slot=slot: sim.door(t, slot),                 {"key": a["key"], "lock": a["lock"], "to_room": target_of(s, a)}
        elif a["kind"] == "item" and sim.present(s, a):
            yield f"take {item_name(a['item'])}", "take", lambda t, slot=slot: sim.pick(t, slot),                 {"item": a["item"], "zone": a["zone"]}
        elif a["kind"] == "action":
            h, words = a.get("handler"), a.get("words", [0, 0, 0])
            if h == 7 and words[0] == 9:
                item = ITEM_USE_BASE + words[1]
                if s.inventory.get(item, 0):
                    yield f"use {item_name(item)}", "use", lambda t, slot=slot, item=item: sim.use(t, slot, item),                         {"item": item, "zone": a["zone"]}
                continue
            if (h == 7 and words[0] == 4) or h == 9:
                # A spot that arms a script flag or opens the item menu (an event): the room may react to an item
                # used there ("item used is X"; 308's rack takes the Flamethrower back this way).
                for item in sorted(sim.reacts_to(s.room)):
                    if s.inventory.get(item, 0):
                        yield f"use {item_name(item)} at spot {slot}", "use",                             lambda t, slot=slot, item=item: sim.use(t, slot, item), {"item": item, "zone": a["zone"]}
            if h in (7, 9):
                push = " (push something onto it)" if a.get("probe", 0) & 0x04 and not a.get("probe", 0) & 0x01 else ""
                yield f"step on spot {slot}{push}", "spot", lambda t, slot=slot: sim.at(t, slot),                     {"zone": a["zone"], "push": bool(push),
                     **({"object": nearest_object(s, a["zone"])} if push and nearest_object(s, a["zone"]) else {})}
    for rule in sim.rules.get("examine", []):
        if s.inventory.get(rule["item"]) and not all(s.inventory.get(i) for i, _ in rule.get("give", [])):
            yield f"examine {item_name(rule['item'])}", "examine", lambda t, item=rule["item"]: sim.examine(t, item),                 {"item": rule["item"]}
    if any(e["killFlag"] != 255 and e["killFlag"] not in s.flags[3] for e in s.enemies):
        yield "kill the enemies", "kill", lambda t: sim.kill(t), {}


def nearest(sim: Sim, start: State, ever: set, max_states: int = 6000, shuffle=None, goal=None):
    """Breadth-first over door moves to the closest productive move; returns (path, state) or None. With `goal`
    (a split: (state before, state after) -> bool), the closest move that reaches it instead, progress or not."""
    queue = deque([(start, [])])
    visited = {start.key()}
    count = 0
    while queue and count < max_states:
        s, path = queue.popleft()
        options = list(moves(sim, s))
        if shuffle is not None:
            shuffle(options)  # --seed: another order among moves as near, to test the playthrough doesn't depend on it
        for text, kind, fn, meta in options:
            t = s.clone()
            sim.log = []
            result = fn(t)
            if (kind == "door" and result is None) or result is False:
                continue
            gained = progress(s, t, ever)
            label = f"{s.room}: {text}" + (f" -> {t.room}" if t.room != s.room else "")
            step = {"label": label, "kind": kind, "room": s.room, "to": t.room, **meta,
                    "flags": [[b, f, "+"] for b in (0, 1, 2, 3, 7) for f in sorted(t.flags[b] - s.flags[b])]
                    + [[b, f, "-"] for b in (0, 1, 2, 3, 7) for f in sorted(s.flags[b] - t.flags[b])],
                    "got": sorted(i for i in t.inventory if t.inventory[i] > s.inventory.get(i, 0)),
                    "lost": sorted(i for i in s.inventory if i not in t.inventory), "_state": t}
            if goal is not None:
                if goal(s, t):
                    return path + [(step, gained)], t
            elif gained:
                return path + [(step, gained)], t
            # Doors, and moves that only undo something (they open other ways), are steps on the way.
            # Moves that arm something in the room (a spot that makes an item pickable) are steps too, keyed by what
            # they armed since the saved flags don't change.
            key = t.key() if kind == "door" or t.room != s.room or undone(s, t) else \
                (t.key(), armed(t), frozenset(t.flags[4])) if (armed(t), t.flags[4]) != (armed(s), s.flags[4]) else None
            if key is not None and key not in visited:
                visited.add(key)
                queue.append((t, path + [(step, [])]))
                count += 1
    return None


def split_goal(split: dict):
    """A split of data/games/re1/splits.json as a goal: (state before a move, state after) -> reached."""
    if split.get("end"):
        return lambda a, b: b.room == "303" and 42 in b.inventory
    if "item" in split:
        # One more of it than before (the second battery while the first is still held), where the split says.
        return lambda a, b: b.inventory.get(split["item"], 0) > a.inventory.get(split["item"], 0) \
            and ("in" not in split or a.room == split["in"])
    room = split["room"]
    if split.get("fromOtherStage"):
        return lambda a, b: b.room == room and a.room[0] != room[0]
    if split.get("anyFrom") or "from" not in split:
        return lambda a, b: b.room == room and a.room != room
    return lambda a, b: b.room == room and a.room == split["from"]


def early(split: dict, route: list[dict]) -> dict | None:
    """The move that already got an item split's item (where the split says, not already another split's), if any."""
    if "item" not in split:
        return None
    return next((m for m in reversed(route) if split["item"] in m["got"] and not m.get("split")
                 and ("in" not in split or m["room"] == split["in"])), None)


WORKERS = max(1, min(24, (os.cpu_count() or 2) - 4))


def build_sim(install: Path, character: str) -> Sim:
    rooms, _ = world_mod.build(install, character)
    rules = json.loads(RULES.read_text(encoding="utf-8"))
    return Sim(rooms, rules.get(character, {}) | {"newGame": rules.get("newGame", {})}, cameras(install, character),
               geometry(install, character))


def trial(install: str, character: str, splits: list[dict], answers: dict, limit: int = 900):
    """One playthrough toward a split set with the given answers, in a process of its own (for play_best's pool)."""
    sim = build_sim(Path(install), character)
    sim.answers = dict(answers)
    route, _, s = play(sim, character, splits, None, limit)
    return route, s.room, 42 in s.inventory, answers


def score(route: list[dict], room: str, flare: bool) -> tuple:
    return sum(1 for m in route if m.get("split")), room == "303" and flare, -len(route)


def play_best(install: Path, character: str, splits: list[dict], limit: int = 900, workers: int = WORKERS):
    """A playthrough toward a split set answering the game's yes/no questions the way that reaches the most splits:
    from all Yes, every question room's answer is flipped at once in parallel (one playthrough each), the best flip is
    kept, and the next round tries the rest on top of it while that helps (Chris has to tell Rebecca No in the save
    room for the Hunter scene that leads to the Master Key). Returns (moves, answers)."""
    from concurrent.futures import ProcessPoolExecutor
    sim = build_sim(install, character)
    route, room, flare, answers = trial(str(install), character, splits, {}, limit)
    best = (route, room, flare)
    asked_before: set[str] = set()
    with ProcessPoolExecutor(max_workers=workers) as pool:
        while score(*best)[0] < len(splits):
            asked = sorted({m["room"] for m in best[0]} | {m["to"] for m in best[0]})
            candidates = [r for r in asked if r in sim.world and sim.asks(r) and not answers.get(r) and r not in asked_before]
            if not candidates:
                break
            asked_before |= set(candidates)
            results = list(pool.map(trial, [str(install)] * len(candidates), [character] * len(candidates),
                                    [splits] * len(candidates), [{**answers, r: 1} for r in candidates],
                                    [limit] * len(candidates)))
            top = max(results, key=lambda r: score(r[0], r[1], r[2]))
            if score(top[0], top[1], top[2]) <= score(*best):
                break
            best, answers = (top[0], top[1], top[2]), top[3]
    return best[0], answers


def play(sim: Sim, character: str, splits: list[dict] | None = None, shuffle=None, limit: int = 900, log=None):
    """Plays from a new game: (moves, states, final state). With `splits`, heads for each split in turn (when it's
    reachable from where the player is) and makes the nearest progress otherwise; the move that reaches a split carries
    its name. Without, the nearest progress every time, to the flare at the heliport."""
    s = new_game(sim, character)
    ever: set = set()
    remember(State(character), s, ever)
    route, states = [], [s]
    goals = list(splits or [])
    wait = 0  # after the next split was out of reach, try again only after a few more steps of progress
    for n in range(limit):
        found, reached = None, None
        while goals and ((goals[0].get("end") and len(goals) > 1) or early(goals[0], route) is not None):
            done = goals.pop(0)
            if "item" in done:
                # Got before its split came up (on the way to an earlier one): the split is the move that took it.
                early(done, route)["split"] = done["name"]
        if goals and wait <= 0:
            found = nearest(sim, s, ever, max_states=900, shuffle=shuffle, goal=split_goal(goals[0]))
            if found is not None:
                reached = goals.pop(0)
            else:
                wait = 3
        wait -= 1
        if found is None:
            found = nearest(sim, s, ever, shuffle=shuffle)
        if found is None:
            break
        path, t = found
        remember(s, t, ever)
        s = t
        for i, (step, gained) in enumerate(path):
            states.append(step.pop("_state"))
            if reached is not None and i == len(path) - 1:
                step["split"] = reached["name"]
            route.append({"move": n, **step, "gained": gained})
            if log and (gained or step.get("split")):
                log(f"{n:3} {step['label']}  {' '.join(gained)}" + (f"  [split: {step['split']}]" if step.get("split") else ""))
        if splits is not None and not goals and (s.room == "303" and 42 in s.inventory
                                                 or not any(x.get("end") for x in splits)):
            break  # the last split (a set that doesn't run to the ending stops there)
    return route, states, s


FIELD_WORDS = {0: ("flagScenarioA", 4), 1: ("flagScenarioB", 8), 2: ("flagLocks", 2), 3: ("flagEnemies", 8),
               4: ("flagSys", 2), 7: ("flagRoomItems", 8)}


def snapshot(sim: Sim, s: State, step: dict) -> dict:
    """What the game would show after a move, for checking the app's route guide (--trace): the room, where the player
    stands (the spot of the move, or the door they came in by), the inventory and the flag words by profile name."""
    zone = step.get("zone") if step.get("to") == step.get("room") else None
    if zone is None:
        came = next((a["zone"] for a in s.actions.values() if a["kind"] == "door" and a.get("zone")
                     and target_of(s, a) == step.get("room")), None)
        zone = came or next((a["zone"] for a in s.actions.values() if a["kind"] == "door" and a.get("zone")), [0, 0, 0, 0])
    words = {}
    for bank, (name, count) in FIELD_WORDS.items():
        for w in range(count):
            words[f"{name}{w}"] = sum(1 << (31 - f % 32) for f in s.flags[bank] if f // 32 == w)
    return {"label": step.get("label"), "room": s.room, "x": zone[0] + zone[2] // 2, "z": zone[1] + zone[3] // 2,
            "inventory": sorted(s.inventory.items()), "fields": words}


def cameras(install: Path, character: str):
    """room, x, z -> the camera showing that point (tools/rdt/rdt_map.py), cached per room."""
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "rdt"))
    import rdt_map
    loaded: dict = {}

    def camera(room: str, x: int, z: int):
        if room not in loaded:
            try:
                loaded[room] = rdt_map.load(install, room, character)
            except Exception:  # noqa: BLE001 - a room without readable geometry: camera unknown
                loaded[room] = None
        return loaded[room].camera_for(x, z) if loaded[room] else None
    return camera


def geometry(install: Path, character: str):
    """room -> where the player can walk (reach.Room), cached per room."""
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "rdt"))
    import rdt_map
    import reach
    loaded: dict = {}

    def room(rid: str):
        if rid not in loaded:
            try:
                loaded[rid] = reach.Room(rdt_map.load(install, rid, character))
            except Exception:  # noqa: BLE001 - no readable geometry: everything counts as reachable
                loaded[rid] = None
        return loaded[rid]
    return room


def new_game(sim: Sim, character: str) -> State:
    s = State(character)
    rules = sim.rules.get("newGame", {}).get(character, {})
    s.flags[7] = set(range(256))  # every room item in place
    for b, f in rules.get("set", []):
        s.flags[b].add(f)
    for b, f in rules.get("clear", []):
        s.flags[b].discard(f)
    for item, qty in rules.get("inventory", []):
        s.inventory[item] = qty
    s.pos = tuple(rules["pos"]) if rules.get("pos") else None
    s.fields[9] = 1 if character == "jill" else 0
    sim.enter(s, rules.get("room", "106"))
    return s


def main(argv: list[str]) -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    install, character = Path(argv[0]), argv[1]
    limit = int(argv[argv.index("--steps") + 1]) if "--steps" in argv else 900
    rooms, problems = world_mod.build(install, character)
    rules = json.loads(RULES.read_text(encoding="utf-8")) if RULES.exists() else {}
    sim = Sim(rooms, rules.get(character, {}) | {"newGame": rules.get("newGame", {})}, cameras(install, character),
              None if "--no-collision" in argv else geometry(install, character))
    shuffle = None
    if "--seed" in argv:
        import random
        shuffle = random.Random(int(argv[argv.index("--seed") + 1])).shuffle
    splits = None
    if "--splits" in argv:
        wanted = argv[argv.index("--splits") + 1]
        sets = json.loads((ROOT / "data" / "games" / "re1" / "splits.json").read_text(encoding="utf-8"))["sets"]
        splits = next(x for x in sets if x["id"] == wanted)["splits"]
    print(f"start in 105; {len(problems)} script problems")
    if splits is not None:
        route, answers = play_best(install, character, splits, limit)
        sim.answers = answers
        route, states, s = play(sim, character, splits, shuffle, limit)  # again here, for the states and the trace
        for m in route:
            if m.get("split") and "--verbose" not in argv:
                print(f"{m['move']:3} {m['label']}  {' '.join(m['gained'])}  [split: {m['split']}]")
        if answers:
            print(f"   answering No in {sorted(answers)}")
    else:
        route, states, s = play(sim, character, None, shuffle, limit, log=print if "--verbose" not in argv else None)
    if "--verbose" in argv:
        for m in route:
            print(f"{m['move']:3} {m['label']}  {' '.join(m['gained'])}" + (f"  [split: {m['split']}]" if m.get("split") else ""))
    if not (s.room == "303" and 42 in s.inventory):
        print(f"-- stuck in {s.room} after {len(route)} moves; holding {', '.join(item_name(i) for i in s.inventory)}")
    else:
        print(f"-- the end: the flare at the heliport after {len(route)} moves")
    if "--trace" in argv:
        with open(argv[argv.index("--trace") + 1], "w", encoding="utf-8") as trace:
            trace.write(json.dumps(snapshot(sim, states[0], {"label": "new game", "room": states[0].room, "to": states[0].room})) + "\n")
            for state, step in zip(states[1:], route):
                trace.write(json.dumps(snapshot(sim, state, step)) + "\n")
    if "--json" in argv:
        Path(argv[argv.index("--json") + 1]).write_text(json.dumps(route, indent=1), encoding="utf-8")
    if "--reach" in argv:
        for room, how in sorted(reach(sim, s).items()):
            print(f"   reach {room} via {how}")
    for room in (argv[argv.index("--explain") + 1].split(",") if "--explain" in argv else []):
        explain(sim, s, room.upper())
    return 0


def explain(sim: Sim, s: State, room: str) -> None:
    """Prints what a room offers from this state: every room-action record and the moves the explorer sees."""
    t = s.clone()
    sim.enter(t, room)
    print(f"== {room} from this state")
    for slot, a in sorted(t.actions.items()):
        extra = {k: a[k] for k in ("handler", "probe", "words", "item", "flag", "lock", "key", "stage", "room") if k in a}
        reach = sim.where(t, a["zone"], a.get("probe", 0x81)) if a.get("zone") else None
        print(f"   slot {slot:2} {a['kind']:6} zone {a.get('zone')} {extra}{'' if reach else '  OUT OF REACH'}")
    print(f"   player at {t.pos}; collision changes {t.mods}")
    for text, kind, _, _ in moves(sim, t):
        print(f"   move: {text}")



def reach(sim: Sim, start: State) -> dict[str, str]:
    """Rooms reachable through doors alone from this state, each with the door move that first got there."""
    seen = {start.room: "start"}
    queue = deque([start])
    while queue:
        s = queue.popleft()
        for text, kind, fn, _ in list(moves(sim, s)):
            if kind not in ("door", "spot") or "door" not in text:
                continue
            t = s.clone()
            if fn(t) in (None, False) or t.room == s.room or t.room in seen:
                continue
            seen[t.room] = f"{s.room}: {text}"
            queue.append(t)
    return seen


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
