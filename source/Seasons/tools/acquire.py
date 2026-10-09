"""Acquire the approved CC0 textures and preserve reproducible source evidence."""
import hashlib
import json
import pathlib
import urllib.request
from datetime import datetime, timezone

ROOT = pathlib.Path(__file__).resolve().parents[1]
ASSETS = ('sparse_grass', 'leafy_grass', 'leaves_forest_ground', 'winter_leaves', 'snow_02')


def fetch(url):
    request = urllib.request.Request(url, headers={'User-Agent': 'San14Tenkazu/1.0 (seasonal terrain)'})
    with urllib.request.urlopen(request, timeout=60) as response:
        return response.read()


def main():
    folder = ROOT / 'Assets' / 'Sources'
    folder.mkdir(parents=True, exist_ok=True)
    rows = []
    license_bytes = fetch('https://polyhaven.com/license')
    (folder / 'polyhaven-license.html').write_bytes(license_bytes)
    for asset in ASSETS:
        metadata_url = 'https://api.polyhaven.com/files/' + asset
        metadata_bytes = fetch(metadata_url)
        (folder / (asset + '-files.json')).write_bytes(metadata_bytes)
        metadata = json.loads(metadata_bytes)
        for kind in ('Diffuse', 'nor_dx', 'Rough'):
            item = metadata[kind]['1k']['png']
            name = item['url'].rsplit('/', 1)[-1]
            path = folder / name
            raw = path.read_bytes() if path.exists() else fetch(item['url'])
            if len(raw) != item['size'] or hashlib.md5(raw).hexdigest() != item['md5']:
                raise RuntimeError('Official source size/MD5 differs: ' + name)
            path.write_bytes(raw)
            rows.append(dict(asset=asset, map=kind, file=name, bytes=len(raw),
                             url=item['url'], source='https://polyhaven.com/a/' + asset,
                             license='CC0-1.0', sha256=hashlib.sha256(raw).hexdigest()))
            print(name, len(raw), flush=True)
    manifest = dict(acquiredUtc=datetime.now(timezone.utc).isoformat(), assets=rows,
                    licenseUrl='https://polyhaven.com/license',
                    licenseSha256=hashlib.sha256(license_bytes).hexdigest())
    (folder.parent / 'sources.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')


if __name__ == '__main__':
    main()
