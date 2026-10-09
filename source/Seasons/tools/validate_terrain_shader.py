"""Independent material oracle and owned WARP tests; never accesses SAN14 or a browser."""
import argparse
import hashlib
import json
import math
from pathlib import Path
import struct
import subprocess

ROOT=Path(__file__).resolve().parents[1]
ASSETS=ROOT/'Assets'
NAMES=('sparse_grass','leafy_grass','leaves_forest_ground','winter_leaves','snow_02')
KINDS=('diff','nor_dx','rough')


def digest(data):
    return hashlib.sha256(data).hexdigest()


def smooth(low,high,value):
    t=max(0,min(1,(value-low)/(high-low)))
    return t*t*(3-2*t)


def normalize(vector):
    squared=sum(v*v for v in vector)
    return [v/math.sqrt(squared) for v in vector] if squared>1e-12 else [0,0,1]


def sampling(data,u,v,lod,kind):
    words=struct.unpack_from('<37I',data)
    levels=[];offset=148
    for mip in range(words[7]):
        width=max(1,words[4]>>mip);height=max(1,words[3]>>mip)
        levels.append((width,height,offset));offset+=width*height*4
    assert offset==len(data)
    def at_level(index):
        width,height,base=levels[index];x=u*width-.5;y=v*height-.5
        xi=math.floor(x);yi=math.floor(y);fx=x-xi;fy=y-yi;result=[0.,0.,0.]
        for dx,dy,weight in ((0,0,(1-fx)*(1-fy)),(1,0,fx*(1-fy)),(0,1,(1-fx)*fy),(1,1,fx*fy)):
            at=base+((yi+dy)%height*width+(xi+dx)%width)*4
            for channel,byte in enumerate((data[at+2],data[at+1],data[at])):
                value=byte/255
                if kind=='diff':value=value/12.92 if value<=.04045 else ((value+.055)/1.055)**2.4
                result[channel]+=value*weight
        return result
    low=math.floor(lod);fraction=lod-low;a=at_level(low);b=at_level(min(low+1,len(levels)-1))
    result=[x+(y-x)*fraction for x,y in zip(a,b)]
    if kind=='nor_dx':result=normalize([v*2-1 for v in result])
    return result


def climate_influence(station,month,day):
    following=month%12+1;fraction=(day-1)/30
    def season(m):return 0 if 3<=m<=5 else 1 if 6<=m<=8 else 2 if 9<=m<=11 else 3
    weights=[(1-fraction if season(month)==s else 0)+(fraction if season(following)==s else 0) for s in range(4)]
    def climate(key):return station[key][month-1]*(1-fraction)+station[key][following-1]*fraction
    temperature=climate('temperatureC');rain=climate('precipitationMm')
    # Artwork response from the existing contract, using unadjusted modern normals.
    return [weights[0]*smooth(4,18,temperature),weights[1],weights[2],weights[3]*(1-smooth(4,14,temperature))],\
        weights[3]*(1-smooth(0,8,temperature))*(.3+.7*min(1,rain/40))


def reference(inputs,layers):
    _,_,_,up_facing,*_=inputs
    contributions=inputs[4:8];coverage,eligibility,snow,_=inputs[8:12]
    strength,snow_strength,low,high=inputs[12:16]
    ground=[v*coverage*strength/max(1,sum(contributions)) for v in contributions]
    snow*=eligibility*snow_strength*smooth(low,high,up_facing)
    # Independent six-material convex weights, rather than the shader's two-phase lerps.
    weights=[(1-sum(ground))*(1-snow)]+[v*(1-snow) for v in ground]+[snow]
    materials=[(inputs[16:19],inputs[20:23],inputs[19])]+layers
    color=[sum(w*m[0][c] for w,m in zip(weights,materials)) for c in range(3)]
    roughness=sum(w*m[2] for w,m in zip(weights,materials))
    normal=[sum(w*m[1][c] for w,m in zip(weights,materials)) for c in range(3)]
    squared=sum(v*v for v in normal)
    normal=[v/math.sqrt(squared) for v in normal] if squared>1e-12 else inputs[20:23]
    return color+[roughness]+normal+[snow]+ground


def prepare(folder):
    folder.mkdir(parents=True,exist_ok=True)
    records={row['file']:row for row in json.loads((ASSETS/'Processed/manifest.json').read_text(encoding='utf-8'))}
    texture_bytes={};lines=['SeasonTerrainTextures\t1'];assets=[]
    for role,kind in enumerate(KINDS,1):
        for name in NAMES:
            filename=f'{name}_{kind}_1k.dds';path=ASSETS/'Processed'/filename;data=path.read_bytes();row=records[filename]
            assert digest(data)==row['sha256'] and row['license']=='CC0-1.0'
            assert row['mips']==11 and row['width']==1024 and row['height']==1024
            texture_bytes[name,kind]=data;lines.append(f'{role}\t{row["sha256"]}\t{path}')
            assets.append(dict(file=filename,sha256=row['sha256'],sourceUrl=row['sourceUrl'],license=row['license']))
    (folder/'textures.tsv').write_text('\n'.join(lines)+'\n',encoding='utf-8')
    stations=json.loads((ASSETS/'climate.json').read_text(encoding='utf-8'))['stations'];cases=[];cache={}
    lods=[float(m) for m in range(11)]+[3.5,9.5]
    # Binary-exact fractional texel positions isolate composition from subtexel coordinate quantization.
    # Wrap repeats, every whole mip and fractional mips are included on owned texture arrays.
    poses=((-0.125,.125),(.125,.5),(.5,.875),(1.125,.5))
    policies=((0,0,1,1,1),(1,1,1,0,0),(1,0,.7,1,1),(.5,1,.5,1,1),(1,1,.7,1,1),(1,1,1,.5,.75),(1,1,1,1,1))
    for station in stations:
        for month in range(1,13):
            for day in (1,15,30):
                ground,snow=climate_influence(station,month,day)
                for lod in lods:
                    for u,v in poses:
                        key=(u,v,lod)
                        if key not in cache:
                            cache[key]=[(sampling(texture_bytes[name,'diff'],u,v,lod,'diff'),
                                sampling(texture_bytes[name,'nor_dx'],u,v,lod,'nor_dx'),
                                sampling(texture_bytes[name,'rough'],u,v,lod,'rough')[0]) for name in NAMES]
                        coverage,eligibility,up,strength,snow_strength=policies[len(cases)%len(policies)]
                        values=[u,v,lod,up]+ground+[coverage,eligibility,snow,0,strength,snow_strength,.5,.9,.2,.3,.4,.7,0,0,1,0]
                        inputs=list(struct.unpack('<24f',struct.pack('<24f',*values)))
                        expected=reference(inputs,cache[key]);cases.append(struct.pack('<36f',*(inputs+expected)))
    assert len(cases)==13104
    data=struct.pack('<4I',0x53475443,1,len(cases),0)+b''.join(cases)
    (folder/'cases.bin').write_bytes(data)
    (folder/'fixture.json').write_text(json.dumps(dict(gameProcessAccess=False,caseCount=len(cases),
        layers=list(NAMES),sampler='MIN_MAG_MIP_LINEAR / WRAP / explicit SampleLevel',lods=lods,
        inputsUseModernNormals=True,historicalTemperatureOffset=False,authoredEligibilityAndSlopeOnly=True,
        gameCoordinatesOrMasksVerified=False,actualGamePassVerified=False,caseSha256=digest(data),assets=assets),indent=2)+'\n',encoding='utf-8')


def run_checks(folder):
    shader=ROOT/'Shaders/SeasonTerrainCheck.hlsl';executable=ROOT/'Native/TerrainShaderChecks.exe'
    def run(label,listing,cases,expected_reason=None):
        result=subprocess.run([str(executable),str(shader),str(listing),str(cases)],capture_output=True,text=True,timeout=60)
        (folder/(label+'.log')).write_text(result.stdout+result.stderr,encoding='utf-8')
        if expected_reason:
            assert result.returncode!=0 and expected_reason in result.stderr and 'deviceCreated=false' in result.stderr,(label,result.stdout,result.stderr)
            return dict(case=label,reason=expected_reason,rejectedBeforeDeviceCreation=True)
        assert result.returncode==0,(result.stdout,result.stderr)
        return json.loads(result.stdout)
    warp=run('warp',folder/'textures.tsv',folder/'cases.bin')
    data=(folder/'cases.bin').read_bytes();rejected=[]
    def reject(name,reason,offset=None,value=None,kind='f',payload=None,listing=None):
        path=folder/'rejected'/name;path.mkdir(parents=True,exist_ok=True)
        changed=bytearray(data if payload is None else payload)
        if offset is not None:struct.pack_into('<'+kind,changed,offset,value)
        file=path/'cases.bin';file.write_bytes(changed)
        list_path=folder/'textures.tsv'
        if listing is not None:list_path=path/'textures.tsv';list_path.write_text(listing,encoding='utf-8')
        rejected.append(run('rejected-'+name,list_path,file,reason))
    for name,offset,value,reason in (
        ('nonfinite-uv',0,float('nan'),'sampling parameters'),('excess-lod',8,11,'sampling parameters'),
        ('padding',44,1,'sampling parameters'),('reserved',92,.0000001,'sampling parameters'),
        ('nonfinite-ground',16,float('nan'),'contribution rejected'),('negative-ground',16,-.01,'contribution rejected'),
        ('snow-range',40,1.1,'artwork parameters'),('mask-range',32,-.1,'artwork parameters'),
        ('slope-order',56,.95,'artwork parameters'),('slope-too-narrow',56,.8999999,'artwork parameters'),('normal-zero',88,0,'normal rejected'),
        ('normal-nonfinite',80,float('inf'),'normal rejected'),('color-range',64,2,'color rejected'),
        ('roughness-range',76,-.1,'roughness rejected'),('nonfinite-expected',96,float('nan'),'expectation rejected')):
        reject(name,reason,16+offset,value)
    changed=bytearray(data);struct.pack_into('<4f',changed,16+16,.5,.5,.5,.5);reject('ground-sum','contribution sum',payload=changed)
    for name,index,value in (('zero-count',2,0),('excess-count',2,16385),('header-version',1,2)):
        reject(name,'cases header',index*4,value,'I')
    reject('truncated','cases header',payload=data[:-1]);reject('trailing','cases header',payload=data+b'\0')
    listing=(folder/'textures.tsv').read_text(encoding='utf-8');rows=listing.splitlines()
    wrong=list(rows);wrong[1]='2'+wrong[1][1:];reject('texture-role','role/hash/path',listing='\n'.join(wrong)+'\n')
    wrong=list(rows);wrong[1]=wrong[1][:2]+'0'*64+wrong[1][66:];reject('texture-hash','SHA256 mismatch',listing='\n'.join(wrong)+'\n')
    reject('texture-count','texture count',listing='\n'.join(rows[:-1])+'\n')
    reject('texture-trailing','Trailing terrain texture',listing=listing+rows[-1]+'\n')
    result=dict(gameProcessAccess=False,gameRenderModified=False,gamePassVerified=False,productionReady=False,
        warp=warp,rejections=rejected,sourceHashes={str(path.relative_to(ROOT)):digest(path.read_bytes()) for path in
            (shader,ROOT/'Shaders/SeasonTerrainComposition.hlsli',ROOT/'Native/TerrainShaderChecks.cpp',Path(__file__),executable)},
        caseSha256=digest(data))
    (folder/'results.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(warp=warp,rejectedBeforeDeviceCreation=len(rejected),gameProcessAccess=False)))


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--prepare-only',action='store_true');args=parser.parse_args();folder=args.output.resolve()
    prepare(folder)
    if not args.prepare_only:run_checks(folder)


if __name__=='__main__':main()
