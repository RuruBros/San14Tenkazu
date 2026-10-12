"""Read-only original report/still lifecycle evidence; no native calls or game writes."""
import argparse
import ctypes
import hashlib
import json
import pathlib
import time
from game import Game, k32
from season_draw_observe import modules


def environment_build13(game):
    """Read-only fields from the exact build inspected in notice-env-*-disasm-08.log."""
    resident = [(path, base) for path, base in modules(game).items()
                if pathlib.Path(path).name.lower() == 'environmentbridge.dll']
    if len(resident) != 1:
        return None
    path, base = resident[0]
    if hashlib.sha256(pathlib.Path(path).read_bytes()).hexdigest() != 'f60d4ab25abbd341b49971a27af36192c18398810a67a493ce93f606fc3e92c4':
        raise ValueError('Build 13 layout requires its exact resident DLL')
    # 0x5508 RIP load addresses current's pointer at 0x34df0.
    # Frame 0x3e69/0x3e83 authenticates thread +0x180 / failure +0x17c.
    return base + 0x34df0


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=pathlib.Path, required=True)
    parser.add_argument('--seconds', type=int, default=50)
    parser.add_argument('--environment-build13', action='store_true')
    args = parser.parse_args()
    if not 1 <= args.seconds <= 60:
        raise ValueError('Bounded observation required')
    game = Game()
    environment_pointer = environment_build13(game) if args.environment_build13 else None
    if args.environment_build13 and environment_pointer is None:
        raise ValueError('Build 13 resident required before internal observation')
    def u32(address):
        return int.from_bytes(game.read(address,4),'little')
    started = time.monotonic()
    previous = None
    try:
        with args.output.open('x', encoding='utf-8') as output:
            while time.monotonic() - started < args.seconds:
                manager = game.exe + 0x1b620b0
                count, items = game.q(manager + 0x10), game.q(manager + 0x20)
                pending, pending_items = game.q(manager + 0x30), game.q(manager + 0x40)
                if count > 128 or pending > 128:
                    raise ValueError('State manager count outside verified bound')
                states = [(game.q(items + i * 8), None) for i in range(count)]
                states = [(pointer, game.q(pointer) - game.exe) for pointer, _ in states]
                still = []
                for pointer, cls in states:
                    if cls != 0x13d68c0:
                        continue
                    layout = game.q(pointer + 0x480)
                    if game.q(layout) != game.exe + 0x13d6050:
                        raise ValueError('Original game layout class differs')
                    target = game.q(layout + 0x1b8)
                    if game.q(target) != game.exe + 0x13d5ec0:
                        raise ValueError('Original still layout class differs')
                    still.append(dict(address=hex(target),shown=u32(target + 0x178),
                                      category=u32(target + 0x184),picture=u32(target + 0x188)))
                blocker = game.q(game.exe + 0x27842a0)
                value = dict(pid=game.pid,states=[dict(address=hex(p),cls=hex(c)) for p,c in states],
                             pending=[hex(game.q(pending_items+i*16+8)) for i in range(pending)],
                             still=still,showBlocker=hex(blocker),showBlocked=u32(blocker+0x1b0) if blocker else 0,
                             audio=hex(game.q(game.exe+0x27d26c8)))
                if environment_pointer:
                    session = game.q(environment_pointer)
                    fields = game.read(session + 0x178, 32) if session else None
                    presentation = game.read(session + 0x1b5d0, 24) if session else None
                    if fields is not None and presentation is not None and game.q(environment_pointer) == session:
                        value['environment'] = dict(session=hex(session),admission=fields[0],
                            failure=int.from_bytes(fields[4:8],'little'),thread=int.from_bytes(fields[8:12],'little'),
                            frames=int.from_bytes(fields[16:24],'little'),
                            state=hex(int.from_bytes(presentation[:8],'little')),
                            layout=hex(int.from_bytes(presentation[8:16],'little')),
                            kind=int.from_bytes(presentation[16:20],'little'),
                            shown=presentation[20],closing=presentation[21],diagnostic=presentation[22])
                    else:
                        value['environment'] = None
                if value != previous:
                    output.write(json.dumps(dict(elapsedMs=round((time.monotonic()-started)*1000),**value))+'\n')
                    output.flush()
                    previous = value
                time.sleep(.01 if environment_pointer else .05)
    finally:
        k32.CloseHandle(ctypes.c_void_p(game.h))


if __name__ == '__main__':
    main()
