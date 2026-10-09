# Vectors for Formulas/Rng.cs: the game's Random(n) (rva 0x3FBB00) from many states and bounds, chained draws too.
# Run by run.py; writes rng.json.gz: [state before, n, result, state after].
import random, struct
from context import *

FN, STATE = 0x3FBB00, 0x1A47B90
COVER = [FN]


def generate(ctx):
    o, g = ctx.o, ctx.g
    at = g.exe + STATE
    saved = o.read(at, 4)
    rng = random.Random(3)
    cases = []
    for k in range(20000):
        if k % 50 == 0 or rng.random() < 0.3:
            s = rng.choice([0, 1, -1, 0x7FFFFFFF, -0x80000000, 0x7B, rng.randrange(-2**31, 2**31)])
            o.poke(at, struct.pack("<i", s))
        before = struct.unpack("<i", o.read(at, 4))[0]
        n = rng.choice([-5, 0, 1, 2, 3, 7, 10, 100, 1000, 10000, 0x7FFFFFFF, rng.randrange(2, 1 << 20)])
        r = o.call32(FN, n, keep=True)
        after = struct.unpack("<i", o.read(at, 4))[0]
        cases.append([before, n, r, after])
    o.poke(at, saved)
    ctx.save("rng.json.gz", {"function": f"{FN:#x}", "cases": cases})
    return f"{len(cases)} cases"
