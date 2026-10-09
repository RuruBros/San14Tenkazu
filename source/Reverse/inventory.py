# The rule inventory: every function that reads the rule-constant block (.data 0x1A47C00..0x1A4A800), the constants
# it reads, and whether a C# formula already mirrors it (an "rva 0x..." mention in Core/Formulas). Writes
# inventory.tsv (one row per function, most constants first) and prints the totals. The block is how far the
# extraction has to go: every constant the game's rules use is read by one of these functions.
import os, re, sys
from collections import defaultdict

from image import Image

HERE = os.path.dirname(os.path.abspath(__file__))
FORMULAS = os.path.join(HERE, "..", "Core", "Formulas")
LO, HI = 0x1A47C00, 0x1A4A800

im = Image()
refs, calls = im.xref()
by_func = defaultdict(set)
for t, a, f, txt in refs:
    if LO <= t < HI and "dword ptr" in txt:
        by_func[f].add(t)

done_text = ""
for name in os.listdir(FORMULAS):
    if name.endswith(".cs"):
        done_text += open(os.path.join(FORMULAS, name), encoding="utf-8").read()
mirrored = {int(m, 16) for m in re.findall(r"\b0x([0-9A-Fa-f]{5,7})\b", done_text)}

rows = []
for f, consts in by_func.items():
    b, e = im.func(f)
    rows.append((f, e - b, sorted(consts), f in mirrored))
rows.sort(key=lambda r: -len(r[2]))
with open(os.path.join(HERE, "inventory.tsv"), "w", encoding="utf-8") as out:
    out.write("function\tsize\tconstants\tmirrored\tconstant rvas = values\n")
    for f, size, consts, done in rows:
        out.write(f"{f:#x}\t{size}\t{len(consts)}\t{'yes' if done else ''}\t" +
                  " ".join(f"{c:#x}={im.i32(c)}" for c in consts) + "\n")
all_consts = set().union(*by_func.values())
done_consts = set().union(*(set(c) for f, s, c, d in rows if d)) if any(r[3] for r in rows) else set()
print(f"functions reading the block: {len(rows)}, mirrored {sum(r[3] for r in rows)}")
print(f"constants read: {len(all_consts)}, by mirrored functions {len(done_consts)}")
