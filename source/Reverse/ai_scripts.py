# Remaining AI script formulas, read-only: copied-page execution, never injected into the game.
#   .venv/Scripts/python.exe ai_scripts.py 13
#   .venv/Scripts/python.exe ai_scripts.py 13 --verify --vectors ../Tests/AiScript13Vectors.json
import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import random
import struct

from ai_friend import ARGS, EXECUTE, HERE, divide, i32, snapshot, vectors_text
from game import Game

FORMULAS = {13: "((arg0 * (100 - arg1)) / 100)"}
FORMULAS[10] = "(arg0 + arg1)"
FORMULAS[4] = "((((((arg0 + arg1) + arg2) + arg3) + arg4) + arg5) + arg6)"


def cases(id_):
    rng = random.Random(0x72E99C29 + id_)
    if id_ == 4:
        result = [[0] * 7, [1] * 7, [40, 20, 10, -20, 0, 0, 0],
                  [20, 100, 0, 0, 0, 0, 0], [0, 0, 0, 0, 0, 0, 50],
                  [2147483647, 1, 0, 0, 0, 0, 0], [-2147483648, -1, 0, 0, 0, 0, 0],
                  [2147483647] * 7]
        result += [[rng.randint(-1000, 1000) for _ in range(7)] for _ in range(128)]
        result += [[rng.randint(-0x80000000, 0x7FFFFFFF) for _ in range(7)] for _ in range(64)]
        return result
    if id_ == 10:
        result = [[-20, -20], [0, 0], [20, 20], [10, 10], [20, -1], [20, 0],
                  [2147483647, 1], [-2147483648, -1]]
        result += [[rng.randint(-20, 20), rng.randint(-20, 20)] for _ in range(128)]
        result += [[rng.randint(-0x80000000, 0x7FFFFFFF) for _ in range(2)] for _ in range(64)]
        return result
    if id_ == 13:
        result = [[10000, -20], [10000, 0], [10000, 20], [1, 20], [-1, 20],
                  [0, 20], [2147483647, -20], [-2147483648, 20]]
        result += [[rng.randint(0, 2000000), rng.randint(-20, 20)] for _ in range(128)]
        result += [[rng.randint(-0x80000000, 0x7FFFFFFF) for _ in range(2)] for _ in range(64)]
        return result
    raise ValueError(id_)


def model(id_, args):
    if id_ == 4:
        return i32(sum(args))
    if id_ == 10:
        return i32(args[0] + args[1])
    if id_ == 13:
        return divide(i32(args[0] * i32(100 - args[1])), 100)
    raise ValueError(id_)


def verify(game, evidence, id_):
    from oracle import Oracle
    oracle = Oracle(game=game)
    answers = []
    for args in cases(id_):
        for index, value in enumerate(args):
            oracle.poke(game.exe + ARGS + index * 32, struct.pack("<i", value))
        actual = oracle.call32(EXECUTE, evidence["scripts"][str(id_)]["entry"], 0)
        expected = model(id_, args)
        if actual != expected:
            raise AssertionError((id_, args, actual, expected))
        answers.append(args + [actual])
    result = {"utc": evidence["utc"], "bt_sha256": evidence["bt_sha256"], "script": id_,
            "method": "native 0x93BA0 in Unicorn copied pages; no game writes",
            "expression": FORMULAS[id_], "cases": answers}
    if id_ == 4:
        # Constructor zeroes all seven argument hashes, including the one not loaded from the eight file params.
        node = oracle.alloc(0x60)
        oracle.poke(node, bytes([0xCD]) * 0x60)
        oracle.call(0x5CAF0, node, read_after=[(node + 0x40, 0x1C)])
        fields = struct.unpack("<7I", oracle.after[0])
        if fields != (0,) * 7:
            raise AssertionError(("constructor", fields))
        import ai
        roots = ai.live_trees(game)
        hashes = []
        for tree, nodes in enumerate(ai.read_file()):
            for n in nodes:
                if n.cls == 0x72E99C29 and n.params[0] == 4:
                    value = struct.unpack("<I", game.read(roots[tree][1][n.i] + 0x58, 4))[0]
                    if value != 0:
                        raise AssertionError(("seventh hash", tree, n.i, value))
                    hashes.append([tree, n.i, value])
        result["constructorArgumentHashes"] = list(fields)
        result["liveSeventhArgumentHashes"] = hashes
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("script", type=int, choices=FORMULAS)
    parser.add_argument("--verify", action="store_true")
    parser.add_argument("--vectors", type=Path)
    args = parser.parse_args()
    if args.vectors and not args.verify:
        parser.error("--vectors requires --verify")
    game = Game()
    evidence = snapshot(game, {args.script: FORMULAS[args.script]})
    folder = HERE / ".cache" / "ai_scripts"
    folder.mkdir(parents=True, exist_ok=True)
    stem = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + f"-script{args.script}"
    path = folder / f"{stem}-tokens.json"
    path.write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
    print(f"script {args.script}: {FORMULAS[args.script]}; PID {game.pid}; {path}")
    if args.verify:
        vectors = verify(game, evidence, args.script)
        text = vectors_text(vectors)
        path = folder / f"{stem}-answers.json"
        path.write_text(text, encoding="utf-8")
        if args.vectors:
            args.vectors.parent.mkdir(parents=True, exist_ok=True)
            args.vectors.write_text(text, encoding="utf-8")
        print(f"{len(vectors['cases'])} native answers matched; {path}")


if __name__ == "__main__":
    main()
