# The real capture caller treats activated status5 marked slots as prisoners, rather than queueing them.
import struct
from context import *
from corps_commands import generate as collect

COVER = [0x2E0CF0, 0x29BFF0, 0x2E8E70, 0x241E20]


def fixtures(ctx, snapshot, source, destination):
    person = next(i for i,b in enumerate(ctx.rec[OFFICERS]) if b[0xE0] == source and b[0xE6] == 4
                  and 1 <= ctx.u16(OFFICERS,i,0xE2) <= 51 and ctx.u16(OFFICERS,i,0xE4) <= 61)
    unit = next(i for i,b in enumerate(ctx.rec[UNITS]) if i and b[0x10] and ctx.u16(UNITS,i,0x12))
    force = ctx.officer_force(ctx.officer_by_id(ctx.u16(UNITS,unit,0x12)))
    officer = ctx.officer_by_id(ctx.u16(CORPS,source,0x12))
    prefix = [['worldstate',0,0x3A,'00'], [hex(OFFICERS),person,0xE0,'00'],
              [hex(OFFICERS),person,0xE6,'05'], [hex(OFFICERS),person,0x1C5,'01'],
              [hex(OFFICERS),person,0x15E,struct.pack('<H',ctx.u16(OFFICERS,person,0x15E)|0x4000).hex()]]
    def f(name, unit=None, force=None, officer=None, extra=()):
        return dict(name=name, source=source,destination=destination,mode=0,person=[hex(OFFICERS),person],
            unit=unit,force=force,officer=officer,pokes=prefix+list(extra))
    return [f('no_captor_preserves_home'), f('attacker_unit',unit=[hex(UNITS),unit]),
            f('captor_force',force=[hex(FORCES),force]), f('captor_officer',officer=[hex(OFFICERS),officer]),
            f('inactive_marker_returns_zero',extra=[[hex(OFFICERS),person,0x1C5,'00']])]


def generate(ctx):
    return collect(ctx,fixtures,0x2E0CF0,'marked_free_capture',capture_free_lists=True)
