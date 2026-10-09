# Native AI district comparisons in copied pages, including signed reads and enum fallbacks.
#   python ai_district_predicates.py --vectors ../Tests/AiDistrictPredicateVectors.json
import argparse
from datetime import datetime, timezone
import hashlib
from itertools import product
import json
from pathlib import Path
import random
import struct

import ai
from ai_friend import HERE
from game import Game

FLAGS = [(0x1B, n + 2) for n in range(5)] + [(0x1C, n + 3) for n in range(8)] + [
    (0x1C, 14), (0x1C, 11), (0x1C, 12), (0x1C, 13), (0x1B, 7)]


def verify(game):
    from oracle import Oracle
    oracle = Oracle(game=game)
    manager = game.q(game.q(game.exe + ai.AI_MANAGER) + 8)
    context = game.q(manager + 0x38)
    board = game.q(context + 0x18)
    raw = game.base - 0x290
    force = next(p for i in range(1, 52) if (p := game.q(raw + 0xDF30 + 8 * i))
                 and oracle.call32(0x32F500, p) == 1)
    district = oracle.call(0x22E7E0, force)
    troops = next(p for i in range(600) if (p := game.q(raw + 0x7F8D8 + 8 * i))
                  and oracle.call32(0x32F500, p) == 1)
    if oracle.call32(0x32F500, district) != 1:
        raise AssertionError("no valid main district")
    key, missing = 0x61A17D01, 0x61A17D02
    args = oracle.alloc(12)
    if oracle.call32(0xB26BA0, board, key, district, keep=True) != 1:
        raise AssertionError("copied blackboard write failed")

    def run(predicate, values, agent=0):
        oracle.poke(args, struct.pack("<" + "I" * len(values), *(v & 0xFFFFFFFF for v in values)))
        # Native bool is returned in AL; higher RAX bits are not part of its return value.
        return oracle.call32(0xC2610, predicate, 0, context, agent,
                             stack_args=(args, len(values))) & 0xFF

    answers = {"agent": [], "flags": [], "directive": [], "settings": [], "missing": []}

    def check(section, row, actual, expected):
        if actual != int(expected):
            raise AssertionError((section, row, actual, expected))
        answers[section].append(row + [actual])

    for predicate, obj, agent_type, offset, fmt, values in [
        (25, force, 4, 0x4A, "b", [-128, -1, 0, 1, 2, 3, 127]),
        (26, district, 3, 0x15, "b", [-128, -1, 0, 1, 2, 3, 127]),
        (27, troops, 2, 0x14, "H", [0, 1, 12, 23, 32767, 32768, 65535]),
    ]:
        if oracle.call32(0xB26990, board, ai.name_hash("AgentType"), agent_type, keep=True) != 1:
            raise AssertionError("copied AgentType write failed")
        for stored, wanted in product(values, sorted(set(values + [-1, 128, 65536]))):
            oracle.poke(obj + offset, struct.pack("<" + fmt, stored))
            check("agent", [predicate, agent_type, stored, wanted], run(predicate, [wanted], obj), stored == wanted)
        check("missing", [predicate, "null-agent"], run(predicate, [0]), False)
    # District setting +0x15 and flags may affect validity in future versions; restore its snapshot first.
    oracle.poke(district, game.read(district, 0x40))
    flag_states = [(0, 0), (255, 0xFFFFFFFF)] + [(1 << bit, 0) for bit in range(8)] + [
        (0, 1 << bit) for bit in range(32)]
    rng = random.Random(0xAA0D0)
    flag_states += [(rng.randrange(256), rng.randrange(1 << 32)) for _ in range(16)]
    selectors = [-1, *range(18), 18, 0x7FFFFFFF, -0x80000000]
    for byte_flags, word_flags in flag_states:
        oracle.poke(district + 0x1B, bytes([byte_flags]))
        oracle.poke(district + 0x1C, struct.pack("<I", word_flags))
        for selector in selectors:
            expected = False
            if 0 <= selector < len(FLAGS):
                offset, bit = FLAGS[selector]
                expected = bool((byte_flags if offset == 0x1B else word_flags) & (1 << bit))
            check("flags", [byte_flags, word_flags, selector], run(108, [key, selector]), expected)
    for stored, selector in product([0, 1, 2, 3, 4, 5, 6, 255], [-1, 0, 1, 2, 3, 4, 5, 0x7FFFFFFF]):
        oracle.poke(district + 0x17, bytes([stored]))
        check("directive", [stored, selector], run(110, [key, selector]), 0 <= selector <= 4 and stored == selector + 1)
    for fields in [(0, 1, 2), (2, 0, 1), (1, 2, 0), (255, 255, 255)]:
        oracle.poke(district + 0x20, bytes(fields))
        for selector, wanted in product([-1, 0, 1, 2, 3, 0x7FFFFFFF], [-1, 0, 1, 2, 3, 255]):
            selected = fields[{0: 0, 1: 2, 2: 1}.get(selector, 0)] if selector in (0, 1, 2) else None
            normalized = wanted if wanted in (1, 2) else 0
            check("settings", [*fields, selector, wanted], run(111, [key, selector, wanted]), selected == normalized)
    # A hash absent from the copied blackboard fails all three object-based comparisons.
    for predicate, values in [(108, [missing, 0]), (110, [missing, 0]), (111, [missing, 0, 0])]:
        check("missing", [predicate, "missing-hash"], run(predicate, values), False)
    # A present null object follows Obj_IsValid rather than the missing-hash path.
    if oracle.call32(0xB26BA0, board, key, 0, keep=True) != 1:
        raise AssertionError("copied null object write failed")
    for predicate, values in [(108, [key, 0]), (110, [key, 0]), (111, [key, 0, 0])]:
        check("missing", [predicate, "null-object"], run(predicate, values), False)
    return {"utc": datetime.now(timezone.utc).isoformat(), "pid": game.pid,
            "method": "native 0xC2610 predicates in Unicorn copied pages; no game writes or predicate stubs",
            "oracle_sha256": hashlib.sha256((HERE / "oracle.py").read_bytes()).hexdigest(),
            "force": hex(force), "district": hex(district), "troops": hex(troops),
            "flag_bits": FLAGS, "cases": answers}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--vectors", type=Path)
    args = parser.parse_args()
    result = verify(Game())
    text = json.dumps({k: v for k, v in result.items() if k != "cases"}, ensure_ascii=False, indent=2)[:-2]
    groups = []
    for key, rows in result["cases"].items():
        groups.append("    " + json.dumps(key) + ": [\n" +
                      ",\n".join("      " + json.dumps(row) for row in rows) + "\n    ]")
    text += ",\n  \"cases\": {\n" + ",\n".join(groups) + "\n  }\n}\n"
    folder = HERE / ".cache" / "ai_district_predicates"
    folder.mkdir(parents=True, exist_ok=True)
    path = folder / (datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + ".json")
    path.write_text(text, encoding="utf-8")
    if args.vectors:
        args.vectors.write_text(text, encoding="utf-8")
    print(f"{sum(map(len, result['cases'].values()))} native predicate answers matched; {path}")


if __name__ == "__main__":
    main()
