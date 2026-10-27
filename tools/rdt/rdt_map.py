r"""Room geometry from the player's own RDT files (read-only; nothing is copied into the repository): cameras, camera
switch zones, collision boxes, walk zones, plus the doors and item zones of the room scripts — the raw material for
a map of every room and for choosing a safe spot and the right camera when a test places the player.

    python tools/rdt/rdt_map.py "G:\GOG Games\Resident Evil" 105 [jill|chris]     # summary of one room

Layout (header pointers at 0x48.., reference: ecruells/resident-evil-pc-decomp Types.h, checked against the files):
- 0x01 camera count; cameras at 0x94, 0x2C bytes: from x/y/z, to x/y/z (i32 at +0x08..+0x1C)
- 0x48 switch zones, 0x14 bytes: camTo, camFrom (i16), four XZ corners (u16 — the game zero-extends them); grouped
  by camFrom, the first entry of each group is the camera's own header; table ends at camTo = camFrom = 0xFFFF
- 0x4C collision: header (split x/z, five per-quadrant counts as i32), then 0x0C-byte boxes xMax, zMax, xMin, zMin
  (u16), type (low byte shape: 1 rect, 3 circle, 4 soft zone, 5 rect), flags (bit 8 = blocks movement)
- 0x58 walk zones: count byte, then 0x0C-byte records from +2: x1, z1, x2, z2 (i16)
"""
from __future__ import annotations

import struct
import sys
from dataclasses import dataclass, field
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import rdt_index  # noqa: E402

VARIANTS = {"chris": "0", "jill": "1"}


@dataclass
class Room:
    room: str
    character: str
    cameras: list[dict] = field(default_factory=list)
    switches: list[dict] = field(default_factory=list)
    boxes: list[dict] = field(default_factory=list)
    walk: list[tuple[int, int, int, int]] = field(default_factory=list)
    lists: list[int] = field(default_factory=list)  # records per boundary list (script command 0x30 addresses them)
    doors: list[dict] = field(default_factory=list)
    items: list[dict] = field(default_factory=list)
    objects: list[dict] = field(default_factory=list)  # script-placed models (furniture, the dining-room clock …)

    def camera_for(self, x: int, z: int) -> int | None:
        """A camera that shows (x, z). Each camera's first switch record is its coverage quad; among the cameras whose
        quad contains the point, the one looking closest to it. Only cameras with a group in the switch table are
        returned: the game's redraw (display_room_camera_bg, 0x462D50) searches the table for the camera's group and
        runs off its end for a camera without one."""
        grouped = [s for s in self.switches if s["header"] and 0 <= s["from"] < len(self.cameras)]
        inside = [s["from"] for s in grouped if point_in_quad(x, z, s["quad"])] or [s["from"] for s in grouped]
        if not inside:
            return None
        def distance(c: int) -> float:
            tx, _, tz = self.cameras[c]["to"]
            return (tx - x) ** 2 + (tz - z) ** 2
        return min(inside, key=distance)

    def blocked(self, x: int, z: int, radius: int = 0) -> bool:
        for b in self.boxes:
            if not b["blocks"]:
                continue
            if b["shape"] == 3:  # circle inscribed in the box
                cx, cz = (b["x0"] + b["x1"]) / 2, (b["z0"] + b["z1"]) / 2
                r = min(b["x1"] - b["x0"], b["z1"] - b["z0"]) / 2 + radius
                if (x - cx) ** 2 + (z - cz) ** 2 <= r * r:
                    return True
            elif b["x0"] - radius <= x <= b["x1"] + radius and b["z0"] - radius <= z <= b["z1"] + radius:
                return True
        return False


def point_in_quad(x: int, z: int, quad: list[tuple[int, int]]) -> bool:
    """Convex quad test (same side of every edge), corners in order."""
    sign = 0
    for i in range(4):
        (ax, az), (bx, bz) = quad[i], quad[(i + 1) % 4]
        cross = (bx - ax) * (z - az) - (bz - az) * (x - ax)
        if cross != 0:
            if sign == 0:
                sign = 1 if cross > 0 else -1
            elif (cross > 0) != (sign > 0):
                return False
    return True


def path_for(install: Path, room: str, character: str) -> Path:
    usa = install / "USA" if (install / "USA").is_dir() else install
    return usa / f"Stage{room[0]}" / f"ROOM{room.upper()}{VARIANTS[character]}.RDT"


def load(install: Path, room: str, character: str) -> Room:
    data = path_for(install, room, character).read_bytes()
    r = Room(room.upper(), character)
    count = data[1]
    for i in range(count):
        o = 0x94 + i * 0x2C
        fx, fy, fz, tx, ty, tz = struct.unpack_from("<6i", data, o + 0x08)
        r.cameras.append({"id": i, "from": (fx, fy, fz), "to": (tx, ty, tz)})
    ptr = struct.unpack_from("<I", data, 0x48)[0]
    while ptr and ptr + 0x14 <= len(data):
        to, frm = struct.unpack_from("<hh", data, ptr)
        if to == -1 and frm == -1:
            break
        q = struct.unpack_from("<8H", data, ptr + 4)
        r.switches.append({"to": to, "from": frm, "quad": [(q[0], q[1]), (q[2], q[3]), (q[4], q[5]), (q[6], q[7])]})
        ptr += 0x14
    # The first entry of each camFrom group is that camera's own header record, not a switch zone.
    previous = None
    for s in r.switches:
        s["header"] = s["from"] != previous
        previous = s["from"]
    ptr = struct.unpack_from("<I", data, 0x4C)[0]
    if ptr:
        counts = struct.unpack_from("<5i", data, ptr + 4)
        r.lists = [c if 0 <= c < 4096 else 0 for c in counts]  # the five boundary lists, stored one after another
        total = sum(c for c in counts if 0 <= c < 4096)
        for i in range(total):
            o = ptr + 0x18 + i * 0x0C
            if o + 0x0C > len(data):
                break
            x1, z1, x0, z0, kind, flags = struct.unpack_from("<6H", data, o)
            r.boxes.append({"x0": x0, "z0": z0, "x1": x1, "z1": z1, "shape": kind & 0xFF, "step": kind >> 8,
                            "blocks": bool(flags & 0x100), "flags": flags})
    ptr = struct.unpack_from("<I", data, 0x58)[0]
    if ptr:
        n = data[ptr]
        for i in range(n):
            r.walk.append(struct.unpack_from("<4h", data, ptr + 2 + i * 0x0C))
    try:
        import scd  # noqa: PLC0415 — script-placed objects with the conditions that place them
        listing = scd.read_room(path_for(install, room, character), scd.Names())
        for part in ("init", "main"):
            for st in listing[part]:
                if st["kind"] == "object":
                    r.objects.append({"slot": st["slot"], "pos": st["pos"], "params": st["params"], "guards": st["guards"]})
    except (ImportError, ValueError, KeyError):
        pass
    for op, b in rdt_index.opcodes(data):
        if op == rdt_index.OP_DOOR:
            x, z, w, d = struct.unpack_from("<hhHH", b, 2)
            dest = b[0x0F]
            r.doors.append({"zone": (x, z, w, d), "to": f"{dest >> 5 or int(room[0])}{dest & 0x1F:02X}",
                            "arrival": struct.unpack_from("<4h", b, 0x10), "lock": b[0x0E]})
        elif op == rdt_index.OP_ITEM:
            x, z, w, d = struct.unpack_from("<hhHH", b, 2)
            r.items.append({"zone": (x, z, w, d), "item": b[0x0A], "quantity": b[0x0B], "flag": b[0x16]})
    return r


def main(argv: list[str]) -> int:
    install, room = Path(argv[0]), argv[1]
    character = argv[2] if len(argv) > 2 else "jill"
    r = load(install, room, character)
    print(f"room {r.room} ({character}): {len(r.cameras)} cameras, {sum(1 for s in r.switches if not s['header'])} switch "
          f"zones, {len(r.boxes)} collision boxes ({sum(b['blocks'] for b in r.boxes)} blocking), {len(r.walk)} walk zones, "
          f"{len(r.doors)} doors, {len(r.items)} items")
    for c in r.cameras:
        print(f"  camera {c['id']}: from {c['from']} looking at {c['to']}")
    for b in r.boxes[:12]:
        print(f"  box {b}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
