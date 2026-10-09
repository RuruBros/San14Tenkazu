# Reproduce 0x26BD40 cooperation scoring with native code in copied pages; never writes to the game.
#   python ai_cooperation.py --vectors ../Tests/AiCooperationVectors.json
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import random
import struct

import ai
from ai_friend import HERE, vectors_text
from game import Game
from image import Image

WEIGHTS = [[2, 6, 4, 2, 0], [2, 2, 0, 2, 4], [4, 0, 0, 0, 4], [0, 0, 2, 4, 2]]
OFFSETS = [0x16A, 0x16C, 0x16E, 0x178, 0x17C]


def evaluate(a):
    mode, friendship, months, flags_a, flags_b, unnamed_a, unnamed_b, hated, same, *traits = a
    if same or (months & 0x7F) <= 5 and not (flags_a & flags_b & 2) or friendship < 100 or hated:
        return 0
    penalty = -10 if unnamed_a > 19 and unnamed_b > 19 else -60
    return int(sum(w * t for w, t in zip(WEIGHTS[mode], traits)) + penalty > 0)


def verify(game):
    from oracle import Oracle
    oracle = Oracle(game=game)
    forces = [p for i in range(1, 52)
              if (p := game.q(game.base - 0x290 + 0xDF30 + 8 * i)) and oracle.call32(0x32F500, p) == 1]
    force_a, force_b = forces[:2]
    lord_a, lord_b = oracle.call(0x40220, force_a), oracle.call(0x40220, force_b)
    id_b = oracle.call32(game.q(game.q(force_b) + 0x60) - game.exe, force_b)
    lord_b_id = struct.unpack("<H", game.read(lord_b + 0x10, 2))[0]
    cases = []
    # Exact score=0 boundaries, +17A=19/20 boundaries, relation high bit, each precondition and all four modes.
    for mode in range(4):
        for value in [0, 1, 5, 6, 10, 19, 20]:
            for u_a, u_b in [(0, 0), (19, 20), (20, 19), (20, 20)]:
                cases.append([mode, 100, 6, 0, 0, u_a, u_b, 0, 0] + [value] * 5)
        # Place a single trait either side of the score=0 boundary (-60), then the -10 boundary.
        trait, boundary = [(1, 10), (4, 15), (0, 15), (3, 15)][mode]
        for value in [boundary - 1, boundary, boundary + 1]:
            traits = [0] * 5
            traits[trait] = value
            cases.append([mode, 100, 6, 0, 0, 0, 0, 0, 0] + traits)
        trait, boundary = [(0, 5), (0, 5), (0, 2), (2, 5)][mode]
        for value in [boundary - 1, boundary, boundary + 1]:
            traits = [0] * 5
            traits[trait] = value
            cases.append([mode, 100, 6, 0, 0, 20, 20, 0, 0] + traits)
    for friendship in [0, 1, 29, 30, 49, 50, 69, 70, 99, 100, 101, 255]:
        for mode in range(4):
            cases.append([mode, friendship, 6, 0, 0, 20, 20, 0, 0] + [20] * 5)
    for months in [0, 5, 6, 127, 128, 133, 134, 255]:
        for flags_a, flags_b in [(0, 0), (2, 0), (0, 2), (2, 2)]:
            cases.append([3, 100, months, flags_a, flags_b, 20, 20, 0, 0] + [20] * 5)
    for hated, same in [(1, 0), (0, 1)]:
        cases.append([3, 100, 6, 2, 2, 20, 20, hated, same] + [20] * 5)
    rng = random.Random(0x26BD40)
    for _ in range(200):
        cases.append([rng.randrange(4), rng.choice([99, 100, 255]), rng.choice([0, 5, 6, 134]),
                      rng.choice([0, 2]), rng.choice([0, 2]), rng.choice([19, 20]), rng.choice([19, 20]),
                      rng.randrange(2), 0] + [rng.randrange(-20, 21) for _ in range(5)])
    answers = []
    for a in cases:
        mode, friendly, months, flags_a, flags_b, u_a, u_b, hated, same, *traits = a
        oracle.poke(force_a + 0x13 + id_b, bytes([friendly]))
        oracle.poke(force_a + 0xEA + id_b, bytes([months]))
        oracle.poke(force_a + 0x12, bytes([flags_a]))
        oracle.poke(force_b + 0x12, bytes([flags_b]))
        oracle.poke(lord_a + 0x17A, struct.pack("<h", u_a))
        oracle.poke(lord_b + 0x17A, struct.pack("<h", u_b))
        oracle.poke(lord_a + 0xD0, struct.pack("<8H", lord_b_id if hated else 6001, *([6001] * 7)))
        for offset, value in zip(OFFSETS, traits):
            oracle.poke(lord_a + offset, struct.pack("<h", value))
        actual = oracle.call32(0x26BD40, mode, force_a, force_a if same else force_b)
        if actual != evaluate(a):
            raise AssertionError((a, evaluate(a), actual))
        answers.append(a + [actual])
    return {"utc": datetime.now(timezone.utc).isoformat(), "pid": game.pid,
            "method": "native 0x26BD40 in Unicorn copied pages; no game writes",
            "oracle_sha256": hashlib.sha256((HERE / "oracle.py").read_bytes()).hexdigest(),
            "force_a": hex(force_a), "force_b": hex(force_b), "lord_a": hex(lord_a), "lord_b": hex(lord_b),
            "weights": WEIGHTS,
            "friendship_thresholds": [Image().u32(r) for r in [0x1A481F8, 0x1A481D4, 0x1A481DC, 0x1A481E4, 0x1A481F0]],
            "cases": answers}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--vectors", type=Path)
    args = parser.parse_args()
    result = verify(Game())
    text = vectors_text(result)
    folder = HERE / ".cache" / "ai_cooperation"
    folder.mkdir(parents=True, exist_ok=True)
    path = folder / (datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + ".json")
    path.write_text(text, encoding="utf-8")
    if args.vectors:
        args.vectors.write_text(text, encoding="utf-8")
    print(f"{len(result['cases'])} native cooperation answers matched; {path}")


if __name__ == "__main__":
    main()
