# Native return routing only: null army/force, one goal, options 8, workspace 2.
# Every route and neighbor is executed on the frozen memory copy without gameplay stubs.
import struct
import time
import gzip
import json
from context import *
from engine_audit_state import EngineAuditState

COVER = [0x1843E0, 0x2A7720]


def generate(ctx, names=None):
    o = ctx.o
    o.insn_limit = 300_000_000
    audit = EngineAuditState(ctx)
    original = audit.read()
    if o.read(ctx.g.exe + 0x2141A84, 4) != bytes(4):
        raise RuntimeError('The native graphical routing trace must be disabled in this input')
    output, context, goals = o.alloc(16), o.alloc(0x40), o.alloc(4)
    previous = None
    if names is not None:
        with gzip.open(os.path.join(ctx.output, 'return_path.json.gz'), 'rt', encoding='utf-8') as file:
            previous = json.load(file)
    neighbors = [] if previous is None else previous['neighbors']
    for center in ([] if previous else [0, 1, 218, 219, 220, 221, 24200, 24201, 47960, 47961, 48180, 48181, 48398, 48399]):
        for direction in range(8):
            result = o.call32(0x2A7720, center, direction)
            neighbors.append([center, direction, result])
    fixtures = []
    def add(name, source, target, pokes=()):
        fixtures.append(dict(name=name, source=source, target=target, pokes=list(pokes)))
    # A dedicated unused terrain record makes local admission branches explicit without changing the whole map.
    blocked = 30
    blocked_poke = [hex(TERRAINS), blocked, 0x2A, '0000']
    def block(hexes):
        return [blocked_poke, *[[hex(TILES), h, 0x10, bytes([blocked]).hex()] for h in hexes]]
    passable = next(i for i in range(30) if struct.unpack_from('<h', ctx.rec[TERRAINS][i], 0x2A)[0] != 0)
    local_area = [[hex(TILES), row * 220 + col, 0x10, bytes([passable]).hex()]
                  for row in range(107, 116) for col in range(10)]
    add('same_tile', 24200, 24200)
    add('same_blocked_tile', 24200, 24200, block([24200]))
    add('even_to_odd', 24200, 24201)
    add('odd_to_even', 24201, 24200)
    add('blocked_source_bypass', 24200, 24201, block([24200]))
    add('blocked_goal_initialization', 24200, 24201, block([24201]))
    add('equal_priority_routes', 24200, 24644, local_area)
    add('bottom_row_fallback', 48180, 48181)
    target = 24201
    surround = [o.call32(0x2A7720, target, d) for d in range(1, 7)]
    add('isolated_goal_failure', 24644, target, block(surround))
    add('terrain_detour', 24200, 24644, local_area + block([24421, 24422, 24642]))
    # Use original active-return endpoints as well as isolated branch fixtures.
    mgr = o.q(ctx.raw + 0x871E0)
    for pointer in audit.members(o.q(mgr + 0x18)):
        pointer = int(pointer, 16)
        vt = o.q(pointer)
        kind = o.call32(o.q(vt + 0x98) - ctx.g.exe, pointer)
        if kind == 15:
            source = struct.unpack('<H', o.read(pointer + 0x4C, 2))[0]
            if o.q(vt + 0xD8) - ctx.g.exe == 0x22B1C0:
                tile = o.call(o.q(vt + 0xC8) - ctx.g.exe, pointer)
                target = o.call32(0x22DDA0, tile)
            else:
                target = struct.unpack('<H', o.read(pointer + 0x6C, 2))[0]
            if source < 48400 and target < 48400:
                add('original_return', source, target)
                break
    if names is not None:
        fixtures = [f for f in fixtures if f['name'] in names]
        if {f['name'] for f in fixtures} != set(names): raise ValueError('Unknown route fixture')
    cases = []
    for f in fixtures:
        began = time.time()
        o.poke(goals, struct.pack('<I', f['target']))
        def run():
            before, captured = audit.read(), []
            def inspect():
                address, count, cost = struct.unpack('<Qii', o.read(output, 16))
                if not 0 <= count <= 1320:
                    raise RuntimeError('The native output buffer capacity was exceeded')
                path = list(struct.unpack('<' + 'i' * count, o.read(address, count * 4)))
                captured.append(dict(hexes=path, cost=cost))
                if audit.read() != before:
                    raise RuntimeError('Routing changed gameplay state')
                return []
            success = o.call(0x1843E0, output, context, f['source'], goals,
                             stack_args=(1, 8, 2), read_after=inspect) & 0xFF
            if bool(success) != bool(captured[0]['hexes']):
                raise RuntimeError('Native success disagrees with the route output')
            return captured[0]
        result = ctx.run(f['pokes'], run)
        if result is None:
            raise RuntimeError(f'{f["name"]}: {ctx.failures[-1]}')
        cases.append(dict(fixture=f, **result))
        print(f'  return path {f["name"]}: {len(result["hexes"])} tiles, cost {result["cost"]} ({time.time()-began:.2f}s)', flush=True)
    if audit.read() != original:
        raise RuntimeError('Return route fixtures did not restore their input')
    if previous is not None:
        cases = [c for c in previous['cases'] if c['fixture']['name'] not in names] + cases
    ctx.save('return_path_world.json.gz', ctx.snap)
    ctx.save('return_path.json.gz', dict(world='return_path_world.json.gz', cases=cases, neighbors=neighbors,
        gameplay_stubs=[], contract=dict(army=0, force=0, goals=1, options=8, workspace=2)))
    return f'return path: {len(cases)} routes, {len(neighbors)} neighbors'
