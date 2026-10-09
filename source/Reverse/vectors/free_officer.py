# Execute ordinary free-officer removal on copied memory, including native destination selection.
import struct
from context import *
from corps_commands import generate as collect

LOYALTY = 0x85DB8
COVER = [0x264DD0, 0x22F2D0, 0x24A9C0, 0x254AF0, 0x323ED0, 0x228710]


def fixtures(ctx, snapshot, source, destination):
    serving = [i for i, b in enumerate(ctx.rec[OFFICERS]) if b[0xE0] == source and b[0xE6] == 4
               and not 61 < ctx.u16(OFFICERS, i, 0xE4) <= 561]
    active = {identifier for command in snapshot['commands'] for identifier in command['officers']}
    member = next(i for i in serving if ctx.u16(OFFICERS, i, 0x10) in active)
    ordinary = next(i for i in serving if ctx.u16(OFFICERS, i, 0x10) not in active)
    leader = ctx.officer_by_id(ctx.u16(CORPS, source, 0x12))
    home = ctx.u16(OFFICERS, ordinary, 0xE2)
    if not 1 <= home <= 51:
        raise RuntimeError('This fixture requires a real city home')
    def f(name, person, relocation, reason=None, extra=()):
        pokes = [['worldstate', 0, 0x3A, '00'], [hex(OFFICERS), person, 0x15E,
                 struct.pack('<H', ctx.u16(OFFICERS, person, 0x15E) & ~0x4000).hex()]]
        return dict(name=name, source=source, destination=destination, mode=0, person=[hex(OFFICERS), person],
            relocation=relocation, release_officer=reason, pokes=pokes+list(extra))
    base = [hex(0xDD38), home]
    force = ctx.officer_force(ordinary)
    tile = ctx.u16(0x6DA98, home, 0x10)
    county = ctx.u16(TILES, tile, 0x12)
    origin = ctx.rec[0x6CAF0][county][0x35]
    item, exclusive = ctx.owned[:2]
    def word(table, index, at, value):
        return [hex(table), index, at, struct.pack('<H', value & 0xFFFF).hex()]
    def rules(initial, lower, upper):
        return [word(LOYALTY, 0, 0x2C, initial), word(LOYALTY, 1, 0x2C, lower), word(LOYALTY, 6, 0x2C, upper),
                ['settings', 0, 0xE58, '00000000'], ['settings', 0, 0x4C4, '00000000']]
    items = [word(ITEMS, item, 0x4A, ctx.u16(OFFICERS, ordinary, 0x10)),
             [hex(ITEMS), item, 0x4F, '08'],
             word(ITEMS, exclusive, 0x4A, ctx.u16(OFFICERS, ordinary, 0x10)),
             [hex(ITEMS), exclusive, 0x4F, '09']]
    return [f('explicit_city_member', member, base), f('explicit_city_ordinary', ordinary, base),
            f('explicit_city_leader', leader, base), f('automatic_ordinary', ordinary, None),
            f('explicit_gate', ordinary, [hex(0xDED8), 1]),
            f('owned_items_and_release_reason', ordinary, base, [hex(OFFICERS), leader], items),
            f('zero_county_item_stays_owned', ordinary, base, extra=items+[word(TILES, tile, 0x12, 0)]),
            f('home_parent_fallback', ordinary, base, extra=[[hex(0x6CAF0), county, 0x35, '00']]),
            f('release_reason_id_clamp', ordinary, base, [hex(OFFICERS), leader],
              [word(OFFICERS, leader, 0x10, 65000)]),
            f('reset_flags_and_counters', ordinary, base, extra=[
                word(OFFICERS, ordinary, 0x15E, (ctx.u16(OFFICERS, ordinary, 0x15E)&~0x4000)|0x2020),
                [hex(OFFICERS), ordinary, 0x152, '11'*12],
                [hex(OFFICERS), ordinary, 0x1BC, 'a5'], [hex(OFFICERS), ordinary, 0x1C0, '11223344'],
                [hex(OFFICERS), ordinary, 0x1C4, 'ff']]),
            f('loyalty_upper_bound', ordinary, base, extra=rules(10, 0, 5)),
            f('loyalty_lower_exceeds_default', ordinary, base, extra=rules(10, 11, 5)),
            f('loyalty_option_second_copy', ordinary, base, extra=rules(0, -10, 100)+[
                word(LOYALTY, 2, 0x2C, 20), ['settings', 0, 0xE58, '01000000'],
                ['settings', 0, 0x128, '01000000']]),
            f('popularity_lower_bound', ordinary, base, extra=[word(FORCES, force, 0x1BE, -1000)]),
            f('no_adjacent_city_returns_after_detach', member, None, extra=[
                [hex(0xDD38), origin, 0x58, '00'*10]]),
            f('two_gate_layers_unique_cities', ordinary, None, extra=[
                [hex(0xDD38), origin, 0x58, bytes([54]+[0]*9).hex()],
                [hex(0xDED8), 3, 0x58, bytes([55]+[0]*9).hex()],
                [hex(0xDED8), 4, 0x58, bytes([54, origin, 45, 51, 45]+[0]*5).hex()]]),
            f('governor_replacement', ordinary, base, extra=[
                [hex(OFFICERS), ordinary, 0xE6, '03'],
                word(0xDD38, home, 0x2E, ctx.u16(OFFICERS, ordinary, 0x10))])]


def generate(ctx):
    return collect(ctx, fixtures, 0x264DD0, 'free_officer', extra_tables=[(LOYALTY, 0x38, 7)])
