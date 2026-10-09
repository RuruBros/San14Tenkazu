# Complete ordinary capture with real corps reevaluation, merger and active-command cancellation.
import struct
from context import *
from corps_commands import generate as collect

COVER = [0x2E0CF0, 0x2E8E70, 0x293050, 0x334780, 0x1D7DA0]


def fixtures(ctx, snapshot, source, destination):
    person = ctx.officer_by_id(ctx.u16(CORPS, source, 0x12))
    original = ctx.rec[OFFICERS][person]
    if original[0xE6] != 2 or ctx.u16(OFFICERS, person, 0x15E) & 0x4000:
        raise RuntimeError('This input requires an unmarked, ordinary secondary-corps leader')
    if ctx.u16(OFFICERS, person, 0x10) in {p for c in snapshot['commands'] for p in c['officers']}:
        raise RuntimeError('The captured leader must not have a separate active command in this batch')
    force = ctx.rec[CORPS][source][0x10]
    captor = next(i for i in range(1, 46) if i != force and ctx.u16(FORCES, i, 0x10)
                  and any(b[0x30] and ctx.rec[CORPS][b[0x30]][0x10] == i for b in ctx.rec[0xDD38][1:]))
    common = [['worldstate', 0, 0x3A, '00']]
    def p(t, i, at, value, fmt='B'):
        return [hex(t), i, at, struct.pack('<'+fmt, value).hex()]
    def f(name, pokes):
        return dict(name=name, source=source, destination=destination, mode=0, person=[hex(OFFICERS), person],
                    unit=None, force=[hex(FORCES), captor], officer=None, pokes=common+pokes)
    others = [p(OFFICERS, i, 0xE6, 6) for i, data in enumerate(ctx.rec[OFFICERS])
              if i != person and data[0xE0] == source and data[0xE6] in [1, 2, 3, 4]]
    return [f('retained_leader_capture', []),
            f('merge_before_prisoner_status', [p(0xDD38, i, 0x30, destination)
                for i, city in enumerate(ctx.rec[0xDD38]) if city[0x30] == source]),
            f('merge_after_last_counted_officer_capture', others)]


def generate(ctx):
    return collect(ctx, fixtures, 0x2E0CF0, 'officer_corps')
