# P148/P149 select custom or preset storage, then compare. Execute in copied pages only.
#   python ai_options_probe.py --vectors ../Tests/AiOptionVectors.json
import argparse
from datetime import datetime, timezone
import hashlib
from pathlib import Path
import struct

from ai_friend import HERE, vectors_text
from game import Game

FIELDS = {148: (0x404, 0x10C, 0x3E9C60, 0), 149: (0x41C, 0x110, 0x3E9C00, 1)}


def verify(game):
    from oracle import Oracle
    oracle = Oracle(game=game)
    options = oracle.call(0x3E9C90)
    cases = []
    for predicate, (custom, preset, getter, wanted) in FIELDS.items():
        for mode in [0, 1, 2, -1]:
            oracle.poke(options + 0xE58, struct.pack("<i", mode))
            for custom_value in [-1, 0, 1, 2]:
                for preset_value in [-1, 0, 1, 2]:
                    oracle.poke(options + custom, struct.pack("<i", custom_value))
                    oracle.poke(options + preset, struct.pack("<i", preset_value))
                    expected = preset_value if mode != 0 else custom_value
                    selected = oracle.call32(getter, options)
                    actual = oracle.call32(0xC2610, predicate, 0, 0, 0)
                    if selected != expected or actual != int(expected == wanted):
                        raise AssertionError((predicate, mode, custom_value, preset_value, selected, actual))
                    cases.append([predicate, mode, custom_value, preset_value, selected, actual])
    return {"utc": datetime.now(timezone.utc).isoformat(), "pid": game.pid,
            "method": "native options getter and 0xC2610 predicates in Unicorn copied pages; no game writes",
            "oracle_sha256": hashlib.sha256((HERE / "oracle.py").read_bytes()).hexdigest(),
            "options": hex(options), "fields": FIELDS, "cases": cases}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--vectors", type=Path)
    args = parser.parse_args()
    result = verify(Game())
    text = vectors_text(result)
    folder = HERE / ".cache" / "ai_options"
    folder.mkdir(parents=True, exist_ok=True)
    path = folder / (datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + ".json")
    path.write_text(text, encoding="utf-8")
    if args.vectors:
        args.vectors.write_text(text, encoding="utf-8")
    print(f"{len(result['cases'])} native getters and predicates matched; {path}")


if __name__ == "__main__":
    main()
