r"""Plays toward every split set given (tools/routesim/LssToSplits.cs output, or Lockpick's own saved sets) and reports,
per set, how many of its splits the playthrough reaches in order, and whether a set that runs to the ending gets there.

    python tools/routesim/check_splits.py "G:\GOG Games\Resident Evil" <set.json>... [--own <folder of saved sets>]

A split with no trigger (manual) borrows one from a saved set with the same name (as the Runs page does); one that still
has none is skipped and reported.
"""
from __future__ import annotations

import json
import sys
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import plan  # noqa: E402
import world as world_mod  # noqa: E402


def norm(name: str) -> str:
    return "".join(ch for ch in name if ch.isalnum()).lower()


def splits_of(saved: dict, borrowed: dict[str, dict]) -> tuple[list[dict], list[str]]:
    out, manual = [], []
    for s in saved["splits"]:
        if s.get("kind") in (None, "manual") and norm(s["name"]) in borrowed:
            s = {**borrowed[norm(s["name"])], "name": s["name"]}
        kind = s.get("kind")
        if kind == "room":
            room = {"room": s["room"]}
            room.update({"from": s["from"]} if s.get("from") else {"fromOtherStage": True} if s.get("otherStage") else {})
            if "from" not in room and "fromOtherStage" not in room:
                room["anyFrom"] = True
            out.append({"name": s["name"], **room})
        elif kind == "item":
            out.append({"name": s["name"], "item": s["item"]})
        elif kind == "end":
            out.append({"name": s["name"], "end": True})
        else:
            manual.append(s["name"])
    return out, manual


def check(install: str, path: str, borrowed: dict) -> str:
    saved = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    character = saved.get("character", "jill")
    splits, manual = splits_of(saved, borrowed)
    rooms, _ = world_mod.build(Path(install), character)
    rules = json.loads(plan.RULES.read_text(encoding="utf-8"))
    sim = plan.Sim(rooms, rules.get(character, {}) | {"newGame": rules.get("newGame", {})},
                   plan.cameras(Path(install), character), plan.geometry(Path(install), character))
    route, _, s = plan.play(sim, character, splits)
    hit = [m["split"] for m in route if m.get("split")]
    missed = [x["name"] for x in splits if x["name"] not in hit]
    ends = any(x.get("end") for x in splits)
    finished = s.room == "303" and 42 in s.inventory
    verdict = "OK" if not missed and (finished or not ends) else "MISSED"
    return (f"{verdict:6} {saved.get('name', Path(path).stem)[:48]:48} {character:5} {len(hit)}/{len(splits)} splits"
            + (f"; missed {missed}" if missed else "") + (f"; manual {manual}" if manual else "")
            + ("" if finished or not ends else f"; stopped in {s.room}"))


def main(argv: list[str]) -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    install = argv[0]
    files = [a for a in argv[1:] if a.endswith(".json")]
    if "--builtin" in argv:
        # The community sets (data/games/re1/splits.json) in the same form.
        folder = Path(__file__).with_name("builtin-sets")
        folder.mkdir(exist_ok=True)
        for x in json.loads((plan.ROOT / "data" / "games" / "re1" / "splits.json").read_text(encoding="utf-8"))["sets"]:
            splits = [{"name": s["name"], "kind": "end"} if s.get("end") else
                      {"name": s["name"], "kind": "item", "item": s["item"]} if "item" in s else
                      {"name": s["name"], "kind": "room", "room": s["room"], "from": s.get("from"),
                       "otherStage": s.get("fromOtherStage", False)} for s in x["splits"]]
            f = folder / f"{x['id']}.json"
            f.write_text(json.dumps({"name": x["name"], "character": x["character"], "splits": splits}), encoding="utf-8")
            files.append(str(f))
    borrowed: dict[str, dict] = {}
    if "--own" in argv:
        for f in Path(argv[argv.index("--own") + 1]).glob("*.json"):
            for s in json.loads(f.read_text(encoding="utf-8-sig")).get("splits", []):
                if s.get("kind") in ("room", "item"):
                    borrowed.setdefault(norm(s["name"]), s)
    with ProcessPoolExecutor(max_workers=6) as pool:
        for line in pool.map(check, [install] * len(files), files, [borrowed] * len(files)):
            print(line, flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
