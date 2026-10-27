r"""Plays a simulated playthrough (tools/routesim) in the real game, move by move, and checks the game against the
simulator after every move (dev-only autoplay, never shipped).

    python -I tools/autoplay/simplay.py <out-dir> <save-backup-dir> jill|chris [--from N] [--moves N] [--no-new-game]

The playthrough comes from tools/routesim/plan.py (run here, so it matches the rules on disk). Moves are made the way a
player would where the harness can: doors opened with the action button (keys used, "discard?" answered), items
picked up, items used at their spots, spots checked; a door that won't open is entered through its record instead
(logged as WARP). Enemies are frozen; a kill sets the enemy's HP to 1 and shoots it once (bosses die through their own
code, so their flags are the game's). Pushes are cheated: the flags the simulator expects are set (logged as CHEAT).
After every move the story, lock and room-item flags are compared with the simulator's: "missing" are flags the
simulator set that the game didn't, "extra" the other way round. SAVE/ stays read-only and is checked at the end.
"""
from __future__ import annotations

import json
import struct
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent / "routesim"))
sys.path.insert(0, str(HERE.parent / "rdt"))
import ap  # noqa: E402
import pickup  # noqa: E402
import plan as sim_plan  # noqa: E402
import rdt_map  # noqa: E402
import route as route_mod  # noqa: E402
import survey  # noqa: E402
import export  # noqa: E402
import world as world_mod  # noqa: E402

BANKS = {0: (0xBE98C0, 16), 1: (0xBE9854, 32), 2: (0xBE9874, 8), 7: (0xBE989C, 32)}
WEAPON, AMMO = 2, 11  # the Beretta and its clips, for the kills


def game_flags() -> dict[int, set[int]]:
    return {b: route_mod.set_flags(ap.peek(addr, size)) for b, (addr, size) in BANKS.items()}


def expected(trace_line: dict) -> dict[int, set[int]]:
    names = {0: ("flagScenarioA", 4), 1: ("flagScenarioB", 8), 2: ("flagLocks", 2), 7: ("flagRoomItems", 8)}
    out = {}
    for b, (name, words) in names.items():
        out[b] = {w * 32 + bit for w in range(words) for bit in range(32)
                  if trace_line["fields"][f"{name}{w}"] & (0x80000000 >> bit)}
    return out


def playthrough(character: str) -> tuple[list[dict], list[dict]]:
    """The simulator's moves and the state after each (trace[0] = the new game, trace[i + 1] = after move i)."""
    rooms, _ = world_mod.build(survey.INSTALL, character)
    rules = json.loads(sim_plan.RULES.read_text(encoding="utf-8"))
    sim = sim_plan.Sim(rooms, rules.get(character, {}) | {"newGame": rules.get("newGame", {})},
                       sim_plan.cameras(survey.INSTALL, character),
                       sim_plan.geometry(survey.INSTALL, character))
    s = sim_plan.new_game(sim, character)
    ever: set = set()
    sim_plan.remember(sim_plan.State(character), s, ever)
    moves, trace = [], [sim_plan.snapshot(sim, s, {"room": s.room, "to": s.room})]
    for _ in range(800):
        found = sim_plan.nearest(sim, s, ever)
        if found is None:
            break
        path, t = found
        sim_plan.remember(s, t, ever)
        s = t
        for step, gained in path:
            state = step.pop("_state")
            trace.append(sim_plan.snapshot(sim, state, step))
            moves.append({**step, "gained": gained})
    return moves, trace


class Player:
    def __init__(self, character: str, moves: list[dict], log) -> None:
        self.character, self.moves, self.log = character, moves, log
        self.route = route_mod.Route(character, log)
        self.route.room = ap.state().get("room")

    # ---- helpers ---------------------------------------------------------------------------------------------------

    def room(self) -> rdt_map.Room:
        return rdt_map.load(survey.INSTALL, self.route.room, self.character)

    def calm(self, timeout: float = 90) -> None:
        ap.settle(timeout)
        for _ in range(3):
            if ap.message_state():
                ap.dismiss()
                ap.settle(20)

    def needed_later(self, index: int) -> set[int]:
        later = self.moves[index:]
        return ({m["item"] for m in later if m["kind"] in ("use", "examine") and "item" in m}
                | {m["key"] for m in later if m["kind"] == "door" and m.get("key") not in (None, 0, 254, 255)}
                | {WEAPON, AMMO, 49, 77, 42})

    def make_room(self, index: int) -> None:
        """Frees inventory slots for a pickup: whatever nothing later in the route needs goes (ammo, herbs, spent keys)."""
        held = ap.inventory()
        limit = ap.slot_limit()
        if len([i for i, _, _ in held if i]) < limit:
            return
        keep = self.needed_later(index)
        kept = [(i, q) for i, q, _ in held if i and i in keep]
        dropped = [i for i, _, _ in held if i and i not in keep]
        if not dropped:
            self.log("  inventory full of things the route still needs")
            return
        base = ap.inventory_slots()
        changes = []
        for slot in range(limit):
            item, qty = kept[slot] if slot < len(kept) else (0, 0)
            for offset, value in ((0, item), (1, qty)):
                changes.append(f"{base + slot * 2 + offset:X}:{value:02X}:{~value & 0xFF:02X}")
        ap.send("state-apply " + " ".join(changes))
        self.log(f"  made room: put away {[ap.item_name(i, shown=False) for i in dropped]}")

    def press_here(self) -> list[str]:
        """Presses the action button and answers what the game asks (yes), as a player checking a spot would."""
        said = []
        ap.send("press A 150", "wait 700")
        end = time.time() + 25
        while time.time() < end:
            state = ap.message_state()
            if state == ap.PROMPT:
                said.append(ap.message_text())
                ap.answer(True)
            elif state in (ap.PAGE, ap.WAITING):
                said += ap.dismiss()
            elif state or not ap.in_control():
                ap.send("wait 250")
            else:
                break
        return said

    def stand(self, zone: list[int]) -> bool:
        self.calm(60)
        self.route.room = ap.state().get("room")
        room = self.room()
        for x, z, angle, _ in pickup.spots({"zone": zone}, room):
            camera = room.camera_for(x, z)
            cut = camera is not None and not ap.flags() & 0x100000  # no cuts while a cutscene holds the camera
            self.retrying(lambda: ap.send(f"place {x} {z} {angle}", "wait 400",
                                          *([f"camera {camera}", "wait 400"] if cut else [])))
            if ap.in_control():
                return True
        return False

    # ---- moves -----------------------------------------------------------------------------------------------------

    def tour(self, done) -> list[int]:
        """Walks the room the way a player exploring it would: stands in each camera's view in turn (a free spot near
        the middle of the camera's area, the camera cut to it), waits for whatever that view sets off (the zombie
        getting up past Kenneth fires on camera 4 of 104), and stops once `done()` says the expected flags are in.
        Returns the cameras visited."""
        self.route.room = ap.state().get("room")
        room = self.room()
        visited = []
        for header in [sw for sw in room.switches if sw["header"] and 0 <= sw["from"] < len(room.cameras)]:
            camera, quad = header["from"], header["quad"]
            xs, zs = [p[0] for p in quad], [p[1] for p in quad]
            cx, cz = sum(xs) / 4, sum(zs) / 4
            best = None
            for gx in range(min(xs), max(xs) + 1, 500):
                for gz in range(min(zs), max(zs) + 1, 500):
                    if rdt_map.point_in_quad(gx, gz, quad) and not room.blocked(gx, gz, 450)                             and room.camera_for(gx, gz) == camera:
                        d = (gx - cx) ** 2 + (gz - cz) ** 2
                        if best is None or d < best[0]:
                            best = (d, gx, gz)
            if best is None:
                continue
            self.calm(30)
            if not ap.in_control() or ap.state().get("room") != self.route.room:
                break
            try:
                cut = not ap.flags() & 0x100000
                ap.send(f"place {best[1]} {best[2]} 0", "wait 300", *([f"camera {camera}"] if cut else []), "wait 1200")
            except ap.Failed:
                continue
            visited.append(camera)
            self.calm(60)
            if done():
                break
        return visited

    def retrying(self, action, tries: int = 2):
        """Runs a harness step, waiting out a cutscene or message that interrupts it (walking into a room's trigger
        starts one: Barry and the blood in the dining room) and trying again."""
        for attempt in range(tries):
            try:
                return action()
            except ap.Failed as error:
                if attempt == tries - 1:
                    raise
                self.log(f"  interrupted ({str(error).splitlines()[-1][:100]}); waiting it out")
                self.calm(30)
        return None

    def door(self, m: dict) -> str:
        target = m["to"]
        if ap.state().get("room") == target:
            return "already there"
        self.route.room = ap.state().get("room")
        if self.route.room == m["room"] and self.retrying(lambda: self.route.open_door(target)):
            return "opened"
        if ap.state().get("room") == target:
            return "opened (after a scene)"
        self.calm(60)
        self.retrying(lambda: self.route.enter(target))
        return "WARP"

    def take(self, m: dict, index: int) -> str:
        self.calm(60)
        self.route.room = ap.state().get("room")
        self.make_room(index)
        if self.retrying(lambda: self.route.take(m["item"])):
            return "taken"
        if m.get("zone") and self.stand(m["zone"]):
            said = self.press_here()
            if m["item"] in [i for i, _, _ in ap.inventory()]:
                return f"taken (said {said})"
        return "FAILED"

    def use(self, m: dict) -> str:
        item = m["item"]
        if item not in [i for i, _, _ in ap.inventory()]:
            return "FAILED (not held)"
        if not m.get("zone") or not self.stand(m["zone"]):
            return "FAILED (no spot)"
        info = ap.use_item(item)
        self.calm(120)
        return f"used ({info})"

    def spot(self, m: dict, index: int) -> str:
        if m.get("push"):
            for b, f, how in m["flags"]:
                if how == "+" and b in (0, 1):
                    ap.send(f"flag {'A' if b == 0 else 'B'} {f} 1")
            return "CHEAT (push)"
        self.make_room(index)
        if not m.get("zone") or not self.stand(m["zone"]):
            return "FAILED (no spot)"
        said = self.press_here()
        self.calm(120)
        return f"checked (said {said})"

    def kill(self, m: dict) -> str:
        if ap.equipped() != WEAPON:
            ap.equip_now(WEAPON, 15, log=self.log)
        killed = []
        for e in ap.enemies():
            if e.get("hp", 0) <= 0:
                continue
            ap.send(f"enemy-hp {e['slot']} 1")
            try:
                shots = ap.fire_at(e["slot"], shots=3)
                killed.append((e["slot"], shots[-1]["after"] if shots else None))
            except ap.Failed as error:
                killed.append((e["slot"], f"missed: {error}"))
        self.calm(120)
        return f"shot {killed}"

    def examine(self, m: dict) -> str:
        return f"examined ({ap.examine(m['item'])})"

    def play(self, index: int, m: dict) -> str:
        kind = m["kind"]
        if kind == "door":
            return self.door(m)
        if kind == "take":
            return self.take(m, index)
        if kind == "use":
            return self.use(m)
        if kind == "spot":
            return self.spot(m, index)
        if kind == "kill":
            return self.kill(m)
        if kind == "examine":
            return self.examine(m)
        return f"unknown move {kind}"


def main(argv: list[str]) -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    out, backup, character = Path(argv[0]), Path(argv[1]), argv[2]
    start = int(argv[argv.index("--from") + 1]) if "--from" in argv else 0
    count = int(argv[argv.index("--moves") + 1]) if "--moves" in argv else 10_000
    out.mkdir(parents=True, exist_ok=True)
    logfile = (out / "simplay.log").open("a", encoding="utf-8")

    def log(text: str) -> None:
        line = f"{time.strftime('%H:%M:%S')} {text}"
        print(line, flush=True)
        logfile.write(line + "\n")
        logfile.flush()

    moves, trace = playthrough(character)
    rooms, _ = world_mod.build(survey.INSTALL, character)
    needed = export.kept(moves, rooms, character)
    log(f"{character}: {len(moves)} simulated moves ({len(needed)} the route keeps, plus doors); "
        f"playing {start}..{min(len(moves), start + count) - 1}")
    survey.protect_saves()
    results = []
    try:
        if "--no-new-game" not in argv:
            ap.serve()
            ap.send(f"record {out / 'captures'}")
            log(f"new game in {ap.new_game(character):.0f} s; {ap.state()}")
            ap.send(f"give {WEAPON} 15", f"give {AMMO} 60")
        player = Player(character, moves, log)
        for index in range(start, min(len(moves), start + count)):
            m = moves[index]
            if index not in needed and m["kind"] != "door" and not (m["kind"] == "spot" and m["to"] != m["room"]):
                continue  # a side move (ammo, a map, a file): not something the route asks for
            ap.send("freeze-enemies", check=False)
            ap.send("heal", check=False)
            before = game_flags()
            try:
                result = player.play(index, m)
            except ap.Failed as error:
                result = f"FAILED ({error})"
                player.calm(30)
            want = expected(trace[index + 1])
            was = expected(trace[index])

            def short() -> dict:
                now = game_flags()
                return {b: v for b, v in ((b, sorted((want[b] ^ was[b]) - (now[b] ^ before[b]))) for b in BANKS) if v}

            if short() and "FAILED" not in result:
                # Expected scenes that need the player to walk the room (a camera's view): explore it like a player.
                toured = player.tour(lambda: not short())
                result += f"; walked cameras {toured}"
            now = game_flags()
            missing = {b: sorted((want[b] ^ was[b]) - (now[b] ^ before[b])) for b in BANKS}
            extra = {b: sorted((now[b] ^ before[b]) - (want[b] ^ was[b])) for b in BANKS}
            missing = {b: v for b, v in missing.items() if v}
            extra = {b: v for b, v in extra.items() if v}
            room = ap.state().get("room")
            ok = not missing and "FAILED" not in result and room == m["to"]
            log(f"{'ok  ' if ok else 'DIFF'} {index:3} {m['label']}: {result}; in {room}"
                + (f"; missing {missing}" if missing else "") + (f"; extra {extra}" if extra else ""))
            results.append({"index": index, "move": m["label"], "result": result, "room": room, "ok": ok,
                            "missing": missing, "extra": extra})
            (out / "simplay.json").write_text(json.dumps(results, indent=1) + "\n", encoding="utf-8")
            if room != m["to"] and m["kind"] == "door":
                # A scene moved the player on (or back): go where the playthrough is, through the door record.
                player.calm(60)
                if ap.state().get("room") != m["to"]:
                    try:
                        player.retrying(lambda: player.route.enter(m["to"]))
                        log(f"  WARP to {m['to']} to keep on the playthrough")
                    except ap.Failed as error:
                        log(f"  stopping: can't get to {m['to']} ({error})")
                        break
    finally:
        ap.send("record stop", check=False)
        survey.check_saves(backup, log)
        good = sum(r["ok"] for r in results)
        log(f"done: {good} of {len(results)} moves matched the simulator")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
