# Native P120/P121/P146/P159 fields, missing variables and invalid objects in copied pages only.
#   python ai_object_predicates.py --vectors ../Tests/AiObjectPredicateVectors.json
import argparse
from datetime import datetime, timezone
import hashlib
from pathlib import Path
import struct

import ai
from ai_friend import HERE, vectors_text
from game import Game
import units


def verify(game):
    from oracle import Oracle
    oracle = Oracle(game=game)
    raw = game.base - 0x290
    manager = game.q(game.q(game.exe + ai.AI_MANAGER) + 8)
    context = game.q(manager + 0x38)
    board = game.q(context + 0x18)
    force = next(p for i in range(1, 52) if (p := game.q(raw + 0xDF30 + 8 * i))
                 and oracle.call32(0x32F500, p) == 1)
    invalid_force = game.q(raw + 0xDF30)
    person, invalid_person = game.officer(736), game.officer(6000)
    army_records = [game.q(raw + units.UNITS + 8 * i) for i in range(units.UNIT_COUNT)]
    army = next(p for p in army_records if p and oracle.call32(0x32F500, p) == 1)
    invalid_army = next(p for p in army_records if p and oracle.call32(0x32F500, p) == 0)
    objects = [(force, invalid_force), (person, invalid_person), (army, invalid_army)]
    for good, bad in objects:
        if oracle.call32(0x32F500, good) != 1 or oracle.call32(0x32F500, bad) != 0:
            raise AssertionError("object validity does not match fixture setup")
    key, missing = 0x61A17001, 0x61A17002
    args = oracle.alloc(8)
    answers = []

    def run(predicate, obj, values, expected, **fields):
        if obj is not None:
            if oracle.call32(0xB26BA0, board, key, obj, keep=True) != 1:
                raise AssertionError("copied blackboard write failed")
        values = [key if obj is not None else missing] + values
        oracle.poke(args, struct.pack("<" + "I" * len(values), *(v & 0xFFFFFFFF for v in values)))
        actual = oracle.call32(0xC2610, predicate, 0, context, 0,
                              stack_args=(args, len(values))) & 0xFF
        row = {"predicate": predicate, "lookup": obj is not None,
               "valid": bool(obj) and oracle.call32(0x32F500, obj) == 1 if obj is not None else False,
               **fields, "native": actual}
        if actual != int(expected):
            raise AssertionError((row, expected))
        answers.append(row)

    for obj in [force, invalid_force]:
        for flags in range(256):
            oracle.poke(obj + 0x12, bytes([flags]))
            run(121, obj, [], bool(flags & 2), flags=flags)
    run(121, None, [], False, flags=2)

    # Each copied rank entry has an independent pointer; rank 0 is the fallback for bytes above 12.
    ranks = []
    for i in range(13):
        p = oracle.alloc(0xC0)
        oracle.poke(p, game.read(game.q(raw + 0x78490 + 8 * i), 0xC0))
        oracle.poke(raw + 0x78490 + 8 * i, struct.pack("<Q", p))
        ranks.append(p)
    for obj in [force, invalid_force]:
        for rank in [*range(13), 13, 128, 255]:
            oracle.poke(obj + 0x47, bytes([rank]))
            for fallback, selected in [(0, 255), (255, 0), (1, 2), (2, 4), (4, 8), (8, 1), (16, 0)]:
                for i, p in enumerate(ranks):
                    oracle.poke(p + 0xB2, bytes([fallback if i == 0 else selected]))
                for selector in [-1, 0, 1, 2, 3, 4, 31, 0x7FFFFFFF]:
                    flags = fallback if rank == 0 or rank > 12 else selected
                    run(120, obj, [selector], 0 <= selector < 4 and bool(flags & (1 << selector)),
                        rank=rank, fallback=fallback, selected=selected, selector=selector)
    run(120, None, [0], False, rank=0, fallback=255, selected=255, selector=0)

    for value in range(256):
        oracle.poke(army + 0x140, bytes([value]))
        run(146, army, [], value == 0, field=value)
    for value in [0, 1, 255]:
        oracle.poke(invalid_army + 0x140, bytes([value]))
        run(146, invalid_army, [], False, field=value)
    for obj in [None, 0]:
        run(146, obj, [], False, field=0)

    flags_values = sorted({0, 0xFFFF, 0x3FFF, 0x4000, 0x8000, 0xC000} |
                          {1 << bit for bit in range(16)} | {0x4000 | (1 << bit) for bit in range(16)})
    for obj in [person, invalid_person]:
        for flags in flags_values:
            oracle.poke(obj + 0x15E, struct.pack("<H", flags))
            valid = oracle.call32(0x32F500, obj) == 1
            run(159, obj, [], valid and bool(flags & 0x4000), field=flags)
    for obj in [None, 0]:
        run(159, obj, [], False, field=0x4000)
    return {"utc": datetime.now(timezone.utc).isoformat(), "pid": game.pid,
            "method": "native 0xC2610 predicates in Unicorn copied pages; no game writes or predicate stubs",
            "oracle_sha256": hashlib.sha256((HERE / "oracle.py").read_bytes()).hexdigest(),
            "force": hex(force), "invalid_force": hex(invalid_force), "person": hex(person),
            "invalid_person": hex(invalid_person), "army": hex(army), "invalid_army": hex(invalid_army),
            "cases": answers}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--vectors", type=Path)
    args = parser.parse_args()
    result = verify(Game())
    text = vectors_text(result)
    folder = HERE / ".cache" / "ai_object_predicates"
    folder.mkdir(parents=True, exist_ok=True)
    path = folder / (datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + ".json")
    path.write_text(text, encoding="utf-8")
    if args.vectors:
        args.vectors.write_text(text, encoding="utf-8")
    print(f"{len(result['cases'])} native predicate answers matched; {path}")


if __name__ == "__main__":
    main()
