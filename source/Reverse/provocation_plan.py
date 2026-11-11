import ctypes
import argparse
import base64
import hashlib
import json
import struct
import sys
from pathlib import Path
root = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(root / 'source/Reverse'))
from oracle import Oracle
from version_audit import MemoryImage
from provocation_capture import connect
from unicorn import UC_HOOK_CODE, UC_HOOK_MEM_READ, UC_HOOK_MEM_WRITE

parser = argparse.ArgumentParser(description='Replay unmodified November provocation code against read-only game copies; emit guarded edits, never write the game.')
parser.add_argument('--inspection', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
folder = args.output.resolve()
folder.mkdir(exist_ok=False, parents=True)
inspection = json.loads(args.inspection.read_text(encoding='utf-8-sig'))
g, path, start = connect()
assert g.pid == inspection['processId'] and g.exe == inspection['moduleBase']
assert hashlib.sha256(path.read_bytes()).hexdigest().upper() == '782CF229A56CDDE2B95399E0E7536740B3F5E96BEA702679365E4DAD061ABDCE'
g.exe_size = 44048384
g.base = g.q(g.exe + 0x2780F60) + 0x290
o = Oracle(g)
# Oracle's legacy ISA hint is irrelevant on this build; restore that copied location.
o.poke(g.exe + 0x1A8BBB0, g.read(g.exe + 0x1A8BBB0, 4))
# Presentation only; admission, trait sums, cap and object resolution execute unchanged.
o.stub(0x1B5C90, 0)
o.stub(0x2F4A80, 0)
meta = json.loads((root / '.cache/game-update-20261105/capture.json').read_text())
image = MemoryImage.load(root / '.cache/game-update-20261105/image.bin', meta['imageBase'])
assert hashlib.sha256(image.data).hexdigest() == 'ccebbe29092437deaecbfe6ae2fc3a22b8ccb3c597604051f35070a3f6b3039a'
reads, executed, written = {}, {}, set()
consumer_trace = []
teb_page = g.main_teb() & ~0xFFF
def owned(address):
    return address & ~0xFFF in o.own or address & ~0xFFF == teb_page
def track_code(uc, address, size, _):
    if g.exe <= address < g.exe + g.exe_size:
        rva = address - g.exe
        actual = bytes(uc.mem_read(address, size))
        assert image.canonical(rva, size, base=g.exe, data=actual) == image.canonical(rva, size), f'Changed Native code {rva:x}'
        for i, value in enumerate(actual): executed[address + i] = value
        if 0x2CC790 <= rva < 0x2CCC5F: consumer_trace.append(rva)
def track_read(uc, _access, address, size, _value, _):
    if not owned(address):
        for i, value in enumerate(uc.mem_read(address, size)):
            reads.setdefault(address + i, value)
def track_write(uc, _access, address, size, _value, _):
    if not owned(address): written.update(range(address, address + size))
o.mu.hook_add(UC_HOOK_CODE, track_code)
o.mu.hook_add(UC_HOOK_MEM_READ, track_read)
o.mu.hook_add(UC_HOOK_MEM_WRITE, track_write)
target = next(unit for unit in inspection['units'] if unit['commander'] == 782)
assert g.read(target['address'], 0x200).hex().upper() == target['bytes']
out = o.alloc(0x400)
print('oracle ready', flush=True)
admission = {str(kind): o.call(0x2C5B00, target['address'], kind) for kind in (21, 24)}
print('immunity ' + json.dumps(admission), flush=True)
o.call(0x26BBE0, target['address'], 0, out, 0, stack_args=[0], read_after=[(out, 0x200)])
sums = struct.unpack('<128i', o.after[0])
print('resistance ' + json.dumps({'all': sums[9], 'provocation': sums[12]}), flush=True)
def distance(a, b):
    dc = abs(b % 220 - a % 220)
    dr = abs((1 if b % 2 == 0 else 0) + (b // 220 - a // 220) * 2 - (1 if a % 2 == 0 else 0))
    return (dr - dc) // 2 + dc if dc <= dr else dc
sources = []
for unit in sorted((u for u in inspection['units'] if u['force'] != target['force']), key=lambda u: distance(u['hex'], target['hex'])):
    hostile = o.call(0x2CC630, unit['force'], target['force']) & 255
    if not hostile:
        continue
    tile = inspection['tileArray'] + unit['hex'] * 0x20
    o.call(0x16FA40, out, out + 8, tile, read_after=[(out, 12)])
    kind, number = struct.unpack_from('<I', o.after[0])[0], struct.unpack_from('<I', o.after[0], 8)[0]
    row = dict(slot=unit['slot'], name=unit['name'], force=unit['force'], hex=unit['hex'], address=unit['address'],
               distance=distance(unit['hex'], target['hex']), kind=kind, number=number, tileAddress=tile)
    sources.append(row)
    if kind == 27 and number == unit['slot']:
        source = row
        break
else:
    raise RuntimeError('No hostile provoker unit resolves natively')
print('source ' + json.dumps(source, ensure_ascii=False), flush=True)
o.call(0x2F0E50, target['address'], source['address'], 2, 12,
       stack_args=[source['tileAddress'], 0, 0], read_after=[(target['address'], 0x200)])
after = o.after[0]
before = bytes.fromhex(target['bytes'])
result = dict(pid=g.pid, startUtc=start.isoformat(), moduleBase=g.exe, gameWrites=False,
              target=target, source=source, immunity=admission, resistance={'all': sums[9], 'provocation': sums[12]},
              duration=after[0x25], kind=after[0x3C], number=int.from_bytes(after[0x3E:0x40], 'little'),
              before=before.hex(), after=after.hex(), differences=[i for i, (a, b) in enumerate(zip(before, after)) if a != b])
(folder / 'native-result.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps({key: result[key] for key in ('immunity', 'resistance', 'duration', 'kind', 'number', 'differences')}, ensure_ascii=False))
assert after[0x25] > 1 and after[0x3C] == 27 and int.from_bytes(after[0x3E:0x40], 'little') == source['slot']
o.poke(target['address'], after)
consumer_trace.clear()
reach = o.call(0x2CC790, target['hex'], source['hex']) & 255
matched_trace = list(consumer_trace)
o.poke(target['address'] + 0x3E, struct.pack('<H', source['slot'] + 1))
consumer_trace.clear()
wrong_reach = o.call(0x2CC790, target['hex'], source['hex']) & 255
wrong_trace = list(consumer_trace)
o.poke(target['address'], before)
assert 0x2CCA97 in matched_trace and 0x2CCA97 not in wrong_trace, 'Native target-consumption branch was not proven'
result['consumer'] = dict(rva='0x2CC790', matchingTargetPassed=True, wrongTargetRejected=True,
                          matchedTargetAttackReach=reach, wrongTargetAttackReach=wrong_reach,
                          distance=source['distance'], matchedTrace=list(map(hex, matched_trace)), wrongTrace=list(map(hex, wrong_trace)))
(folder / 'native-result.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
print('Native target-consumption equality passed; mismatched number rejected', flush=True)
o.save_pages(folder / 'native-pages.pkl')
allowed = {target['address'] + offset for offset in (0x25, 0x3C, 0x3E, 0x3F)}
assert written <= allowed, ('Unexpected native writes', [hex(address) for address in written - allowed][:20])
def ranges(values):
    items = sorted(values)
    groups = []
    for address in items:
        if groups and address == groups[-1][0] + len(groups[-1][1]) and len(groups[-1][1]) < 4096:
            groups[-1][1].append(values[address])
        else:
            groups.append([address, bytearray([values[address]])])
    return [dict(address=address, expectedHex=data.hex().upper()) for address, data in groups]
input_values = {address: value for address, value in reads.items() if address not in written and address not in executed}
guards = ranges(input_values)
for guard in guards:
    assert g.read(guard['address'], len(guard['expectedHex']) // 2).hex().upper() == guard['expectedHex'], f'Input moved {guard["address"]:x}'
plan = dict(schemaVersion=1, processId=g.pid, startTimeUtc=inspection['startTimeUtc'],
            exeHash=hashlib.sha256(path.read_bytes()).hexdigest().upper(), moduleBase=g.exe,
            year=inspection['date']['Item1'], month=inspection['date']['Item2'], day=inspection['date']['Item3'], hour=inspection['hour'],
            playerForce=inspection['playerForce'], commander=782, slot=target['slot'], address=target['address'],
            sourceCommander=next(unit['commander'] for unit in inspection['units'] if unit['slot'] == source['slot']),
            sourceSlot=source['slot'], sourceAddress=source['address'], sourceBeforeHex=g.read(source['address'], 0x200).hex().upper(),
            sourceName=source['name'], beforeHex=before.hex().upper(), heldHex=after.hex().upper(),
            immunityAll=admission['21'], immunityProvocation=admission['24'], resistanceAll=sums[9], resistanceProvocation=sums[12],
            codeGuards=ranges(executed), inputGuards=guards, nativeReplay=True, liveNativeCall=False)
(folder / 'plan.json').write_text(json.dumps(plan, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(dict(plan=str(folder/'plan.json'), codeGuards=len(plan['codeGuards']), inputGuards=len(guards), gameWrites=False)))
