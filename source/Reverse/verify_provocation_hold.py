"""Independent held-provocation evidence verification; never access a game process."""
import argparse
import hashlib
import json
from pathlib import Path

def sha(data):
    return hashlib.sha256(data).hexdigest().upper()

def require(condition, reason):
    if not condition:
        raise ValueError(reason)

def verify(receipt, native, applied, finished, confirmation):
    plan = receipt['plan']
    original, held = bytes.fromhex(plan['beforeHex']), bytes.fromhex(plan['heldHex'])
    require(len(original) == len(held) == 0x200, 'Wrong unit record size')
    require(original[0x25] == 0 and held[0x25] >= 2 and held[0x3C] == 27
            and int.from_bytes(held[0x3E:0x40], 'little') == plan['sourceSlot'], 'Wrong Native provocation state')
    allowed = {0x25, 0x3C, 0x3E, 0x3F}
    require(all(a == b or offset in allowed for offset, (a, b) in enumerate(zip(original, held))), 'Neighbor changed by plan')
    require(native['before'].upper() == plan['beforeHex'].upper() and native['after'].upper() == plan['heldHex'].upper(), 'Native output differs')
    require(native['immunity'] == {'21': 0, '24': 0} and native['consumer']['matchingTargetPassed']
            and native['consumer']['wrongTargetRejected'], 'Native admission/target consumption missing')
    require(plan['nativeReplay'] and not plan['liveNativeCall'], 'Wrong execution classification')
    require(confirmation['confirmed'] and confirmation['observed'], 'User did not confirm visible state')
    expected_stages = [('before-apply', original), ('held-readback', held)]
    decreased = bytearray(held); decreased[0x25] -= 1
    expected_stages += [('decreased-readback', bytes(decreased)), ('undo-readback', held), ('restored-readback', original)]
    actual = [row for row in applied + finished if 'actualHex' in row]
    require([row['stage'] for row in actual] == [stage for stage, _ in expected_stages], 'Missing/out-of-order stages')
    for row, (stage, expected) in zip(actual, expected_stages, strict=True):
        require(bytes.fromhex(row['actualHex']) == bytes.fromhex(row['expectedHex']) == expected, stage + ': wrong raw bytes')
    require(not any(row.get('stage') == 'failed' for row in applied + finished), 'Execution failure present')
    records = {row['address']: row for row in finished if row.get('stage') == 'record-final'}
    require(len(records) == len(receipt['records']), 'Final record scope incomplete')
    for before in receipt['records']:
        row = records[before['address']]
        require(row['matches'] and row['beforeSha256'] == row['afterSha256'] == before['sha256'], 'Global record differs')
    arrays = {row['name']: row for row in finished if row.get('stage') == 'array-final'}
    require(set(arrays) == set(receipt['arrays']), 'Final world arrays incomplete')
    for name, digest in receipt['arrays'].items():
        require(arrays[name]['matches'] and arrays[name]['beforeSha256'] == arrays[name]['afterSha256'] == digest, 'World array differs')
    target = next(row for row in receipt['records'] if row['address'] == plan['address'])
    require(target['sha256'] == sha(original), 'Original target hash differs')
    return dict(validEvidence=True, userObserved=confirmation['observed'], durationSequence=[0, held[0x25], decreased[0x25], held[0x25], 0],
                targetKindSequence=[original[0x3C], held[0x3C], original[0x3C]],
                targetNumberSequence=[int.from_bytes(original[0x3E:0x40], 'little'), plan['sourceSlot'], int.from_bytes(original[0x3E:0x40], 'little')],
                fieldSizes=[1, 1, 2], recordsRestored=len(records), arraysRestored=len(arrays), neighborDifferences=0,
                nativeReplay=True, liveNativeCall=False, userVisibleStateConfirmed=True)

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--evidence', type=Path, required=True)
    args = parser.parse_args(); folder = args.evidence
    read = lambda path: json.loads(path.read_text(encoding='utf-8-sig'))
    rows = lambda path: [json.loads(line) for line in path.read_text(encoding='utf-8-sig').splitlines() if line.strip()]
    receipt = read(folder/'live-held/receipt.json')
    result = verify(receipt, read(folder/'native-plan-apply/native-result.json'), rows(folder/'live-held/evidence.jsonl'),
                    rows(folder/'live-finish/evidence.jsonl'), read(folder/'user-confirmation.json'))
    left, right = read(folder/'live-held/runner.json'), read(folder/'live-finish/runner.json')
    require(left['coreSha256'] == right['coreSha256'], 'Different Core binaries during apply/finish')
    result['coreSha256'] = left['coreSha256']
    result['applyLogSha256'] = sha((folder/'live-held/evidence.jsonl').read_bytes())
    result['finishLogSha256'] = sha((folder/'live-finish/evidence.jsonl').read_bytes())
    (folder/'independent-proof.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps(result, ensure_ascii=False))

if __name__ == '__main__':
    main()
