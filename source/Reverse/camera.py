# The game's map camera, read from the running game (read-only), and its flight to a hex run in the oracle: what the
# editor's [위치로 가기] calls (GameMemory.ShowTileInGame). Findings in notes/camera.md.
#   python camera.py state            the camera keeper now: look-at, distance, turn, pitch, the camera in charge, held
#   python camera.py fly <hex>        runs exe+0x451360(hex, 0, 500, -1.0, -1.0, 1) in the oracle (the game is not
#                                     touched) and prints the move the camera was given, beside the hex grid's formula
import math, struct, sys

from game import Game

KEEPER = 0x1B62450                # the camera keeper (exe+0x11170 makes it on first use)
MAIN = 0x50                       # CMainCameraCtrl (vtable exe+0x13426F8)
MAIN_VTABLE = 0x13426F8
OTHER = 0x280                     # the camera in charge when not the main one
HELD = MAIN + 0x188               # the main camera takes no moves while set
EVENT = 0x160                     # the main camera's CCameraEvent (+0x110): the glide under way
HEIGHT_FLAG = 0x1B59290           # 0: the flight's target height is 0
FLY = 0x451360
COLUMNS, STEP_X, RADIUS = 220, 702.0, 468.0
ORIGIN_X, ORIGIN_Z = 0x1A20578, 0x2141A80   # floats: x of column 0 (.data), z of row 0 (set when the game starts)


def d(read, a): return struct.unpack("<d", read(a, 8))[0]


def look(read, keeper):
    # the main camera's look-at (doubles at +0x50..), distance +0x90, turn +0xA8, pitch +0xC0
    m = keeper + MAIN
    return {"look-at": tuple(round(d(read, m + 0x50 + 8 * k), 1) for k in range(3)), "distance": d(read, m + 0x90),
            "turn": round(d(read, m + 0xA8), 4), "pitch": round(d(read, m + 0xC0), 4)}


def grid(read, exe, hex_):
    # exe+0x188700: x = column × 702 + x0; z = √3·468 × row (+ √3·468/2 on even hexes) + z0
    x0 = struct.unpack("<f", read(exe + ORIGIN_X, 4))[0]
    z0 = struct.unpack("<f", read(exe + ORIGIN_Z, 4))[0]
    col, row = hex_ % COLUMNS, hex_ // COLUMNS
    h = math.sqrt(3) * RADIUS
    return col * STEP_X + x0, h * row + (h / 2 if hex_ % 2 == 0 else 0) + z0


def state():
    g = Game()
    k = g.exe + KEEPER
    vt = g.q(k + MAIN) - g.exe
    print(f"main camera vtable {vt:#x} ({'CMainCameraCtrl' if vt == MAIN_VTABLE else 'not the main camera'})")
    print(f"camera in charge {g.q(k + OTHER):#x} (0 = the main one), held {struct.unpack('<i', g.read(k + HELD, 4))[0]}, "
          f"height flag {struct.unpack('<i', g.read(g.exe + HEIGHT_FLAG, 4))[0]}")
    for name, value in look(g.read, k).items(): print(f"{name}: {value}")


def fly(hex_):
    from oracle import Oracle
    o = Oracle()
    k = o.exe + KEEPER
    keep = struct.unpack("<Q", struct.pack("<d", -1.0))[0]
    print("before:", look(o.read, k))
    o.call(FLY, hex_, 0, 500, keep, xmm=(0, 0, 0, keep), stack_args=(keep, 1))
    e = k + EVENT + 0x10      # the move copied in: ms, from (x, y, z, w floats), to, then (from, to) doubles
    f = lambda a: struct.unpack("<4f", o.read(a, 16))
    print("move: ms", struct.unpack("<i", o.read(e, 4))[0], "from", f(e + 0x10), "to", f(e + 0x20))
    print("      distance", d(o.read, e + 0x30), d(o.read, e + 0x38), "turn", d(o.read, e + 0x40), d(o.read, e + 0x48),
          "pitch", d(o.read, e + 0x50), d(o.read, e + 0x58), "(-1: kept)")
    print("grid formula x, z:", grid(o.read, o.exe, hex_))


if __name__ == "__main__":
    if sys.argv[1] == "state": state()
    elif sys.argv[1] == "fly": fly(int(sys.argv[2], 0))
