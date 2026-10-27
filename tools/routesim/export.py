r"""Turns a simulated playthrough (plan.py --json) into route steps for data/games/re1/routes.json.

    python tools/routesim/export.py "G:\GOG Games\Resident Evil" jill <route.json> [--compare re1-jill-any-nmg] [--out steps.json]

Kept: items the playthrough later uses, examines or opens a door with; every use; scenes, spots and kills whose story
flags another room's scripts test (a cutscene that opens the way elsewhere). Dropped: ammo, herbs, files, flags only
their own room reads. Each step's done flag is the first story flag it set, its spot (when it has one) the place the
guide's line points at, its hint the flag's label when data/games/re1/flag-labels.json has one.

--compare lists the steps of a hand-made route the playthrough never does, and the pairs it does in the other order.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import world as world_mod  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
DATA = ROOT / "data" / "games" / "re1"
FIELDS = {0: "flagScenarioA", 1: "flagScenarioB", 2: "flagLocks", 7: "flagRoomItems"}
NAMES = json.loads((DATA / "names.json").read_text(encoding="utf-8"))
RULES = json.loads(Path(__file__).with_name("rules.json").read_text(encoding="utf-8"))


def field(bank: int, flag: int) -> str:
    return f"{FIELDS[bank]}{flag // 32}:0x{1 << (31 - flag % 32):08X}"


def tested(rooms: dict) -> dict[tuple[int, int], set[str]]:
    """(bank, flag) -> the rooms whose scripts test it."""
    out: dict[tuple[int, int], set[str]] = {}

    def walk(nodes: list[dict], room: str) -> None:
        for n in nodes:
            if "if" in n:
                for c in n["if"]:
                    if c["op"] == 0x04:
                        out.setdefault((c["bank"], c["flag"]), set()).add(room)
                walk(n["then"], room)
                walk(n["else"], room)

    for room, r in rooms.items():
        walk(r["init"], room)
        walk(r["main"], room)
        for script in r["events"]:
            for st in script:
                walk(st.get("block", []) + st.get("wait", []), room)
    return out


def wanted(rooms: dict) -> dict[str, set[int]]:
    """room -> the items its scripts test with "inventory has" (Richard's room and the serum)."""
    out: dict[str, set[int]] = {}

    def walk(nodes: list[dict], room: str) -> None:
        for n in nodes:
            if "if" in n:
                out.setdefault(room, set()).update(c["item"] for c in n["if"] if c["op"] == 0x1A)
                walk(n["then"], room)
                walk(n["else"], room)

    for room, r in rooms.items():
        walk(r["init"], room)
        walk(r["main"], room)
        for script in r["events"]:
            for st in script:
                walk(st.get("block", []) + st.get("wait", []), room)
    return out


def labels(character: str) -> dict[tuple[int, int], str]:
    out = {}
    banks = {v: k for k, v in FIELDS.items()}
    for entry in json.loads((DATA / "flag-labels.json").read_text(encoding="utf-8"))["labels"]:
        name = entry["bank"].rstrip("0123456789")
        if name not in banks or not entry.get("label") or character not in entry.get("characters", [character]):
            continue
        word = int(entry["bank"][len(name):] or 0)
        mask = int(entry["mask"], 16)
        out[(banks[name], word * 32 + 31 - (mask.bit_length() - 1))] = entry["label"]
    return out


def centre(zone: list[int] | None) -> list[int] | None:
    return [zone[0] + zone[2] // 2, zone[1] + zone[3] // 2] if zone and zone[2] > 1 else None


def item(i: int) -> str:
    return NAMES["items"].get(str(i), f"item {i}")


def kept(route: list[dict], rooms: dict, character: str) -> set[int]:
    """The moves the route's steps come from (tools/autoplay/simplay.py plays these, and every door)."""
    used: set[int] = set()
    steps(route, rooms, character, used)
    return used


def steps(route: list[dict], rooms: dict, character: str, used: set[int] | None = None) -> list[dict]:
    reads = tested(rooms)
    named = labels(character)
    alias = {int(k): v for k, v in RULES.get(character, {}).get("keyAlias", {}).items()}
    # Items that matter: used, examined, or a key that opened a door later in the playthrough.
    needed = {m["item"] for m in route if m["kind"] in ("use", "examine")}
    needed |= {i for m in route if m["kind"] in ("door", "spot") for i in m.get("lost", [])}  # taken by a scene
    tests = wanted(rooms)
    held: set[int] = set()
    brought: dict[int, list[int]] = {}  # move index -> items a scene there looks for in the inventory
    for i, m in enumerate(route):
        held |= set(m["got"])
        held -= set(m.get("lost", []))
        room = m["to"] if m["kind"] == "door" else m["room"]
        if m["kind"] in ("door", "spot") and any(how == "+" and b in (0, 1) for b, _, how in m["flags"]):
            items = sorted((tests.get(room, set()) & held) - {1, 49, 77})  # not the knife, lockpick or radio
            if items:
                brought[i] = items
                needed |= set(items)
    needed |= {alias.get(m["key"], m["key"]) for m in route if m["kind"] == "door" and m.get("key") not in (None, 0, 254, 255)
               and any(b == 2 and how == "+" for b, _, how in m["flags"])}
    needed |= {g for rule in RULES.get(character, {}).get("examine", []) for g, _ in rule.get("give", [])}
    needed.add(42)  # the flare that ends the game
    out: list[dict] = []

    def story(m: dict) -> list[tuple[int, int]]:
        """Story flags this move set that some other room's scripts read."""
        return [(b, f) for b, f, how in m["flags"] if how == "+" and b in (0, 1) and reads.get((b, f), set()) - {m["room"]}]

    def label(flag: tuple[int, int] | None, room: str) -> str | None:
        """A flag's label, unless it names another room (labels are written for the scene they were seen in)."""
        text = named.get(flag) if flag else None
        if not text or any(f"({r})" in text and r != room for r in rooms):
            return None
        return text[0].upper() + text[1:] + "."

    def best(flags: list[tuple[int, int]], thing: str | None) -> tuple[int, int] | None:
        """The flag to judge a step by: one whose label names the item, else any labelled one, else the first."""
        if not flags:
            return None
        if thing:
            for f in flags:
                if thing.lower() in named.get(f, "").lower():
                    return f
        return next((f for f in flags if f in named), flags[0])

    last, size = None, 0
    for index, m in enumerate(route):
        if used is not None and last is not None and len(out) > size:
            used.add(last)
        last, size = index, len(out)
        if index in brought and story(m):
            for g in brought[index]:
                where = m["to"] if m["kind"] == "door" else m["room"]
                flag = best(story(m), item(g))
                step = {"use": g, "in": where, "done": field(*flag)}
                if label(flag, where):
                    step["hint"] = label(flag, where)
                out.append(step)
            continue
        got = [g for g in m["got"] if g in needed]
        if got and m["kind"] not in ("examine",):
            for g in got:
                step = {"get": g, "in": m["room"]}
                if m["kind"] != "take":
                    step["hint"] = "Handed over here" + (f": {label(best(story(m), item(g)), m['room'])}" if story(m) and label(best(story(m), item(g)), m["room"]) else ".")
                    if m["kind"] == "spot" and centre(m.get("zone")):
                        step["at"] = centre(m["zone"])
                if not out or out[-1] != step:
                    out.append(step)
            continue
        if m["kind"] in ("door", "spot") and m.get("lost") and story(m):
            # A scene takes an item (Richard and the serum): the step is to bring it here.
            for lost in m["lost"]:
                flag = best(story(m), item(lost))
                step = {"use": lost, "in": m["to"] if m["kind"] == "door" else m["room"], "done": field(*flag)}
                if label(flag, step["in"]):
                    step["hint"] = label(flag, step["in"])
                if not out or out[-1] != step:
                    out.append(step)
            continue
        if m["kind"] == "use" and any(how == "+" for _, _, how in m["flags"]):
            step = {"use": m["item"], "in": m["room"]}
            flag = best([(b, f) for b, f, how in m["flags"] if how == "+" and b in FIELDS and b != 7], item(m["item"]))
            if flag:
                step["done"] = field(*flag)
                if label(flag, m["room"]):
                    step["hint"] = label(flag, m["room"])
        elif m["kind"] == "examine":
            for got in m["got"]:
                out.append({"get": got, "hint": f"Examine the {item(m['item'])} in your items: the {item(got)} is inside."})
            continue
        elif m["kind"] in ("door", "spot", "kill") and story(m):
            b, f = best(story(m), None)
            where = m["room"] if m["kind"] != "door" else m["to"]
            text = label((b, f), where)
            if m["kind"] == "door" and text and text.startswith("First time"):
                continue  # bookkeeping of a first visit, not something to go and do
            step = {"go": where, "done": field(b, f)}
            if m["kind"] == "kill":
                step["hint"] = "Beat what's in here" + (f": {text[0].lower() + text[1:]}" if text else ".")
            elif text:
                step["hint"] = text
            elif m["kind"] == "spot":
                step["hint"] = "Push something onto the marked spot." if m.get("push") else "Check the marked spot."
            else:
                step["hint"] = "A scene plays here."
            if m["kind"] == "spot" and centre(m.get("zone")):
                step["at"] = centre(m["zone"])
        else:
            continue
        if m["kind"] == "use" and centre(m.get("zone")):
            step["at"] = centre(m["zone"])
        if not out or out[-1] != step:
            out.append(step)
    if used is not None and last is not None and len(out) > size:
        used.add(last)
    out.append({"end": True})
    return out


def compare(ours: list[dict], theirs: list[dict]) -> None:
    def key(s: dict):
        for k in ("get", "use"):
            if k in s:
                return (k, s[k], s.get("in"))
        return None
    mine = [key(s) for s in ours]
    order = {k: i for i, k in reversed(list(enumerate(mine))) if k}
    missing = [k for k in (key(s) for s in theirs) if k and k not in order]
    print(f"hand route: {sum(1 for s in theirs if key(s))} get/use steps; the playthrough misses {len(missing)}:")
    for k in missing:
        print(f"   {k[0]} {item(k[1])} in {k[2]}")
    hand = [k for k in (key(s) for s in theirs) if k in order]
    swaps = [(a, b) for i, a in enumerate(hand) for b in hand[i + 1:] if order[a] > order[b]]
    print(f"   {len(swaps)} pairs in the other order, e.g.: " + "; ".join(f"{item(a[1])} ({a[0]}, {a[2]}) after {item(b[1])} ({b[0]}, {b[2]})" for a, b in swaps[:6]))


def main(argv: list[str]) -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    install, character, route = Path(argv[0]), argv[1], json.loads(Path(argv[2]).read_text(encoding="utf-8"))
    rooms, _ = world_mod.build(install, character)
    out = steps(route, rooms, character)
    print(f"{len(route)} moves -> {len(out)} steps "
          f"({sum('get' in s for s in out)} get, {sum('use' in s for s in out)} use, {sum('go' in s for s in out)} go)")
    if "--compare" in argv:
        rid = argv[argv.index("--compare") + 1]
        hand = next(r for r in json.loads((DATA / "routes.json").read_text(encoding="utf-8"))["routes"] if r["id"] == rid)
        compare(out, hand["steps"])
    if "--out" in argv:
        Path(argv[argv.index("--out") + 1]).write_text(json.dumps(out, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    else:
        for s in out:
            print("  ", json.dumps(s, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
