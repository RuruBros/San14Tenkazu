"""Inspect generated files/readback only. Does not open the game or make GPU calls."""
import hashlib
import json
import pathlib
import struct
import subprocess
import numpy as np

ROOT = pathlib.Path(__file__).resolve().parents[1]
CACHE = ROOT.parent / 'Reverse' / '.cache' / 'season-safety' / '20261006-assets'


def sha(data):
    return hashlib.sha256(data).hexdigest()


def terrain():
    manifest = json.loads((ROOT / 'Assets/Processed/manifest.json').read_text())
    checks = []
    for item in manifest:
        raw = (ROOT / 'Assets/Processed' / item['file']).read_bytes()
        assert sha(raw) == item['sha256'], item['file']
        words = struct.unpack_from('<37I', raw)
        assert words[0] == 0x20534444 and words[1] == 124 and words[19] == 32
        assert words[21] == 0x30315844 and words[32] == (91 if item['format'].endswith('_SRGB') else 87)
        assert words[33:37] == (3, 0, 1, 0)
        assert words[7] == item['mips'] == 11 and words[3:5] == (item['height'], item['width'])
        w, h, offset = item['width'], item['height'], 148
        for level in range(item['mips']):
            n = w*h*4
            pixels = np.frombuffer(raw[offset:offset+n], np.uint8).reshape(h, w, 4)
            assert np.all(pixels[:, :, 3] == 255), (item['file'], level, 'alpha')
            assert np.array_equal(pixels[:, 0], pixels[:, -1]), (item['file'], level, 'x seam')
            assert np.array_equal(pixels[0], pixels[-1]), (item['file'], level, 'z seam')
            normal_error = None
            if '_nor_dx_' in item['file']:
                vectors = pixels[:, :, :3].astype(np.float64)/127.5-1
                normal_error = float(np.max(np.abs(np.linalg.norm(vectors, axis=2)-1)))
                assert normal_error <= .007, (item['file'], level, 'normal length')
            checks.append(dict(file=item['file'], level=level, width=w, height=h, normalLengthError=normal_error))
            offset += n
            w, h = max(1, w//2), max(1, h//2)
        assert offset == len(raw), (item['file'], 'unexpected DDS bytes')
    return dict(terrainMaps=len(manifest), validatedMips=len(checks), failures=0, checks=checks)


def filter_precision():
    raw = (ROOT / 'Assets/Processed/climate-monthly.dds').read_bytes()
    _, _, _, h, w = struct.unpack_from('<5I', raw)
    atlas = np.frombuffer(raw, '<f4', offset=148).reshape(12, h, w, 2)
    vectors = (CACHE / 'climate-vectors.bin').read_bytes()
    magic, version, count, _ = struct.unpack_from('<4I', vectors)
    assert (magic, version) == (0x53434154, 1)
    dtype = np.dtype([('position', '<f4', (2,)), ('months', '<u4', (2,)), ('fraction', '<f4'),
                      ('padding', '<f4', (3,)), ('weights', '<f4', (4,)), ('expected', '<f4', (9,))])
    records = np.frombuffer(vectors, dtype, offset=16)
    assert len(records) == count and dtype.itemsize == 84
    gpu = np.fromfile(CACHE / 'climate-vectors.bin.gpu.bin', '<f4').reshape(count, 12)[:, :2]
    assert np.isfinite(gpu).all()
    positions = np.clip(records['position'] * np.float32(1/204800), 0, 1)
    pixel = positions * np.array([w, h], dtype=np.float32) - np.float32(.5)
    floor = np.floor(pixel).astype(np.int32)
    fraction = pixel-floor
    x0 = np.clip(floor[:, 0], 0, w-1); x1 = np.clip(floor[:, 0]+1, 0, w-1)
    y0 = np.clip(floor[:, 1], 0, h-1); y1 = np.clip(floor[:, 1]+1, 0, h-1)
    checks = []
    for bits,mode in ((None,'none'),(8,'nearest'),(8,'floor'),(8,'ceil'),(9,'nearest'),(10,'nearest'),(12,'nearest'),(16,'nearest')):
        quantize = {'nearest':np.rint,'floor':np.floor,'ceil':np.ceil}
        f = fraction if bits is None else quantize[mode](fraction*2**bits)/2**bits
        layers = []
        for k in (0, 1):
            m = records['months'][:, k]
            a, b, c, d = atlas[m,y0,x0], atlas[m,y0,x1], atlas[m,y1,x0], atlas[m,y1,x1]
            top = a+(b-a)*f[:, 0, None]; bottom = c+(d-c)*f[:, 0, None]
            layers.append(top+(bottom-top)*f[:, 1, None])
        calculated = layers[0]+(layers[1]-layers[0])*records['fraction'][:, None]
        error = np.abs(calculated-gpu)
        checks.append(dict(subtexelFractionBits=bits, rounding=mode, maxTemperatureDifferenceC=float(error[:,0].max()),
                           maxPrecipitationDifferenceMm=float(error[:,1].max())))
    # Record the observed comparison; no production precision is inferred from the spec alone.
    return dict(vectors=count, comparisons=checks,
                specification='https://microsoft.github.io/DirectX-Specs/d3d/archive/D3D11_3_FunctionalSpec.htm')


def climate_contract():
    folder = ROOT/'Assets/Processed'
    manifest = json.loads((folder/'climate-manifest.json').read_text())
    source = (ROOT/'Assets/climate.json').read_bytes()
    dds = (folder/manifest['file']).read_bytes()
    assert sha(source) == manifest['sourceSha256'] and sha(dds) == manifest['sha256']
    words = struct.unpack_from('<37I', dds)
    assert words[32:37] == (16,3,0,12,0) and words[7] == 1
    h,w = words[3:5]
    assert (w,h) == (manifest['width'],manifest['height']) == (512,512)
    assert len(dds) == 148+w*h*12*8
    atlas = np.frombuffer(dds,'<f4',offset=148).reshape(12,h,w,2)
    assert np.isfinite(atlas).all()
    stations = json.loads(source)['stations']
    for month in range(12):
        for channel,key in enumerate(('temperatureC','precipitationMm')):
            values = [s[key][month] for s in stations]
            # Inverse-distance interpolation is convex; only float32 rounding is allowed here.
            assert atlas[month,:,:,channel].min() >= min(values)-1e-4
            assert atlas[month,:,:,channel].max() <= max(values)+1e-4
    return dict(arrayLayers=12,width=w,height=h,finiteSamples=int(atlas.size),failures=0,
                maxAnchorTemperatureErrorC=manifest['anchorValidation']['maxTemperatureErrorC'],
                maxAnchorPrecipitationErrorMm=manifest['anchorValidation']['maxPrecipitationErrorMm'])


def rejection_checks():
    exe = ROOT/'Native/ShaderChecks.exe'
    dds = ROOT/'Assets/Processed/climate-monthly.dds'
    vectors = CACHE/'climate-vectors.bin'
    shader = ROOT/'Shaders/SeasonClimateCheck.hlsl'
    malformed_dds = bytearray(dds.read_bytes()[:148]);struct.pack_into('<I',malformed_dds,128,87)
    bad_dds = CACHE/'invalid-climate-format.dds';bad_dds.write_bytes(malformed_dds)
    malformed_vector = bytearray(vectors.read_bytes());struct.pack_into('<I',malformed_vector,24,13)
    bad_vectors = CACHE/'invalid-month.bin';bad_vectors.write_bytes(malformed_vector)
    bad_shader = CACHE/'invalid-shader.hlsl';bad_shader.write_text('INVALID_SHADER_SOURCE',encoding='utf-8')
    tests = [('format',bad_dds,vectors,shader,'Season DDS format differs from role'),
             ('date',dds,bad_vectors,shader,'Malformed shader input'),
             ('shader',dds,vectors,bad_shader,'error X')]
    results = []
    for name,texture,reference,code,reason in tests:
        run = subprocess.run([str(exe),str(texture),str(reference),str(code)],capture_output=True,text=True)
        assert run.returncode != 0 and reason in run.stderr, (name,run.stdout,run.stderr)
        (CACHE/f'rejected-{name}.log').write_text(run.stdout+run.stderr,encoding='utf-8')
        results.append(dict(case=name,rejected=True,exitCode=run.returncode))
    return results


if __name__ == '__main__':
    result = dict(gameAccess=False, assets=terrain(), climate=climate_contract(),
                  filterPrecision=filter_precision(), rejectionChecks=rejection_checks())
    (CACHE/'asset-validation.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps({k:v for k,v in result.items() if k != 'assets'}, indent=2))
    print('Validated', result['assets']['terrainMaps'], 'terrain maps /', result['assets']['validatedMips'], 'mips')
