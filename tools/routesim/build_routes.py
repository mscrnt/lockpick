r"""Plays RE1 through in the simulator as Jill and as Chris and writes the two full-game routes into
data/games/re1/routes.json, and one route per built-in split set (data/games/re1/splits.json), made by heading for
each split in turn (the hand-made routes there stay as they are).

    python tools/routesim/build_routes.py "G:\GOG Games\Resident Evil" [--check]

--check rewrites nothing: it fails when the file differs from what the simulator makes now (rules or tools changed
without regenerating).
"""
from __future__ import annotations

import json
import sys
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import export  # noqa: E402
import plan  # noqa: E402
import world as world_mod  # noqa: E402

ROUTES = export.DATA / "routes.json"
GENERATED = {
    "jill": {"id": "re1-jill-full-sim", "name": "Jill full game (simulated)", "category": "Jill"},
    "chris": {"id": "re1-chris-full-sim", "name": "Chris full game (simulated)", "category": "Chris"},
}
SOURCE = ("Simulated (tools/routesim): a playthrough from a new game to the heliport, worked out from the game's own "
          "room scripts plus the rules in data/games/re1/route-rules.json, always taking the nearest thing that moves the "
          "story on (not a speedrun order). Not yet followed in a real game.")


SPLIT_SOURCE = ("Simulated (tools/routesim) from the split set of the same name (data/games/re1/splits.json): a "
                "playthrough from a new game that heads for each split in turn and picks up what it needs on the way, "
                "worked out from the game's own room scripts plus data/games/re1/route-rules.json; no glitches (a glitched "
                "set's splits are reached the ordinary way). Not yet followed in a real game.")


def playthrough(install: Path, character: str, splits: list[dict] | None = None, to_the_end: bool = True) -> list[dict]:
    rooms, _ = world_mod.build(install, character)
    rules = json.loads(plan.RULES.read_text(encoding="utf-8"))
    sim = plan.Sim(rooms, rules.get(character, {}) | {"newGame": rules.get("newGame", {})}, plan.cameras(install, character),
                    plan.geometry(install, character))
    if splits:
        _, sim.answers = plan.play_best(install, character, splits)
    route, _, s = plan.play(sim, character, splits)
    if to_the_end and (42 not in s.inventory or s.room != "303"):
        raise SystemExit(f"{character}: the playthrough stopped in {s.room} without the flare; not writing a route")
    return export.steps(route, rooms, character)


def dump(doc: dict) -> str:
    """The file's own layout: one step (and one timer) per line."""
    def inline(o: dict) -> str:
        return "{ " + json.dumps(o, ensure_ascii=False, separators=(", ", ": "))[1:-1] + " }"

    lines = ["{", f'  "comment": {json.dumps(doc["comment"], ensure_ascii=False)},', '  "routes": [']
    for i, r in enumerate(doc["routes"]):
        lines.append("    {")
        head = [k for k in r if k not in ("timers", "steps")]
        for k in head:
            lines.append(f'      "{k}": {json.dumps(r[k], ensure_ascii=False)},')
        if "timers" in r:
            lines.append('      "timers": [')
            lines += [f"        {inline(t)}" + ("," if j < len(r["timers"]) - 1 else "") for j, t in enumerate(r["timers"])]
            lines.append("      ],")
        lines.append('      "steps": [')
        lines += [f"        {inline(s)}" + ("," if j < len(r["steps"]) - 1 else "") for j, s in enumerate(r["steps"])]
        lines.append("      ]")
        lines.append("    }" + ("," if i < len(doc["routes"]) - 1 else ""))
    lines += ["  ]", "}"]
    return "\n".join(lines) + "\n"


def main(argv: list[str]) -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    install = Path(argv[0])
    text = ROUTES.read_bytes().decode("utf-8")
    doc = json.loads(text)
    if dump(doc) != text and "--check" not in argv:
        print("note: routes.json isn't in the generator's layout; it will be rewritten in it")
    sets = json.loads((export.DATA / "splits.json").read_text(encoding="utf-8"))["sets"]
    jobs = [(meta, character, None) for character, meta in GENERATED.items()]
    jobs += [({"id": f"{x['id']}-sim", "name": f"{x['name']} (simulated)", "category": x.get("category", x["name"])},
              x["character"], x["splits"]) for x in sets]
    ids = {meta["id"] for meta, _, _ in jobs}
    routes = [r for r in doc["routes"] if r["id"] not in ids]
    with ProcessPoolExecutor(max_workers=4) as pool:
        results = list(pool.map(playthrough, [install] * len(jobs), [c for _, c, _ in jobs], [s for _, _, s in jobs]))
    for (meta, character, splits), steps in zip(jobs, results):
        source = SOURCE if splits is None else SPLIT_SOURCE
        routes.append({**meta, "character": character, "verified": False, "source": source, "steps": steps})
        print(f"{meta['id']}: {len(steps)} steps")
    out = dump({**doc, "routes": routes})
    if "--check" in argv:
        if out != text:
            print("routes.json differs from what the simulator makes now: run tools/routesim/build_routes.py")
            return 1
        print("routes.json is up to date")
        return 0
    ROUTES.write_bytes(out.encode("utf-8"))
    print(f"wrote {ROUTES}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
