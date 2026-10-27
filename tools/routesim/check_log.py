r"""Checks the simulator against a story-flag log of real play (one line per room change or flag change: "HH:MM:SS
room 105 B5 +0x40000 -0x0", words B0-B7 = flagScenarioB, A0-A3 = flagScenarioA, as the scratch watcher writes them).

    python tools/routesim/check_log.py "G:\GOG Games\Resident Evil" jill <log.txt>

Starts a new game, follows the logged rooms one after another (entering each the way a door would) and compares
the story flags the game set on entering each room with the simulator's. Flags set later in a visit (pickups, uses)
aren't the simulator's business here: they are applied from the log before the next room so the state keeps up.
"""
from __future__ import annotations

import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import plan  # noqa: E402
import world as world_mod  # noqa: E402
from sim import Sim  # noqa: E402

LINE = re.compile(r"^(\S+) room (\w+)\s*(.*)$")
CHANGE = re.compile(r"([AB])(\d) \+0x([0-9a-f]+) -0x([0-9a-f]+)")


def flags(word: int, mask: int) -> list[int]:
    return [word * 32 + 31 - b for b in range(32) if mask & (1 << b)]


def main(argv: list[str]) -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    install, character, log = Path(argv[0]), argv[1], Path(argv[2])
    rooms, _ = world_mod.build(install, character)
    rules = json.loads(plan.RULES.read_text(encoding="utf-8"))
    sim = Sim(rooms, rules.get(character, {}) | {"newGame": rules.get("newGame", {})}, plan.cameras(install, character))
    s = plan.new_game(sim, character)
    entries = agree = 0
    overreach = 0
    lines = [LINE.match(l.strip()) for l in log.read_text(encoding="utf-8").splitlines()]
    lines = [m for m in lines if m]

    def changes(rest: str) -> set:
        out = set()
        for bank, w, plus, minus in CHANGE.findall(rest):
            out |= {(0 if bank == "A" else 1, f, "+") for f in flags(int(w), int(plus, 16))}
            out |= {(0 if bank == "A" else 1, f, "-") for f in flags(int(w), int(minus, 16))}
        return out

    i = 0
    while i < len(lines):
        room = lines[i].group(2)
        visit = set()
        j = i
        while j < len(lines) and lines[j].group(2) == room:
            visit |= changes(lines[j].group(3))
            j += 1
        if len(visit) > 12:  # a load or a new game: everything changes at once
            s = plan.new_game(sim, character) if not visit & {(1, 1, "+")} else s
            i = j
            continue
        if room not in sim.world:
            i = j
            continue
        before = [set(s.flags[0]), set(s.flags[1])]
        sim.enter(s, room)
        predicted = {(b, f, "+") for b in (0, 1) for f in s.flags[b] - before[b]}
        predicted |= {(b, f, "-") for b in (0, 1) for f in before[b] - s.flags[b]}
        entries += 1
        extra, missed = predicted - visit, visit - predicted
        if not extra and not missed:
            agree += 1
        else:
            overreach += bool(extra)
            print(f"{lines[i].group(1)} {room}: sim only {sorted(extra)}  game only {sorted(missed)}")
        for b, f, how in visit:  # keep up with what the game did in the visit (pickups, uses, scenes)
            (s.flags[b].add if how == "+" else s.flags[b].discard)(f)
        for b, f, how in extra:  # and undo what the simulator did that the game didn't
            (s.flags[b].discard if how == "+" else s.flags[b].add)(f)
        i = j
    print(f"{agree} of {entries} visits agree; {overreach} with flags only the simulator set")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
