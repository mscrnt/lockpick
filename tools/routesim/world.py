r"""World model of RE1's rooms for the route simulator, read from the player's own room files (RDT), read-only.

Every room's init and main scripts become condition trees (if / else nesting kept, so a command's guards are judged
when the script reaches them, as the game does), and every event script (RDT +0x68) a list of steps whose inline
room-script blocks are trees too. Nothing is interpreted here; sim.py runs it.

    python tools/routesim/world.py "G:\GOG Games\Resident Evil" [jill|chris] [--out world.json] [--room 105]

Opcode sizes: tools/rdt/rdt_index.py (BioRand). Operand meanings: ecruells/resident-evil-pc-decomp
docs/SCD_SCRIPT_SYSTEM.md and docs/SCD_COMMAND_OPCODES.md (facts only). Event-machine widths: tools/rdt/events.py.
"""
from __future__ import annotations

import json
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "rdt"))
import events  # noqa: E402
import rdt_index  # noqa: E402

SIZES = rdt_index.SIZES
# Opcodes that are conditions when they follow an `if` (they return true/false to the interpreter).
CONDITIONS = {0x04, 0x06, 0x07, 0x10, 0x11, 0x1A, 0x1D, 0x22, 0x2C, 0x36, 0x38, 0x3C, 0x3F, 0x50}


def u16(b: bytes, at: int) -> int:
    return struct.unpack_from("<H", b, at)[0]


def s16(b: bytes, at: int) -> int:
    return struct.unpack_from("<h", b, at)[0]


def decode(op: int, b: bytes) -> dict:
    """One command or condition as a dict the simulator reads ({"op": …, named operands})."""
    if op in (0x04, 0x05):
        # [op, bank][sel, x]: bit index sel & 0x1F of byte (sel & 0xE0) >> 3 — the flat flag number is sel itself
        # read as byte*8 + bit with the game's MSB-first numbering, which is how the profiles' words count (flag n =
        # bit 31 - n % 32 of word n / 32).
        d = {"op": op, "bank": b[1], "flag": b[2]}
        if op == 0x04:
            d["set"] = b[3] == 0
        else:
            d["mode"] = {0: "set", 1: "clear", 2: "toggle"}.get(b[3], "set")
        return d
    if op == 0x06:
        return {"op": op, "field": b[1], "cmp": b[2], "value": b[3]}
    if op == 0x07:
        return {"op": op, "field": b[2], "cmp": b[3], "value": u16(b, 4)}
    if op == 0x08:
        return {"op": op, "field": b[1], "value": b[2]}
    if op == 0x0C:
        # Door record (rdt_index / Re1RoomMaps): zone at +2, target room +0x0F (stage in the top 3 bits), lock +0x0E
        # (0x80 = locked, low 6 bits its flagLocks index), key +0x18 (item id; 254 this side, 255 other side, 0 event).
        dest = b[0x0F]
        return {"op": op, "slot": b[1], "zone": [u16(b, 2), u16(b, 4), u16(b, 6), u16(b, 8)],
                "stage": dest >> 5, "room": dest & 0x1F, "lock": b[0x0E], "key": b[0x18], "probe": b[0x19],
                "arrival": [s16(b, 0x10), s16(b, 0x14)]}
    if op == 0x0D:
        return {"op": op, "slot": b[1], "zone": [u16(b, 2), u16(b, 4), u16(b, 6), u16(b, 8)], "handler": b[0x0A],
                "probe": b[0x0B], "words": [u16(b, 0x0C), u16(b, 0x0E), u16(b, 0x10)]}
    if op in (0x10, 0x11, 0x1A, 0x1D, 0x2C):
        return {"op": op, "item": b[1]}
    if op == 0x12:
        # Rewrites a room action's handler, probe flags and words; the zone stays (decomp SCD_COMMAND_OPCODES.md).
        return {"op": op, "slot": b[1], "handler": b[2], "probe": b[3], "words": [u16(b, 4), u16(b, 6), u16(b, 8)]}
    if op == 0x13:
        return {"op": op, "slot": b[1], "handler": b[2], "probe": b[3]}
    if op == 0x14:
        return {"op": op, "slot": b[2], "script": b[3]}
    if op == 0x18:
        # +0x17 is the pick-up entry's probe flags: without 0x01 the item lies there dead until a script arms it.
        return {"op": op, "slot": b[1] & 0x7F, "zone": [s16(b, 2), s16(b, 4), u16(b, 6), u16(b, 8)], "item": b[0x0A],
                "quantity": b[0x0B], "flag": b[0x16], "probe": b[0x17]}
    if op == 0x1B:
        return {"op": op, "type": b[1], "killFlag": b[3]}
    if op == 0x30:
        # A collision record rewritten (decomp: words [2], [3], [0], [1] from +4, +6, +8, +10; rdt_map reads the words
        # as x1, z1, x0, z0). A non-zero mode patches the record's 0x0F00 flag bits (solid or not).
        return {"op": op, "list": b[1], "index": b[2], "mode": b[3],
                "box": [u16(b, 8), u16(b, 10), u16(b, 4), u16(b, 6)]}
    if op == 0x20:
        return {"op": op, "pos": [s16(b, 8), s16(b, 10), s16(b, 12)]}
    if op == 0x22:
        return {"op": op, "family": b[1], "cmp": b[2], "value": b[3]}
    if op in (0x24, 0x2D):
        return {"op": op, "slot": b[1], "action": b[2]}
    if op == 0x29:
        return {"op": op, "fmv": b[1]}
    if op == 0x44:
        return {"op": op, "slot": b[1]}
    if op == 0x0B:
        return {"op": op, "message": b[1]}
    return {"op": op}


def block(data: bytes, start: int, end: int, problems: list[str], where: str) -> list[dict]:
    """A script block as a tree: {"if": [conds], "then": [...], "else": [...]} nodes and command dicts, in order."""
    root: list[dict] = []
    stack = [{"list": root, "end": end, "node": None, "collecting": False}]
    pos = start
    while pos < end:
        while len(stack) > 1 and pos >= stack[-1]["end"]:
            stack.pop()
        op = data[pos]
        size = SIZES[op] if op < len(SIZES) else 0
        if size == 0 or pos + size > end:
            problems.append(f"{where}: opcode {op:#04x} at {pos:#x} doesn't fit")
            break
        b = data[pos:pos + size]
        top = stack[-1]
        if op == 0x01:
            node = {"if": [], "then": [], "else": []}
            top["list"].append(node)
            stack.append({"list": node["then"], "end": pos + 2 + b[1], "node": node, "collecting": True})
        elif op == 0x02:
            if top["node"] is None:
                problems.append(f"{where}: else without if at {pos:#x}")
            else:
                top.update(list=top["node"]["else"], end=pos + b[1], collecting=False)
        elif op == 0x03:
            if top["node"] is not None:
                stack.pop()
        elif op == 0x00:
            pass
        elif op in CONDITIONS and top["collecting"]:
            top["node"]["if"].append(decode(op, b))
        else:
            top["collecting"] = False
            top["list"].append(decode(op, b))
        pos += size
    return root


def event(data: bytes, start: int, end: int, problems: list[str], where: str) -> list[dict]:
    """An event script's steps: {"block": tree} for inline room-script commands, start/goto/stop for other events,
    {"wait": tree} for a called condition the event waits on, {"end": True}. Animation and movement are skipped."""
    steps: list[dict] = []
    pos, state = start, 0
    pending_call: list[dict] | None = None
    while pos < end and len(steps) < 600:
        op = data[pos]
        if op == 0xFC:
            # Call: the command that follows (length p[1]) is run by the next 0xFD, which loops back while it fails.
            length = max(data[pos + 1], 2)
            pending_call = block(data, pos + 2, pos + length, problems, where) if length > 2 else []
            pos += length
            continue
        if op in events.CONTROL:
            if op == 0xFD and pending_call is not None:
                steps.append({"wait": pending_call})
                pending_call = None
            elif op == 0xFF:
                steps.append({"end": True})
                break
            pos += events.CONTROL[op]
            continue
        if state == 0:
            if op in (0x06, 0x07):
                length = data[pos + 1]
                if length < 2 or pos + length > end:
                    problems.append(f"{where}: bad inline block length at {pos:#x}")
                    break
                if op == 0x06:
                    # run_command_functions gets p+2 and reads a u16 block size there before the first command (the
                    # decomp's tools/evt_disasm.py); the size counts itself.
                    body_start, body_end = pos + 4, min(pos + 2 + u16(data, pos + 2), pos + length)
                else:
                    # One command, dispatched at p+2 and read at its own size (some length bytes are shorter).
                    size = SIZES[data[pos + 2]] if data[pos + 2] < len(SIZES) else 0
                    body_start, body_end = pos + 2, pos + 2 + max(size, length - 2)
                steps.append({"block": block(data, body_start, body_end, problems, where)})
                pos += length
                continue
            size = events.STATE0.get(op)
            if size is None:
                problems.append(f"{where}: unknown state-0 opcode {op:#04x} at {pos:#x}")
                break
            if op == 0x01:
                state = 1
            elif op in (0x02, 0x03):
                state = 2
            elif op == 0x05:
                steps.append({"start": data[pos + 2], "slot": data[pos + 1]})
            elif op == 0x08:
                steps.append({"goto": data[pos + 1]})
                break
            elif op == 0x09:
                steps.append({"stop": data[pos + 1]})
            pos += size
        elif state == 1:
            size = (10 if data[pos + 1] & 0x0F else 2) if op == 0x81 else events.STATE1.get(op)
            if size is None:
                problems.append(f"{where}: unknown state-1 opcode {op:#04x} at {pos:#x}")
                break
            if op in (0x80, 0x8B):
                state = 0
            pos += size
        else:
            size = events.STATE2.get(op)
            if size is None:
                problems.append(f"{where}: unknown state-2 opcode {op:#04x} at {pos:#x}")
                break
            if op == 0x01:
                state = 0
            pos += size
    return steps


def read_room(path: Path, problems: list[str]) -> dict:
    data = path.read_bytes()
    room = {}
    offsets = struct.unpack_from("<19I", data, 0x48)
    for label, index in (("init", 6), ("main", 7)):
        pos, nodes = offsets[index], []
        while (length := u16(data, pos)) != 0:
            nodes += block(data, pos + 2, pos + length, problems, f"{path.name} {label}")
            nodes.append({"op": 0x00, "chunk": True})  # each chunk is run on its own (a failed condition ends only it)
            pos += length
        room[label] = nodes
    starts = events.table(data)
    bounds = sorted(set(starts)) + [len(data)]
    room["events"] = [event(data, s, next(b for b in bounds if b > s), problems, f"{path.name} event {i}")
                      for i, s in enumerate(starts)]
    return room


def build(install: Path, character: str = "jill") -> tuple[dict, list[str]]:
    usa = install / "USA" if (install / "USA").is_dir() else install
    variant = "1" if character == "jill" else "0"
    rooms, problems = {}, []
    for path in sorted(usa.glob(f"Stage*/ROOM*{variant}.RDT")):
        if path.stat().st_size >= 0x94:
            rooms[path.name[4:7].upper()] = read_room(path, problems)
    return rooms, problems


def main(argv: list[str]) -> int:
    sys.stdout.reconfigure(encoding="utf-8")
    character = next((a for a in argv if a in ("jill", "chris")), "jill")
    rooms, problems = build(Path(argv[0]), character)
    if "--room" in argv:
        print(json.dumps(rooms[argv[argv.index("--room") + 1].upper()], indent=1))
    if "--out" in argv:
        Path(argv[argv.index("--out") + 1]).write_text(json.dumps(rooms), encoding="utf-8")
    print(f"{len(rooms)} rooms, {sum(len(r['events']) for r in rooms.values())} events, {len(problems)} problems",
          file=sys.stderr)
    for p in problems[:30]:
        print("  " + p, file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
