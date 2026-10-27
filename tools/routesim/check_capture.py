r"""Checks the simulator against real play: for every room change in a Lockpick capture, loads the flags from just
before the door into the simulator, enters the room, and compares the saved flags it predicts with what the game had
a moment after the room loaded.

    python tools/routesim/check_capture.py "G:\GOG Games\Resident Evil" <recording-1.jsonl> [more recordings...]

A flag the game changed that the simulator didn't ("missed"), or one the simulator changed that the game didn't
("extra"), is a place the interpreter or the rules are wrong — or something happened in the room in that moment
(a pickup right by the door). Only the init script and the frames right after are compared, not the whole visit.
"""
from __future__ import annotations

import json
import sys
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import world as world_mod  # noqa: E402
from sim import Sim, State  # noqa: E402

BANKS = {0: ("flagScenarioA", 4), 1: ("flagScenarioB", 8), 2: ("flagLocks", 2), 7: ("flagRoomItems", 8)}
SETTLE_MS = 1500  # how long after the room change the "after" snapshot is taken


def flags_of(raw: dict, bank: int) -> set[int] | None:
    name, words = BANKS[bank]
    out = set()
    for w in range(words):
        v = raw.get(f"{name}{w}", {}).get("value")
        if v is None or raw.get(f"{name}{w}", {}).get("status") != "valid":
            return None
        for bit in range(32):
            if v & (1 << bit):
                out.add(w * 32 + 31 - bit)
    return out


def frames(path: Path):
    for line in path.open(encoding="utf-8"):
        try:
            o = json.loads(line)
        except json.JSONDecodeError:
            continue
        if "snapshot" in o:
            yield o["offsetMs"], o["snapshot"]


def room_of(snap: dict) -> str | None:
    r = snap["rawFields"]
    stage, room = r.get("stage", {}).get("value"), r.get("room", {}).get("value")
    if stage is None or room is None or not snap.get("inGameplay", {}).get("value"):
        return None
    return f"{stage + 1}{room:02X}"


def main(argv: list[str]) -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    install, recordings = Path(argv[0]), [Path(p) for p in argv[1:]]
    worlds, sims = {}, {}
    totals, misses, extras = Counter(), Counter(), Counter()
    for rec in recordings:
        snaps = list(frames(rec))
        i = 0
        while i < len(snaps) - 1:
            t0, before = snaps[i]
            t1, nxt = snaps[i + 1]
            a, b = room_of(before), room_of(nxt)
            if not a or not b or a == b:
                i += 1
                continue
            # the frame just after the room settled
            j = i + 1
            while j < len(snaps) - 1 and snaps[j][0] - t1 < SETTLE_MS and room_of(snaps[j + 1][1]) == b:
                j += 1
            after = snaps[j][1]
            character = "jill" if (before["rawFields"].get("character", {}).get("value", 0) & 3) == 1 else "chris"
            if character not in sims:
                worlds[character], _ = world_mod.build(install, character)
                rules = json.loads(Path(__file__).with_name("rules.json").read_text(encoding="utf-8"))
                sims[character] = Sim(worlds[character], rules.get(character, {}))
            sim = sims[character]
            s = State(character)
            s.room = a
            s.fields[9] = 1 if character == "jill" else 0
            ok = True
            for bank in BANKS:
                f = flags_of(before["rawFields"], bank)
                if f is None:
                    ok = False
                    break
                s.flags[bank] = f
            if not ok or b not in sim.world:
                i = j
                continue
            for slot in range(8):
                v = before["rawFields"].get(f"inv{slot}", {}).get("value")
                if v and v & 0xFF:
                    s.inventory[v & 0xFF] = s.inventory.get(v & 0xFF, 0) + max(v >> 8, 1)
            sim.enter(s, b)
            totals[b] += 1
            for bank in BANKS:
                real = flags_of(after["rawFields"], bank)
                was = flags_of(before["rawFields"], bank)
                if real is None:
                    continue
                for f in sorted((real ^ was) - (s.flags[bank] ^ was)):
                    misses[(a, b, bank, f, "set" if f in real else "clear")] += 1
                for f in sorted((s.flags[bank] ^ was) - (real ^ was)):
                    extras[(a, b, bank, f, "set" if f in s.flags[bank] else "clear")] += 1
            i = j
    print(f"{sum(totals.values())} room entries checked in {len(totals)} rooms")
    print(f"missed (the game changed, the simulator didn't): {sum(misses.values())}")
    for (a, b, bank, f, how), n in misses.most_common(40):
        print(f"   {a}->{b} bank {bank} flag {f} {how} x{n}")
    print(f"extra (the simulator changed, the game didn't): {sum(extras.values())}")
    for (a, b, bank, f, how), n in extras.most_common(40):
        print(f"   {a}->{b} bank {bank} flag {f} {how} x{n}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
