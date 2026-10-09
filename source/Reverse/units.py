# Units (부대) as the game keeps them, read from the running game (read-only). What the output means is written up in
# notes/units.md.
#   python units.py fields           every unit on the map: kind, commander, 진형, move progress, 능력 변화, 상태 이상
#   python units.py formations       the formation table: kind (1 land, 2 water) and 기동, as 0x2B0210 reads them
#   python units.py officers         officers' 진형 bits (+0x12C) by formation, and whom 사이 goes to
#   python units.py speed [seconds]  each marching unit's move step: what +0x14 gains a tick (0x269150's result)
#   python units.py rate [seconds]   timers the game is counting down now, against the game clock (hours a count)
#   python units.py messages         the game's own words for the 능력 변화 and 상태 이상, run in the oracle
import struct, sys, time

from game import Game

sys.stdout.reconfigure(encoding="utf-8")

UNITS, UNIT_COUNT, UNIT_SIZE = 0x7E838, 501, 0x200       # raw + UNITS + i*8 (CArmyUnitData)
FORMATIONS, FORMATION_COUNT = 0x77428, 21                 # raw + FORMATIONS + i*8 (CFormationData, 0xC8)
OFFICERS, OFFICER_COUNT = 0x73A50, 1851
STATE = 0x871E8                                           # raw + STATE -> CWorldData: +0x34 year, +0x36 month, +0x37 day, +0x38 hour
BOOSTS = ["기동", "공군", "공성", "방어", "파성", "#6"]   # +0x4A.. level (i8), +0x50.. time; names from the game's messages 0x22C..0x230
AILMENTS = ["혼란", "저지", "도발"]                       # +0x23.. time; names from the messages when one ends (0x5F65..0x5F67)


def raw(g):
    return g.base - 0x290


def units(g):
    r = raw(g)
    for slot in range(UNIT_COUNT):
        p = g.q(r + UNITS + slot * 8)
        if not p: continue
        b = g.read(p, UNIT_SIZE)
        if b and b[0x10] and struct.unpack_from("<H", b, 0x12)[0]:
            yield slot, p, b


def formation_names(g):
    r = raw(g)
    out = {}
    for i in range(FORMATION_COUNT):
        b = g.read(g.q(r + FORMATIONS + i * 8), 0xC8)
        out[i] = (b[0x10:0x30].decode("utf-16le", "ignore").split("\0")[0], b[0x7E], b[0x7F])
    return out


def clock(g):
    b = g.read(g.q(raw(g) + STATE), 0x40)
    return struct.unpack_from("<H", b, 0x34)[0], b[0x36], b[0x37], b[0x38]


def fields(g):
    names = formation_names(g)
    for slot, p, b in units(g):
        cmd = struct.unpack_from("<H", b, 0x12)[0]
        levels = struct.unpack_from("6b", b, 0x4A)
        boosts = " ".join(f"{BOOSTS[i]}{levels[i]:+d}/{b[0x50 + i]}" for i in range(6) if levels[i] or b[0x50 + i])
        ailments = " ".join(f"{AILMENTS[i]} {b[0x23 + i]}" for i in range(3) if b[0x23 + i])
        name = g.officer_name(cmd) if cmd < OFFICER_COUNT else f"#{cmd}"
        print(f"{slot:3} kind {b[0x10]} {name:8} 진형 {names[b[0x1B]][0]}({b[0x1B]}) 수상 {b[0x1C]:2} "
              f"진행 {struct.unpack_from('<H', b, 0x14)[0]:4}  {boosts}  {ailments}")


def formations(g):
    for i, (name, kind, mobility) in formation_names(g).items():
        print(f"{i:2} {name:4} kind {kind} 기동 {mobility}")


def officers(g):
    r = raw(g)
    names = formation_names(g)
    counts = [0] * 32
    tribe = set()
    for i in range(OFFICER_COUNT):
        p = g.q(r + OFFICERS + i * 8)
        if not p: continue
        b = g.read(p, 0x1F0)
        mask = struct.unpack_from("<I", b, 0x12C)[0]
        for k in range(32):
            if mask >> k & 1: counts[k] += 1
        if mask >> 14 & 1: tribe.add(struct.unpack_from("<H", b, 0x10)[0])
    for k, n in enumerate(counts):
        if n: print(f"{k:2} {names.get(k, ('?',))[0]:4} {n} officers")
    print(f"사이 holders' IDs: {min(tribe)}..{max(tribe)}" if tribe else "no 사이 holders")


def speed(g, seconds):
    last, steps = {}, {}
    end = time.time() + seconds
    while time.time() < end:
        for slot, p, b in units(g):
            v = struct.unpack_from("<H", b, 0x14)[0]
            if slot in last and v > last[slot]: steps.setdefault(slot, []).append(v - last[slot])
            last[slot] = v
        time.sleep(0.02)
    names = formation_names(g)
    for slot, p, b in units(g):
        if slot in steps:
            print(f"{slot:3} 진형 {names[b[0x1B]][0]}(기동 {names[b[0x1B]][2]}) 기동 {struct.unpack_from('b', b, 0x4A)[0]:+d}: steps {sorted(set(steps[slot]))}")


def rate(g, seconds):
    def timers():
        return {(slot, off): b[off] for slot, p, b in units(g) for off in [*range(0x50, 0x56), *range(0x23, 0x26)] if b[off]}
    # A timer a facility or tactic sets again goes up as well: those are told apart, as only a timer left alone shows
    # the game's pace.
    start, before = clock(g), timers()
    seen = dict(before)
    down, up = {}, {}
    end = time.time() + seconds
    while time.time() < end:
        for key, v in timers().items():
            if key in seen and v < seen[key]: down[key] = down.get(key, 0) + seen[key] - v
            if key in seen and v > seen[key]: up[key] = up.get(key, 0) + v - seen[key]
            seen[key] = v
        time.sleep(0.02)
    stop = clock(g)
    print(f"clock {start} -> {stop}")
    for key in sorted(down):
        slot, off = key
        print(f"unit {slot} +0x{off:X}: down {down[key]}, set again +{up.get(key, 0)} ({before.get(key, 0)} -> {seen[key]})")
    if not down: print("no timer went down: is the game paused, or does no unit carry one?")


# The words the game shows: Unit_SetBoost (0x2EC3D0) names boost k by the short text 0x22F, 0x22C, 0x22D, 0x22E,
# 0x230 (Text_Message 0x30CF80), with 0x24C~0x24F for 증가·감소·상승·저하; Unit_TimerTick (0x302200) tells an
# ailment's end by the long messages 0x5F65, 0x5F67, 0x5F66 of the object at exe+0x1A23440 (its vtable's +8).
BOOST_TEXTS = [0x22F, 0x22C, 0x22D, 0x22E, 0x230]
CHANGE_TEXTS = [0x24C, 0x24D, 0x24E, 0x24F]
AILMENT_TEXTS = [0x5F65, 0x5F67, 0x5F66]
MESSAGES_RVA = 0x1A23440


def messages(g):
    from oracle import Oracle
    o = Oracle(g)

    def text(address):
        return o.read(address, 200).decode("utf-16le", "ignore").split("\0")[0] if address else None

    for k, mid in enumerate(BOOST_TEXTS):
        print(f"boost {k} (+0x{0x4A + k:X}): {mid:#x} {text(o.call(0x30CF80, mid))}")
    print("changes:", ", ".join(f"{mid:#x} {text(o.call(0x30CF80, mid))}" for mid in CHANGE_TEXTS))
    obj = o.exe + MESSAGES_RVA
    long_text = o.q(o.q(obj) + 8) - o.exe
    for k, mid in enumerate(AILMENT_TEXTS):
        print(f"ailment {k} (+0x{0x23 + k:X}): {mid:#x} {text(o.call(long_text, obj, mid))}")


if __name__ == "__main__":
    g = Game()
    cmd = sys.argv[1] if len(sys.argv) > 1 else "fields"
    seconds = float(sys.argv[2]) if len(sys.argv) > 2 else 10
    {"fields": lambda: fields(g), "formations": lambda: formations(g), "officers": lambda: officers(g),
     "speed": lambda: speed(g, seconds), "rate": lambda: rate(g, seconds), "messages": lambda: messages(g)}[cmd]()
