"""Replay exact editor revival frames in copied native memory. Never writes to the game."""
import argparse
import gzip
import hashlib
import json
from pathlib import Path
import struct

from ai_frozen_preflight import relocated_text, validate_pool
from game import Game, FrozenGame
from image import Image
from oracle import Oracle

ROOT = Path('.cache/revival-check')


def membership(o, exe, raw, person):
    q = lambda a: struct.unpack('<Q', o.read(a, 8))[0]
    wrapper = q(raw + 0x100)
    index = struct.unpack('<I', o.read(wrapper, 4))[0]
    node = q(q(exe + 0x27D10F0) + index * 8)
    seen = set()
    count = 0
    while node:
        if node in seen or len(seen) > 6000:
            raise AssertionError('Invalid native officer list')
        seen.add(node)
        count += q(node) == person
        node = q(node + 8)
    return count


def main():
    global ROOT
    args = argparse.ArgumentParser()
    args.add_argument('--offline', action='store_true')
    args.add_argument('--appearance', action='store_true')
    opt = args.parse_args()
    if opt.appearance:
        ROOT = Path('.cache/appearance-check')
    captured = json.loads((ROOT / 'input.json').read_text())
    segments = [(r['address'], bytes.fromhex(r['hex'])) for r in captured['reads']]
    im = Image()

    if opt.offline:
        game = FrozenGame(ROOT / 'native.pkl')
    else:
        class Captured(Game):
            def read(self, address, length):
                for start, value in segments:
                    if start <= address and address + length <= start + len(value):
                        return value[address - start:address - start + length]
                return super().read(address, length)
        game = Captured()
        if game.pid != captured['pid'] or game.exe != captured['exe'] or game.base - 0x290 != captured['raw']:
            raise AssertionError('Game changed since preflight capture')
        pool = validate_pool(game.read, game.exe + 0x27D10E0)
        assert pool['valid'] and pool['complete']
        for address, value in segments:
            if len(value) > 4096 or address == (game.exe + 0x27D10E0) & ~0xFFF:
                continue
            if Game.read(game, address, len(value)) != value:
                raise AssertionError(f'Captured input changed: {address:X}')
    begin, text, _ = relocated_text(im, game.exe)
    for rva in (0x31F4F0, 0x2341F0, 0x18DF0, 0x2301F0, 0x22CA00):
        bounds = im.func(rva)
        end = bounds[1] if bounds else rva + 128
        if game.read(game.exe + rva, end - rva) != text[rva - begin:end - begin]:
            raise AssertionError(f'Original native code differs: {rva:X}')

    out = dict(gameWrites=False, helperReplacements=[], verifiedCode=True, inputDate=captured['date'],
               pid=captured['pid'], imageSha256=hashlib.sha256(im.img).hexdigest(), cases=[])
    frozen_pages = {}
    for row in captured['rows']:
        o = Oracle(game)
        frame = bytes.fromhex(row['body'])
        entry = o.alloc(len(frame))
        o.emu_write(entry, frame)
        original = o.read(row['address'], 0x1F0)
        q = lambda at: struct.unpack('<Q', o.read(at, 8))[0]
        base_slot = captured['raw'] + (0xDD38 + row['home'] * 8 if row['home'] <= 46 else 0xDED8 + (row['home'] - 51) * 8)
        base = q(base_slot)
        target_corps = struct.unpack('<H', o.read(base + 0x30, 2))[0]
        corps = q(captured['raw'] + 0xE0D0 + target_corps * 8)
        before_count = o.call32(0x2301F0, corps, 0, 0, keep=True)
        result = o.call(entry - game.exe, keep=True)
        after = o.read(row['address'], 0x1F0)
        count = membership(o, game.exe, captured['raw'], row['address'])
        for field in row['expected']:
            value = bytes.fromhex(field['hex'])
            assert after[field['offset']:field['offset'] + len(value)] == value
        changed = {field['offset'] + i for field in row['expected'] for i in range(len(bytes.fromhex(field['hex'])))}
        assert all(original[i] == after[i] for i in range(len(original)) if i not in changed)
        assert original[0xAA:0xAE] == after[0xAA:0xAE]
        assert result == 1 and count == 1
        after_count = o.call32(0x2301F0, corps, 0, 0, keep=True)
        assert after_count - before_count == (1 if row['mode'] == 0 else 0)
        force = o.call(0x22CA00, row['address'], keep=True)
        corps_force = o.read(corps + 0x10, 1)[0] if row['mode'] == 0 else 0
        assert force == q(captured['raw'] + 0xDF30 + corps_force * 8)
        duplicate = o.call(0x31F4F0, captured['raw'], row['address'], keep=True)
        assert duplicate == 0 and membership(o, game.exe, captured['raw'], row['address']) == 1
        repeated_frame = o.call(entry - game.exe, keep=True)
        assert repeated_frame == 0 and o.read(row['address'], 0x1F0) == after
        pool = validate_pool(o.read, game.exe + 0x27D10E0)
        assert pool['valid'] and pool['complete']
        out['cases'].append(dict(id=row['id'], mode=row['mode'], home=row['home'], result=result,
            originalStatus=original[0xE6], birthYear=struct.unpack_from('<H', after, 0xAC)[0],
            debutYear=struct.unpack_from('<H', after, 0xAA)[0], preservedBirthAndDebut=True,
            status=after[0xE6], corps=after[0xE0], place=struct.unpack_from('<H', after, 0xE4)[0],
            deathYear=struct.unpack_from('<H', after, 0xAE)[0], membership=count,
            duplicateResult=duplicate, repeatedFrameResult=repeated_frame, preservedOtherBytes=True, poolValid=True,
            nativeServingCountDelta=after_count-before_count, nativeForceConfirmed=True))
        for page in o.pages - o.own:
            if hasattr(game, 'pages') and page in game.pages:
                frozen_pages[page] = game.pages[page]
            else:
                pristine = game.read(page, 4096)
                if pristine is None:
                    raise AssertionError(f'Cannot retain original page {page:X}')
                frozen_pages[page] = pristine
        # Each stale input rejects the complete frame before the first write.
        stale_guards = [row['guards'][0], next(g for g in row['guards'] if g['address'] == row['address']),
                        next(g for g in row['guards'] if g['address'] == game.exe + 0x27D10E0)]
        for guard in stale_guards:
            o = Oracle(game)
            entry = o.alloc(len(frame)); o.emu_write(entry, frame)
            value = bytearray(bytes.fromhex(guard['hex'])); value[-1] ^= 1
            o.emu_write(guard['address'], bytes(value))
            before = o.read(row['address'], 0x1F0)
            result = o.call(entry - game.exe, keep=True)
            assert result == 0 and o.read(row['address'], 0x1F0) == before
            out['cases'].append(dict(id=row['id'], mode=row['mode'], home=row['home'], stale=True,
                guardAddress=guard['address'], result=result, unchanged=True))
        for guard in (g for g in row['guards'] if g.get('nonzero')):
            o = Oracle(game)
            entry = o.alloc(len(frame)); o.emu_write(entry, frame)
            o.emu_write(guard['address'], bytes(8))
            before = o.read(row['address'], 0x1F0)
            result = o.call(entry - game.exe, keep=True)
            assert result == 0 and o.read(row['address'], 0x1F0) == before
            out['cases'].append(dict(id=row['id'], mode=row['mode'], home=row['home'], stale=True,
                guardAddress=guard['address'], emptyPool=True, result=result, unchanged=True))
        print(json.dumps(out['cases'][-4:], ensure_ascii=False), flush=True)

    if not opt.offline:
        out['clockStable'] = Game.read(game, captured['state'] + 0x34, 5) == bytes([
            captured['date']['year'] & 255, captured['date']['year'] >> 8, captured['date']['month'],
            captured['date']['day'], captured['hour']])
        out['officerRecordsStable'] = all(Game.read(game, row['address'], 0x1F0) == next(
            bytes.fromhex(r['hex']) for r in captured['officers'] if r['address'] == row['address']) for row in captured['rows'])
        assert out['clockStable'] and out['officerRecordsStable']
        # Retain complete original pool pages for subsequent offline replay, never emulated post-write pages.
        for start, value in segments:
            if start % 4096 or len(value) % 4096: continue
            for at in range(0, len(value), 4096): frozen_pages[start + at] = value[at:at + 4096]
        import pickle
        (ROOT / 'native.pkl').write_bytes(pickle.dumps(dict(exe=game.exe, exe_size=game.exe_size,
            base=game.base, teb=game.main_teb(), imports=o.imports(), pages=frozen_pages)))
    else:
        out['offlineReplay'] = True
    (ROOT / ('offline.json' if opt.offline else 'native.json')).write_text(json.dumps(out, ensure_ascii=False, indent=2), encoding='utf-8')
    if not opt.offline:
        fixture = Path('source/Tests/Vectors/' + ('officer_appearance_native.json.gz' if opt.appearance else 'officer_revival_native.json.gz'))
        with gzip.open(fixture, 'wt', encoding='utf-8') as f:
            json.dump(dict(input=captured, native=out), f, ensure_ascii=False)
    print(json.dumps(dict(cases=len(out['cases']), gameWrites=False, offline=opt.offline)), flush=True)


if __name__ == '__main__':
    main()
