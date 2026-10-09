"""Read the native binding producer, then replay its cache branches in Unicorn.

No writes or function calls in the game. The controlled changed views below
are values in the emulator, not replacement D3D11 objects in the live game.
"""
import argparse
import hashlib
import json
import pathlib
import struct

from unicorn import Uc, UC_ARCH_X86, UC_MODE_64
from unicorn.x86_const import *

from game import Game
from graphics_gpu import driver_path
from graphics_probe import observe
from image import Image


PRODUCER = 0x8759F0


def replay(code, record, name, changed_view=False, changed_sampler=False,
           linear=False, null_record=False, resource_only=False):
    mu = Uc(UC_ARCH_X86, UC_MODE_64)
    mu.mem_map(0x100000, 0x700000)
    entry, driver, texture, commands = 0x100000, 0x200000, 0x300000, 0x400000
    stack, stop = 0x508008, 0x600000
    stage, slot = 4, 4
    source = bytearray(record)
    old_resource, view0, view1 = struct.unpack_from('<3Q', source)
    old_sampler = struct.unpack_from('<Q', source, 0x38)[0]
    sampler = old_sampler + 1 if changed_sampler else old_sampler
    view = view0 if linear else view1
    if changed_view:
        view += 0x10000  # A synthetic value; never used as a live COM pointer.
    resource = old_resource + 0x10000 if resource_only else old_resource
    struct.pack_into('<3Q', source, 0, resource, view if linear else view0,
                     view1 if linear else view)
    struct.pack_into('<Q', source, 0x38, sampler)
    flags = struct.unpack_from('<H', source, 0x36)[0]
    struct.pack_into('<H', source, 0x36, flags & 0x7FFF if linear else flags | 0x8000)
    view_cache = driver + 0x530 + stage * 0x400 + slot * 8
    sampler_cache = driver + 0x1D30 + stage * 0x80 + slot * 8
    resource_cache = driver + 0x2B80 + stage * 0x80 + slot * 8
    def write(addr, data): mu.mem_write(addr, bytes(data))
    def q(addr): return struct.unpack('<Q', mu.mem_read(addr, 8))[0]
    write(entry, code)
    write(texture, source)
    write(driver + 0x2B58, struct.pack('<Q', commands))
    write(view_cache, struct.pack('<Q', view0 if linear else view1))
    write(sampler_cache, struct.pack('<Q', old_sampler))
    write(resource_cache, struct.pack('<Q', old_resource))
    write(stack, struct.pack('<Q', stop))
    mu.reg_write(UC_X86_REG_RSP, stack)
    for reg, value in ((UC_X86_REG_RCX, driver), (UC_X86_REG_RDX, stage),
                       (UC_X86_REG_R8, slot), (UC_X86_REG_R9, 0 if null_record else texture)):
        mu.reg_write(reg, value)
    saved = (UC_X86_REG_RBX, UC_X86_REG_RBP, UC_X86_REG_RSI, UC_X86_REG_RDI,
             UC_X86_REG_R14)
    for i, reg in enumerate(saved): mu.reg_write(reg, 0x123400 + i)
    mu.emu_start(entry, stop, count=10000)
    assert mu.reg_read(UC_X86_REG_RSP) == stack + 8
    assert all(mu.reg_read(reg) == 0x123400 + i for i, reg in enumerate(saved))
    emitted = changed_view or changed_sampler or null_record
    expected_length = 20 if emitted else 0
    assert q(driver + 0x2B58) == commands + expected_length
    packet = bytes(mu.mem_read(commands, expected_length))
    if emitted:
        expected_view = 0 if null_record else view
        expected_sampler = 0xFFFFFFFFFFFFFFFF if null_record else sampler
        assert packet == struct.pack('<IQQ', 0x0404001E, expected_sampler, expected_view)
        assert q(view_cache) == expected_view and q(sampler_cache) == expected_sampler
        assert q(resource_cache) == (0 if null_record else resource)
    else:
        assert q(resource_cache) == old_resource
    return dict(name=name, environment='Unicorn controlled replay',
                command_bytes=expected_length, packet=packet.hex(),
                cached_view=hex(q(view_cache)), cached_sampler=hex(q(sampler_cache)),
                cached_resource=hex(q(resource_cache)))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--identified', required=True, type=pathlib.Path)
    parser.add_argument('--out', required=True, type=pathlib.Path)
    args = parser.parse_args()
    proof = json.loads(args.identified.read_text())
    g, im = Game(), Image()
    state = observe(g) | {key: hex(value) for key, value in driver_path(g).items()}
    if state != proof['state']:
        raise RuntimeError('Identified texture belongs to another live state')
    candidate = proof['candidate']['candidate']
    field = int(candidate['resource_field'], 16)
    record = g.read(field, 72)
    if record is None or struct.unpack_from('<Q', record)[0] != int(candidate['resource'], 16):
        raise RuntimeError('The original resource record is no longer installed')
    begin, end = im.func(PRODUCER)
    code = g.read(g.exe + begin, end - begin)
    if code != im.img[begin:end]:
        raise RuntimeError('Live producer code differs from the preserved image')
    cases = [replay(code, record, 'same view and sampler'),
             replay(code, record, 'changed SRGB view', changed_view=True),
             replay(code, record, 'changed linear view', changed_view=True, linear=True),
             replay(code, record, 'changed sampler', changed_sampler=True),
             replay(code, record, 'null record unbind', null_record=True),
             replay(code, record, 'resource only, unchanged views', resource_only=True)]
    result = dict(state=state, game_access='read only; no native dispatch',
                  function_rva=hex(begin), end_rva=hex(end),
                  code_sha256=hashlib.sha256(code).hexdigest(),
                  original_record=record.hex(), cases=cases,
                  after=observe(g) | {key: hex(value) for key, value in driver_path(g).items()})
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(result, indent=2), encoding='utf-8')
    args.out.with_suffix('.dis.txt').write_text(im.text(begin, end), encoding='utf-8')
    print(json.dumps(result, indent=2))


if __name__ == '__main__': main()
