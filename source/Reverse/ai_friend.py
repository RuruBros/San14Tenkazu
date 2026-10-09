# Read-only reproduction of the friend-force score in force tree #77.
# snapshot: read the live CSymboScript table and prefix tokens, save evidence.
# verify: run scripts 8 -> 11 -> 12 in oracle.py's copied pages (never in the game),
#         compare the expression, and save native answers for the Core tests.
# Run in source/Reverse: .venv/Scripts/python.exe ai_friend.py snapshot|verify
import argparse
import hashlib
import json
from pathlib import Path
import random
import struct
from datetime import datetime, timezone

import ai
from game import Game

TABLE = 0x1B99990
ARGS = 0x1B99AB8
EXECUTE = 0x93BA0
OPERATORS = {0x40000001: ("+", 0x92D20), 0x40000002: ("-", 0x92D90),
             0x40000003: ("*", 0x92E00), 0x40000004: ("/", 0x92E70)}
ARGUMENT = 0x20000000
EXPECTED = {8: "(arg0 * arg1)", 11: "(arg0 / arg1)",
            12: "((((((100 * arg0) / arg1) - arg2) - arg3) + arg4) + arg5)"}
HERE = Path(__file__).resolve().parent


def read(game, address, size):
    data = game.read(address, size)
    if data is None or len(data) != size:
        raise RuntimeError(f"unreadable {address:#x}, {size} bytes")
    return data


def expression(tokens):
    def parse(index, depth):
        token = tokens[index]
        if token["depth"] != depth:
            raise ValueError("unexpected token depth")
        op = token["opcode"]
        index += 1
        if op in OPERATORS:
            left, index = parse(index, depth + 1)
            right, index = parse(index, depth + 1)
            return f"({left} {OPERATORS[op][0]} {right})", index
        if op == ARGUMENT:
            arg, index = parse(index, depth + 1)
            if not arg.isdecimal() or int(arg) >= 8:
                raise ValueError("unexpected argument index")
            return f"arg{arg}", index
        if op & 0x60000000 and op < 0x80000000:
            raise ValueError(f"unknown opcode {op:#x}")
        return str(i32(op)), index
    result, end = parse(0, 1)
    if end != len(tokens):
        raise ValueError("tokens left over")
    return result


def snapshot(game, expected=EXPECTED):
    header = read(game, game.exe + TABLE, 24)
    vtable, count, capacity, buffer = struct.unpack("<QIIQ", header)
    if vtable - game.exe != 0x134A5B8 or not max(expected) < count <= capacity <= 4096:
        raise ValueError("unexpected script table")
    for op, (_, rva) in OPERATORS.items():
        address = game.q(game.exe + 0x1A202D0 + (op & 0x3FFFFFFF) * 8)
        if address != game.exe + rva:
            raise ValueError("operator table changed")
    if game.q(game.exe + 0x1A202C8) != game.exe + 0x93780:
        raise ValueError("argument reader changed")
    trees = ai.read_file()
    anchors = {}
    for tree, nodes in enumerate(trees):
        for n in nodes:
            if n.cls == 0x72E99C29 and n.params[0] in expected:
                anchors[f"{tree}:{n.i}"] = n.params
    if not anchors:
        raise ValueError("no verified script callers in the trees")
    scripts = {}
    for id_ in expected:
        entry = buffer + id_ * 32
        vt, elem_vt, kind, depth, token = struct.unpack("<QQIIQ", read(game, entry, 32))
        if (vt - game.exe, elem_vt - game.exe, kind, depth) != (0x1352CA8, 0x1352C88, 0x40000000, 0):
            raise ValueError("unexpected script entry")
        tokens, seen = [], set()
        while token:
            if token in seen or len(tokens) >= 128:
                raise ValueError("cyclic or oversized expression")
            seen.add(token)
            vt, op, dep, next_ = struct.unpack("<QIIQ", read(game, token, 24))
            if vt - game.exe != 0x1352C88:
                raise ValueError("unexpected token vtable")
            tokens.append({"address": token, "opcode": op, "depth": dep, "next": next_})
            token = next_
        expr = expression(tokens)
        if expr != expected[id_]:
            raise ValueError(f"script {id_} changed: {expr}")
        scripts[str(id_)] = {"entry": entry, "expression": expr, "tokens": tokens}
    return {"utc": datetime.now(timezone.utc).isoformat(), "pid": game.pid, "exe": game.exe,
            "bt_sha256": hashlib.sha256(Path(ai.game_dir(), ai.FILE).read_bytes()).hexdigest(),
            "table_rva": TABLE, "count": count, "capacity": capacity, "buffer": buffer,
            "anchors": anchors, "scripts": scripts}


def i32(value):
    value &= 0xFFFFFFFF
    return value - 0x100000000 if value >= 0x80000000 else value


def divide(a, b):
    # 0x92E70: denominator first; zero returns 1; idiv truncates toward zero.
    if b == 0:
        return 1
    if a == -0x80000000 and b == -1:
        raise OverflowError("x86 signed division overflow")
    return (abs(a) // abs(b)) * (-1 if (a < 0) != (b < 0) else 1)


def score(a):
    own, other, friendship, liked, compatible, hated = a
    return i32(i32(i32(divide(i32(100 * own), other) - friendship) - liked) + compatible + hated)


def verify(game, evidence):
    from oracle import Oracle
    oracle = Oracle(game=game)
    def call(id_, args):
        for index, value in enumerate(args):
            oracle.poke(game.exe + ARGS + index * 32, struct.pack("<i", value))
        return oracle.call32(EXECUTE, evidence["scripts"][str(id_)]["entry"], 0)
    cases = [
        [100, 100, 100, 0, 0, 0], [1, 300, 0, 0, 75, 0],
        [100, 0, 100, 30, 74, 30], [1, -3, -17, 30, -74, 0],
        [21474837, 1, 0, 0, 0, 0], [-2147483648, 1, 0, 0, -2147483648, 0],
        [100, 100, 120, 0, 0, 0], [100, 100, 121, 0, 0, 0],
    ]
    rng = random.Random(0xBE2B2510)
    for _ in range(128):
        cases.append([rng.randint(1, 2000000), rng.randint(1, 2000000), rng.randint(0, 130),
                      rng.choice([0, 30]), rng.randint(0, 75), rng.choice([0, 30])])
    for _ in range(64):
        cases.append([rng.randint(-0x80000000, 0x7FFFFFFF) for _ in range(6)])
    answers = []
    for case in cases:
        own, other, friendship, liked, compatibility, hated = case
        unchanged = call(8, [compatibility, 1])
        compatible = call(11, [unchanged, 3])
        assert unchanged == compatibility and compatible == divide(compatibility, 3)
        args = [own, other, friendship, liked, compatible, hated]
        actual = call(12, args)
        if actual != score(args):
            raise AssertionError((args, actual, score(args)))
        answers.append(case + [actual])
    zero = call(11, [123, 0])
    assert zero == 1
    # The game's actual compatibility getter, on two valid copied officer records.
    first, second = game.officer(736), game.officer(735)
    compatibility_answers = []
    for a in [0, 1, 74, 75, 76, 149]:
        for b in [0, 1, 74, 75, 76, 149]:
            oracle.poke(first + 0xBE, bytes([a]))
            oracle.poke(second + 0xBE, bytes([b]))
            value = oracle.call32(0x2A5E20, first, second)
            assert value == min(abs(a - b), 150 - abs(a - b), 75)
            compatibility_answers.append([a, b, value])
    return {"utc": evidence["utc"], "bt_sha256": evidence["bt_sha256"],
            "method": "Unicorn copied pages; native 0x93BA0 scripts 8 -> 11 -> 12; no game writes",
            "columns": ["ownPower", "otherPower", "weightedFriendship", "likedBias", "compatibility", "hatedBias", "score"],
            "cases": answers, "compatibility": compatibility_answers, "zeroDivisor": zero}


def vectors_text(vectors):
    # One native answer per line keeps the checked-in fixture easy to review.
    keys = [k for k in ("cases", "compatibility") if k in vectors]
    metadata = {k: v for k, v in vectors.items() if k not in keys}
    text = json.dumps(metadata, ensure_ascii=False, indent=2)[:-2]
    for key in keys:
        rows = ",\n".join("    " + json.dumps(row) for row in vectors[key])
        text += f",\n  {json.dumps(key)}: [\n{rows}\n  ]"
    return text + "\n}\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["snapshot", "verify"])
    parser.add_argument("--vectors", type=Path, help="also save native answers to a Core test fixture")
    args = parser.parse_args()
    if args.vectors and args.command != "verify":
        parser.error("--vectors requires verify")
    game = Game()
    evidence = snapshot(game)
    folder = HERE / ".cache" / "ai_friend"
    folder.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    path = folder / f"{stamp}-scripts.json"
    path.write_text(json.dumps(evidence, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    for id_, script in evidence["scripts"].items():
        print(f"script {id_}: {script['expression']}")
    print(f"snapshot: {path}; PID {game.pid}; {evidence['count']} entries; read only")
    if args.command == "verify":
        vectors = verify(game, evidence)
        path = folder / f"{stamp}-answers.json"
        data = vectors_text(vectors)
        path.write_text(data, encoding="utf-8")
        if args.vectors:
            args.vectors.parent.mkdir(parents=True, exist_ok=True)
            args.vectors.write_text(data, encoding="utf-8")
        print(f"{len(vectors['cases']) * 3 + 1} script calls and {len(vectors['compatibility'])} compatibility calls matched; {path}")


if __name__ == "__main__":
    main()
