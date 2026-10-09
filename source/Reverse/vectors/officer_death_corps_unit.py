# Compose the verified corps-leader death with its actual commanded unit removal.
import struct
from context import *
from corps_commands import generate as collect
from free_officer import LOYALTY

COVER = [0x295AA0, 0x258EE0, 0x2301F0, 0x293050, 0x2E8E70, 0x2BDD50, 0x226300]


def fixtures(ctx, snapshot, source, destination):
    person, unit, corps = 1403, 18, 2
    pid = ctx.u16(OFFICERS, person, 0x10)
    registered = [n[0][1] for n in snapshot['lists']['0x100']['items']]
    active = {p for command in snapshot['commands'] for p in command['officers']}
    pending = {n[0][1] for n in snapshot['lists']['0x70']['items']}
    p, c = ctx.rec[OFFICERS][person], ctx.rec[CORPS][corps]
    if (p[0xE6] != 2 or p[0xE0] != corps or c[0x10] != 45 or ctx.u16(CORPS, corps, 0x12) != pid
        or ctx.u16(UNITS, unit, 0x12) != pid or ctx.u16(OFFICERS, person, 0xE4) != unit + 0x3D
        or pid in active or unit in pending or person not in registered
        or struct.unpack_from('<Q', p, 0x1B0)[0] > 7 or ctx.u16(OFFICERS, person, 0x15E) & 0x4000
        or ctx.rec[OFFICES][p[0xE7]][0x37] < 2):
        raise RuntimeError('The frozen fixture must retain the actual unmarked NPC corps leader in unit18')
    main = 1
    if ctx.rec[CORPS][main][0x10] != c[0x10] or not ctx.rec[CORPS][main][0x11]:
        raise RuntimeError('The original force requires its distinct surviving main corps')
    prefix = [['worldstate', 0, 0x3A, '00'], ['exe', 'challenge_active', 0, '00000000']]
    def word(table, index, at, value): return [hex(table), index, at, struct.pack('<H', value & 0xFFFF).hex()]
    item_pokes = [entry for i in ctx.owned[:2] for entry in
        [word(ITEMS, i, 0x4A, pid), [hex(ITEMS), i, 0x4F, '09']]]
    marked = [word(OFFICERS, person, 0x15E, ctx.u16(OFFICERS, person, 0x15E) | 0x4000),
        [hex(OFFICERS), person, 0x1C5, '01']]
    def case(name, destroy=1, pokes=(), pending_units=(), merges=0, checks=1):
        return dict(name=name, source=corps, destination=main, mode=0, person=[hex(OFFICERS), person],
            destroy_own_unit=destroy, inherits=True, attacker=None, pending_units=list(pending_units),
            expected_merges=merges, expected_checks=checks, pokes=prefix + list(pokes))
    return [case('actual_corps_leader_own_unit'), case('own_unit_disabled', destroy=0),
        case('own_unit_with_items', pokes=item_pokes),
        case('own_unit_marked_leader_defers', pokes=marked, checks=0),
        case('own_unit_already_pending', pending_units=[unit])]


def generate(ctx):
    return collect(ctx, fixtures, 0x295AA0, 'officer_death_corps_unit',
        capture_free_lists=True, extra_tables=[(LOYALTY,0x38,7)])
