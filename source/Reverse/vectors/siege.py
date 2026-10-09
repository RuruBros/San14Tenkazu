# Vectors for Attack.VsBase (the game's attack value against bases, rva 0x2B2440): attack.py's cases run on it.
# Run by run.py; writes siege.json.gz.
import attack

FN = 0x2B2440
COVER = [FN, 0x2B2690]


def generate(ctx):
    return attack.generate(ctx, fn=FN, name="siege.json.gz", kind=6, buff=0x4C, seed=56)
