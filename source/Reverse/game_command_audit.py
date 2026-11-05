"""Compare native command entry points without attaching to a game or registering a build."""
import argparse
import json
import math
from pathlib import Path
import struct

from version_audit import MemoryImage, digest


# Legacy command and follow-up entry points used by GameMemory's frame dispatchers.
COMMANDS = {
    'frame-update': (0x14b600,),
    'demo-switch': (0x33a630,),
    'force-surrender': (0x3c1a00,),
    'base-owner': (0x3a9300,),
    'owned-item-add': (0x31f2e0,),
    'owned-item-remove': (0x32b0e0,),
    'camera': (0x451360,),
    'alliance': (0x2ebcc0, 0x278270, 0x292760, 0x2754a0, 0x33ced0),
    'capture-officer': (0x2e0cf0,),
    'revive-officer': (0x31f4f0,),
    'independence': (0x3bf120, 0x39fd00, 0x2d7c80),
    'transfer-officer': (0x233440, 0x241c90, 0x240cb0, 0x2408e0, 0x2ea1b0, 0x2e8e70,
                         0x29ede0, 0x228710, 0x240c90, 0x2827c0, 0x26cc10),
}


def image_pointers(image):
    for site in image.relocation_sites:
        target, = struct.unpack_from('<Q', image.data, site)
        rva = target - image.base
        if image.is_code(rva):
            yield site, rva


def event_types(image, pointers, function):
    names = set()
    for site, target in pointers:
        if target != function or site < 8 or site - 8 not in image.relocations:
            continue
        locator = struct.unpack_from('<Q', image.data, site - 8)[0] - image.base
        if image.section(locator) != '.rdata':
            continue
        signature, _, _, type_rva, _, self_rva = struct.unpack_from('<6I', image.data, locator)
        if signature != 1 or self_rva != locator or type_rva >= image.size - 16:
            continue
        label = image.data[type_rva + 16:type_rva + 160].split(b'\0', 1)[0]
        if label.startswith(b'.?AUSExec') and label.endswith(b'@S14Event@@'):
            names.add(label.decode('ascii'))
    return sorted(names)


def scan(old, new):
    sources = {rva: old.describe(rva) for rvas in COMMANDS.values() for rva in rvas}
    wanted = {row['prefix_sha256'] for row in sources.values()}
    prefixes = new.candidates(wanted)
    pointers = list(image_pointers(new))
    # Some leaf setters only appear as relocated vtable entries, without an unwind record or direct call.
    for rva in sorted({rva for _, rva in pointers} - new.starts):
        prefix = new.prefix(rva)
        if prefix in wanted and rva not in prefixes[prefix]:
            prefixes[prefix].append(rva)
    legacy_events = event_types(old, image_pointers(old), COMMANDS['force-surrender'][0])
    matches, changed = {}, {}
    for rva, row in sources.items():
        candidates, changed_shape = [], []
        for start in sorted(prefixes[row['prefix_sha256']]):
            candidate = new.describe(start)
            if candidate['shape_sha256'] != row['shape_sha256']:
                changed_shape.append(dict(rva=start, instructionCount=candidate['instruction_count'],
                                          shapeSha256=candidate['shape_sha256']))
                continue
            edges = zip(row['direct_edges'], candidate['direct_edges'], strict=True)
            refs = zip(row['references'], candidate['references'], strict=True)
            candidates.append(dict(rva=start, codeSha256=candidate['raw_code_sha256'],
                                   calls=[[a['instruction'], a['rva'], b['rva']] for a, b in edges],
                                   references=[[a['instruction'], a['rva'], b['rva'], a['role']] for a, b in refs],
                                   issues=candidate['issues']))
            if rva == COMMANDS['force-surrender'][0]:
                candidates[-1]['eventRtti'] = event_types(new, pointers, start)
        matches[rva] = candidates
        changed[rva] = changed_shape
    def entry(rva):
        rows = matches[rva]
        identity = [candidate['rva'] for candidate in rows
                    if legacy_events and candidate.get('eventRtti') == legacy_events]
        result = dict(legacyRva=rva, normalizedMatches=rows,
                      status='ambiguous' if len(rows) > 1 else 'candidate-only' if rows else 'unmatched')
        if len(identity) == 1:
            result.update(status='event-type-match', eventRtti=legacy_events, selectedRva=identity[0])
        if not rows:
            result.update(legacyInstructionCount=sources[rva]['instruction_count'], changedShapePrefixes=changed[rva])
        return result
    return {
        'legacy': old.identity(), 'candidate': new.identity(), 'gameAccess': False,
        'commands': {name: [entry(rva) for rva in rvas] for name, rvas in COMMANDS.items()},
    }


def replay_camera(image, rva, keeper, vtable):
    """Run the real flight/grid/TLS code up to the native virtual camera boundary."""
    from unicorn import Uc, UC_ARCH_X86, UC_MODE_64, UC_HOOK_CODE
    from unicorn.x86_const import (UC_X86_REG_GS_BASE, UC_X86_REG_RCX, UC_X86_REG_RDX,
        UC_X86_REG_R8, UC_X86_REG_R9, UC_X86_REG_RIP, UC_X86_REG_RSP, UC_X86_REG_XMM3)

    base, scratch = image.base, 0x200000000
    cpu = Uc(UC_ARCH_X86, UC_MODE_64)
    cpu.mem_map(base, image.size)
    cpu.mem_write(base, image.data)
    cpu.mem_map(scratch, 0x50000)
    qword = lambda address: struct.unpack('<Q', cpu.mem_read(address, 8))[0]
    integer = lambda address: struct.unpack('<I', cpu.mem_read(address, 4))[0]

    def put(address, value, fmt='<Q'):
        cpu.mem_write(address, struct.pack(fmt, value))

    flight = image.describe(rva)
    initializer = image.describe(flight['direct_edges'][0]['rva'])
    relative_refs = [ref['rva'] for ref in initializer['references']]
    tls_index_rva, guard = relative_refs[:2]
    if keeper not in relative_refs or qword(base + keeper + 0x50) != base + vtable:
        raise ValueError('Camera keeper changed')
    locator = qword(base + vtable - 8)
    type_rva = integer(locator + 12)
    class_name = b'.?AVCMainCameraCtrl@@\0'
    if cpu.mem_read(base + type_rva + 16, len(class_name)) != class_name:
        raise ValueError('Camera RTTI changed')
    grid_slot = flight['references'][1]['rva']
    height_flag = flight['references'][2]['rva']
    grid, stop, stack = scratch + 0x10000, scratch + 0x6000, scratch + 0x4ff8
    cpu.reg_write(UC_X86_REG_GS_BASE, scratch)
    put(scratch + 0x58, scratch + 0x1000)
    put(scratch + 0x1000 + integer(base + tls_index_rva) * 8, scratch + 0x2000)
    put(scratch + 0x2010, integer(base + guard), '<I')
    put(base + grid_slot, grid)
    put(base + height_flag, 0, '<I')
    target = qword(base + vtable + 0x10)
    moves = []

    def capture(uc, address, size, context):
        if uc.reg_read(UC_X86_REG_RCX) != base + keeper + 0x50:
            raise ValueError('Wrong camera receiver')
        data = uc.mem_read(uc.reg_read(UC_X86_REG_RDX), 0x40)
        position = struct.unpack_from('<4f', data, 0x10)
        distance, _, pitch, mode = struct.unpack_from('<3dI', data, 0x20)
        moves.append(dict(milliseconds=struct.unpack_from('<I', data)[0], position=position,
                          distance=distance, pitch=pitch, mode=mode))
        rsp = uc.reg_read(UC_X86_REG_RSP)
        uc.reg_write(UC_X86_REG_RIP, qword(rsp))
        uc.reg_write(UC_X86_REG_RSP, rsp + 8)

    cpu.hook_add(UC_HOOK_CODE, capture, begin=target, end=target)
    keep = 0xbff0000000000000
    tiles = [0, 1, 220, 12345, 48399]
    for tile in tiles:
        put(grid + 0x40 + tile * 4, 1.0, '<f')
        put(stack, stop)
        put(stack + 0x28, keep)
        put(stack + 0x30, 1)
        for register, value in [(UC_X86_REG_RSP, stack), (UC_X86_REG_RCX, tile), (UC_X86_REG_RDX, 0),
                                (UC_X86_REG_R8, 500), (UC_X86_REG_R9, keep), (UC_X86_REG_XMM3, keep)]:
            cpu.reg_write(register, value)
        cpu.emu_start(base + rva, stop, count=10000)
        if cpu.reg_read(UC_X86_REG_RIP) != stop:
            raise ValueError('Camera flight did not return')
    if len(moves) != len(tiles):
        raise ValueError('Camera did not produce one move for each tile')
    x0, _, z0, _ = moves[0]['position']
    step_z = math.sqrt(3) * 468
    for tile, move in zip(tiles, moves, strict=True):
        x, y, z, w = move['position']
        col, row = tile % 220, tile // 220
        if (move['milliseconds'], move['distance'], move['pitch'], move['mode'], y, w) != (500, -1.0, -1.0, 1, 0.0, 1.0):
            raise ValueError(f'Unexpected camera move arguments for tile {tile}')
        if abs(x - (x0 + 702 * col)) > 0.1 or abs(z - (z0 + step_z * (row - (tile % 2) / 2))) > 0.1:
            raise ValueError(f'Unexpected camera position for tile {tile}')
    end = image.ends[rva]
    return dict(rva=rva, keeper=keeper, vtable=vtable, codeSha256=digest(image.data[rva:end]),
                tiles=tiles, moves=moves, boundary='CMainCameraCtrl virtual move receiver')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for version in ('legacy', 'candidate'):
        parser.add_argument(f'--{version}-image', type=Path, required=True)
        parser.add_argument(f'--{version}-base', type=lambda s: int(s, 0), required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--verify-known-camera', action='store_true')
    args = parser.parse_args()
    old = MemoryImage.load(args.legacy_image, args.legacy_base)
    new = MemoryImage.load(args.candidate_image, args.candidate_base)
    result = scan(old, new)
    if args.verify_known_camera:
        previous = replay_camera(old, 0x451360, 0x1b62450, 0x13426f8)
        current = replay_camera(new, 0x453570, 0x1b654a0, 0x13456f8)
        if previous['moves'] != current['moves']:
            raise ValueError('Camera requests differ across the known builds')
        result['cameraReplay'] = [previous, current]
        print('Verified ten native camera command packets at the virtual move boundary')
    with args.output.open('x', encoding='utf-8') as report:
        json.dump(result, report, indent=2)
        report.write('\n')
    for name, rows in result['commands'].items():
        print(name, ', '.join(f'{row["legacyRva"]:#x} {row["status"]} ({len(row["normalizedMatches"])})' for row in rows))


if __name__ == '__main__':
    main()
