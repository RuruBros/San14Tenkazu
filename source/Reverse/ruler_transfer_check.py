"""Execute exact managed ruler-transfer frames in copied memory; never write to the game."""
from pathlib import Path
import gzip, hashlib, json, re, struct
from ai_native_snapshot import ListPoolSnapshot
from ai_frozen_preflight import relocated_text
from image import Image
from game import Game
from oracle import Oracle

root = Path('.cache/ruler-transfer-check')
captured = json.loads((root / 'input.json').read_text())

class Captured(ListPoolSnapshot):
    def __init__(self):
        super().__init__()
        assert self.exe == captured['exe'] and self.base - 0x290 == captured['raw']
        self.segments = [(r['address'], bytes.fromhex(r['hex'])) for r in captured['reads']]

    def read(self, address, size):
        data = super().read(address, size)
        if data is None:
            return None
        data = bytearray(data)
        for start, value in getattr(self, 'segments', []):
            lo, hi = max(address, start), min(address + size, start + len(value))
            if lo < hi:
                data[lo - address:hi - address] = value[lo - start:hi - start]
        return bytes(data)

game = Captured()
im = Image()
begin, text, _ = relocated_text(im, game.exe)
prefixes = {int(rva, 16): bytes.fromhex(value) for rva, value in re.findall(
    r'\[(0x[0-9A-Fa-f]+)\] = Convert.FromHexString\("([0-9A-Fa-f]+)"\)',
    Path('source/Core/GameMemory.Transfers.cs').read_text())}
for rva, prefix in prefixes.items():
    assert game.read(game.exe + rva, len(prefix)) == prefix, f'Native prefix differs: {rva:X}'
assert game.read(game.exe + 0x2DAE80, 30) == text[0x2DAE80 - begin:0x2DAE80 - begin + 30]
clock = Game.read(game, captured['state'] + 0x34, 5)
before_live = [(r['address'], Game.read(game, r['address'], 0x1F0)) for r in captured['rows'] if 'body' in r]
rows = [r for r in captured['rows'] if 'body' in r]
before_live.extend((u['address'], Game.read(game, u['address'], 0x200)) for r in rows for u in r['units'])
selected = []
for predicate in (
    lambda r: r['player'] and r['destinationCorps'] == r['oldCorps'],
    lambda r: r['player'] and r['destinationCorps'] != r['oldCorps'],
    lambda r: not r['player'] and r['destinationCorps'] == r['oldCorps'],
    lambda r: not r['player'] and r['destinationCorps'] != r['oldCorps'],
    lambda r: bool(r['units']),
):
    row = next((r for r in rows if predicate(r)), None)
    if row is not None and row not in selected:
        selected.append(row)
selected.extend(r for r in rows if r['units'] and r not in selected)
assert any(r['player'] for r in selected), 'Player ruler coverage is required'
assert any(r['destinationCorps'] != r['oldCorps'] for r in selected), 'Delegated city coverage is required'
results, stale = [], []
for row in selected:
    targets = [(row['address'], 0x1F0), (row['forceAddress'], 0x268), (row['corpsAddress'], 0x30),
               (row['baseAddress'], 0x168)] + [(u['address'], 0x200) for u in row['units']]
    oracle = Oracle(game)
    oracle.insn_limit = 800_000_000
    for patch in row['inputChanges']:
        oracle.emu_write(patch['address'], bytes.fromhex(patch['hex']))
    code = bytes.fromhex(row['body'])
    entry = oracle.alloc(len(code))
    oracle.emu_write(entry, code)
    before_flags = oracle.read(row['address'] + 0x15E, 2)
    result = oracle.call(entry - game.exe, read_after=targets)
    person, force, corps, city, *unit_records = oracle.after
    value = dict(id=row['Id'], home=row['home'], oldCorps=row['oldCorps'],
                 destinationCorps=row['destinationCorps'], player=row['player'], scenario=row['scenario'],
                 result=result, status=person[0xE6], corps=person[0xE0], base=struct.unpack_from('<H', person, 0xE2)[0],
                 place=struct.unpack_from('<H', person, 0xE4)[0], ruler=struct.unpack_from('<H', force, 0x10)[0],
                 leader=struct.unpack_from('<H', corps, 0x12)[0], cityCorps=struct.unpack_from('<H', city, 0x30)[0],
                 travelFlags=struct.unpack_from('<H', person, 0x15E)[0], oldTravelFlags=int.from_bytes(before_flags, 'little'))
    assert result == 1 and value['status'] == 1 and value['corps'] == row['oldCorps'], value
    assert value['base'] == row['home'] and value['ruler'] == row['Id'] and value['leader'] == row['Id'], value
    assert value['cityCorps'] == row['oldCorps'], value
    assert person[0x15E:0x160] == before_flags, 'Relocation must not construct a personal travel command'
    if not row['units']:
        assert value['place'] == row['home'], value
    value['units'] = []
    for unit, record in zip(row['units'], unit_records):
        unit_value = dict(slot=unit['slot'], tile=struct.unpack_from('<H', record, 0x2A)[0], order=record[0x37],
                          target=struct.unpack_from('<H', record, 0x3A)[0], next=struct.unpack_from('<H', record, 0x48)[0],
                          group=struct.unpack_from('<H', record, 0x58)[0], flags=record[0x150])
        assert value['place'] == unit['place'] and unit_value['tile'] == unit['tile'], value
        assert unit_value['order'] == 11 and unit_value['next'] < 48400 and unit_value['group'] == 0, unit_value
        assert record[0x150] & 2 == 0, 'Commander must remain active for unit return'
        value['units'].append(unit_value)
    results.append(value)
    print(json.dumps(value), flush=True)
    for address in (row['address'] + 0xE6, row['forceAddress'] + 0x10, row['baseAddress'] + 0x30):
        oracle = Oracle(game)
        oracle.insn_limit = 800_000_000
        for patch in row['inputChanges']:
            oracle.emu_write(patch['address'], bytes.fromhex(patch['hex']))
        original = oracle.read(address, 1)
        oracle.emu_write(address, bytes([original[0] ^ 1]))
        before = [oracle.read(a, n) for a, n in targets]
        entry = oracle.alloc(len(code))
        oracle.emu_write(entry, code)
        result = oracle.call(entry - game.exe, read_after=targets)
        unchanged = all(a == b for a, b in zip(before, oracle.after))
        assert result == 0 and unchanged
        stale.append(dict(id=row['Id'], address=address, result=result, unchanged=unchanged))
assert clock == Game.read(game, captured['state'] + 0x34, 5)
assert all(value == Game.read(game, address, len(value)) for address, value in before_live)
output = dict(gameWrites=False, helperReplacements=[], verifiedCode=True, clockStable=True,
              imageSha256=hashlib.sha256(im.img).hexdigest(), input=captured, cases=results, stale=stale)
(root / 'results.json').write_text(json.dumps(output, indent=2))
with gzip.open('source/Tests/Vectors/ruler_transfer_native.json.gz', 'wt', encoding='utf-8') as file:
    json.dump(output, file)
print(json.dumps(dict(cases=len(results), stale=len(stale), gameWrites=False, clockStable=True)), flush=True)
