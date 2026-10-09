# The native return-start caller may create a return, place an arrived officer or release a lost home.
import struct
from context import *
from corps_commands import generate as collect
from free_officer import LOYALTY

COVER = [0x2EA6D0,0x1F2360,0x1D7DA0,0x1F6720,0x264DD0]


def fixtures(ctx,snapshot,source,destination):
    active = {pid for command in snapshot['commands'] for pid in command['officers']}
    person = next(i for i,b in enumerate(ctx.rec[OFFICERS]) if b[0xE0] == source and b[0xE6] == 4
                  and 1 <= ctx.u16(OFFICERS,i,0xE2) <= 51 and ctx.u16(OFFICERS,i,0x10) not in active)
    home = ctx.u16(OFFICERS,person,0xE2)
    force = ctx.officer_force(person)
    enemy = next(i for i,b in enumerate(ctx.rec[0xDD38]) if i and b[0x69] == 0 and
                 ctx.rec[CORPS][b[0x30]][0x10] not in [0,force])
    unit = next(i for i,b in enumerate(ctx.rec[UNITS]) if i and b[0x10] and ctx.u16(UNITS,i,0x12)
                and ctx.u16(UNITS,i,0x12) != ctx.u16(OFFICERS,person,0x10))
    def word(at,value):
        return [hex(OFFICERS),person,at,struct.pack('<H',value&0xFFFF).hex()]
    prefix = [['worldstate',0,0x3A,'00'],word(0x15E,ctx.u16(OFFICERS,person,0x15E)&~0x4000),word(0xE4,unit+61)]
    def f(name,extra=()):
        return dict(name=name,source=source,destination=destination,mode=0,person=[hex(OFFICERS),person],pokes=prefix+list(extra))
    return [f('create_return_from_unit'),f('arrived_home_place_only',[word(0xE4,home)]),
            f('lost_home_releases_officer',[word(0xE2,enemy)]),f('missing_home_detaches_without_candidate',[word(0xE2,0)])]


def generate(ctx):
    return collect(ctx,fixtures,0x2EA6D0,'return_start',capture_free_lists=True,extra_tables=[(LOYALTY,0x38,7)])
