"""RE1 route simulator: runs the room scripts of world.py against an abstract game state.

The state is what the scripts read and write: the flag banks, the inventory, the room and the transient "item used /
picked up / standing at" bytes. A move (enter a room through a door, pick up an item, use an item at a spot, step on
a trigger, push something onto a spot, kill what's in the room) changes it the way the game would: the room's init
script on entering, then the main script and the running event scripts until nothing changes.

What the scripts can't say (position, timing, the camera) is assumed in the player's favour: distance and facing tests
pass, the camera is the room's first, random rolls come up 0. Enemies die when asked (the route may cheat).
"""
from __future__ import annotations

import copy
from dataclasses import dataclass, field

BANKS = 10
PERSISTENT = (0, 1, 2, 3, 7, 8)  # saved in the BioCard (decomp docs/SCENARIO_FLAGS.md); 4/5/6/9 reset or per frame
STORY = (0, 1)
# State-byte fields (index from g_stageId, decomp docs/SCD_COMMAND_OPCODES.md 0x06).
STAGE, ROOM, CAMERA, CHARACTER, FWD_ACTION, ENT_ACTION, USED_ITEM, PICKED_ITEM = 0, 1, 2, 9, 16, 17, 18, 19
# Byte 3 (the decomp names it attractMode_RoomCameraId) is tested against the neighbouring rooms' numbers (305: 15/16
# for 30F/310, 302: 0/1 for 300/301 where the lifts arrive): it holds the room the player came from.
CAME_FROM = 3
MENU_CHOICE = 5
NO_ACTION = 0xFF
PENDING_DOOR = 1000  # not a game byte: a door a script sends the player through once the frame ends
KNOWN_FIELDS = {STAGE, ROOM, CAME_FROM, CHARACTER, FWD_ACTION, ENT_ACTION, USED_ITEM, PICKED_ITEM}
ITEM_USE_BASE = 0x1B  # g_itemUseFlags bit = item id - 0x1B: "this item can be used here"


@dataclass
class Event:
    room: str
    script: int
    pc: int = 0


@dataclass
class State:
    character: str  # "jill" / "chris"
    room: str = ""
    answer: int = 0  # how the player answers a yes/no question asked during the move (0 yes, 1 no)
    flags: list[set[int]] = field(default_factory=lambda: [set() for _ in range(BANKS)])
    inventory: dict[int, int] = field(default_factory=dict)  # item id -> quantity (capacity is ignored)
    fields: dict[int, int] = field(default_factory=dict)
    actions: dict[int, dict] = field(default_factory=dict)  # room action table: slot -> door / item / action record
    enemies: list[dict] = field(default_factory=list)
    events: dict[int, Event] = field(default_factory=dict)  # slot -> running event
    pos: tuple[int, int] | None = None  # where the player stands (a door's arrival, a script's move, the last spot)
    mods: dict = field(default_factory=dict)  # this visit's collision changes: (list, record) -> (x1, z1, x0, z0, mode)

    def key(self) -> tuple:
        """What tells two states apart for the search: persistent flags, inventory, room."""
        return (self.room, tuple(frozenset(self.flags[b]) for b in PERSISTENT if b != 8),
                tuple(sorted(self.inventory.items())))

    def clone(self) -> "State":
        return copy.deepcopy(self)


def room_id(stage: int, room: int) -> str:
    return f"{stage + 1}{room:02X}"


class Sim:
    """Runs one character's world (world.py output) against a State."""

    def __init__(self, world: dict, rules: dict | None = None, cameras=None, geometry=None):
        self.world = world
        self.rules = rules or {}
        self.log: list[str] = []
        self.cameras = cameras  # room -> function (x, z) -> camera id or None (tools/rdt/rdt_map.py)
        self.geometry = geometry  # room -> reach.Room or None: where the player can walk

    def where(self, s: State, zone: list[int], probe: int = 0x81) -> tuple[int, int] | None:
        """Where the player can stand to reach a zone from where they are (the collision records as the room's scripts
        left them), or None if it's out of reach. Without the room's geometry, or a position, the zone counts as
        reachable (its centre)."""
        centre = (zone[0] + zone[2] // 2, zone[1] + zone[3] // 2)
        geo = self.geometry(s.room) if self.geometry and s.pos else None
        return centre if geo is None else geo.reachable(s.mods, s.pos, zone, probe)

    def stand(self, s: State, zone: list[int]) -> None:
        """The player stands in a zone: the camera that shows it (None when unknown: every camera test passes)."""
        if self.cameras is not None:
            s.fields[CAMERA] = self.cameras(s.room, zone[0] + zone[2] // 2, zone[1] + zone[3] // 2)

    # ---- conditions and commands -------------------------------------------------------------------------------

    def test(self, s: State, c: dict) -> bool:
        op = c["op"]
        if op == 0x04:
            if c["bank"] == 5:
                return True  # runtime frame state (cutscene / control bits): the player can be in either
            return (c["flag"] in s.flags[c["bank"]]) == c["set"]
        if op == 0x06:
            if c["field"] == CAMERA:
                camera = s.fields.get(CAMERA)
                # Walking around the room the player passes every camera; standing at a spot, the one showing it.
                return True if camera is None else compare(camera, c["cmp"], c["value"])
            if c["field"] == MENU_CHOICE and c["value"] in (0, 1):
                # The answer to a yes/no question (bit 0: 0 yes, 1 no; decomp g_menu_choice_id): the player's choice.
                return compare(s.answer, c["cmp"], c["value"])
            if c["field"] not in KNOWN_FIELDS:
                return True  # camera, a message being up, attract: the player can walk to any camera
            return compare(s.fields.get(c["field"], 0), c["cmp"], c["value"])
        if op == 0x07:
            return compare(0, c["cmp"], c["value"])  # random roll / countdown: 0
        if op == 0x10:
            return s.fields.get(USED_ITEM, 0) == c["item"]
        if op == 0x11:
            return s.fields.get(PICKED_ITEM, 0) == c["item"]
        if op == 0x1A:
            return s.inventory.get(c["item"], 0) > 0
        if op == 0x1D:
            return False  # equipped weapon: not modelled
        if op == 0x2C:
            if s.inventory.get(c["item"], 0) > 0:
                del s.inventory[c["item"]]
                self.log.append(f"  (script takes {c['item']})")
                return True
            return False
        if op in (0x22, 0x36, 0x3C, 0x3F):
            return True  # ammo count, object state, distance, facing: in the player's favour
        return False  # d-pad held, costume

    def run(self, s: State, nodes: list[dict], room: str, top: bool = True) -> bool:
        """Runs a block; False once a condition standing on its own fails, which ends the block there (the dispatcher
        stops on a command returning 0, decomp docs/SCD_SCRIPT_SYSTEM.md §6: a switch's event counts only if the
        switch before it was pressed). At the top, a script's chunks (each ended by 0x00) are run one by one."""
        skipping = False
        for n in nodes:
            if n.get("chunk"):
                skipping = False
                continue
            if skipping:
                continue
            if "if" in n:
                ok = self.run(s, n["then"] if all(self.test(s, c) for c in n["if"]) else n["else"], room, top=False)
            elif n["op"] in COND_OPS:
                ok = self.test(s, n)
            else:
                self.command(s, n, room)
                ok = True
            if not ok:
                if not top:
                    return False
                skipping = True
        return not skipping

    def command(self, s: State, c: dict, room: str) -> None:
        op = c["op"]
        if op == 0x05:
            bank, flag = c["bank"], c["flag"]
            if c["mode"] == "set" or (c["mode"] == "toggle" and flag not in s.flags[bank]):
                s.flags[bank].add(flag)
            else:
                s.flags[bank].discard(flag)
        elif op == 0x08 and c["field"] != CHARACTER:
            # Cutscenes write the character byte to swap models for a scene; the playable character stays (Chris's
            # Rebecca stretch isn't modelled).
            s.fields[c["field"]] = c["value"]
        elif op == 0x0C:
            s.actions[c["slot"]] = {"kind": "door", **c}
        elif op == 0x0D:
            s.actions[c["slot"]] = {"kind": "action", **c}
        elif op == 0x12 and c["slot"] in s.actions:
            s.actions[c["slot"]].update(handler=c["handler"], probe=c["probe"], words=c["words"])
        elif op == 0x13 and c["slot"] in s.actions:
            s.actions[c["slot"]].update(handler=c["handler"], probe=c["probe"])
        elif op == 0x18:
            s.actions[c["slot"]] = {"kind": "item", **c}
        elif op == 0x1B:
            s.enemies.append(c)
        elif op == 0x20:
            s.pos = (c["pos"][0], c["pos"][2])
        elif op == 0x30:
            s.mods[(c["list"], c["index"])] = (*c["box"], c["mode"])
        elif op == 0x14:
            self.start(s, room, c["slot"], c["script"])
        elif op == 0x44:
            s.events.pop(c["slot"], None)
        elif op in (0x24, 0x2D):
            # A room action run by the script: the pick-up handlers (include_key 3, set_key_flag 4, set_room_event_flag 0x0D,
            # pickup_key_event 0x0F) on an item record hand the item over (the serum on the shelf, items given in
            # cutscenes); got_item (0x2D) always does.
            record = s.actions.get(c["slot"])
            if record and record["kind"] == "item" and (op == 0x2D or c["action"] in (3, 4, 0x0D, 0x0F))                     and record["flag"] in s.flags[7]:
                self.give(s, record)
                s.fields[PICKED_ITEM] = record["item"]
            elif record and record["kind"] == "door" and op == 0x24 and c["action"] in (1, 5, 6):
                s.fields[PENDING_DOOR] = c["slot"]  # a lift or a cutscene takes the player through this door
            elif record and op == 0x24 and c["action"] == 7 and "words" in record:
                bank, flag, value = record["words"]
                (s.flags[bank].add if value else s.flags[bank].discard)(flag)

    def start(self, s: State, room: str, slot: int, script: int) -> None:
        if script >= len(self.world[room]["events"]):
            return
        if slot > 7:
            slot = next((i for i in range(8) if i not in s.events), 7)
        s.events[slot] = Event(room, script)

    def step_events(self, s: State) -> None:
        """One frame of the event machine: every running event takes one step (decomp: room_events_check runs 8 slots,
        one step each per frame), so events that hand a flag back and forth see each other's changes in between
        (the gallery: event 13 clears sys[22] for a moment, the switch's event waits for exactly that)."""
        for slot in sorted(list(s.events)):
            e = s.events.get(slot)
            if e is None:
                continue
            if e.room != s.room:
                s.events.pop(slot, None)
                continue
            script = self.world[e.room]["events"][e.script]
            if e.pc >= len(script):
                s.events.pop(slot, None)
                continue
            st = script[e.pc]
            if "block" in st:
                self.run(s, st["block"], e.room)
            elif "wait" in st:
                if not all(self.test(s, c) for c in st["wait"] if "op" in c and c["op"] in COND_OPS):
                    continue  # still waiting: try again next frame
            elif "start" in st:
                self.start(s, e.room, st["slot"], st["start"])
            elif "stop" in st:
                s.events.pop(st["stop"], None)
            elif "goto" in st:
                e.script, e.pc = st["goto"], 0
                continue
            elif "end" in st:
                s.events.pop(slot, None)
                continue
            if s.events.get(slot) is e:
                e.pc += 1

    def settle(self, s: State, quiet: int = 30, frames: int = 600) -> None:
        """Frames of the main script and the event machine until nothing has changed for `quiet` frames; the
        transient bytes (item used, item picked up, the item-use bits) last the first frame only."""
        calm = 0
        for frame in range(frames):
            before = (tuple(frozenset(f) for f in s.flags), tuple(sorted(s.inventory.items())),
                      tuple(sorted((k, v) for k, v in s.fields.items() if v is not None)))
            self.run(s, self.world[s.room]["main"], s.room)
            self.step_events(s)
            if frame == 0:
                s.fields[USED_ITEM] = 0
                s.fields[PICKED_ITEM] = 0
                s.flags[9].clear()
            after = (tuple(frozenset(f) for f in s.flags), tuple(sorted(s.inventory.items())),
                     tuple(sorted((k, v) for k, v in s.fields.items() if v is not None)))
            calm = calm + 1 if after == before else 0
            if calm >= quiet or (not s.events and calm >= 2):
                break
        slot = s.fields.pop(PENDING_DOOR, None)
        if slot is not None and slot in s.actions:
            target = target_of(s, s.actions[slot])
            if target in self.world:
                arrival = s.actions[slot].get("arrival")
                s.pos = tuple(arrival) if arrival else None
                self.enter(s, target)

    # ---- moves ----------------------------------------------------------------------------------------------------

    def enter(self, s: State, room: str) -> None:
        if s.room:
            s.fields[CAME_FROM] = int(s.room[1:], 16)
        s.room = room
        s.fields[STAGE], s.fields[ROOM] = int(room[0]) - 1, int(room[1:], 16)
        s.fields[CAMERA] = None
        s.fields[FWD_ACTION] = s.fields[ENT_ACTION] = NO_ACTION
        s.actions, s.enemies, s.events, s.mods = {}, [], {}, {}
        for b in (4, 9):
            s.flags[b].clear()
        visited = self.rules.get("visitedFlag", {}).get(room)
        if visited is not None:
            s.flags[8].add(visited)
        self.run(s, self.world[room]["init"], room)
        for record in s.actions.values():  # items this game never places (Jill's ink ribbons): the room clears them
            if record["kind"] == "item" and record["item"] in self.rules.get("absent", []):
                s.flags[7].discard(record["flag"])
        self.settle(s)

    def give(self, s: State, record: dict) -> None:
        s.flags[7].discard(record["flag"])
        s.inventory[record["item"]] = s.inventory.get(record["item"], 0) + max(record.get("quantity", 1), 1)

    def present(self, s: State, record: dict) -> bool:
        """An item lying in the room that can be reached (rules "gates": behind a wall, plant or statue until a
        flag says it's open)."""
        if not (record["kind"] == "item" and record["item"] > 0 and record["flag"] in s.flags[7]
                and record["zone"][2] > 1 and record.get("probe", 0x81) & 0x01
                and record.get("handler", 4) in (3, 4, 0x0F)):  # a pick-up handler; 0 = switched off
            return False
        for gate in self.rules.get("gates", []):
            if gate["room"] == s.room and gate["item"] == record["item"]:
                return all(f in s.flags[b] for b, f in gate["needs"])
        return True

    def pick(self, s: State, slot: int) -> bool:
        record = s.actions[slot]
        spot = self.where(s, record["zone"], record.get("probe", 0x81))
        if spot is None:
            return False
        s.pos = spot
        self.stand(s, record["zone"])
        self.give(s, record)
        s.fields[PICKED_ITEM] = record["item"]
        s.fields[FWD_ACTION] = slot
        self.settle(s)
        return True

    def at(self, s: State, slot: int, press: bool = True) -> bool:
        """Stands at a room action (walking in, then the action key): its handler runs. False if it can't be reached."""
        a = s.actions[slot]
        spot = self.where(s, a["zone"], a.get("probe", 0x81))
        if spot is None or not self.open_way(s, "spot", {"slot": slot}):
            return False
        s.pos = spot
        self.stand(s, a["zone"])
        s.fields[FWD_ACTION] = s.fields[ENT_ACTION] = slot
        for rule in self.rules.get("spots", []):  # what the exe does at a spot (the lab computer's passwords)
            if rule["room"] == s.room and rule["slot"] == slot:
                apply(s, rule)
        h, words = a.get("handler"), a.get("words", [0, 0, 0])
        if h == 7:
            if words[2]:
                s.flags[words[0]].add(words[1])
            else:
                s.flags[words[0]].discard(words[1])
        elif h == 9:
            self.start(s, s.room, words[0], words[1])
        self.settle(s)
        return True

    def use(self, s: State, slot: int, item: int) -> bool:
        """Uses an item from the inventory while standing at a room action."""
        a = s.actions[slot]
        spot = self.where(s, a["zone"], a.get("probe", 0x81))
        if spot is None:
            return False
        s.pos = spot
        self.stand(s, a["zone"])
        s.fields[FWD_ACTION] = s.fields[ENT_ACTION] = slot
        for other in s.actions.values():  # every item-use zone overlapping this one arms its bit
            if other.get("handler") == 7 and other["words"][0] in (4, 9) and same_zone(other["zone"], a["zone"]):
                s.flags[other["words"][0]].add(other["words"][1])
        s.fields[USED_ITEM] = item
        before = [set(s.flags[b]) for b in (0, 1, 2, 7)]
        for rule in self.rules.get("use", []):
            if rule["item"] == item and rule.get("room", s.room) == s.room:
                apply(s, rule)
        self.settle(s)
        # The item menu takes an item the room reacted to (placed, inserted, poured), unless it is a tool kept.
        if before != [s.flags[b] for b in (0, 1, 2, 7)] and s.inventory.get(item) and item not in self.rules.get("keep", []):
            s.inventory[item] -= 1
            if s.inventory[item] <= 0:
                del s.inventory[item]
        return True

    def asks(self, room: str) -> bool:
        """Whether the room's scripts ask a yes/no question whose answer they test (cached)."""
        cache = self.__dict__.setdefault("_asks", {})
        if room not in self.world:
            return False  # a stub room (110, 20C...): no scripts
        if room not in cache:
            found = False

            def walk(nodes: list[dict]) -> None:
                nonlocal found
                for n in nodes:
                    conds = n["if"] if "if" in n else [n] if n.get("op") in COND_OPS else []
                    if any(c["op"] == 0x06 and c["field"] == MENU_CHOICE and c["value"] in (0, 1) for c in conds):
                        found = True
                    if "if" in n:
                        walk(n["then"])
                        walk(n["else"])

            walk(self.world[room]["init"])
            walk(self.world[room]["main"])
            for script in self.world[room]["events"]:
                for st in script:
                    walk(st.get("block", []) + st.get("wait", []))
            cache[room] = found
        return cache[room]

    def reacts_to(self, room: str) -> set[int]:
        """Items the room's main script or events test with "item used is" (cached)."""
        cache = self.__dict__.setdefault("_reacts", {})
        if room not in cache:
            found: set[int] = set()

            def walk(nodes: list[dict]) -> None:
                for n in nodes:
                    if "if" in n:
                        found.update(c["item"] for c in n["if"] if c["op"] == 0x10)
                        found.update(c["value"] for c in n["if"] if c["op"] == 0x06 and c["field"] == USED_ITEM
                                     and c["cmp"] == 0)
                        walk(n["then"])
                        walk(n["else"])

            walk(self.world[room]["main"])
            for script in self.world[room]["events"]:
                for st in script:
                    walk(st.get("block", []) + st.get("wait", []))
            cache[room] = found
        return cache[room]

    def kill(self, s: State) -> None:
        """Kills what's in the room (the route may cheat). Bosses whose death the enemy code reports (rules "kills":
        Plant 42 sets A[41]) get their flags too."""
        for e in s.enemies:
            if e["killFlag"] != 255:
                s.flags[3].add(e["killFlag"])
        for rule in self.rules.get("kills", []):
            if rule["room"] == s.room:
                apply(s, rule)
        self.settle(s)

    def door(self, s: State, slot: int) -> str | None:
        """Goes through a door if it opens (unlocking it with the key when held); returns the room or None."""
        d = s.actions[slot]
        if d.get("handler", 1) != 1 or not d.get("probe", 1) & 0x01 or not self.open_way(s, "door", d) \
                or self.where(s, d["zone"], d.get("probe", 0x81)) is None:
            return None  # re-armed as something else (a message), switched off, or out of reach
        lock = d["lock"]
        if lock & 0x80 and lock != 0xFF and (lock & 0x3F) not in s.flags[2]:
            key = self.rules.get("keyAlias", {}).get(str(d["key"]), d["key"])
            if key in (0, 255) or (key != 254 and s.inventory.get(key, 0) == 0 and key not in self.held(s)):
                return None
            s.flags[2].add(lock & 0x3F)
        target = target_of(s, d)
        if target not in self.world:
            return None
        s.pos = tuple(d["arrival"]) if d.get("arrival") else None
        self.enter(s, target)
        return target

    def open_way(self, s: State, kind: str, record: dict) -> bool:
        """rules "ways": a door or spot across water, behind a gate... reachable only once the flags say so."""
        for way in self.rules.get("ways", []):
            if way["room"] != s.room:
                continue
            if (kind == "door" and way.get("to") == target_of(s, record)) or                     (kind == "spot" and way.get("slot") == record.get("slot")):
                return all(f in s.flags[b] for b, f in way.get("needs", []))                     and not any(f in s.flags[b] for b, f in way.get("without", []))
        return True

    def examine(self, s: State, item: int) -> None:
        """Examines an item in the inventory (rules "examine": the Doom books open to a medal)."""
        for rule in self.rules.get("examine", []):
            if rule["item"] == item and s.inventory.get(item):
                apply(s, rule)
        self.settle(s)

    def held(self, s: State) -> set[int]:
        """Keys the character holds without an inventory slot (Jill's lockpick)."""
        out = set()
        for h in self.rules.get("heldBy", []):
            if all(f in s.flags[b] for b, f in h["flags"]):
                out.add(h["item"])
        return out


COND_OPS = {0x04, 0x06, 0x07, 0x10, 0x11, 0x1A, 0x1D, 0x22, 0x2C, 0x36, 0x38, 0x3C, 0x3F, 0x50}


def target_of(s: State, door: dict) -> str:
    stage = door["stage"] or int(s.room[0])  # 1-based, 0 = this stage (Re1RoomMaps)
    if 0 in s.flags[0] and stage in (1, 2):
        stage += 5  # the stage-variant bit: the mansion revisited (decomp docs/SCENARIO_FLAGS.md bank 0 bit 0)
    return f"{stage}{door['room']:02X}"


def compare(a: int, cmp: int, b: int) -> bool:
    return {0: a == b, 1: a > b, 2: a >= b, 3: a < b, 4: a <= b, 5: a != b}.get(cmp, False)


def same_zone(a: list[int], b: list[int]) -> bool:
    return a[0] < b[0] + b[2] and b[0] < a[0] + a[2] and a[1] < b[1] + b[3] and b[1] < a[1] + a[3]


def apply(s: State, rule: dict) -> None:
    for b, f in rule.get("set", []):
        s.flags[b].add(f)
    for b, f in rule.get("clear", []):
        s.flags[b].discard(f)
    for item in rule.get("take", []):
        s.inventory.pop(item, None)
    for item, qty in rule.get("give", []):
        s.inventory[item] = s.inventory.get(item, 0) + qty
