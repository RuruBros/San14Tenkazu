# Complete non-notifying prisoner release, including actual return allocation and free-officer fallthrough.
import struct
from context import *
from corps_commands import generate as collect
from free_officer import LOYALTY

COVER = [0x2E75C0,0x2EA6D0,0x1F2360,0x1D7DA0,0x1F6720,0x264DD0]


def fixtures(ctx,snapshot,source,destination):
    active = {pid for command in snapshot['commands'] for pid in command['officers']}
    serving = [i for i,b in enumerate(ctx.rec[OFFICERS]) if b[0xE0] == source and b[0xE6] == 4
               and 1 <= ctx.u16(OFFICERS,i,0xE2) <= 51]
    person = next(i for i in serving if ctx.u16(OFFICERS,i,0x10) not in active)
    force = ctx.officer_force(person)
    ruler = ctx.officer_by_id(ctx.u16(FORCES,force,0x10))
    main = ctx.u16(OFFICERS,ruler,0xE2)
    unit = next(i for i,b in enumerate(ctx.rec[UNITS]) if i and b[0x10] and ctx.u16(UNITS,i,0x12)
                and ctx.u16(UNITS,i,0x12) != ctx.u16(OFFICERS,person,0x10))
    def word(index,at,value):
        return [hex(OFFICERS),index,at,struct.pack('<H',value&0xFFFF).hex()]
    prefix = [['worldstate',0,0x3A,'00'],[hex(OFFICERS),person,0xE6,'06'],word(person,0xE4,unit+61),
              word(person,0x15E,ctx.u16(OFFICERS,person,0x15E)&~0x4000)]
    def f(name,extra=()):
        return dict(name=name,source=source,destination=destination,mode=0,person=[hex(OFFICERS),person],pokes=prefix+list(extra))
    no_force = [[hex(OFFICERS),person,0xE0,'00'],word(person,0x162,ctx.u16(FORCES,force,0x10)),
                [hex(OFFICERS),person,0x164,'0c']]
    return [f('restore_force_main_city_and_return'), f('already_at_main_city',[word(person,0xE4,main)]),
            f('no_force_exclusion_half',no_force+[word(person,0x16C,0)]),
            f('no_force_exclusion_clear',no_force+[word(person,0x16C,-10)]),
            f('no_force_exclusion_unchanged',no_force+[word(person,0x16C,10)]),
            f('no_force_exclusion_minimum',no_force+[[hex(OFFICERS),person,0x164,'00'],word(person,0x16C,0)]),
            f('marked_without_force_keeps_prisoner',[[hex(OFFICERS),person,0xE0,'00'],
                word(person,0x15E,ctx.u16(OFFICERS,person,0x15E)|0x4000),[hex(OFFICERS),person,0x1C5,'01']]),
            f('inactive_marked_returns_zero',[word(person,0x15E,ctx.u16(OFFICERS,person,0x15E)|0x4000),
                [hex(OFFICERS),person,0x1C5,'00']])]


def generate(ctx):
    return collect(ctx,fixtures,0x2E75C0,'prisoner_release',capture_free_lists=True,extra_tables=[(LOYALTY,0x38,7)])
