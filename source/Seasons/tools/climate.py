"""Acquire JMA monthly normals; never fill missing observations or alter history."""
import hashlib
import html
import json
import pathlib
import re
from datetime import datetime, timezone
from acquire import fetch, ROOT

# Rendering anchors are representative modern cities, not reconstructions of ancient weather.
# Game coordinates are the repository's recorded city castle centres (MapTerrain.CityTiles).
STATIONS = (
    (54511, 'BEIJING', 3, '계', 'Beijing metropolitan area'),
    (53772, 'TAIYUAN', 4, '진양', 'Taiyuan metropolitan area'),
    (57036, "XI'AN", 20, '장안', 'Xian metropolitan area'),
    (52679, 'WUWEI', 24, '무위', 'Wuwei metropolitan area'),
    (58238, 'NANJING', 25, '건업', 'Nanjing metropolitan area'),
    (59287, 'GUANGZHOU', 30, '남해', 'Guangzhou metropolitan area'),
    (48820, 'HA NOI', 31, '교지', 'Hanoi metropolitan area'),
    (57494, 'WUHAN', 32, '강하', 'Wuhan metropolitan area'),
    (57679, 'CHANGSHA', 36, '장사', 'Changsha metropolitan area'),
    (56294, 'CHENGDU', 44, '성도', 'Chengdu metropolitan area'),
    (56778, 'KUNMING', 46, '운남', 'Kunming metropolitan area'),
)


def parse(raw):
    text = raw.decode('utf-8')
    info = re.search(r'<div id="info">(.*?)</div>', text, re.S | re.I)
    if not info: raise ValueError('JMA station metadata is missing')
    plain = html.unescape(re.sub('<[^>]+>', '', info[1]))
    location = re.search(r'Lat\.:\s*([\d.]+)\s*°([NS])\s*/\s*Lon\.:\s*([\d.]+)°([EW]).*?Height:\s*(\d+)', plain, re.S)
    if not location: raise ValueError('JMA coordinates are missing')
    latitude, north, longitude, east, height = location.groups()
    table = re.search(r'<table class="list">(.*?)</table>', text, re.S | re.I)
    if not table: raise ValueError('JMA normal table is missing')
    normals = {}
    for row in re.findall('<tr[^>]*>(.*?)</tr>', table[1], re.S | re.I):
        cells = [html.unescape(re.sub('<[^>]+>', '', c)).strip()
                 for c in re.findall('<t[dh][^>]*>(.*?)</t[dh]>', row, re.S | re.I)]
        if len(cells) == 3 and cells[0].isdigit():
            month, temp, rain = int(cells[0]), float(cells[1]), float(cells[2])
            if month in normals or not -60 <= temp <= 60 or not 0 <= rain <= 3000:
                raise ValueError('Invalid or duplicate JMA normal')
            normals[month] = [temp, rain]
    if sorted(normals) != list(range(1, 13)): raise ValueError('Twelve complete monthly normals are required')
    return dict(stationName=plain.split(' -')[0].strip(), latitude=float(latitude)*(1 if north=='N' else -1),
                longitude=float(longitude)*(1 if east=='E' else -1), elevationM=int(height),
                temperatureC=[normals[m][0] for m in range(1,13)],
                precipitationMm=[normals[m][1] for m in range(1,13)])


def main():
    source = ROOT / 'Assets' / 'Sources'
    source.mkdir(parents=True, exist_ok=True)
    rows, unavailable = [], []
    city_source = (ROOT.parents[0] / 'MapTerrain.cs').read_text(encoding='utf-8-sig')
    city_tiles = {int(i): int(tile) for tile,i in re.findall(r'(\d+), // (\d+) ', city_source)}
    for station, expected, city, label, area in STATIONS:
        url = f'https://www.data.jma.go.jp/tcc/tcc/products/climate/climatview/graph_mkhtml_nrm.php?m=1&n={station}'
        path = source / f'jma-{station}.html'
        try:
            raw = path.read_bytes() if path.exists() else fetch(url)
            path.write_bytes(raw)
            data = parse(raw)
            # Official names may contain spaces or apostrophes; normalize those only.
            normalized = lambda s: re.sub(r"[^A-Z]", '', s.upper())
            if normalized(expected) != normalized(data['stationName']):
                raise ValueError('Station identity differs: '+data['stationName'])
            tile=city_tiles[city];column=tile%220;row=tile//220
            x=468*(1.5*column+1)+37126
            z=468*(3**0.5)*(row+(1 if column%2==0 else 0.5))+12930
            rows.append(dict(stationId=station,cityId=city,cityName=label,cityTile=tile,
                             gameX=x,gameZ=z,**data,source=url,sourceFile=path.name,
                             sourceSha256=hashlib.sha256(raw).hexdigest(),
                             correspondence=f'Representative modern {area}; anchored to the recorded game city centre. No ancient temperature correction.'))
            print(station,data['stationName'],data['temperatureC'],flush=True)
        except Exception as e:
            unavailable.append(dict(stationId=station,source=url,error=str(e)))
            print('Unavailable',station,str(e),flush=True)
    result=dict(acquiredUtc=datetime.now(timezone.utc).isoformat(),provider='Japan Meteorological Agency ClimatView',
                interpretation='Modern monthly normals used directly as visual inputs; not ancient climate or actual weather.',
                stations=rows,unavailable=unavailable)
    (ROOT/'Assets'/'climate.json').write_text(json.dumps(result,indent=2,ensure_ascii=False),encoding='utf-8')
    if len(rows)<5:raise RuntimeError('At least five verified regional anchors are required')


if __name__=='__main__':main()
