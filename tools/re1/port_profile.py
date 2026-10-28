r"""Writes an address profile for another build of RE1 from a verified one, by static cross-mapping (tools/re1/xmap.py).

    python tools/re1/port_profile.py data/games/re1/profiles/re1-gog-usa-hotfix4.json "G:\GOG Games\Resident Evil\ResidentEvil.exe" ^
        <other build's exe> --id re1-jpn-101 --name "Biohazard (Japanese PC 1.01)" --out data/games/re1/profiles/re1-jpn-101.json

A build packed on disk (Steam) is ported from its unpacked image: tools/re1/dump_image.py <pid> image.exe, then
image.exe as the other build's exe with --identity <the shipped exe>.

Rules (CLAUDE.md: never guess offsets):
- A field or named address is "static" only when the code that uses it in the source build is found in the other
  build and those sites agree on the new address (at least 90% of the votes, every disagreement listed).
- Something no code names directly (an array element) carries over only when the nearest mapped neighbours on both
  sides moved by the same amount; its evidence says so.
- Nothing is ever "runtime" here, and write bounds are dropped: the trainer stays off until a live trace confirms the
  fields on that build (tools/Lockpick.Probe trace) and someone raises them to runtime by hand.
- Anchors are the same instructions, found by their surrounding code; a missing anchor fails the port.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
from pathlib import Path

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pefile  # noqa: E402

from xmap import load, map_code, map_data  # noqa: E402

SIZES = {"u8": 1, "i8": 1, "u16": 2, "i16": 2, "u32": 4, "i32": 4}
HEX = re.compile(r"0x[0-9A-Fa-f]{6,8}")
MIN_AGREEMENT = 0.9


def read_profile(path: Path) -> dict:
    text = "\n".join(line for line in path.read_text(encoding="utf-8").splitlines() if not line.lstrip().startswith("//"))
    return json.loads(text)


def accepted(m) -> bool:
    return m.delta is not None and m.agreeing / (m.agreeing + m.disagreeing) >= MIN_AGREEMENT


def site_evidence(m, source_id: str) -> str:
    text = (f"Cross-mapped from {source_id} (0x{m.address:X}) by tools/re1/xmap.py: {m.agreeing} of {m.sites} code sites "
            f"that use it there are found here and use 0x{m.target:X} at the same instruction")
    if m.disagreeing:
        others = ", ".join(f"{d:+#x} x{c}" for d, c in m.votes.most_common()[1:])
        text += f"; {m.disagreeing} site(s) disagree ({others}, neighbouring globals in the same window)"
    return text + "."


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("source_profile", type=Path)
    ap.add_argument("source_exe")
    ap.add_argument("target_exe")
    ap.add_argument("--id", required=True)
    ap.add_argument("--name", required=True)
    ap.add_argument("--out", type=Path, required=True)
    ap.add_argument("--note", action="append", default=[])
    ap.add_argument("--replace-anchor", action="append", default=[], metavar="NAME=0xSOURCE=NEW NAME=WHY",
                    help="use another source instruction for an anchor (e.g. one a patch like Classic REbirth rewrites in memory)")
    ap.add_argument("--live", type=Path, help="live evidence for this build (data/games/re1/profiles/live/<id>.json): "
                    "fields and addresses seen behaving as described in a trace become runtime")
    ap.add_argument("--identity", help="the exe as shipped, when target_exe is its unpacked image (tools/re1/dump_image.py): "
                    "the profile matches this file's name, size and hash, and the image's headers")
    args = ap.parse_args()

    source = read_profile(args.source_profile)
    live = json.loads(args.live.read_text(encoding="utf-8")) if args.live else {}
    live_fields, live_addresses = live.get("fields", {}), live.get("addresses", {})
    a, b = load(args.source_exe), load(args.target_exe)

    items = []  # (kind, entry, address, length)
    for f in source["fields"]:
        items.append(("field", f, int(f["address"], 16), SIZES[f["type"]]))
    for n in source.get("addresses", []):
        items.append(("address", n, int(n["address"], 16), int(n.get("length", "0x1"), 16)))
    for r in source.get("captureRegions", []):
        items.append(("region", r, int(r["address"], 16), int(r["length"], 16)))

    mappings = map_data(a, b, [(f"{kind}:{e['name']}", addr, length) for kind, e, addr, length in items])
    by_key = {m.name: m for m in mappings}

    # Address -> delta of everything mapped from its own code, for carrying over unreferenced neighbours.
    direct = sorted(((addr, by_key[f"{kind}:{e['name']}"]) for kind, e, addr, _ in items
                     if kind != "region" and accepted(by_key[f"{kind}:{e['name']}"])), key=lambda p: p[0])

    def bracketed(address: int):
        before = [m for addr, m in direct if addr <= address]
        after = [m for addr, m in direct if addr >= address]
        if before and after and before[-1].delta == after[0].delta:
            return before[-1], after[0]
        return None

    # Named addresses (whole tables/arrays) that map from references anywhere inside them.
    blocks = [(addr, length, e["name"], by_key[f"address:{e['name']}"]) for kind, e, addr, length in items
              if kind == "address" and accepted(by_key[f"address:{e['name']}"])]

    def inside(address: int, length: int, region: bool):
        for start, size, name, m in blocks:
            # A field inside a mapped table; a capture region around one (regions are drawn around those tables).
            if start <= address < start + size or (region and address <= start < address + length):
                return name, start, size, m
        return None

    translations: dict[int, int] = {}
    resolved: dict[str, tuple[int, str]] = {}
    unresolved = []
    for kind, e, addr, length in items:
        key = f"{kind}:{e['name']}"
        m = by_key[key]
        if accepted(m):
            resolved[key] = (m.target, site_evidence(m, source["id"]))
        elif (block := inside(addr, length, kind == "region")) is not None:
            name, start, size, bm = block
            resolved[key] = ((addr + bm.delta) & 0xFFFFFFFF,
                             f"Not used directly by code in {source['id']} (0x{addr:X}); it is part of '{name}' "
                             f"(0x{start:X}, 0x{size:X} bytes), whose references map by {bm.delta:+#x} at {bm.agreeing} of "
                             f"{bm.sites} code sites, so the layout carries over (tools/re1/xmap.py).")
        elif (pair := bracketed(addr)) is not None:
            low, high = pair
            resolved[key] = ((addr + low.delta) & 0xFFFFFFFF,
                             f"Not used directly by code in {source['id']} (0x{addr:X}); it lies between 0x{low.address:X} and "
                             f"0x{high.address:X}, which both map by {low.delta:+#x} from their own code ({low.agreeing} and "
                             f"{high.agreeing} sites), so the layout carries over (tools/re1/xmap.py).")
        else:
            unresolved.append(f"{kind} {e['name']} (0x{addr:X})")
            continue
        translations[addr] = resolved[key][0]

    # Ranges that moved as a whole (named addresses and capture regions), for addresses quoted in descriptions.
    ranges = [(addr, length, resolved[f"{kind}:{e['name']}"][0] - addr) for kind, e, addr, length in items
              if kind in ("address", "region") and f"{kind}:{e['name']}" in resolved]

    def moved(value: int) -> int | None:
        if value in translations:
            return translations[value]
        return next(((value + delta) & 0xFFFFFFFF for start, length, delta in ranges if start <= value <= start + length), None)

    def translate(text: str | None) -> str | None:
        if text is None:
            return None
        return HEX.sub(lambda h: f"0x{v:X}" if (v := moved(int(h.group(0), 16))) is not None else h.group(0), text)

    fields = []
    for f in source["fields"]:
        key = f"field:{f['name']}"
        if key not in resolved:
            continue
        target, evidence = resolved[key]
        out = {k: v for k, v in f.items() if k not in ("write", "evidence", "verification", "sources")}
        out["address"] = f"0x{target:X}"
        out["description"] = translate(f.get("description"))
        trusted = f.get("verification", "unverified") != "unverified"
        out["verification"] = "static" if trusted else "unverified"
        out["evidence"] = evidence if trusted else f"Lead only (unverified in {source['id']}); {evidence}"
        if trusted and f["name"] in live_fields:
            out["verification"] = "runtime"
            seen = live_fields[f["name"]]
            # An entry from another session than the file's own: {"date", "session", "seen"}.
            when = f"Live {seen['date']} ({seen['session']})" if isinstance(seen, dict) else f"Live {live['date']} ({live['session']})"
            out["evidence"] = f"{when}: {seen['seen'] if isinstance(seen, dict) else seen} {evidence}"
            if "write" in f:
                out["write"] = f["write"]  # the trainer may write it again once it is confirmed in play
        out["sources"] = [f"static cross-map from {source['id']}"] + [f"{source['id']}: {x}" for x in f.get("sources", [])]
        fields.append(out)

    named = []
    for n in source.get("addresses", []):
        key = f"address:{n['name']}"
        if key in resolved:
            target, evidence = resolved[key]
            confirmed = n["name"] in live_addresses
            named.append({"name": n["name"], "address": f"0x{target:X}", "length": n.get("length", "0x1"),
                          "verification": "runtime" if confirmed else "static", "description": translate(n.get("description")),
                          "evidence": (f"Live {live['date']} ({live['session']}): {live_addresses[n['name']]} " if confirmed else "") + evidence})

    regions = []
    for r in source.get("captureRegions", []):
        key = f"region:{r['name']}"
        if key in resolved:
            regions.append({"name": r["name"], "address": f"0x{resolved[key][0]:X}", "length": r["length"],
                            "description": translate(r.get("description")) + f" (Mapped from {source['id']}; code addresses and live observations quoted are that build's.)"})

    anchors = []
    replacements = {r.split("=")[0]: r.split("=", 3)[1:] for r in args.replace_anchor}
    for anchor in source["anchors"]:
        if anchor["name"] in replacements:
            address, name, why = replacements[anchor["name"]]
            anchor = {"name": name, "address": address, "evidence": why}
        found = map_code(a, b, int(anchor["address"], 16))
        if found is None:
            print(f"anchor not found: {anchor['name']}", file=sys.stderr)
            return 1
        at, raw = found
        anchors.append({"name": anchor["name"], "address": f"0x{at:X}", "bytes": raw.hex(" ").upper(),
                        "evidence": f"The same instruction as {source['id']}'s anchor at {anchor['address']}, found by its surrounding code "
                                    f"(tools/re1/xmap.py); " + (translate(anchor.get("evidence")) or "")})

    # Code patches (a trainer feature's changes to the game's code in memory): the instruction each is in, found here
    # by its surrounding code; the original bytes are this build's own; a relative jump keeps its destination (mapped
    # the same way). Static until a live check on this build says otherwise (--live "patches").
    patches = []
    live_patches = live.get("patches", {})
    for p in source.get("patches", []):
        instruction, address = int(p["instruction"], 16), int(p["address"], 16)
        offset = address - instruction
        original = bytes.fromhex(p["original"].replace(" ", ""))
        patched = bytearray.fromhex(p["patched"].replace(" ", ""))
        found = map_code(a, b, instruction)
        if found is None:
            print(f"patch not carried over (instruction not found): {p['name']}", file=sys.stderr)
            continue
        at, raw = found
        here = bytes(raw[offset:offset + len(original)])
        jump = offset == 0 and patched[:1] == b"\xE9"
        if len(here) != len(original) or (not jump and here != original):
            print(f"patch not carried over (different code here): {p['name']}", file=sys.stderr)
            continue
        if jump:
            destination = address + 5 + int.from_bytes(patched[1:5], "little", signed=True)
            mapped = map_code(a, b, destination)
            if mapped is None:
                print(f"patch not carried over (jump destination not found): {p['name']}", file=sys.stderr)
                continue
            patched[1:5] = (mapped[0] - (at + 5)).to_bytes(4, "little", signed=True)
        seen = live_patches.get(p["name"])
        entry = {"name": p["name"], "address": f"0x{at + offset:X}", "instruction": f"0x{at:X}",
                 "original": here.hex(" ").upper(), "patched": bytes(patched).hex(" ").upper(),
                 "verification": "runtime" if seen else "static", "description": translate(p.get("description")),
                 "evidence": (f"Live {seen['date']} ({seen['session']}): {seen['seen']} " if seen else "")
                 + f"The same instruction as {source['id']}'s at {p['instruction']}, found by its surrounding code "
                   f"(tools/re1/xmap.py); " + (translate(p.get("evidence")) or "")}
        patches.append(entry)

    pe = pefile.PE(args.target_exe, fast_load=True)
    shipped = Path(args.identity or args.target_exe)
    data = shipped.read_bytes()
    profile = {
        "schemaVersion": 1,
        "id": args.id,
        "gameId": source["gameId"],
        "displayName": args.name,
        "executable": {
            "fileName": shipped.name,
            "fileSize": len(data),
            "sha256": hashlib.sha256(data).hexdigest().upper(),
            "machine": "x86",
            "timeDateStamp": f"0x{pe.FILE_HEADER.TimeDateStamp:X}",
            "imageBase": f"0x{pe.OPTIONAL_HEADER.ImageBase:X}",
            "sizeOfImage": f"0x{pe.OPTIONAL_HEADER.SizeOfImage:X}",
        },
        "anchors": anchors,
        "patches": patches,
        "addresses": named,
        "captureRegions": regions,
        "fields": fields,
        "notes": ([f"Ported from {source['id']} by tools/re1/port_profile.py: static evidence only. No field is runtime-verified "
                   "on this build yet, so the trainer's writes stay off until a live trace confirms them."] if not live else
                  [f"Ported from {source['id']} by tools/re1/port_profile.py; fields seen in play on this build ({live['date']}, "
                   f"{args.live.as_posix()}) are runtime, the rest static (the trainer writes only runtime fields)."])
                 + args.note + ([f"Not carried over (no agreeing code): {', '.join(unresolved)}."] if unresolved else []),
    }
    header = (f"// {args.name}. Generated by tools/re1/port_profile.py from {source['id']}; re-run it rather than editing addresses.\n"
              "// Addresses are absolute VAs. \"static\" = the code that uses it was found in this exact executable; \"runtime\" = also seen\n"
              "// behaving as described in this build's live game (--live).\n")
    args.out.write_text(header + json.dumps(profile, indent=2, ensure_ascii=False) + "\n", encoding="utf-8", newline="\n")
    print(f"{len(fields)} fields, {len(named)} addresses, {len(regions)} regions, {len(anchors)} anchors; not carried over: {len(unresolved)}")
    for u in unresolved:
        print("  ", u)
    return 0


if __name__ == "__main__":
    sys.exit(main())
