r"""Makes a route for one of the player's own split sets (Lockpick's "Follow my splits"): plays toward its splits in turn
in the simulator and writes the route where Lockpick loads it, <data folder>\routes\<set id>.json.

    python tools/routesim/route_for_splits.py "G:\GOG Games\Resident Evil" <split set .json> <set id> [--data <folder>]

The split set is one saved in Lockpick (<data folder>\splits\mine\*.json: room splits {"kind": "room", "room", "from"},
item splits {"kind": "item", "item"}); <set id> is the id the Runs page uses for it (settings.json "runSetId" while it's
the chosen set). The data folder is %LOCALAPPDATA%\Lockpick unless --data says otherwise (a portable copy's `data`).
"""
from __future__ import annotations

import json
import os
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import build_routes  # noqa: E402


def splits_of(saved: dict) -> list[dict]:
    """A saved set's splits in splits.json's terms."""
    out = []
    for s in saved["splits"]:
        kind = s.get("kind")
        if kind == "room":
            out.append({"name": s["name"], "room": s["room"], **({"from": s["from"]} if s.get("from") else {"fromOtherStage": True})})
        elif kind == "item":
            out.append({"name": s["name"], "item": s["item"]})
        elif kind == "end" or s.get("end"):
            out.append({"name": s["name"], "end": True})
    return out


def main(argv: list[str]) -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    install, saved, set_id = Path(argv[0]), json.loads(Path(argv[1]).read_text(encoding="utf-8-sig")), argv[2]
    data = Path(argv[argv.index("--data") + 1]) if "--data" in argv else Path(os.environ["LOCALAPPDATA"]) / "Lockpick"
    character = saved.get("character", "jill")
    steps = build_routes.playthrough(install, character, splits_of(saved), to_the_end=False)
    route = {"id": f"{set_id}-sim", "name": f"{saved['name']} (simulated)", "category": saved.get("category", saved["name"]),
             "character": character, "verified": False, "source": build_routes.SPLIT_SOURCE, "steps": steps}
    folder = data / "routes"
    folder.mkdir(parents=True, exist_ok=True)
    out = folder / f"{set_id}.json"
    out.write_bytes(build_routes.dump({"comment": "A route for the player's own split set (tools/routesim/route_for_splits.py).",
                                       "routes": [route]}).encode("utf-8"))
    print(f"{len(steps)} steps -> {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
