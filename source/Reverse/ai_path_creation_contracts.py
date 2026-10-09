# Standard creation constraints for remaining A* inputs. Saved files only.
# python ai_path_creation_contracts.py --snapshot <frozen.pkl> --output <report.json>
import argparse
from collections import Counter
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import struct

from ai_executor_audit import Harness
from ai_facility_special import read_clock
from ai_frozen_preflight import FrozenImage
from ai_static_facts import anchored, dominates
from image import Image


def code_contracts(im):
    im.md.detail = True
    commander = anchored(im, 0x2D28D0, {
        0x2D2A93: 'mov eax, dword ptr [r13]', 0x2D2A97: 'test eax, eax',
        0x2D2A99: 'jns 0x2d2aa0', 0x2D2A9B: 'mov eax, r14d',
        0x2D2A9E: 'jmp 0x2d2aaa', 0x2D2AA0: 'mov ecx, 0x1770',
        0x2D2AA5: 'cmp eax, ecx', 0x2D2AA7: 'cmovg eax, ecx',
        0x2D2AAA: 'mov word ptr [rsi + 0x12], ax', 0x2D2953: 'xor r14d, r14d'})
    if not dominates(im, 0x2D28D0, 0x2D2A93, 0x2D2AAA) or not dominates(im, 0x2D28D0, 0x2D2953, 0x2D2A9B):
        raise ValueError('Commander assignment bypasses its clamp')
    zero_writes = [i.address for i in im.flow(0x2D28D0) if i.address < 0x2D2AAA
                   and any(im.md.reg_name(r) in ('r14', 'r14d', 'r14w', 'r14b') for r in i.regs_access()[1])]
    if zero_writes != [0x2D2953]:
        raise ValueError('Commander clamp zero register acquired another writer')
    stores = [i.address for i in im.flow(0x2D28D0)
              if i.mnemonic == 'mov' and i.op_str.startswith('word ptr [rsi + 0x12],')]
    if stores != [0x2D2AAA]:
        raise ValueError('Commander gained another direct assignment')
    placement = anchored(im, 0x279190, {
        0x2791FB: 'movzx edx, byte ptr [rdi + 0x10]', 0x2791FF: 'mov rcx, rbp',
        0x279202: 'call 0x42b40', 0x279207: 'test eax, eax',
        0x279209: 'je 0x2791ba', 0x2791BA: 'xor eax, eax',
        0x2791BC: 'jmp 0x2792fa', 0x2792DB: 'xor ebx, ebx'})
    if not dominates(im, 0x279190, 0x279202, 0x2792DB):
        raise ValueError('Placement success bypasses terrain admission')
    lookups = anchored(im, 0x22FB10, {0x22FB10: 'movzx eax, word ptr [rcx + 0x1a]',
        0x22FB14: 'cmp eax, 0xbb8', 0x22FB24: 'mov rax, qword ptr [rax + rcx*8 + 0x6da98]'})
    lookups += anchored(im, 0x22A850, {0x22A850: 'movzx edx, word ptr [rcx + 0x1a]',
        0x22A856: 'cmp edx, 0x33', 0x22A859: 'mov eax, ecx', 0x22A867: 'cmove edx, ecx'})
    lookups += anchored(im, 0x22CF90, {0x22CF90: 'movzx edx, word ptr [rcx + 0x1a]',
        0x22CF9A: 'lea eax, [rdx - 0x34]', 0x22CF9D: 'cmp eax, 9',
        0x22CFAD: 'test ecx, ecx', 0x22CFAF: 'cmove edx, r8d'})
    lookups += anchored(im, 0x232650, {0x232657: 'sub rcx, qword ptr [rax + 0x6da98]',
        0x232660: 'sar rcx, 5', 0x232666: 'cmp ecx, 0x33', 0x232673: 'setne al'})
    lookups += anchored(im, 0x232BD0, {0x232BD7: 'sub rcx, qword ptr [rax + 0x6da98]',
        0x232BE0: 'sar rcx, 5', 0x232BE4: 'add ecx, -0x34', 0x232BE7: 'cmp ecx, 9'})
    lookups += anchored(im, 0x18A080, {0x18A194: 'call 0x22e0d0', 0x18A19C: 'je 0x18a256',
        0x18A1A5: 'jne 0x18a391', 0x18A1AE: 'call 0x22cf90', 0x18A1B9: 'call 0x32f500',
        0x18A1C0: 'je 0x18a391', 0x18A259: 'call 0x32f500', 0x18A260: 'je 0x18a38a',
        0x18A269: 'call 0x22a850', 0x18A274: 'call 0x32f500', 0x18A27B: 'je 0x18a391'})
    lookups += anchored(im, 0x183A30, {0x1840BB: 'call 0x232650', 0x1840C2: 'jne 0x184100',
        0x1840C7: 'call 0x232bd0', 0x1840CE: 'jne 0x184100', 0x1840D3: 'call 0xee90'})
    lookups += anchored(im, 0x184EB0, {0x1852C1: 'call 0x232650', 0x1852C8: 'jne 0x185332',
        0x1852CD: 'call 0x232bd0', 0x1852D4: 'jne 0x185332', 0x1852D9: 'call 0xee90'})
    return dict(commanderAssignment=dict(anchors=commander, store=0x2D2AAA, minimum=0,
                maximum=6000, dominates=True, directStores=stores, zeroRegisterWrites=zero_writes, nativeObserved=False,
                scope='the original assignment and an unchanged commander field; later helper effects, other writers and loading are not proved'),
                placementTerrainGate=dict(anchors=placement, dominates=True,
                scope='original279190 with these unchanged definitions and terrain; alternate construction and later field changes are not excluded'),
                sharedBuildingId=dict(anchors=lookups, tileOffset=0x1A,
                scope='failed generic admission and an intact matching building/city/gate table; stable fields; only a necessary condition, not final admission'))


def definition_masks(h):
    rows = []
    for index in range(31):
        pointer = h.game.q(h.raw + 0x73958 + index * 8)
        valid = bool(pointer and h.o.call32(0x32F500, pointer))
        row = dict(index=index, pointer=pointer, valid=valid)
        if valid:
            record = h.game.read(pointer, 0x68)
            row.update(category=record[0x2C], recordSha256=hashlib.sha256(record).hexdigest())
            if row['category'] in (2, 3):
                begin, end = struct.unpack_from('<QQ', record, 0x50)
                size = (end - begin) & 0xFFFFFFFFFFFFFFFF
                legacy = struct.unpack_from('<I', record, 0x3C)[0]
                encoded = [h.game.read(begin + 0x34 + i * 4, 1)[0] for i in range(4)] if size & ~3 == 76 else []
                mask = sum(((((b >> 1) | (b << 7)) & 255) ^ (index & 255)) << (8 * i)
                           for i, b in enumerate(encoded)) if encoded else legacy
                native = [h.o.call32(0x42B40, pointer, terrain) for terrain in (16, 17)]
                if native != [int(bool(mask & (1 << terrain))) for terrain in (16, 17)]:
                    raise ValueError('Original terrain admission and stored mask disagree')
                row.update(parameterBytes=size, legacyMask=legacy, encodedBytes=encoded,
                           decodedMask=mask, terrains=[16, 17], native=native)
        rows.append(row)
    return rows


def build(snapshot):
    im = Image(); facts = code_contracts(im); game = FrozenImage(snapshot, im, False)
    if not game.pool_snapshot['valid']:
        raise ValueError('Frozen pool is invalid')
    before = read_clock(game); h = Harness(game); definitions = definition_masks(h)
    dummy_validity = {name: h.o.call32(0x32F500, game.q(h.raw + offset))
                      for name, offset in (('city', 0xDD38), ('gate', 0xDED8))}
    if any(dummy_validity.values()):
        raise ValueError('Dummy city/gate validity contradicts the lookup contract')
    buildings = struct.unpack('<3001Q', game.read(h.raw + 0x6DA98, 3001 * 8))
    if not buildings[0] or any(value != buildings[0] + i * 32 for i, value in enumerate(buildings)):
        raise ValueError('Building table does not match pointer-index classification')
    tiles = struct.unpack('<48400Q', game.read(h.raw + 0xE270, 48400 * 8)); sites = []
    for index, pointer in enumerate(tiles):
        raw = game.read(pointer, 32)
        if raw[0x10] in (16, 17):
            sites.append(dict(hex=index, terrain=raw[0x10], buildingId=struct.unpack_from('<H', raw, 0x1A)[0]))
    selected = [row for row in definitions if row.get('category') in (2, 3)]
    if not selected:
        raise ValueError('No category2/3 definitions were audited')
    after = read_clock(game)
    return dict(utc=datetime.now(timezone.utc).isoformat(), snapshot=dict(file=str(snapshot), sha256=game.snapshot_sha256),
                clockBefore=before, clockAfter=after, clockStable=before == after,
                gameAccess=False, gameWrites=0, sourceFieldEdits=[], helperReplacements=[], codeSource=game.code_source,
                codeContracts=facts, definitions=definitions, dummyBaseValidity=dummy_validity,
                analyzerSha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                terrainMaskNativeCalls=len(selected) * 2,
                allCategory2Or3DefinitionsDenyExceptionTerrains=all(row['native'] == [0, 0] for row in selected),
                buildingTable=dict(count=3001, first=buildings[0], stride=32, verified=True), exceptionTerrainSites=sites,
                terrainSiteCounts=dict(Counter(str(row['terrain']) for row in sites)), nativeSearchCalls=0,
                globalCreationInvariantProved=False, loopTakenObserved=False,
                limits=['commander clamp is a scoped assignment fact, not a native whole-creator execution',
                        'normal placement exclusions require unchanged audited definitions and terrain',
                        'alternate creation, loading, later helper effects and field writers remain outside these scopes',
                        'no A* direction is newly observed and183ECB transitive mutation closure remains unproved'])


def main():
    parser = argparse.ArgumentParser(); parser.add_argument('--snapshot', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True); args = parser.parse_args()
    result = build(args.snapshot); args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print('commander assignment range0..6000; definition mask calls', result['terrainMaskNativeCalls'],
          'exception masks denied', result['allCategory2Or3DefinitionsDenyExceptionTerrains'],
          'A* calls', result['nativeSearchCalls'], flush=True)


if __name__ == '__main__': main()
