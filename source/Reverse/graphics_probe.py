"""Read-only renderer discovery; saves code and live-state evidence for graphics tests."""
import argparse
import hashlib
import json
import pathlib
import struct
import time

from game import Game, BASE_POINTER_RVA, BASE_ADJUST
from image import Image

FUNCTIONS = {
    0x14CB00: 8, 0x14B854: 0x14, 0x188A40: 0x41, 0x18C350: 0x3A, 0x18C8B0: None,
    0xCE7790: 0x1C, 0xCE78B0: 0x1E, 0xCE7870: 0xC,
}


def observe(g):
    # Game.base is cached at attachment; save loading can replace the live world.
    world = g.q(g.exe + BASE_POINTER_RVA)
    if not world: raise RuntimeError('Game world pointer is unreadable')
    world += BASE_ADJUST
    state = g.q(world + 0x86F58)
    clock = g.read(state + 0x34, 5) if state else None
    manager = g.q(g.exe + 0x1B9FB70)
    mode = g.read(manager + 8, 4) if manager else None
    return dict(pid=g.pid, exe=hex(g.exe), world=hex(world),
                state=hex(state or 0), clock=clock.hex() if clock else None,
                date=list(struct.unpack('<HBBB', clock)) if clock else None,
                manager=hex(manager or 0),
                manager_mode=struct.unpack('<i', mode)[0] if mode else None,
                manager_scene=hex(g.q(manager + 0x210) or 0) if manager else None)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--out', required=True)
    args = parser.parse_args()
    out = pathlib.Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    im, g = Image(), Game()
    code = []
    for rva, size in FUNCTIONS.items():
        end = rva + size if size else im.func(rva)[1]
        saved, live = im.img[rva:end], g.read(g.exe + rva, end-rva)
        if saved != live:
            raise RuntimeError(f'Live code differs from preserved image at {rva:#x}')
        code.append(f'== {rva:#x} SHA256={hashlib.sha256(live).hexdigest()}\n{im.text(rva,end)}')
    (out/'code.txt').write_text('\n'.join(code), encoding='utf-8')
    before = observe(g)
    time.sleep(2)
    after = observe(g)
    # The caller at 0x14B854 supplies this global object; 0x188A40 resolves its target.
    renderer = g.q(g.exe+0x1BB3D00)
    context = g.q(renderer+0x2F8A0) if renderer else None
    target = g.q(context+0x20) if context else None
    vector = g.read(target+0x1020,16) if target else None
    result = dict(before=before, after=after,
                  clock_unchanged=before['clock'] is not None and before['clock']==after['clock'],
                  renderer=hex(renderer or 0),context=hex(context or 0),target=hex(target or 0),
                  vector_bytes=vector.hex() if vector else None,
                  vector_values=list(struct.unpack('<4f',vector)) if vector else None)
    (out/'probe.json').write_text(json.dumps(result,indent=2),encoding='utf-8')
    print(json.dumps(result,indent=2))


if __name__=='__main__': main()
