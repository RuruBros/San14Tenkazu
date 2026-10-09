# Numeric calendar/travel helpers and officer/active-command location on one frozen input.
import copy
import gzip
import json
import struct
from context import *
from engine_audit_state import EngineAuditState

COVER = [0x33A5B0, 0x32BA50, 0x333480, 0x231790, 0x22B460, 0x22D580]


def generate(ctx):
    o = ctx.o
    o.insn_limit = 300_000_000
    audit = EngineAuditState(ctx)
    original = audit.read()
    with gzip.open(os.path.join(ctx.output, 'return_command_world.json.gz'), 'rt', encoding='utf-8') as file:
        snapshot = json.load(file)
    if snapshot['tables'] != audit.snapshot['tables'] or snapshot['worldstate'] != audit.snapshot['worldstate']:
        raise RuntimeError('Travel input differs from the verified return construction input')
    scratch = o.alloc(6)
    initial_date = 'abcdef0123a5'
    dates = []
    for days in [-2147483648, -361, -360, -1, 0, 1, 29, 30, 359, 360, 114999, 126720, 2147483647]:
        o.poke(scratch, bytes.fromhex(initial_date))
        o.call(0x33A5B0, scratch, days)
        after = o.read(scratch, 6).hex()
        encoded = o.call32(0x32BA50, scratch)
        valid = bool(o.call(0x333480, scratch) & 0xFF)
        dates.append(dict(days=days, before=initial_date, after=after, encoded=encoded, valid=valid))
    ret = next(c for c in snapshot['commands'] if c['kind'] == 15)
    first = ctx.officer_by_id(ret['officers'][0])
    force = ctx.officer_force(first)
    travel = []
    for steps, owner, mode in [(0, None, None), (1, None, None), (3, None, None), (4, None, None),
                               (195, None, None), (195, 0, None), (195, force, None), (195, force, 2)]:
        pokes = [] if mode is None else [['worldstate', 0, 0x40, struct.pack('<i', mode).hex()]]
        result = ctx.run(pokes, lambda: o.call32(0x231790, steps, 0, 0 if owner is None else ctx.addr(FORCES, owner)))
        if result is None: raise RuntimeError(ctx.failures[-1])
        travel.append(dict(steps=steps, force=owner, pokes=pokes, days=result))
    command_index = next(i for i, c in enumerate(snapshot['commands']) if c['handlers']['0xd8'] == '0x22b460')
    command = snapshot['commands'][command_index]
    pointer = int(command['address'], 16)
    passable = next(i for i in range(30) if struct.unpack_from('<h', ctx.rec[TERRAINS][i], 0x2A)[0])
    area = [[hex(TILES), row*220+col, 0x10, bytes([passable]).hex()] for row in range(107, 116) for col in range(10)]
    def date(day): return struct.pack('<hBBBB', 191, 8, day, 0, 0).hex()
    locations = []
    for name, source, target, start, end, now in [('route_start', 24200, 24644, 10, 20, 10),
        ('route_half', 24200, 24644, 10, 20, 15), ('route_finished', 24200, 24644, 10, 20, 30),
        ('negative_elapsed', 24200, 24644, 20, 30, 10), ('zero_duration', 24200, 24644, 10, 10, 10),
        ('negative_duration', 24200, 24644, 20, 10, 15), ('same_tile', 24200, 24200, 10, 20, 15)]:
        pokes = area + [['worldstate', 0, 0x34, date(now)[:10]]]
        command_pokes = [[0x4C, struct.pack('<H', source).hex()], [0x6C, struct.pack('<H', target).hex()],
                         [0x60, date(start)], [0x66, date(end)]]
        undo = []
        for at, h in command_pokes:
            data = bytes.fromhex(h); undo.append((pointer+at, o.read(pointer+at, len(data)))); o.poke(pointer+at, data)
        try:
            result = ctx.run(pokes, lambda: o.call(0x22B460, pointer))
            if result is None: raise RuntimeError(ctx.failures[-1])
            locations.append(dict(name=name, command=command_index, command_pokes=command_pokes, pokes=pokes,
                                  tile=(result-ctx.addr(TILES, 0))//32))
        finally:
            for address, data in reversed(undo): o.poke(address, data)
    active_ids = {pid for c in snapshot['commands'] for pid in c['officers']}
    person = next(i for i in range(1, len(ctx.rec[OFFICERS])) if ctx.u16(OFFICERS, i, 0x10) not in active_ids
                  and ctx.rec[OFFICERS][i][0xE6] != 0)
    valid_unit = next(i for i in range(1, 501) if ctx.u16(UNITS, i, 0x12) and ctx.rec[UNITS][i][0x10])
    invalid_unit = next(i for i in range(1, 501) if not ctx.u16(UNITS, i, 0x12) or not ctx.rec[UNITS][i][0x10])
    officer_locations = []
    for name, place in [('city', 1), ('gate', 52), ('county', 562), ('unit', 61+valid_unit),
                         ('invalid_unit', 61+invalid_unit), ('invalid_place', 0)]:
        pokes = [[hex(OFFICERS), person, 0xE4, struct.pack('<H', place).hex()]]
        result = ctx.run(pokes, lambda: o.call(0x22D580, ctx.addr(OFFICERS, person)))
        if result is None: raise RuntimeError(ctx.failures[-1])
        officer_locations.append(dict(name=name, person=person, pokes=pokes,
                                      tile=None if result == 0 else (result-ctx.addr(TILES, 0))//32))
    active_person = ctx.officer_by_id(command['officers'][0])
    pokes = [[hex(OFFICERS), active_person, 0xE4, '3202']]
    result = ctx.run(pokes, lambda: o.call(0x22D580, ctx.addr(OFFICERS, active_person)))
    if result is None: raise RuntimeError(ctx.failures[-1])
    officer_locations.append(dict(name='active_command_precedes_place', person=active_person, pokes=pokes,
                                  tile=None if result == 0 else (result-ctx.addr(TILES, 0))//32))
    if audit.read() != original: raise RuntimeError('Travel helper fixtures changed their frozen input')
    ctx.save('command_travel.json.gz', dict(world='return_command_world.json.gz', dates=dates, travel=travel,
        locations=locations, officer_locations=officer_locations, gameplay_stubs=[]))
    return f'command travel: {len(dates)} dates, {len(travel)} durations, {len(locations)} command locations, {len(officer_locations)} officer locations'
