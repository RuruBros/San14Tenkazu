# Target the automatic destination comparator with complete native free-officer bodies.
import struct
from context import *
from corps_commands import generate as collect
from free_officer import LOYALTY

COVER = [0x264DD0, 0x254AF0, 0x24D7B0, 0x24A9C0]


def fixtures(ctx, snapshot, source, destination):
    person = next(i for i, b in enumerate(ctx.rec[OFFICERS]) if b[0xE0] == source and b[0xE6] == 4
                  and 1 <= ctx.u16(OFFICERS, i, 0xE2) <= 51 and ctx.u16(OFFICERS, i, 0xE4) <= 61)
    home = ctx.u16(OFFICERS, person, 0xE2)
    county = ctx.u16(TILES, ctx.u16(0x6DA98, home, 0x10), 0x12)
    origin = ctx.rec[0x6CAF0][county][0x35]
    own_force = ctx.officer_force(person)
    other = next(i for i,b in enumerate(ctx.rec[CORPS]) if i and b[0x10] not in [0,own_force,46,47,48,49,50,51]
                 and b[0x11] and ctx.u16(CORPS,i,0x12) and ctx.u16(FORCES,b[0x10],0x10))
    ruler = ctx.u16(FORCES, ctx.rec[CORPS][other][0x10], 0x10)
    def word(table, index, at, value):
        return [hex(table), index, at, struct.pack('<H', value & 0xFFFF).hex()]
    base_pokes = [['worldstate', 0, 0x3A, '00'],
        word(OFFICERS,person,0x15E,ctx.u16(OFFICERS,person,0x15E)&~0x4000),
        [hex(0xDD38),origin,0x58,bytes([44,45,51]+[0]*7).hex()], [hex(0xDD38),44,0x30,bytes([other]).hex()],
        word(OFFICERS,person,0x162,0), [hex(OFFICERS),person,0xD0,'00'*16]]
    # Start county priorities equal, then isolate each native mask. Existing list order/nodes are preserved.
    base_pokes += [[hex(0x6CAF0),i,0x38,bytes([b[0x38]&~5]).hex()]
                  for i,b in enumerate(ctx.rec[0x6CAF0]) if b[0x34] in [44,45,51]]
    county44 = next(i for i,b in enumerate(ctx.rec[0x6CAF0]) if b[0x34] == 44)
    def f(name, extra=()):
        return dict(name=name, source=source, destination=destination, mode=0, person=[hex(OFFICERS),person],
            relocation=None, release_officer=None, pokes=base_pokes+list(extra))
    return [f('normal_force_before_tribe'), f('excluded_ruler', [word(OFFICERS,person,0x162,ruler)]),
        f('hated_ruler', [word(OFFICERS,person,0xD0,ruler)]),
        f('one_invalid_ruler_skips_pair_hate', [[hex(0xDD38),44,0x30,'00'],
            word(OFFICERS,person,0xD0,ctx.u16(FORCES,own_force,0x10))]),
        f('county_mask_one', [[hex(0x6CAF0),county44,0x38,bytes([ctx.rec[0x6CAF0][county44][0x38]|1]).hex()]]),
        f('county_mask_four', [[hex(0x6CAF0),county44,0x38,bytes([ctx.rec[0x6CAF0][county44][0x38]|4]).hex()]]),
        f('invalid_excluded_id', [word(OFFICERS,person,0x162,6001)]),
        f('unowned_original_county_is_eligible', [[hex(0x6CAF0),county44,0x35,'00'],
            [hex(TILES),ctx.u16(0x6CAF0,county44,0x3A),0x14,'00']])]


def generate(ctx):
    return collect(ctx, fixtures, 0x264DD0, 'free_destination', extra_tables=[(LOYALTY,0x38,7)])
