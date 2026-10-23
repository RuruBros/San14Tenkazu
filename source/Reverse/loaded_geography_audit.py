"""Offline replay of the native fixed-city deserializer for original/mod data.

Usage: python loaded_geography_audit.py --original <game-dir> --patch <data-dir> --output <directory>
Uses the existing frozen pages/image, never opens a live game. No mod code is imported.
The resulting native-cities.json can be passed to RenderChecks --loaded-geography.
"""
import argparse
import hashlib
import json
import struct
import sys
from pathlib import Path

from lwc_decode import decode


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--original', type=Path, required=True)
    parser.add_argument('--patch', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    # This audited loader replaces the game module with a denied implementation
    # before Oracle is imported, and verifies the saved image's hash and code pages.
    sys.argv.append('--tenkazu')
    from mod_patch_isolation import FrozenMemory, Oracle, HERE
    frozen = FrozenMemory(HERE / '.cache/frozen.pkl')
    oracle = Oracle(frozen)
    rows, inputs = [], []
    for kind, folder in [('stock', args.original), ('patch', args.patch)]:
        path = folder / '0010_KO/fixdataexce.s14'
        raw = path.read_bytes()
        if raw[:4] != struct.pack('<I', 0x66) or raw[4:19] != b'SN14SCEXVER0001':
            raise ValueError('Unsupported fixed-data header')
        data = decode(raw, raw.index(b'LWC\x1a'))
        # The supported serializer's city table has 52 entries, including its null
        # entry. Require a unique count/null-name header and the next table marker.
        pattern = struct.pack('<I', 52) + '무효'.encode('utf-16le') + b'\0' * 4
        candidates = [at + 4 for at in range(len(data)) if data.startswith(pattern, at)
                      and data[at + 4 + 52 * 53:at + 8 + 52 * 53] == struct.pack('<I', 2)]
        if len(candidates) != 1:
            raise ValueError(f'Fixed-city table is ambiguous: {candidates}')
        start = candidates[0]
        records = []
        for index in range(52):
            record = oracle.q(frozen.base + 0xDAA8 + index * 8)
            payload = data[start + 53 * index:start + 53 * (index + 1)]
            buf = oracle.alloc(53)
            oracle.poke(buf, payload)
            stream = oracle.alloc(0x100)
            state = bytearray(0x100)
            struct.pack_into('<I', state, 0x20, 1)
            for offset, value in [(0x30, buf), (0x38, buf + 53), (0x40, buf)]:
                struct.pack_into('<Q', state, offset, value)
            struct.pack_into('<I', state, 0x88, 0x66)
            oracle.poke(stream, bytes(state))
            oracle.call(0x23BE50, record, stream, read_after=[(record, 0x168), (stream, 0x100)])
            rec, after = oracle.after
            if (struct.unpack_from('<Q', after, 0x30)[0] != buf + 53
                    or struct.unpack_from('<I', after, 0x50)[0] != 0
                    or rec[0xCE] != payload[52] or rec[0x12:0x1A] != payload[:8]):
                raise ValueError(f'Native fixed-city replay differs: {kind}/{index}')
            records.append(dict(Id=index, Data=rec.hex(), State=rec[0xCE],
                                Name=rec[0x12:0x1A].decode('utf-16le').split('\0')[0]))
        digest = hashlib.sha256(raw).hexdigest()
        if hashlib.sha256(path.read_bytes()).hexdigest() != digest:
            raise ValueError('Input changed during audit')
        inputs.append(dict(path=str(path), sha256=digest, tableOffset=start))
        rows.append(dict(Kind=kind, Records=records))
    (args.output / 'native-cities.json').write_text(json.dumps(rows, ensure_ascii=False, indent=2), encoding='utf-8')
    summary = dict(passed=True, nativeRecords=104, gameAccess=False, inputs=inputs,
                   cityCounts={row['Kind']:sum(1 for rec in row['Records'][1:] if rec['State'] < 18) for row in rows},
                   codePages=len(frozen.code_page_comparison))
    if any(not row['equal'] for row in frozen.code_page_comparison):
        raise ValueError('Saved native code differs')
    (args.output / 'summary.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
    print(json.dumps(summary))


if __name__ == '__main__':
    main()
