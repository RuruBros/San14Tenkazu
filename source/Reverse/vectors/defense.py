# Vectors for Attack.Defense (the game's defense value, rva 0x2A9E50): attack.py's cases run on it.
# Run by run.py; writes defense.json.gz.
import attack

FN = 0x2A9E50
COVER = [FN, 0x2A9DC0, 0x22C6D0]


def generate(ctx):
    return attack.generate(ctx, fn=FN, name="defense.json.gz", kind=7, buff=0x4D, seed=57)
