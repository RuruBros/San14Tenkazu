# Audit literal/annotation hash candidates without assigning them as original names.
# Hash matches alone can collide: this tool never modifies ai_vars.txt or the catalog.
#   python ai_name_audit.py
from collections import defaultdict
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re

import ai
from image import Image


def hash_bytes(raw):
    result, multiplier = 0, 1
    for byte in raw:
        if byte == 0:
            break
        multiplier = multiplier * 31 & 0xFFFFFFFF
        result = result + (byte - 256 if byte > 127 else byte) * multiplier & 0xFFFFFFFF
    return result


def main():
    image = Image()
    trees = ai.read_file()
    used = {v & 0xFFFFFFFF for tree in trees for node in tree for v in node.params if abs(v) >= 65536}
    blocks = {node.params[0] & 0xFFFFFFFF for tree in trees for node in tree if node.cls == ai.STATIC_LINK}
    known = ai.var_names()
    unknown = used - known.keys() - blocks
    variable_uses = defaultdict(list)
    classes, roles, info, predicates = ai.classes(), ai.leaf_roles(), ai.info_kinds(), ai.pred_names()
    for tree in trees:
        for node in tree:
            if node.cls == ai.CONDITION:
                for kind, _, function, _, args in ai.terms(node.params):
                    if kind != 2:
                        continue
                    signature = predicates.get(function, ("", ""))[1].split()
                    for index, (value, role) in enumerate(zip(args, signature)):
                        if role[:1] in ["v", "o", "l"] and value:
                            variable_uses[value & 0xFFFFFFFF].append([node.tree, node.i, "predicate", function, index])
            elif node.leaf:
                name = classes[node.cls]["name"]
                for index, value in enumerate(node.params):
                    role = roles.get(name, {}).get(index, ("",))[0]
                    variable = index != ai.kind_param(name) if name in info else ai.role_letter(role) == "v"
                    if variable and value:
                        variable_uses[value & 0xFFFFFFFF].append([node.tree, node.i, name, index])
    candidates = defaultdict(set)
    for raw in re.findall(rb"[\x20-\x7E]{3,200}\x00", image.img):
        text = raw[:-1].decode("ascii")
        if re.fullmatch(r"[A-Za-z_][A-Za-z0-9_:\-]{2,100}", text):
            candidates[text].add("exe ASCII literal")
    for tree in trees:
        for node in tree:
            if node.text:
                candidates[node.text].add(f"tree {node.tree} node {node.i} annotation")
                candidates[node.text.strip()].add("stripped annotation candidate")
    for text in known.values():
        candidates[text].add("known hash name")
    originals = list(candidates)
    for text in originals:
        if text.isascii():
            candidates[text.lower()].add("lowercase candidate")
            candidates[text.upper()].add("uppercase candidate")
            candidates[text[:1].lower() + text[1:]].add("lowercase initial candidate")
    matches = []
    checked = 0
    for text, sources in sorted(candidates.items()):
        for encoding in (["ascii"] if text.isascii() else ["cp932", "utf-8", "utf-16le"]):
            try:
                raw = text.encode(encoding)
            except UnicodeEncodeError:
                continue
            checked += 1
            hash_ = hash_bytes(raw)
            if hash_ in blocks or hash_ in unknown:
                matches.append({"candidate": text, "encoding": encoding, "hash": f"{hash_:08x}",
                                "kind": "block" if hash_ in blocks else "unresolved parameter hash",
                                "provenance": sorted(sources)})
    result = {"utc": datetime.now(timezone.utc).isoformat(),
              "image_sha256": hashlib.sha256(image.img).hexdigest(),
              "method": "literal and annotation candidates; signed-byte hash; matches do not establish original names",
              "parameter_hash_filter": "abs(value)>=65536, minus known variables and StaticLink block hashes",
              "known_variables": len(known), "unresolved_parameter_hashes": len(unknown), "block_hashes": len(blocks),
              "typed_unresolved_variables": len(variable_uses.keys() - known.keys()),
              "collision_example": {"Dis": f"{ai.name_hash('Dis'):08x}", "chs": f"{ai.name_hash('chs'):08x}"},
              "candidates": len(candidates), "encoded_candidates": checked, "matches": matches,
              "matched_uses": {m["hash"]: variable_uses[int(m["hash"], 16)] for m in matches},
              "unresolved": sorted(f"{x:08x}" for x in unknown), "blocks": sorted(f"{x:08x}" for x in blocks)}
    folder = Path(ai.CACHE) / "ai_names"
    folder.mkdir(parents=True, exist_ok=True)
    path = folder / (datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ") + ".json")
    path.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"{checked} encoded candidates; {len(blocks)} blocks, {len(unknown)} unresolved parameters; {len(matches)} matches")
    print(json.dumps(matches, ensure_ascii=False))
    print(path)


if __name__ == "__main__":
    main()
