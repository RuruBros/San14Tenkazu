# Real marked-officer removal, including command membership and the deferred free queue.
import struct
from context import *
from corps_commands import generate as collect

COVER = [0x264DD0, 0x32B5A0, 0x235490, 0x228710]


def fixtures(ctx, snapshot, source, destination):
    serving = [i for i, b in enumerate(ctx.rec[OFFICERS]) if b[0xE0] == source and b[0xE6] == 4
               and not 61 < ctx.u16(OFFICERS, i, 0xE4) <= 561]
    active = {identifier for command in snapshot['commands'] for identifier in command['officers']}
    member = next(i for i in serving if ctx.u16(OFFICERS, i, 0x10) in active)
    ordinary = next(i for i in serving if ctx.u16(OFFICERS, i, 0x10) not in active)
    leader = ctx.officer_by_id(ctx.u16(CORPS, source, 0x12))
    def f(name, person, extra=None):
        pokes = [['worldstate', 0, 0x3A, '00'], [hex(OFFICERS), person, 0x15E,
                 struct.pack('<H', ctx.u16(OFFICERS, person, 0x15E)|0x4000).hex()],
                 [hex(OFFICERS), person, 0x1C5, '01']]
        return dict(name=name, source=source, destination=destination, mode=0, person=[hex(OFFICERS), person],
                    pokes=pokes+(extra or []))
    return [f('marked_active_member', member), f('marked_ordinary', ordinary), f('marked_corps_leader', leader),
            f('marked_already_free', ordinary, [[hex(OFFICERS), ordinary, 0xE0, '00'],
                                               [hex(OFFICERS), ordinary, 0xE6, '05']])]


def generate(ctx):
    return collect(ctx, fixtures, 0x264DD0, 'marked_officer')
