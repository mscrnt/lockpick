r"""Plays RE1 through in the simulator: from a new game, always the nearest move that makes progress.

    python tools/routesim/plan.py "G:\GOG Games\Resident Evil" jill [--steps 400] [--verbose]

Progress = a story flag, a door unlocked, an item taken or placed, a new item in the inventory. The nearest is the one
the fewest doors away (breadth-first over rooms, each room entered through the simulator, so cutscenes on entering
happen on the way). The moves taken are printed as a route.
"""
from __future__ import annotations

import json
import sys
from collections import deque
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import world as world_mod  # noqa: E402
from sim import ITEM_USE_BASE, Sim, State, target_of  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
NAMES = json.loads((ROOT / "data" / "games" / "re1" / "names.json").read_text(encoding="utf-8"))
RULES = Path(__file__).with_name("rules.json")


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
    """Every move in the current room: (description, kind, function on a state)."""
    for slot, a in sorted(s.actions.items()):
        if a["kind"] == "door" and a.get("handler", 1) in (7, 9):
            yield f"step up to door {slot}", "spot", lambda t, slot=slot: sim.at(t, slot), {"zone": a["zone"]}
        elif a["kind"] == "door":
            yield f"door {slot}", "door", lambda t, slot=slot: sim.door(t, slot), {"key": a["key"], "lock": a["lock"]}
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
                for item in sim.reacts_to(s.room):
                    if s.inventory.get(item, 0):
                        yield f"use {item_name(item)} at spot {slot}", "use",                             lambda t, slot=slot, item=item: sim.use(t, slot, item), {"item": item, "zone": a["zone"]}
            if h in (7, 9):
                push = " (push something onto it)" if a.get("probe", 0) & 0x04 and not a.get("probe", 0) & 0x01 else ""
                yield f"step on spot {slot}{push}", "spot", lambda t, slot=slot: sim.at(t, slot),                     {"zone": a["zone"], "push": bool(push)}
    for rule in sim.rules.get("examine", []):
        if s.inventory.get(rule["item"]) and not all(s.inventory.get(i) for i, _ in rule.get("give", [])):
            yield f"examine {item_name(rule['item'])}", "examine", lambda t, item=rule["item"]: sim.examine(t, item),                 {"item": rule["item"]}
    if any(e["killFlag"] != 255 and e["killFlag"] not in s.flags[3] for e in s.enemies):
        yield "kill the enemies", "kill", lambda t: sim.kill(t), {}


def nearest(sim: Sim, start: State, ever: set, max_states: int = 6000):
    """Breadth-first over door moves to the closest productive move; returns (path, state) or None."""
    queue = deque([(start, [])])
    visited = {start.key()}
    count = 0
    while queue and count < max_states:
        s, path = queue.popleft()
        for text, kind, fn, meta in list(moves(sim, s)):
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
                    "got": sorted(i for i in t.inventory if i not in s.inventory),
                    "lost": sorted(i for i in s.inventory if i not in t.inventory), "_state": t}
            if gained:
                return path + [(step, gained)], t
            # Doors, and moves that only undo something (they open other ways), are steps on the way.
            # Moves that arm something in the room (a spot that makes an item pickable) are steps too, keyed by what
            # they armed since the saved flags don't change.
            key = t.key() if kind == "door" or t.room != s.room or undone(s, t) else                 (t.key(), armed(t), frozenset(t.flags[4])) if (armed(t), t.flags[4]) != (armed(s), s.flags[4]) else None
            if key is not None and key not in visited:
                visited.add(key)
                queue.append((t, path + [(step, [])]))
                count += 1
    return None


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


def new_game(sim: Sim, character: str) -> State:
    s = State(character)
    rules = sim.rules.get("newGame", {}).get(character, {})
    s.flags[7] = set(range(256))  # every room item in place
    for b, f in rules.get("set", []):
        s.flags[b].add(f)
    for item, qty in rules.get("inventory", []):
        s.inventory[item] = qty
    s.fields[9] = 1 if character == "jill" else 0
    sim.enter(s, rules.get("room", "106"))
    return s


def main(argv: list[str]) -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    install, character = Path(argv[0]), argv[1]
    limit = int(argv[argv.index("--steps") + 1]) if "--steps" in argv else 400
    rooms, problems = world_mod.build(install, character)
    rules = json.loads(RULES.read_text(encoding="utf-8")) if RULES.exists() else {}
    sim = Sim(rooms, rules.get(character, {}) | {"newGame": rules.get("newGame", {})}, cameras(install, character))
    s = new_game(sim, character)
    ever: set = set()
    remember(State(character), s, ever)
    print(f"start in {s.room}; {len(problems)} script problems")
    route = []
    trace = open(argv[argv.index("--trace") + 1], "w", encoding="utf-8") if "--trace" in argv else None
    if trace is not None:
        trace.write(json.dumps(snapshot(sim, s, {"label": "new game", "kind": "start", "room": s.room, "to": s.room})) + "\n")
    for n in range(limit):
        found = nearest(sim, s, ever)
        if found is None:
            print(f"-- stuck in {s.room} after {n} moves; holding {', '.join(item_name(i) for i in s.inventory)}")
            break
        path, t = found
        remember(s, t, ever)
        s = t
        for step, gained in path:
            state = step.pop("_state")
            if trace is not None:
                trace.write(json.dumps(snapshot(sim, state, step)) + "\n")
            route.append({"move": n, **step, "gained": gained})
            if gained or "--verbose" in argv:
                print(f"{n:3} {step['label']}  {' '.join(gained)}")
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
        print(f"   slot {slot:2} {a['kind']:6} zone {a.get('zone')} {extra}")
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
