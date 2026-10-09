# Read-only node snapshots and native cache-field evidence (notes/ai.md).
# All execution and changed inputs are in Oracle's copied pages, never in the game.
#   python ai_node_state.py --legacy PATH/nodes_t0.pkl --verify --vectors ../Tests/AiNodeRuntimeVectors.json
import argparse
from collections import Counter
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import pickle
import struct

import ai
from ai_friend import HERE, vectors_text
from game import Game
import units


class DataOnly(pickle.Unpickler):
    def find_class(self, module, name):
        raise ValueError("legacy snapshot must contain only lists and bytes")


def snapshot(game):
    trees = ai.read_file()
    live = ai.live_trees(game)
    result = []
    for t, (nodes, (_, pointers)) in enumerate(zip(trees, live)):
        for node, address in zip(nodes, pointers):
            raw = game.read(address, 0x60)
            if struct.unpack_from("<I", raw, 8)[0] != node.cls:
                raise AssertionError((t, node.i, "class changed"))
            result.append({"tree": t, "node": node.i, "class": f"{node.cls:08x}",
                           "address": hex(address), "raw": raw.hex()})
    return result


def differences(rows, legacy):
    with legacy.open("rb") as stream:
        before = DataOnly(stream).load()
    if not isinstance(before, list) or len(before) != 4:
        raise ValueError("expected four tree snapshots")
    result = []
    for row in rows:
        old = before[row["tree"]][row["node"]]
        new = bytes.fromhex(row["raw"])
        if not isinstance(old, bytes) or len(old) != len(new):
            raise ValueError("expected a 0x60-byte node snapshot")
        fields = [{"offset": o, "before": old[o:o + 4].hex(), "after": new[o:o + 4].hex()}
                  for o in range(0, len(old), 4) if old[o:o + 4] != new[o:o + 4]]
        if fields:
            result.append({k: v for k, v in row.items() if k != "raw"} | {"fields": fields})
    return result


def verify(game):
    from oracle import Oracle, SENTINEL
    from unicorn import UC_HOOK_CODE
    from unicorn.x86_const import UC_X86_REG_RIP
    oracle = Oracle(game=game)
    manager = game.q(game.q(game.exe + ai.AI_MANAGER) + 8)
    context = game.q(manager + 0x38)
    board = game.q(context + 0x18)
    live = ai.live_trees(game)
    loop = live[1][1][4143]
    if struct.unpack("<I", game.read(loop + 8, 4))[0] != 0x78F9C619:
        raise AssertionError("loop anchor changed")
    cache = []
    for agent in range(5):
        oracle.call32(0xB26990, board, ai.name_hash("AgentType"), agent, keep=True)
        oracle.call(0x5CA50, loop, context, 0, read_after=[(loop + 0x48, 4)])
        actual = struct.unpack("<i", oracle.after[0])[0]
        if actual != agent:
            raise AssertionError((agent, actual))
        cache.append([agent, actual])
    node, params = oracle.alloc(0x60), oracle.alloc(16)
    loader = []
    for values in [[0, 1, 0x11223344, -1], [2, 4, -2147483648, 2147483647]]:
        oracle.poke(params, struct.pack("<4i", *values))
        oracle.call(0x5CAD0, node, params, 4, read_after=[(node, 0x50)])
        raw = oracle.after[0]
        actual = [struct.unpack_from("<i", raw, o)[0] for o in [0x28, 0x2C, 0x30, 0x40, 0x44]]
        if actual != values + [values[1]]:
            raise AssertionError((values, actual))
        loader.append({"params": values, "raw": raw.hex()})
    live_params = []
    for t, (tree, (_, pointers)) in enumerate(zip(ai.read_file(), live)):
        for n, address in zip(tree, pointers):
            if n.cls != 0x78F9C619:
                continue
            values = [struct.unpack("<i", game.read(address + o, 4))[0] for o in [0x28, 0x2C, 0x30, 0x40]]
            if values != n.params:
                raise AssertionError((t, n.i, n.params, values))
            live_params.append([t, n.i, values])

    # Observe only the prefix ending immediately after the cache writes. The later commands are not evaluated.
    def prefix(start, stop, args, reads):
        reached = []
        def done(uc, address, size, user):
            reached.append(address - game.exe)
            uc.reg_write(UC_X86_REG_RIP, SENTINEL)
            uc.emu_stop()
        hook = oracle.mu.hook_add(UC_HOOK_CODE, done, None, game.exe + stop, game.exe + stop)
        try:
            oracle.call(start, *args, read_after=reads)
            if reached != [stop]:
                raise AssertionError((hex(start), "prefix not reached"))
            return [b.hex() for b in oracle.after]
        finally:
            oracle.mu.hook_del(hook)

    oracle.call32(0xB26990, board, ai.name_hash("AgentType"), 4, keep=True)
    force = next(p for i in range(1, 52)
                 if (p := game.q(game.base - 0x290 + 0xDF30 + 8 * i))
                 and oracle.call32(0x32F500, p) == 1)
    main = oracle.call(0x22E7E0, force)
    doctrine = prefix(0x52350, 0x52392, [node, context, force], [(node + 0x40, 16)])
    if struct.unpack("<QQ", bytes.fromhex(doctrine[0])) != (force, main):
        raise AssertionError("doctrine cached force/main-corps differ")

    # The first pointer getter accepts a copied blackboard object; the prefix stops before list dereference.
    source, lo, hi = 0x61A17F11, 0x61A17F12, 0x61A17F13
    oracle.call32(0xB26BA0, board, source, force, keep=True)
    oracle.call32(0xB26990, board, lo, 5000, keep=True)
    oracle.call32(0xB26990, board, hi, 5000, keep=True)
    oracle.poke(node + 0x38, struct.pack("<4I", source, lo, 0, hi))
    occupy = prefix(0x295B0, 0x29649, [node, context, 0], [(node + 0x54, 8)])
    if struct.unpack("<ii", bytes.fromhex(occupy[0])) != (5000, 5000):
        raise AssertionError("occupy copied thresholds differ")
    return {"loop_agent_type": cache, "loop_loader": loader, "live_loop_params": live_params,
            "doctrine_prefix": {"start": "0x52350", "stop": "0x52392", "raw": doctrine,
                                "force": hex(force), "main_corps": hex(main)},
            "occupy_prefix": {"start": "0x295B0", "stop": "0x29649", "raw": occupy}}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--legacy", type=Path)
    parser.add_argument("--verify", action="store_true")
    parser.add_argument("--vectors", type=Path)
    args = parser.parse_args()
    game = Game()
    rows = snapshot(game)
    result = {"utc": datetime.now(timezone.utc).isoformat(), "pid": game.pid, "clock": units.clock(game),
              "method": "read-only game snapshot; native verification in Unicorn copied pages",
              "oracle_sha256": hashlib.sha256((HERE / "oracle.py").read_bytes()).hexdigest(), "nodes": rows}
    if args.legacy:
        result["legacy_sha256"] = hashlib.sha256(args.legacy.read_bytes()).hexdigest()
        result["differences"] = differences(rows, args.legacy)
        counts = Counter(d["class"] for d in result["differences"])
        print("changed nodes:", dict(counts))
    if args.verify:
        result["verification"] = verify(game)
        print("5 loop agent types, 2 loaders, 2 cache-write prefixes verified")
    folder = HERE / ".cache" / "ai_node_state"
    folder.mkdir(parents=True, exist_ok=True)
    path = folder / (datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + ".json")
    path.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    if args.vectors:
        evidence = {k: v for k, v in result.items() if k not in ["nodes", "differences"]}
        args.vectors.write_text(vectors_text(evidence), encoding="utf-8")
    print(path)


if __name__ == "__main__":
    main()
