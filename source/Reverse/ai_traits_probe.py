# Read-only AI personality evidence. Field edits happen exclusively on oracle.py's copied pages.
#   .venv/Scripts/python.exe ai_traits_probe.py --vectors ../Tests/AiTraitVectors.json
# Reports case-variant hash matches separately from literal names found in the exe.
import argparse
from collections import Counter
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import struct

import ai
from ai_friend import HERE, vectors_text
from game import Game
from image import Image

FIELDS = [(0x16A, 13), (0x16C, 14), (0x16E, 15), (0x170, 16), (0x172, 17), (0x174, 18),
          (0x176, 19), (0x178, 20), (0x17A, 31), (0x17C, 32), (0x17E, 34), (0x180, 35)]


def case_names():
    image = Image()
    words = set(ai.TRAIT_WORDS.split())
    for name in ai.var_names().values():
        words.update(re.findall(r"[A-Z][a-z]*|[a-z]+", name))
    words.update(["Fame", "Humanity", "Friendly", "Honor", "Nobility", "Prestige", "Cooperation", "Cooperative", "Peace", "Harmony"])
    occurrences = Counter(v & 0xFFFFFFFF for tree in ai.read_file() for n in tree for v in n.params if abs(v) >= 65536)
    matches = {}
    for prefix in ["Personality", "Personality_", "PersonalityValue", "Trick", "PersonalityPerson", "PersonalityAI"]:
        for word in sorted(words):
            for suffix in [word, word.lower(), word.upper(), word + "Value"]:
                name = prefix + suffix
                hash_ = ai.name_hash(name)
                if hash_ in occurrences:
                    matches[name] = {"name": name, "hash": f"{hash_:08x}", "uses": occurrences[hash_],
                                     "literal_in_image": name.encode() in image.img}
    return list(matches.values())


def verify(game):
    from oracle import Oracle
    oracle = Oracle(game=game)
    manager = game.q(game.exe + ai.AI_MANAGER)
    tree_manager = game.q(manager + 8)
    context = game.q(tree_manager + 0x38)  # district-tree execution context, CJuicyBehaviorTreeManager +0x38
    blackboard = game.q(context + 0x18)
    person = game.officer(736)
    node, output = oracle.alloc(0x60), oracle.alloc(4)
    source, target = 0x61A17F01, 0x61A17F02
    if oracle.call32(0xB26BA0, blackboard, source, person, keep=True) != 1:
        raise AssertionError("source object write failed in copied blackboard")
    answers = []
    for offset, kind in FIELDS:
        oracle.poke(node + 0x38, struct.pack("<III", source, kind, target))
        for value in [-20, -10, 0, 10, 20, 1234, -1234]:
            oracle.poke(person + offset, struct.pack("<h", value))
            if oracle.call32(0x8E7C0, node, context, 0, keep=True) != 1:
                raise AssertionError(("person info", offset, kind))
            ok = oracle.call32(0xB26300, blackboard, target, output, 0, read_after=[(output, 4)])
            actual = struct.unpack("<i", oracle.after[0])[0]
            if ok != 1 or actual != value:
                raise AssertionError((offset, kind, value, actual))
            answers.append([offset, kind, value, actual])
    return {"utc": datetime.now(timezone.utc).isoformat(), "pid": game.pid,
            "method": "native 0x8E7C0 -> 0xB26300 in Unicorn copied pages; no game writes",
            "oracle_sha256": hashlib.sha256((HERE / "oracle.py").read_bytes()).hexdigest(),
            "case_name_matches": case_names(), "cases": answers}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--vectors", type=Path)
    args = parser.parse_args()
    result = verify(Game())
    folder = HERE / ".cache" / "ai_traits"
    folder.mkdir(parents=True, exist_ok=True)
    path = folder / (datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + ".json")
    text = vectors_text(result)
    path.write_text(text, encoding="utf-8")
    if args.vectors:
        args.vectors.write_text(text, encoding="utf-8")
    print(f"{len(result['cases'])} signed field reads matched; {path}")
    print(json.dumps([m for m in result["case_name_matches"] if m["hash"] == "9300703f"], ensure_ascii=False))


if __name__ == "__main__":
    main()
