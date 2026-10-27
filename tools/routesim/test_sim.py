"""Tests for the route simulator that need no game files: script bytes and small worlds written by hand."""
from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import world  # noqa: E402
from sim import CAME_FROM, Sim, State  # noqa: E402


def bit_op(bank: int, flag: int, mode: int = 0) -> bytes:
    return bytes([0x05, bank, flag, mode])


def bit_test(bank: int, flag: int, is_set: bool = True) -> bytes:
    return bytes([0x04, bank, flag, 0 if is_set else 1])


def if_block(conds: bytes, then: bytes, otherwise: bytes | None = None) -> bytes:
    """if / (else) / end-if as the game lays them out: the if's skip length covers the then-body and the else."""
    if otherwise is None:
        body = conds + then + bytes([0x03, 0x00])
        return bytes([0x01, len(body)]) + body
    tail = bytes([0x03, 0x00])
    else_part = bytes([0x02, 2 + len(otherwise) + len(tail)]) + otherwise
    body = conds + then + else_part
    return bytes([0x01, len(body)]) + body + tail


def door(slot: int, room: int, stage: int = 0, lock: int = 0, key: int = 0, probe: int = 0x81) -> bytes:
    b = bytearray(26)
    b[0], b[1] = 0x0C, slot
    b[2:10] = (1000).to_bytes(2, "little") * 2 + (500).to_bytes(2, "little") * 2
    b[0x0E], b[0x0F], b[0x18], b[0x19] = lock, (stage << 5) | room, key, probe
    return bytes(b)


def parse(data: bytes) -> list[dict]:
    problems: list[str] = []
    nodes = world.block(data, 0, len(data), problems, "test")
    assert not problems, problems
    return nodes


class Blocks(unittest.TestCase):
    def test_if_and_else_become_a_tree(self):
        nodes = parse(if_block(bit_test(1, 5), bit_op(0, 1), bit_op(0, 2)))
        self.assertEqual(len(nodes), 1)
        self.assertEqual(nodes[0]["if"], [{"op": 4, "bank": 1, "flag": 5, "set": True}])
        self.assertEqual(nodes[0]["then"][0]["flag"], 1)
        self.assertEqual(nodes[0]["else"][0]["flag"], 2)

    def test_event_inline_block_skips_its_size_word(self):
        payload = bit_op(0, 7)
        size = (2 + len(payload) + 1).to_bytes(2, "little")
        script = bytes([0x06, 2 + 2 + len(payload) + 1]) + size + payload + b"\x00" + b"\xff"
        steps = world.event(script, 0, len(script), [], "test")
        self.assertEqual(steps[0]["block"][0]["flag"], 7)
        self.assertEqual(steps[-1], {"end": True})


def sim_with(rooms: dict[str, tuple[bytes, bytes]], rules: dict | None = None) -> Sim:
    w = {r: {"init": parse(i), "main": parse(m), "events": []} for r, (i, m) in rooms.items()}
    return Sim(w, rules or {})


class Running(unittest.TestCase):
    def test_init_takes_the_branch_the_flags_say(self):
        sim = sim_with({"101": (if_block(bit_test(1, 5), bit_op(0, 1), bit_op(0, 2)), b"")})
        s = State("jill")
        sim.enter(s, "101")
        self.assertEqual(s.flags[0], {2})
        s.flags[1].add(5)
        sim.enter(s, "101")
        self.assertIn(1, s.flags[0])

    def test_state_byte_3_is_the_room_the_player_came_from(self):
        came_from_2 = bytes([0x06, CAME_FROM, 0, 0x02])
        sim = sim_with({"102": (b"", b""), "101": (if_block(came_from_2, bit_op(0, 9)), b"")})
        s = State("jill")
        sim.enter(s, "102")
        sim.enter(s, "101")
        self.assertIn(9, s.flags[0])

    def test_a_door_opens_with_its_key_and_a_dead_door_doesnt(self):
        sim = sim_with({"101": (door(0, 2, lock=0x80 | 5, key=53) + door(1, 3, probe=0xC0), b""),
                        "102": (b"", b""), "103": (b"", b"")})
        s = State("jill")
        sim.enter(s, "101")
        self.assertIsNone(sim.door(s.clone(), 0))
        self.assertIsNone(sim.door(s.clone(), 1))
        s.inventory[53] = 1
        t = s.clone()
        self.assertEqual(sim.door(t, 0), "102")
        self.assertIn(5, t.flags[2])

    def test_a_door_to_stage_0_stays_on_this_stage(self):
        sim = sim_with({"201": (door(0, 2), b""), "202": (b"", b"")})
        s = State("jill")
        sim.enter(s, "201")
        self.assertEqual(sim.door(s, 0), "202")

    def test_a_script_can_turn_a_door_into_a_message(self):
        rearm = bytes([0x12, 0, 2, 0x81, 0, 0, 0, 0, 0, 0])  # handler 2 (message) on slot 0
        sim = sim_with({"101": (door(0, 2) + rearm, b""), "102": (b"", b"")})
        s = State("jill")
        sim.enter(s, "101")
        self.assertIsNone(sim.door(s, 0))

    def test_a_used_item_the_room_reacts_to_is_used_up(self):
        use_zone = bytes([0x0D, 0]) + (1000).to_bytes(2, "little") * 4 + bytes([7, 1]) + \
            (9).to_bytes(2, "little") + (31 - 0x1B).to_bytes(2, "little") + (1).to_bytes(2, "little")
        react = if_block(bytes([0x10, 31]), bit_op(1, 170))
        sim = sim_with({"105": (use_zone, react)})
        s = State("jill")
        s.inventory[31] = 1
        sim.enter(s, "105")
        sim.use(s, 0, 31)
        self.assertIn(170, s.flags[1])
        self.assertNotIn(31, s.inventory)


if __name__ == "__main__":
    unittest.main()
