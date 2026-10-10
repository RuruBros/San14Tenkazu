"""Compare editor capture frames with unmodified native capture in copied memory; no game writes."""
import argparse
import gzip
import json
import pickle
import struct
from pathlib import Path

from ai_frozen_preflight import relocated_text
from game import Game, FrozenGame
from image import Image
from oracle import Oracle, PAGE
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_R9, UC_X86_REG_RSP

ROOT = Path(__file__).resolve().parents[2]
OUTPUT = ROOT / '.cache/status-change-check'


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--offline', action='store_true')
    args = parser.parse_args()
    captured = json.loads((OUTPUT / 'input.json').read_text(encoding='utf-8'))
    segments = [(r['address'], bytes.fromhex(r['hex'])) for r in captured['reads']]
    im = Image()
    begin, text, _ = relocated_text(im, captured['exe'])

    if args.offline:
        game = FrozenGame(OUTPUT / 'native.pkl')
    else:
        class Captured(Game):
            def __init__(self):
                self.pages = {}
                super().__init__()

            def read(self, address, length):
                chunks = []
                for page in range(address & ~(PAGE - 1), address + length, PAGE):
                    if page not in self.pages:
                        value = Game.read(self, page, PAGE)
                        if begin <= page - self.exe < begin + len(text):
                            offset = page - self.exe - begin
                            size = min(PAGE, len(text) - offset)
                            if value[:size] != text[offset:offset + size]:
                                raise AssertionError(f'Original code differs: {page-self.exe:X}')
                        self.pages[page] = value
                    lo, hi = max(address, page), min(address + length, page + PAGE)
                    chunks.append(self.pages[page][lo-page:hi-page])
                return b''.join(chunks)

        game = Captured()
        if (game.pid, game.exe, game.base - 0x290) != (captured['pid'], captured['exe'], captured['raw']):
            raise AssertionError('Game changed since capture')
        for address, value in segments:
            if game.read(address, len(value)) != value:
                raise AssertionError(f'Captured input changed: {address:X}')

    native = dict(gameWrites=False, helperReplacements=[], verifiedCode=True, cases=[])

    def execute(row, frame=True, stale=None):
        oracle = Oracle(game)
        calls = []

        def trace(uc, _address, _size, _user):
            stack = uc.reg_read(UC_X86_REG_RSP)
            values = [uc.reg_read(reg) for reg in (UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_R9)]
            values += [struct.unpack('<Q', oracle.read(stack + at, 8))[0] for at in (0x28, 0x30)]
            calls.append(values)

        oracle.mu.hook_add(UC_HOOK_CODE, trace, None, game.exe + 0x2E0CF0, game.exe + 0x2E0CF0)
        if stale:
            value = bytearray(bytes.fromhex(stale['hex'])); value[-1] ^= 1
            oracle.poke(stale['address'], bytes(value))
        if frame:
            body = bytes.fromhex(row['body'])
            entry = oracle.alloc(len(body)); oracle.emu_write(entry, body)
            result = oracle.call(entry - game.exe, keep=True)
        else:
            force = struct.unpack('<Q', game.read(captured['raw'] + 0xDF30 + row['captor'] * 8, 8))[0]
            result = oracle.call(0x2E0CF0, row['address'], 0, 0, 0, stack_args=[force, 0], keep=True)
        after = oracle.read(row['address'], 0x1F0)
        writes = {page: bytes(oracle.mu.mem_read(page, PAGE)) for page in oracle.pristine if page not in oracle.own}
        return result, after, writes, calls, oracle

    for row in captured['rows']:
        direct, expected, expected_writes, expected_calls, _ = execute(row, frame=False)
        result, after, writes, calls, oracle = execute(row)
        if direct != 1 or result != 1 or expected != after or expected_writes != writes or expected_calls != calls:
            raise AssertionError(f'Editor frame differs from native capture: {row["id"]}/{row["captor"]}')
        if len(calls) != 1 or calls[0][:4] != [row['address'], 0, 0, 0] or calls[0][5] != 0:
            raise AssertionError('Native capture arguments differ')
        home, place = struct.unpack_from('<HH', after, 0xE2)
        if after[0xE6] != 6 or after[0xE7] != 0x49 or after[0x168] != 0 or home != row['home'] or place != row['place']:
            raise AssertionError('Native prisoner values differ from prepared destination')
        native['cases'].append(dict(id=row['id'], captor=row['captor'], originalStatus=row['originalStatus'],
            originalForce=row['originalForce'], home=home, place=place, status=after[0xE6], result=result,
            nativeResult=direct, exactNativeWrites=True, nativeCalls=len(calls), changedPages=len(writes)))
        for guard in row['guards'][:3] + [next(g for g in row['guards'] if g['address'] == row['address'])]:
            result, _, writes, calls, _ = execute(row, stale=guard)
            if result != 0 or calls or writes:
                raise AssertionError('A stale frame reached native capture or changed game memory')
            native['cases'].append(dict(id=row['id'], captor=row['captor'], stale=True, result=result,
                guardAddress=guard['address'], unchanged=True, nativeCalls=0))
        print(json.dumps(native['cases'][-5:], ensure_ascii=False), flush=True)

    if not args.offline:
        native['clockStable'] = Game.read(game, captured['state'] + 0x34, 5) == bytes([
            captured['date']['year'] & 255, captured['date']['year'] >> 8, captured['date']['month'],
            captured['date']['day'], captured['hour']])
        native['officerRecordsStable'] = all(Game.read(game, r['address'], 0x1F0) == bytes.fromhex(r['hex'])
            for r in captured['officers'])
        if not native['clockStable'] or not native['officerRecordsStable']:
            raise AssertionError('Live input changed during read-only replay')
        (OUTPUT / 'native.pkl').write_bytes(pickle.dumps(dict(exe=game.exe, exe_size=game.exe_size,
            base=game.base, teb=game.main_teb(), imports=oracle.imports(), pages=game.pages)))
        with gzip.open(ROOT / 'source/Tests/Vectors/officer_captivity_native.json.gz', 'wt', encoding='utf-8') as file:
            json.dump(dict(input=captured, native=native), file, ensure_ascii=False)
    else:
        native['offlineReplay'] = True
    (OUTPUT / ('offline.json' if args.offline else 'native.json')).write_text(
        json.dumps(native, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(dict(cases=len(captured['rows']), staleChecks=len(native['cases'])-len(captured['rows']),
        gameWrites=False, exactNativeWrites=True, offline=args.offline)), flush=True)


if __name__ == '__main__':
    main()
