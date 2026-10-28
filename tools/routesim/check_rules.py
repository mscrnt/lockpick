r"""Which rules in rules.json the game's own data now covers: each rule is dropped in turn and the game played over a
few shuffled orders; a rule is still needed if a playthrough then gets stuck, makes the move it guards before its
flags allow (gates, ways), or never gets the flags it sets (use, spots, kills, examine, heldBy).

    python tools/routesim/check_rules.py "G:\GOG Games\Resident Evil" [--seeds 4]
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import tempfile
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path

HERE = Path(__file__).resolve().parent
RULES = HERE / "rules.json"
KINDS = ("gates", "ways", "use", "spots", "kills", "examine", "heldBy")


def flag_set(fields: dict, bank: int, f: int) -> bool:
    name = {0: "flagScenarioA", 1: "flagScenarioB", 2: "flagLocks", 7: "flagRoomItems"}[bank]
    return bool(fields[f"{name}{f // 32}"] & (0x80000000 >> (f % 32)))


def guarded(rule: dict, kind: str, move: dict) -> bool:
    if kind == "gates":
        return move["room"] == rule["room"] and move["kind"] == "take" and move.get("item") == rule["item"]
    if kind == "ways":
        return move["room"] == rule["room"] and (
            (rule.get("to") and move["to"] == rule["to"] and move["kind"] in ("door", "spot"))
            or (rule.get("slot") is not None and move["label"].endswith(f"spot {rule['slot']}")))
    return False


def trial(install: str, character: str, kind: str, index: int, seed: int) -> str | None:
    """Plays without rule `index` of `kind`; returns why the rule is still needed, or None."""
    rules = json.loads(RULES.read_text(encoding="utf-8"))
    rule = rules[character][kind][index]
    del rules[character][kind][index]
    tmp = Path(tempfile.mkdtemp())
    (tmp / "rules.json").write_text(json.dumps(rules), encoding="utf-8")
    trace, route = tmp / "trace.jsonl", tmp / "route.json"
    out = subprocess.run([sys.executable, "-I", str(HERE / "plan.py"), install, character, "--seed", str(seed),
                          "--trace", str(trace), "--json", str(route)],
                         env=dict(os.environ, ROUTESIM_RULES=str(tmp / "rules.json")), capture_output=True, text=True).stdout
    if "the end" not in out.strip().splitlines()[-1]:
        return "stuck: " + out.strip().splitlines()[-1][:70]
    states = [json.loads(line) for line in trace.read_text(encoding="utf-8").splitlines()]
    moves = json.loads(route.read_text(encoding="utf-8"))
    for n, m in enumerate(moves):
        if guarded(rule, kind, m):
            f = states[n]["fields"]
            if not (all(flag_set(f, b, x) for b, x in rule.get("needs", []))
                    and not any(flag_set(f, b, x) for b, x in rule.get("without", []))):
                return "shortcut: " + m["label"]
    if kind in ("use", "spots", "kills"):
        final = states[-1]["fields"]
        missing = [(b, x) for b, x in rule.get("set", []) if b in (0, 1, 2, 7) and not flag_set(final, b, x)]
        if missing:
            return f"flags never set: {missing}"
    return None


def main(argv: list[str]) -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    install = argv[0]
    seeds = int(argv[argv.index("--seeds") + 1]) if "--seeds" in argv else 4
    rules = json.loads(RULES.read_text(encoding="utf-8"))
    jobs = [(c, k, i) for c in ("jill", "chris") for k in KINDS for i in range(len(rules[c].get(k, [])))]
    with ProcessPoolExecutor(max_workers=6) as pool:
        futures = {job: [pool.submit(trial, install, *job, seed) for seed in range(1, seeds + 1)] for job in jobs}
        for (character, kind, index), runs in futures.items():
            reasons = [r for r in (f.result() for f in runs) if r]
            rule = rules[character][kind][index]
            what = rule.get("room", "") + " " + str(rule.get("to", rule.get("slot", rule.get("item", ""))))
            print(f"{character:5} {kind:7} {what:10} {'NEEDED  ' + reasons[0] if reasons else 'covered by the game data'}",
                  flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
