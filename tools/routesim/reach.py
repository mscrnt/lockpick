"""Where the player can walk in a room, for the route simulator: the room file's collision records with the room
scripts' changes to them (command 0x30: the lift shaft in 300 closes while the lift is down at the falls), as a grid
of free cells grouped into connected areas — the same grid as the app's RoomPath (src/Lockpick.Core/Routes/RoomPath.cs:
250-unit cells, 350 clearance; walls 0x300 and furniture 0x200 are solid, whole-room zones aren't walls).

Only what the scripts change is trusted: the base geometry alone sometimes splits a room that's one in the game
(stairs between floors, the same rooms the app's straight lines show), so a zone is out of reach only when the
scripts' changes are what cut it off (it's reachable with the records they touch cleared, not with them as they are),
or when a record a script made solid covers a zone the player has to stand in (probe flag 0x40: the lift).
"""
from __future__ import annotations

import sys
from collections import deque
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "rdt"))
import rdt_map  # noqa: E402

CELL = 250
CLEARANCE = 350
SOLID = 0x300
REACH = 750  # the action probe tests 600 ahead of the player, from a cell centre


class Room:
    """One room's collision records, by boundary list, and its walkable areas for a set of script changes."""

    def __init__(self, room: rdt_map.Room) -> None:
        self.records = [dict(b) for b in room.boxes]
        self.lists = room.lists or [len(self.records)]
        self._areas: dict = {}

    def reachable(self, mods: dict, pos: tuple[int, int], zone: list[int], probe: int = 0x81) -> tuple[int, int] | None:
        """Where to stand for the zone, or None when the scripts' collision changes keep the player from it."""
        centre = (zone[0] + zone[2] // 2, zone[1] + zone[3] // 2)
        if not mods:
            return centre
        changes = tuple(sorted(mods.items()))
        full = self.areas(changes)
        spot = stand(full, area_at(full, *pos), zone)
        if spot is None:
            cleared = self.areas(tuple((k, (1, 1, 0, 0, 0)) for k, _ in changes))
            if stand(cleared, area_at(cleared, *pos), zone) is None:
                spot = centre  # the base geometry can't tell either (stairs): the scripts aren't what stops it
        # A zone tested at the player's own position (probe flag 0x40) needs standing in it; others are reached by
        # the action probe 600 ahead (a medal slot in the fountain, used from its edge).
        if spot is not None and probe & 0x40 and self.covered(changes, zone):
            return None
        return spot

    def covered(self, changes: tuple, zone: list[int]) -> bool:
        """A record a script made solid covers the zone's centre (the lift shaft while the lift is down)."""
        cx, cz = zone[0] + zone[2] / 2, zone[1] + zone[3] / 2
        for (listed, record), (x1, z1, x0, z0, mode) in changes:
            i = self.index(listed, record)
            if i is None:
                continue
            flags = self.records[i]["flags"] if not mode else (self.records[i]["flags"] & ~0x0F00) | ((mode << 8) & 0x0F00)
            if flags & SOLID and x0 <= cx <= x1 and z0 <= cz <= z1:
                return True
        return False

    def index(self, listed: int, record: int) -> int | None:
        if listed >= len(self.lists) or record >= self.lists[listed]:
            return None
        return sum(self.lists[:listed]) + record

    def areas(self, changes: tuple) -> tuple:
        """(x0, z0, width, height, labels): labels[i] = the area of cell i, -1 where it's blocked."""
        if changes not in self._areas:
            records = [dict(r) for r in self.records]
            for (listed, record), (x1, z1, x0, z0, mode) in changes:
                i = self.index(listed, record)
                if i is None:
                    continue
                records[i].update(x1=x1, z1=z1, x0=x0, z0=z0)
                if mode:
                    records[i]["flags"] = (records[i]["flags"] & ~0x0F00) | ((mode << 8) & 0x0F00)
            self._areas[changes] = label(records)
        return self._areas[changes]


def label(records: list[dict]) -> tuple:
    solid = [r for r in records if r["flags"] & SOLID and r["x1"] - r["x0"] < 60000 and r["z1"] - r["z0"] < 60000
             and r["x1"] > r["x0"] and r["z1"] > r["z0"]]
    xs = [r["x0"] for r in records] + [r["x1"] for r in records] or [0, 1]
    zs = [r["z0"] for r in records] + [r["z1"] for r in records] or [0, 1]
    x0, z0 = min(xs) - CELL, min(zs) - CELL
    width, height = (max(xs) - x0) // CELL + 3, (max(zs) - z0) // CELL + 3
    blocked = [False] * (width * height)
    for r in solid:
        if r["shape"] == 3:
            cx, cz = (r["x0"] + r["x1"]) / 2, (r["z0"] + r["z1"]) / 2
            radius = max(r["x1"] - r["x0"], r["z1"] - r["z0"]) / 2 + CLEARANCE
            box = (cx - radius, cz - radius, cx + radius, cz + radius)
        else:
            box = (r["x0"] - CLEARANCE, r["z0"] - CLEARANCE, r["x1"] + CLEARANCE, r["z1"] + CLEARANCE)
        for gx in range(max(0, int((box[0] - x0) // CELL)), min(width, int((box[2] - x0) // CELL) + 1)):
            for gz in range(max(0, int((box[1] - z0) // CELL)), min(height, int((box[3] - z0) // CELL) + 1)):
                px, pz = x0 + gx * CELL + CELL / 2, z0 + gz * CELL + CELL / 2
                if r["shape"] == 3 and (px - cx) ** 2 + (pz - cz) ** 2 > radius ** 2:
                    continue
                if box[0] <= px <= box[2] and box[1] <= pz <= box[3]:
                    blocked[gz * width + gx] = True
    labels = [-1] * (width * height)
    area = 0
    for start in range(width * height):
        if blocked[start] or labels[start] >= 0:
            continue
        labels[start] = area
        queue = deque([start])
        while queue:
            i = queue.popleft()
            gx, gz = i % width, i // width
            for nx, nz in ((gx + 1, gz), (gx - 1, gz), (gx, gz + 1), (gx, gz - 1)):
                if 0 <= nx < width and 0 <= nz < height:
                    j = nz * width + nx
                    if not blocked[j] and labels[j] < 0:
                        labels[j] = area
                        queue.append(j)
        area += 1
    return x0, z0, width, height, labels


def cell(grid: tuple, x: float, z: float) -> int | None:
    x0, z0, width, height, _ = grid
    gx, gz = int((x - x0) // CELL), int((z - z0) // CELL)
    return gz * width + gx if 0 <= gx < width and 0 <= gz < height else None


def area_at(grid: tuple, x: float, z: float) -> int | None:
    """The area the point is in, or the nearest free cell's (a position on furniture's edge, a door's zone)."""
    x0, z0, width, height, labels = grid
    start = cell(grid, x, z)
    if start is None:
        gx, gz = min(max(int((x - x0) // CELL), 0), width - 1), min(max(int((z - z0) // CELL), 0), height - 1)
        start = gz * width + gx
    if labels[start] >= 0:
        return labels[start]
    best = None
    for i, a in enumerate(labels):
        if a >= 0:
            d = (i % width - start % width) ** 2 + (i // width - start // width) ** 2
            if best is None or d < best[0]:
                best = (d, a)
    return best[1] if best else None


def stand(grid: tuple, area: int | None, zone: list[int]) -> tuple[int, int] | None:
    """A free cell of `area` within reach of the zone (the one nearest its centre), or None."""
    if area is None:
        return None
    x0, z0, width, height, labels = grid
    zx, zz, zw, zd = zone
    best = None
    for gx in range(max(0, int((zx - REACH - x0) // CELL)), min(width, int((zx + zw + REACH - x0) // CELL) + 1)):
        for gz in range(max(0, int((zz - REACH - z0) // CELL)), min(height, int((zz + zd + REACH - z0) // CELL) + 1)):
            if labels[gz * width + gx] != area:
                continue
            px, pz = x0 + gx * CELL + CELL / 2, z0 + gz * CELL + CELL / 2
            dx = max(zx - px, 0, px - (zx + zw))
            dz = max(zz - pz, 0, pz - (zz + zd))
            if dx * dx + dz * dz <= REACH * REACH:
                d = (px - (zx + zw / 2)) ** 2 + (pz - (zz + zd / 2)) ** 2
                if best is None or d < best[0]:
                    best = (d, int(px), int(pz))
    return (best[1], best[2]) if best else None
