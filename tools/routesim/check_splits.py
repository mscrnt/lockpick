r"""Plays toward every split set given (tools/routesim/LssToSplits.cs output, or Lockpick's own saved sets) and reports,
per set, how many of its splits the playthrough reaches in order, and whether a set that runs to the ending gets there.

    python tools/routesim/check_splits.py "G:\GOG Games\Resident Evil" <set.json>... [--own <folder of saved sets>]

A split with no trigger (manual) borrows one from a saved set with the same name (as the Runs page does); one that still
has none is skipped and reported.
"""
from __future__ import annotations

import hashlib
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


def check(install: str, path: str, borrowed: dict, answers: dict | None = None) -> tuple[str, bool, dict]:
    """(the report line, whether every split was reached, the set) for one set, with the given answers."""
    saved = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    character = saved.get("character", "jill")
    splits, manual = splits_of(saved, borrowed)
    route, room, flare, _ = plan.trial(install, character, splits, answers or {})
    return report(saved, path, character, splits, manual, route, room, flare, answers or {})


def report(saved, path, character, splits, manual, route, room, flare, answers) -> tuple[str, bool, dict]:
    hit = [m["split"] for m in route if m.get("split")]
    missed = [x["name"] for x in splits if x["name"] not in hit]
    ends = any(x.get("end") for x in splits)
    finished = room == "303" and flare
    ok = not missed and (finished or not ends)
    line = (f"{'OK' if ok else 'MISSED':6} {saved.get('name', Path(path).stem)[:48]:48} {character:5} {len(hit)}/{len(splits)} splits"
            + (f"; answering No in {sorted(answers)}" if answers else "")
            + (f"; missed {missed}" if missed else "") + (f"; manual {manual}" if manual else "")
            + ("" if finished or not ends else f"; stopped in {room}"))
    return line, ok, {"path": path, "character": character, "splits": splits, "manual": manual, "saved": saved}


CACHE = Path(__file__).with_name(".cache") / "check_splits"


def fingerprint(path: str) -> str:
    """The set's content and everything the result depends on (the simulator's code, the rules, the room files'
    install): a result saved under it is still right."""
    h = hashlib.sha256(Path(path).read_bytes())
    for f in sorted(Path(__file__).parent.glob("*.py")) + [plan.RULES]:
        h.update(f.read_bytes())
    return h.hexdigest()[:24]


def cached(path: str) -> str | None:
    f = CACHE / f"{fingerprint(path)}.txt"
    return f.read_text(encoding="utf-8") if f.exists() else None


def save(path: str, line: str) -> None:
    CACHE.mkdir(parents=True, exist_ok=True)
    (CACHE / f"{fingerprint(path)}.txt").write_text(line, encoding="utf-8")


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
    # Results already worked out for the same set and the same simulator are reused (--fresh plays everything again).
    todo = []
    for f in files:
        line = None if "--fresh" in argv else cached(f)
        if line is not None:
            print(line + "  (saved)", flush=True)
        else:
            todo.append(f)
    # Every set at once with all-Yes answers, printed as each finishes; then the answer search (itself parallel) for
    # the ones that miss splits.
    from concurrent.futures import as_completed
    retry = []
    with ProcessPoolExecutor(max_workers=plan.WORKERS) as pool:
        futures = {pool.submit(check, install, f, borrowed): f for f in todo}
        for future in as_completed(futures):
            line, ok, info = future.result()
            if ok:
                print(line, flush=True)
                save(futures[future], line)
            else:
                print(line + "  (searching the answers)", flush=True)
                retry.append(info)
    for info in retry:
        route, answers = plan.play_best(Path(install), info["character"], info["splits"])
        _, room, flare, _ = plan.trial(install, info["character"], info["splits"], answers)
        line = report(info["saved"], info["path"], info["character"], info["splits"], info["manual"], route, room, flare,
                      answers)[0]
        print(line, flush=True)
        save(info["path"], line)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
