"""Generate/check the native copy of the authored source catalog. No game access."""
import argparse
import hashlib
import json
from pathlib import Path


def native(catalog):
    records = catalog['assets']
    if catalog['version'] != 1 or len(records) != 26:
        raise ValueError('Catalog size/version differs')
    if len({r['ktid'] for r in records}) != len(records):
        raise ValueError('Duplicate material identifier')
    lines = ['#pragma once', '#include <array>',
             'struct TreeAsset {uint32_t ktid,width,height,mips,format,bytes,pixels,policy,green,autumn,branchRule;std::array<uint32_t,8> counts;const char* hash;};',
             'inline constexpr TreeAsset TreeAssets[]={']
    for r in records:
        dimensions = [(max(1, r['width'] >> m), max(1, r['height'] >> m)) for m in range(r['mips'])]
        pixels = sum(w*h for w, h in dimensions)
        size = sum(max(1, (w+3)//4)*max(1, (h+3)//4)*(8 if r['format'] == 71 else 16) for w, h in dimensions)
        if r['pixels'] != pixels or r['bytes'] != size or not 1 <= len(r['counts']) <= 8:
            raise ValueError('Invalid bounded source dimensions')
        for key in ('green', 'autumn'):
            reference = records[r[key]]
            if any(reference[k] != r[k] for k in ('width', 'height', 'mips')):
                raise ValueError('Reference texture coordinate layout differs')
        numbers = [int(r['ktid'], 16)] + [r[k] for k in ('width', 'height', 'mips', 'format', 'bytes', 'pixels', 'policy', 'green', 'autumn', 'branchRule')]
        counts = r['counts'] + [0]*(8-len(r['counts']))
        lines.append('    {' + ','.join(map(str, numbers)) + ',{' + ','.join(map(str, counts)) + '},"' + r['hash'] + '"}, // ' + r['ktid'])
    lines += ['};', f'inline constexpr uint32_t TreeSourceBytes={sum(r["bytes"] for r in records)};',
              f'inline constexpr uint32_t TreeMaskBytes={sum(r["pixels"]*8 for r in records)};',
              'inline bool treeCount(uint32_t count){if(!count)return false;for(const auto& a:TreeAssets)for(auto c:a.counts)if(c==count)return true;return false;}',
              'inline bool treeAssetCount(uint32_t index,uint32_t count){for(auto c:TreeAssets[index].counts)if(c&&c==count)return true;return false;}']
    return '\n'.join(lines) + '\n'


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1] / 'Seasons'
    payload = (root / 'Assets/tree-catalog.json').read_bytes()
    expected = native(json.loads(payload))
    output = root / 'Native/SeasonTreeCatalog.h'
    if args.check:
        if output.read_text(encoding='utf-8') != expected:
            raise ValueError('Native and managed tree catalogs differ')
    else:
        output.write_text(expected, encoding='utf-8')
    print(json.dumps({'gameAccess': False, 'catalogSha256': hashlib.sha256(payload).hexdigest(), 'matched': True}))
