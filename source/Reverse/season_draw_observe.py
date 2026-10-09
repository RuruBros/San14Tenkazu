"""Bounded engine Draw observation through the authored native DLL.

Default is read-only preflight. --live loads this diagnostic module, whose worker
detaches the code hook on its own deadline. No retired command probe, command
table patch, mutable D3D11 Draw vtable patch or RenderDoc injection is used.
"""
import argparse
import ctypes as c
from ctypes import wintypes as w
import hashlib
import json
from pathlib import Path
import struct
import time

from game import Game, k32, psapi, _MBI
from season_watch import read_snapshot
from season_shader_snapshot import EXE_HASH
from season_trace_contract import check_identity
from image import Image

MAGIC, VERSION, CAPACITY, TARGET = 0x53444f31, 2, 64, 0x960f90
IMAGE_HASH='aa85c23361c67cd085f3101d2ecb9bf1579f79d46d081effa0184044f1c48e59'
CODE_HASH='782647f22a91f452bddacbef5c8d341bef5f2bea7d22584162ae1a0c213fe41b'

class Binding(c.Structure):
    _fields_=[('object',c.c_uint64),('resource',c.c_uint64)]+[(n,c.c_uint32) for n in
        ('dimension','format','width','height','mipLevels','arraySize')]
class Constant(c.Structure):
    _fields_=[('buffer',c.c_uint64),('cpuSource',c.c_uint64),('byteWidth',c.c_uint32),
              ('copiedBytes',c.c_uint32),('bytes',c.c_ubyte*256)]
class Record(c.Structure):
    _fields_=[(n,c.c_uint64) for n in ('sequence','tick','driver','context','device','nativeDraw',
        'psOwner','vsOwner','ps','vs')]+[(n,c.c_uint32) for n in ('thread','contextType','creationFlags',
        'start','count','counterBefore','counterAfter','complete')]+[('instances',c.c_uint32),('drawKind',c.c_uint32),('captureEndTick',c.c_uint64)]+[
        ('views',Binding*12),('targets',Binding*4),('depth',Binding),
        ('psConstants',Constant*14),('vsConstants',Constant*14),('viewport',c.c_float*6)]
class Request(c.Structure):
    _fields_=[(n,c.c_uint32) for n in ('magic','version','size','durationMs')]+[
        (n,c.c_uint64) for n in ('moduleBase','context','device')]+[(n,c.c_int32) for n in
        ('cancel','status','error','records','active','skipped','rejected','restoreAttempts')]+[
        (n,c.c_uint32) for n in ('codeRestored','protectionRestored','callbacksDrained','referencesReleased',
        'originalProtection','finalProtection')]+[(n,c.c_uint64) for n in ('target','trampoline','workerThread')]+[
        (n,c.c_uint32) for n in ('failedThread','failedStage','failedThreadExit','failedThreadExitRead')]+[('rows',Record*64)]
assert c.sizeof(Binding)==40 and c.sizeof(Constant)==280 and c.sizeof(Record)==8672 and c.sizeof(Request)==555144
class TextureRead(c.Structure):
    _fields_=[(n,c.c_uint32) for n in ('magic','version','size','slot')]+[(n,c.c_uint64) for n in
        ('psOwner','view','resource','output')]+[(n,c.c_uint32) for n in
        ('capacity','state','bytes','width','height','mipLevels','format','arraySize')]+[
        ('hresult',c.c_int32),('reserved',c.c_uint32)]+[(n,c.c_uint32) for n in
        ('sampleCount','sampleQuality','usage','bindFlags','cpuAccessFlags','miscFlags','viewFormat','viewDimension','mostDetailedMip','viewMipLevels')]
assert c.sizeof(TextureRead)==128
TEXTURE_BYTES=22369616
class TerrainTrial(c.Structure):
    _fields_=[(n,c.c_uint32) for n in ('magic','version','size','year','month','day','renderThread','padding')]+[
        (n,c.c_uint64) for n in ('psOwner','ps','view','resource','input')]+[(n,c.c_uint32) for n in
        ('bytes','state','swaps','restores','originalBindingsRestored','gpuResourcesReleased','prepareCodeRestored','prepareProtectionRestored')]+[
        ('newView',c.c_uint64),('newResource',c.c_uint64),('hresult',c.c_int32),('changedBlocks',c.c_uint32)]
assert c.sizeof(TerrainTrial)==128
class TreeMaterialTrial(c.Structure):
    _fields_=[(n,c.c_uint32) for n in ('magic','version','size','year','month','day','renderThread','padding')]+[
        (n,c.c_uint64) for n in ('psOwner','ps','view','resource','output')]+[(n,c.c_uint32) for n in
        ('bytes','state','swaps','restores','originalBindingsRestored','gpuResourcesReleased','prepareCodeRestored','prepareProtectionRestored')]+[
        ('newView',c.c_uint64),('newResource',c.c_uint64),('hresult',c.c_int32),('changedPixels',c.c_uint32)]+[
        (n,c.c_uint64) for n in ('vsOwner','vs','inputLayout')]+[(n,c.c_uint32) for n in
        ('indexCount','viewFormat','newViewFormat','width','height','mips','alphaPixels','outputBytes','failureStage','gpuReadbackComplete')]
assert c.sizeof(TreeMaterialTrial)==192
TREE_OUTPUT_BYTES=1398016
class MeshBlob(c.Structure):
    _fields_=[('resource',c.c_uint64)]+[(n,c.c_uint32) for n in ('kind','slot','offset','bytes','format','width','height','mipLevels','arraySize',
        'usage','bindFlags','miscFlags','stride','bindingOffset','structureStride','sourceOffset')]
class MeshRead(c.Structure):
    _fields_=[(n,c.c_uint32) for n in ('magic','version','size','indexCount','renderThread','requireColor','state','hresult','capacity','bytes','blobCount',
        'indexFormat','indexOffset','topology','failureStage','failureKind','failureSlot','indexStart','instances','maxIndex')]+[(n,c.c_uint64) for n in ('view','resource','output','inputLayout')]+[('blobs',MeshBlob*12)]
assert c.sizeof(MeshBlob)==72 and c.sizeof(MeshRead)==976
class MeshReadBatch(c.Structure):
    _fields_=[(n,c.c_uint32) for n in ('magic','version','size','count')]+[('items',MeshRead*3)]
assert c.sizeof(MeshReadBatch)==2944


def mesh_read_requests(rows, counts, require_color):
    """Select exact recorded identities; index counts never authenticate assets."""
    if not 1<=len(counts)<=3 or len(set(counts))!=len(counts) or any(n not in (918,1104,1179) for n in counts):
        raise ValueError('Select one to three distinct observed Mesh counts: 918, 1104, 1179')
    result=[]
    for count in counts:
        candidates=[r for r in rows if r['count']==count and r['start']==0 and r['views'][0]['object']!='0x0']
        identities={(r['views'][0]['object'],r['views'][0]['resource'],r['thread']) for r in candidates}
        if len(identities)!=1:raise ValueError('One exact resource/thread candidate per Mesh count is required')
        row=candidates[0];v=row['views'][0]
        dimensions=(512,256,7) if count==918 else (512,512,8)
        if (v['format'],v['width'],v['height'],v['mipLevels'],v['arraySize'])!=(97,*dimensions,1):
            raise ValueError('Observed BC7 descriptor differs')
        result.append(MeshRead(magic=0x534d5231,version=2,size=c.sizeof(MeshRead),indexCount=count,renderThread=row['thread'],
            requireColor=int(require_color),view=int(v['object'],16),resource=int(v['resource'],16),capacity=4*1024*1024))
    if len({r.renderThread for r in result})!=1:raise ValueError('One render thread is required for the bounded batch')
    return result

def digest(data):return hashlib.sha256(data).hexdigest()

def export_rva(data,name):
    """Read a named PE export without loading/executing the diagnostic DLL locally."""
    pe=struct.unpack_from('<I',data,0x3c)[0]
    if data[:2]!=b'MZ' or data[pe:pe+4]!=b'PE\0\0' or struct.unpack_from('<H',data,pe+4)[0]!=0x8664:
        raise ValueError('Expected an x64 PE DLL')
    sections,opt=struct.unpack_from('<H',data,pe+6)[0],struct.unpack_from('<H',data,pe+20)[0]
    if struct.unpack_from('<H',data,pe+24)[0]!=0x20b:raise ValueError('Expected PE32+')
    def offset(rva):
        for i in range(sections):
            vs,va,size,raw=struct.unpack_from('<4I',data,pe+24+opt+i*40+8)
            if va<=rva<va+size:return raw+rva-va
        raise ValueError('Export points outside file-backed sections')
    rva,size=struct.unpack_from('<II',data,pe+24+112)
    row=struct.unpack_from('<IIHH7I',data,offset(rva))
    count,names,ordinals,functions=row[7],row[9],row[10],row[8]
    if count>4096:raise ValueError('Too many DLL exports')
    for i in range(count):
        nrva=struct.unpack_from('<I',data,offset(names)+i*4)[0];at=offset(nrva)
        nm=data[at:at+256].split(b'\0')[0].decode('ascii')
        if nm!=name:continue
        ordinal=struct.unpack_from('<H',data,offset(ordinals)+i*2)[0]
        result=struct.unpack_from('<I',data,offset(functions)+ordinal*4)[0]
        if rva<=result<rva+size:raise ValueError('Forwarded observer export is not supported')
        offset(result);return result
    raise ValueError('Observer export is missing')

def modules(g):
    result={};mods=(c.c_void_p*1024)();needed=w.DWORD()
    if not psapi.EnumProcessModulesEx(c.c_void_p(g.h),mods,c.sizeof(mods),c.byref(needed),3):raise c.WinError(c.get_last_error())
    if needed.value>c.sizeof(mods):raise ValueError('Module list exceeded its bounded buffer')
    for address in mods[:needed.value//8]:
        name=c.create_unicode_buffer(32768)
        if not psapi.GetModuleFileNameExW(c.c_void_p(g.h),c.c_void_p(address),name,len(name)):raise c.WinError(c.get_last_error())
        result[str(Path(name.value)).lower()]=address
    return result

def remote_load_library(g):
    # Resolve the target process's public export. Its existing API detour is part
    # of that process's loader; do not assume local code equality or bypass it.
    candidates=[(path,base) for path,base in modules(g).items() if Path(path).name.lower()=='kernel32.dll']
    if len(candidates)!=1:raise ValueError('Unique remote KERNEL32 module is required')
    path,base=candidates[0];data=Path(path).read_bytes();rva=export_rva(data,'LoadLibraryW');address=base+rva
    code=g.read(address,64)
    if code is None or g.region(address)[3] not in (0x20,0x40,0x80):raise ValueError('Remote loader export is not readable/executable')
    row=dict(module=path,moduleBase=hex(base),moduleFileSha256=digest(data),exportRva=hex(rva),
             entry=hex(address),entryBytes=code.hex(),existingEntryPreserved=True)
    if code[0]==0xe9:
        jump=address+5+struct.unpack_from('<i',code,1)[0]
        row['existingJump']=hex(jump);row['relaySnapshots']=[];visited={address};owner=None
        loaded=modules(g)
        # The existing Steam relay can be E9 rel32 or FF25 + inline address.
        # Follow only these bounded shapes for validation, never for invocation.
        for _ in range(4):
            if jump in visited:raise ValueError('Existing loader relay cycle')
            visited.add(jump);thunk=g.read(jump,32);protect=g.region(jump)[3]
            if thunk is None or protect not in (0x20,0x40,0x80):raise ValueError('Existing loader relay is not readable/executable')
            row['relaySnapshots'].append(dict(address=hex(jump),bytesHex=thunk.hex(),protection=protect))
            for module,module_base in loaded.items():
                header=g.read(module_base,4096)
                if header is None or header[:2]!=b'MZ':continue
                pe=struct.unpack_from('<I',header,0x3c)[0]
                if pe+24+60>len(header):continue
                size=struct.unpack_from('<I',header,pe+24+56)[0]
                if module_base<=jump<module_base+size:owner=(module,module_base);break
            if owner is not None:break
            if thunk[0]==0xe9:jump+=5+struct.unpack_from('<i',thunk,1)[0]
            elif thunk[:6]==b'\xff\x25\0\0\0\0':jump=struct.unpack_from('<Q',thunk,6)[0]
            else:raise ValueError('Existing loader relay shape is unrecognized')
        if owner is None:raise ValueError('Existing loader jump has no loaded executable owner')
        owner_hash=digest(Path(owner[0]).read_bytes())
        if Path(owner[0]).name.lower()!='gameoverlayrenderer64.dll' or owner_hash!='59057f983ae6f02b7eb2495bf65e0430129dd81dff8d9628c764bd1a07e23fee' or jump-owner[1]!=0xad730:
            raise ValueError('Existing loader hook owner differs from the recorded Steam module/function')
        row['existingJumpBytes']=row['relaySnapshots'][0]['bytesHex']
        row.update(existingDispatch=hex(jump),existingJumpModule=owner[0],existingJumpModuleBase=hex(owner[1]),
                   existingJumpModuleFileSha256=owner_hash)
    return address,code,row

def bind(row):
    return {name:(hex(value) if name in ('object','resource') else value) for name,_ in row._fields_ for value in [getattr(row,name)]}
def constant(row):
    result=dict(buffer=hex(row.buffer),cpuSource=hex(row.cpuSource),byteWidth=row.byteWidth,copiedBytes=row.copiedBytes,
                source='Verified engine CPU upload shadow, not GPU readback')
    if not 0<=row.copiedBytes<=256:raise ValueError('Native constant length is invalid')
    result['bytesHex']=bytes(row.bytes[:row.copiedBytes]).hex();return result
def record(row):
    result={name:hex(getattr(row,name)) for name in ('driver','context','device','nativeDraw','psOwner','vsOwner','ps','vs')}
    result.update({name:getattr(row,name) for name in ('sequence','tick','thread','contextType','creationFlags','start','count','counterBefore','counterAfter','complete','instances','drawKind','captureEndTick')})
    result['nativeDispatchPointerAtReceipt']=result.pop('nativeDraw')
    result.update(views=[bind(x) for x in row.views],targets=[bind(x) for x in row.targets],depth=bind(row.depth),
                  psConstants=[constant(x) for x in row.psConstants],vsConstants=[constant(x) for x in row.vsConstants],
                  viewport=list(row.viewport));return result

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--build',required=True,type=Path);parser.add_argument('--out',required=True,type=Path)
    parser.add_argument('--live',action='store_true');parser.add_argument('--milliseconds',type=int,default=100)
    parser.add_argument('--indexed',action='store_true',help='Observe the verified six-argument engine DrawIndexed entry')
    parser.add_argument('--instanced',action='store_true',help='Observe the verified engine DrawIndexedInstanced entry')
    parser.add_argument('--mesh',action='store_true',help='Observe the separately verified seven-argument engine DrawIndexedInstanced entry')
    parser.add_argument('--color',action='store_true',help='With --instanced, select draws with a non-null color RT at slot 0')
    parser.add_argument('--texture-from',type=Path,help='Verified current color Draw receipt directory; read only its land texture at PS slot 4')
    parser.add_argument('--texture-slot',type=int,choices=(3,4),default=4,help='Select original land control (3) or base color (4) for bounded GPU readback')
    parser.add_argument('--trial-from',type=Path,help='Completed original GPU readback receipt directory; one paused April land pass with own shader and automatic restoration')
    parser.add_argument('--mesh-read-from',type=Path,help='Completed Mesh Draw receipt; one matching texture and IA/constant GPU resource readback')
    parser.add_argument('--mesh-counts',type=int,nargs='+',default=[918],help='With --mesh-read-from, one to three recorded counts; a single bounded one-shot lease')
    parser.add_argument('--tree-trial-from',type=Path,help='Completed GPU resource and original asset identity; bounded RGB-only tree trial with alpha verification')
    args=parser.parse_args()
    if sum((args.indexed,args.instanced,args.mesh))>1:raise ValueError('Select one engine path per bounded observation')
    if args.color and not (args.instanced or args.mesh):raise ValueError('--color requires --instanced or --mesh')
    if args.texture_from and not (args.color and args.instanced):raise ValueError('--texture-from requires --color --instanced')
    if args.texture_slot!=4 and (not args.texture_from or args.trial_from):raise ValueError('Control readback requires --texture-from without a material trial')
    if args.mesh_read_from and not args.mesh:raise ValueError('--mesh-read-from requires --mesh')
    if args.mesh_counts!=[918] and not args.mesh_read_from:raise ValueError('--mesh-counts requires --mesh-read-from')
    if not 50<=args.milliseconds<=(30000 if args.trial_from or args.tree_trial_from else 3000 if args.mesh_read_from else 500):raise ValueError('Duration exceeds this diagnostic mode limit')
    if args.trial_from and not args.texture_from:raise ValueError('--trial-from requires --texture-from color Draw evidence')
    if args.tree_trial_from and (not (args.mesh and args.color) or args.texture_from or args.trial_from or args.mesh_read_from):
        raise ValueError('--tree-trial-from requires only --mesh --color and its own completed resource evidence')
    args.out.mkdir(parents=True,exist_ok=False)
    g=Game();baseline=read_snapshot(g);image=Image()
    if digest(image.img)!=IMAGE_HASH:raise ValueError('Saved engine image differs')
    target,size,code_hash=(0x960e50,52,'4a1653de5006945e8945504e51158704032d997f08c4c2b03ed0b8a7ff705fdf') if args.indexed else (TARGET,70,CODE_HASH)
    if args.instanced:target,size,code_hash=0x960ee0,82,'dae9d45952c317094fd857fbce61bfd7e3f40696404ac5cfb213d0569a4a18b2'
    if args.mesh:target,size,code_hash=0x960e90,67,'ed619f25c921d2e81ce43933df8798febdac7bdd432a3378c4163fc562c8f5ff'
    code=image.img[target:target+size]
    if digest(code)!=code_hash or g.read(g.exe+target,size)!=code:raise ValueError('Live engine Draw code differs')
    exe=c.create_unicode_buffer(32768)
    if not psapi.GetModuleFileNameExW(c.c_void_p(g.h),c.c_void_p(g.exe),exe,len(exe)):raise c.WinError(c.get_last_error())
    if digest(Path(exe.value).read_bytes())!=EXE_HASH:raise ValueError('Game executable hash differs')
    texture=None
    mesh_read=None
    mesh_items=[]
    if args.mesh_read_from:
        prior=json.loads((args.mesh_read_from/'audit.json').read_text());check_identity(prior['baseline'],baseline,check_clock=True)
        if not prior['actualDrawDispatched'] or not prior['independentCodeAndProtectionReadback']:raise ValueError('Prior Mesh Draw/recovery missing')
        mesh_items=mesh_read_requests(json.loads((args.mesh_read_from/'draws.json').read_text()),args.mesh_counts,args.color)
        mesh_read=mesh_items[0] if len(mesh_items)==1 else MeshReadBatch(magic=0x534d4231,version=1,size=c.sizeof(MeshReadBatch),count=len(mesh_items))
    if args.texture_from:
        prior=json.loads((args.texture_from/'audit.json').read_text())
        check_identity(prior['baseline'],baseline,check_clock=True)
        if not prior['actualDrawDispatched'] or not prior['independentCodeAndProtectionReadback']:raise ValueError('Prior Draw or recovery was not verified')
        row=json.loads((args.texture_from/'draws.json').read_text())[0]
        metadata=json.loads((args.texture_from/'bound-shader-metadata.json').read_text())
        owners=[entry for entry in metadata['entries'] if entry['stage']=='ps' and entry['owner']==row['psOwner']]
        if len(owners)!=1 or not any(r['name']=='sLandAlbedoOcclusion' and r['kind']==2 and r['slot']==4 for r in owners[0]['metadata']['resources']):
            raise ValueError('A recognized land texture binding is required')
        v=row['views'][args.texture_slot]
        expected=(27,2049,2049,1,1) if args.texture_slot==3 else (76,4096,4096,11,1)
        if (v['format'],v['width'],v['height'],v['mipLevels'],v['arraySize'])!=expected:raise ValueError('Prior texture descriptor differs')
        texture=TextureRead(magic=0x53545231,version=2,size=c.sizeof(TextureRead),slot=args.texture_slot,
            psOwner=int(row['psOwner'],16),view=int(v['object'],16),resource=int(v['resource'],16),capacity=16793604 if args.texture_slot==3 else TEXTURE_BYTES)
    trial=None;trial_input=None;prepare_code=None;prepare_protection=None
    if args.trial_from:
        evidence=json.loads((args.trial_from/'audit.json').read_text());check_identity(evidence['baseline'],baseline,check_clock=True)
        t=evidence['textureRead'];trial_input=(args.trial_from/'original-land-bc3-mips.bin').read_bytes()
        if not t['gpuReadbackCompleted'] or len(trial_input)!=TEXTURE_BYTES or digest(trial_input)!=t['sha256'] or t['view']!=texture.view or t['resource']!=texture.resource:
            raise ValueError('Completed original texture evidence differs')
        if baseline['date'][1]!=4:raise ValueError('This minimal source experiment accepts only the currently verified April date')
        prepare_code=image.img[0x963c60:0x963e75];prepare_protection=g.region(g.exe+0x963c60)[3]
        if digest(prepare_code)!='8a6edf8fc9bdf6daf9356ba7ed78adb312ad41d48c97651308298078a9e6d044' or g.read(g.exe+0x963c60,533)!=prepare_code or prepare_protection!=0x20:
            raise ValueError('Live prepare function differs')
        trial=TerrainTrial(magic=0x53545431,version=1,size=c.sizeof(TerrainTrial),year=baseline['date'][0],month=4,day=baseline['date'][2],
            renderThread=evidence['renderThreads'][0],psOwner=texture.psOwner,ps=int(row['ps'],16),view=texture.view,resource=texture.resource,bytes=TEXTURE_BYTES)
        texture=None
    if args.tree_trial_from:
        evidence=json.loads((args.tree_trial_from/'audit.json').read_text());check_identity(evidence['baseline'],baseline,check_clock=True)
        source_read=evidence['meshResourceRead']
        if not source_read['gpuReadbackCompleted'] or not evidence['actualDrawDispatched'] or not evidence['independentCodeAndProtectionReadback']:
            raise ValueError('Completed actual tree Draw/GPU read/recovery required')
        assets=json.loads((args.tree_trial_from/'texture-source-comparison.json').read_text())
        geometry=json.loads((args.tree_trial_from/'geometry-source-comparison.json').read_text())
        if len(assets['matched'])!=1 or assets['matched'][0]['ktid']!='A224A71D' or assets['gpuSha256']!='b696b37084bdcee62cade9988cc3372f2f82cfcb1b33052fa2398064678af520':
            raise ValueError('Exact original tree texture identity missing')
        if geometry['model']!='obj-n00-tree-01' or len(geometry['matches'])!=1 or geometry['matches'][0]['submesh']['index']!=2:
            raise ValueError('Exact original tree submesh identity missing')
        pinned={0:'b696b37084bdcee62cade9988cc3372f2f82cfcb1b33052fa2398064678af520',
            1:'3481111bcf59bd4900663aca036da4009021597d7d6b143322d671902bc66ce0',
            2:'e5289540c2f8514114fcc5906de9c78884e57676219c5f8fd836f07a3b206095'}
        for i,sha in pinned.items():
            b=source_read['blobs'][i]
            if digest((args.tree_trial_from/b['file']).read_bytes())!=sha or b['sha256']!=sha:raise ValueError('Saved GPU texture/geometry hash differs')
        if baseline['date'][1]!=4:raise ValueError('Minimum source trial requires verified April date')
        row=json.loads((args.tree_trial_from/'draws.json').read_text())[0]
        if row['start']!=0 or row['count']!=918 or row['views'][0]['object']!=hex(source_read['view']):raise ValueError('Tree Draw differs')
        prepare_code=image.img[0x963c60:0x963e75];prepare_protection=g.region(g.exe+0x963c60)[3]
        if digest(prepare_code)!='8a6edf8fc9bdf6daf9356ba7ed78adb312ad41d48c97651308298078a9e6d044' or g.read(g.exe+0x963c60,533)!=prepare_code or prepare_protection!=0x20:
            raise ValueError('Live prepare function differs')
        trial=TreeMaterialTrial(magic=0x53544d31,version=1,size=c.sizeof(TreeMaterialTrial),year=baseline['date'][0],month=4,day=baseline['date'][2],
            renderThread=row['thread'],psOwner=int(row['psOwner'],16),ps=int(row['ps'],16),view=source_read['view'],resource=source_read['resource'],
            bytes=TREE_OUTPUT_BYTES,vsOwner=int(row['vsOwner'],16),vs=int(row['vs'],16),inputLayout=source_read['inputLayout'],indexCount=918)
    dll_name,hash_key=('TerrainTrial.dll','terrainTrialDllSha256') if trial else ('TextureReadObserver.dll','textureReadDllSha256') if texture else ('ColorInstancedObserver.dll','colorInstancedDllSha256') if args.color else ('DrawInstancedObserver.dll','instancedDllSha256') if args.instanced else ('DrawIndexedObserver.dll','indexedDllSha256') if args.indexed else ('DrawObserver.dll','dllSha256')
    if args.mesh:dll_name,hash_key=('ColorMeshObserver.dll','colorMeshDllSha256') if args.color else ('DrawMeshObserver.dll','meshDllSha256')
    if mesh_read:dll_name,hash_key='MeshResourceRead.dll','dllSha256'
    if args.tree_trial_from:dll_name,hash_key='TreeMaterialTrial.dll','dllSha256'
    dll=(args.build/dll_name).resolve();manifest=json.loads((args.build/'build.json').read_text(encoding='utf-8-sig'))
    dll_data=dll.read_bytes()
    if digest(dll_data)!=manifest[hash_key]:raise ValueError('Observer DLL differs from its build receipt')
    source=Path(__file__).resolve().parent.parent/'Seasons/Native'
    for file,sha in manifest['hashes'].items():
        if digest((source/file).read_bytes())!=sha:raise ValueError('Observer source changed after this build')
    entry=export_rva(dll_data,'SeasonDrawObserve')
    protection=g.region(g.exe+target)[3]
    current_modules=modules(g)
    if any(Path(name).name.lower()=='renderdoc.dll' for name in current_modules):raise ValueError('This diagnostic rejects a RenderDoc-wrapped context')
    if any(Path(name).name.lower()==dll.name.lower() for name in current_modules):
        raise ValueError('This one-shot diagnostic is already loaded from a build; normal game restart required, never reload by changing its path')
    audit=dict(mode='Engine Draw observation' if args.live else 'Read-only preflight',baseline=baseline,
        targetRva=hex(target),nativeMethod='DrawIndexedInstanced' if args.instanced or args.mesh else 'DrawIndexed' if args.indexed else 'Draw',originalCodeSha256=code_hash,originalProtection=protection,
        dllSha256=digest(dll_data),exportRva=hex(entry),retiredCommandProbeRetried=False,
        drawVtableModified=False,gameBytecodeIdentityVerified=False,completeFrame=False,
        gamePassVerified=False,seasonApplied=False,productionReady=False)
    audit['selection']='Non-null color RT slot 0; skipped includes filtered Depth-only Draws' if args.color else 'All dispatched Draws on this single engine path'
    if texture:audit['textureReadSelection']=dict(slot=texture.slot,psOwner=hex(texture.psOwner),view=hex(texture.view),resource=hex(texture.resource),bytes=texture.capacity)
    if mesh_read:audit['meshResourceReadSelection']=[dict(slot=0,indexCount=item.indexCount,view=hex(item.view),resource=hex(item.resource),
        requireColor=bool(item.requireColor),gpuSourceResourcesModified=False) for item in mesh_items]
    if trial and not args.tree_trial_from:audit['minimalTerrainTrial']=dict(date=baseline['date'][:3],originalGpuTextureSha256=digest(trial_input),
        ownComputeShader=True,originalAlphaBlocksAndColorIndicesPreserved=True,climateApplied=False,treesApplied=False,
        finalSeasonMaterialImplemented=False,prepareCodeSha256=digest(prepare_code),hardDeadlineMs=args.milliseconds)
    if args.tree_trial_from:audit['minimalTreeMaterialTrial']=dict(date=baseline['date'][:3],model='obj-n00-tree-01',submesh=2,texture='A224A71D',
        originalGpuTextureSha256=assets['gpuSha256'],ownComputeShader=True,originalVertexAndPixelShaderPreserved=True,
        originalAlphaVerifiedBeforeSwap=True,leafMaskAndSpeciesPolicyImplemented=False,climateApplied=False,
        finalSeasonMaterialImplemented=False,prepareCodeSha256=digest(prepare_code),hardDeadlineMs=args.milliseconds)
    mesh_outputs=[]
    def save():
        (args.out/'audit.json').write_text(json.dumps(audit,indent=2),encoding='utf-8')
        for folder,read in mesh_outputs:
            child={**audit,'meshResourceRead':read,'parentBatch':'../audit.json'}
            (folder/'audit.json').write_text(json.dumps(child,indent=2),encoding='utf-8')
    save()
    if not args.live:print(json.dumps(audit));return
    k32.VirtualAllocEx.restype=c.c_void_p;k32.CreateRemoteThread.restype=c.c_void_p
    handle=k32.OpenProcess(0x43a,False,g.pid)
    if not handle:raise c.WinError(c.get_last_error())
    buffers=[];threads=[];loader_done=False;safe_to_release=False
    def allocate(data):
        address=k32.VirtualAllocEx(c.c_void_p(handle),None,c.c_size_t(len(data)),0x3000,0x04)
        if not address:raise c.WinError(c.get_last_error())
        buffers.append((address,len(data)));got=c.c_size_t();buf=c.create_string_buffer(data)
        if not k32.WriteProcessMemory(c.c_void_p(handle),c.c_void_p(address),buf,len(data),c.byref(got)) or got.value!=len(data):raise c.WinError(c.get_last_error())
        if g.read(address,len(data))!=data:raise ValueError('Prepared data readback differs before starting any worker')
        return address
    def launch(address,argument):
        thread=k32.CreateRemoteThread(c.c_void_p(handle),None,0,c.c_void_p(address),c.c_void_p(argument),0,None)
        if not thread:raise c.WinError(c.get_last_error())
        threads.append(thread);return thread
    try:
        check_identity(baseline,read_snapshot(g),check_clock=True)
        loader_address,loader_code,loader_receipt=remote_load_library(g)
        audit['loader']=loader_receipt;save()
        path_buffer=allocate((str(dll)+'\0').encode('utf-16le'))
        if g.read(loader_address,64)!=loader_code:raise ValueError('Remote loader entry changed before dispatch')
        for hop in loader_receipt.get('relaySnapshots',[]):
            at=int(hop['address'],16);expected=bytes.fromhex(hop['bytesHex'])
            if g.read(at,len(expected))!=expected or g.region(at)[3]!=hop['protection']:
                raise ValueError('Existing public loader relay changed before dispatch')
        loader=launch(loader_address,path_buffer)
        if k32.WaitForSingleObject(c.c_void_p(loader),5000)!=0:raise TimeoutError('DLL load did not finish; retain the diagnostic pages')
        loader_done=True
        remote=modules(g).get(str(dll).lower())
        if not remote:raise RuntimeError('Diagnostic DLL was not loaded')
        if digest(dll.read_bytes())!=audit['dllSha256']:raise ValueError('Diagnostic DLL file changed during loading')
        check_identity(baseline,read_snapshot(g),check_clock=True)
        request=Request(magic=MAGIC,version=VERSION,size=c.sizeof(Request),durationMs=args.milliseconds,
                        moduleBase=g.exe,context=int(baseline['immediate'],16),device=int(baseline['device'],16))
        if texture:texture.output=allocate(bytes(texture.capacity))
        if trial:
            if args.tree_trial_from:trial.output=allocate(bytes(TREE_OUTPUT_BYTES))
            else:trial.input=allocate(trial_input)
        if mesh_read:
            for i,item in enumerate(mesh_items):
                item.output=allocate(bytes(item.capacity))
                if isinstance(mesh_read,MeshReadBatch):mesh_read.items[i]=item
        address=allocate(bytes(request)+(bytes(texture) if texture else bytes(trial) if trial else bytes(mesh_read) if mesh_read else b''));audit.update(requestAddress=hex(address),moduleAddress=hex(remote),
            residentDiagnosticModule=True,allocatedRequestBytes=c.sizeof(Request)+(c.sizeof(texture) if texture else c.sizeof(trial) if trial else c.sizeof(mesh_read) if mesh_read else 0),requestedDurationMs=args.milliseconds);save()
        worker=launch(remote+entry,address)
        if trial:
            started=time.monotonic();last_identity=started;observed=False;cancelled=False
            while k32.WaitForSingleObject(c.c_void_p(worker),100)==0x102:
                now=time.monotonic()
                if now-last_identity>=1:
                    last_identity=now
                    try:check_identity(baseline,read_snapshot(g),check_clock=True)
                    except Exception as error:
                        audit['trialIdentityChange']=str(error);cancelled=True
                if now-started>args.milliseconds/1000+5:cancelled=True
                if cancelled:
                    cancel=c.c_int32(1);written=c.c_size_t()
                    if not k32.WriteProcessMemory(c.c_void_p(handle),c.c_void_p(address+Request.cancel.offset),c.byref(cancel),4,c.byref(written)) or written.value!=4:
                        raise RuntimeError('Cannot cancel the private native lease')
                    if k32.WaitForSingleObject(c.c_void_p(worker),5000)!=0:raise TimeoutError('Trial callback recovery is unconfirmed')
                    break
                done=g.read(address+Request.rows.offset+Record.complete.offset,4)
                if not observed and done==b'\x01\0\0\0':
                    value=type(trial).from_buffer_copy(g.read(address+c.sizeof(Request),c.sizeof(trial)));observed=True
                    audit['liveTrialDrawObserved']=True;audit['liveTrialView']=hex(value.newView);save()
                    print(json.dumps(dict(event='Minimum tree Draw recorded' if args.tree_trial_from else 'Minimum terrain Draw recorded',view=hex(value.newView),hardDeadlineMs=args.milliseconds)),flush=True)
        started=time.monotonic();audit['recoveryWaits']=[]
        while True:
            wait=k32.WaitForSingleObject(c.c_void_p(worker),1000)
            if wait==0:audit['nativeWorkerSignaled']=True;save();break
            if wait!=0x102:raise c.WinError(c.get_last_error())
            elapsed=time.monotonic()-started;audit['recoveryWaits'].append(dict(elapsedSeconds=elapsed));save()
            if elapsed>=30:raise TimeoutError('Native observer recovery is unconfirmed; do not unload/free its pages')
        data=g.read(address,c.sizeof(Request))
        if data is None:raise RuntimeError('Cannot read the native observer recovery receipt')
        (args.out/'native-receipt.bin').write_bytes(data);result=Request.from_buffer_copy(data)
        if not 0<=result.records<=CAPACITY:raise ValueError('Native record count is invalid')
        rows=[record(result.rows[i]) for i in range(result.records)]
        (args.out/'draws.json').write_text(json.dumps(rows,indent=2),encoding='utf-8')
        restored=g.read(g.exe+target,size)==code and g.region(g.exe+target)[3]==protection
        consistent=all(r['complete']==1 and r['context']==baseline['immediate'] and r['device']==baseline['device'] and
            r['contextType']==0 and r['counterAfter']==(r['counterBefore']+1)&0xffffffff for r in rows)
        audit.update(native={n:getattr(result,n) for n in ('status','error','records','active','skipped','rejected','restoreAttempts',
            'codeRestored','protectionRestored','callbacksDrained','referencesReleased','originalProtection','finalProtection',
            'failedThread','failedStage','failedThreadExit','failedThreadExitRead')},after=read_snapshot(g),
            independentCodeAndProtectionReadback=restored,recordIdentityVerified=consistent,
            actualDrawDispatched=bool(rows) and consistent,renderThreads=sorted({r['thread'] for r in rows}),
            gpuCompletionVerified=False)
        check_identity(baseline,audit['after'],check_clock=True);save()
        if result.status!=2 or result.error or not restored or not consistent or not all((result.codeRestored,result.protectionRestored,result.callbacksDrained,result.referencesReleased)):
            raise RuntimeError('Engine observation or its recovery is incomplete; no additional live trial is permitted')
        if trial:
            extension=g.read(address+c.sizeof(Request),c.sizeof(trial));(args.out/'native-extension.bin').write_bytes(extension)
            value=type(trial).from_buffer_copy(extension)
            trial_key='treeMaterialTrial' if args.tree_trial_from else 'terrainTrial'
            audit[trial_key]={n:getattr(value,n) for n,_ in value._fields_ if n not in ('input','output')}
            prepared_restored=g.read(g.exe+0x963c60,533)==prepare_code and g.region(g.exe+0x963c60)[3]==prepare_protection
            audit['independentPrepareCodeAndProtectionReadback']=prepared_restored;save()
            if value.state!=2 or value.hresult or not prepared_restored or value.swaps!=value.restores or not all((value.originalBindingsRestored,value.gpuResourcesReleased,value.prepareCodeRestored,value.prepareProtectionRestored)):
                raise RuntimeError('Minimum material application or recovery is incomplete')
            slot=0 if args.tree_trial_from else 4
            audit['minimalTreeDrawApplied' if args.tree_trial_from else 'minimalTerrainDrawApplied']=bool(rows) and rows[0]['views'][slot]['object']==hex(value.newView)
            if args.tree_trial_from:
                if not value.gpuReadbackComplete or value.outputBytes!=TREE_OUTPUT_BYTES or value.alphaPixels!=174752 or not value.changedPixels:
                    raise RuntimeError('Tree GPU mip/alpha verification incomplete')
                payload=g.read(value.output,value.outputBytes);half=TREE_OUTPUT_BYTES//2
                original,changed=payload[:half],payload[half:]
                if original[3::4]!=changed[3::4]:raise RuntimeError('Tree alpha readback differs')
                (args.out/'original-decoded-rgba-mips.bin').write_bytes(original);(args.out/'spring-rgba-mips.bin').write_bytes(changed)
                audit['treeGpuMipVerification']=dict(mips=7,pixels=half//4,alphaBytesEqual=True,
                    originalSha256=digest(original),springSha256=digest(changed),viewFormat=value.viewFormat,newViewFormat=value.newViewFormat)
                save()
        safe_to_release=True
        if mesh_read:
            extension=g.read(address+c.sizeof(Request),c.sizeof(mesh_read));(args.out/'native-extension.bin').write_bytes(extension)
            completed=type(mesh_read).from_buffer_copy(extension)
            values=list(completed.items)[:completed.count] if isinstance(completed,MeshReadBatch) else [completed]
            if len(values)!=len(mesh_items):raise RuntimeError('Mesh batch count differs')
            results=[]
            for expected,value in zip(mesh_items,values):
                if any(getattr(expected,n)!=getattr(value,n) for n in ('magic','version','size','indexCount','renderThread','requireColor','capacity','view','resource','output')):
                    raise RuntimeError('Mesh readback selection changed')
                folder=args.out if len(values)==1 else args.out/f'mesh-{value.indexCount}'
                if folder!=args.out:folder.mkdir(exist_ok=False)
                read={n:getattr(value,n) for n,_ in value._fields_ if n not in ('output','blobs')}
                if not 0<=value.blobCount<=12 or not 0<=value.bytes<=value.capacity:raise RuntimeError('Mesh output bounds invalid')
                payload=g.read(value.output,value.bytes) if value.bytes else b''
                if payload is None:raise RuntimeError('Completed Mesh GPU output is unreadable')
                blobs=[]
                for i in range(value.blobCount):
                    b=value.blobs[i]
                    if b.offset>len(payload) or b.bytes>len(payload)-b.offset:raise ValueError('Mesh blob exceeds completed output')
                    data=payload[b.offset:b.offset+b.bytes];file=f'gpu-{i:02d}-kind-{b.kind}-slot-{b.slot}.bin';(folder/file).write_bytes(data)
                    blobs.append(dict(**{n:(hex(getattr(b,n)) if n=='resource' else getattr(b,n)) for n,_ in b._fields_},file=file,sha256=digest(data)))
                read.update(gpuReadbackCompleted=value.state==2 and not value.hresult,blobs=blobs,partialOutputPreserved=value.state!=2)
                results.append(read)
                if folder==args.out:audit['meshResourceRead']=read
                else:
                    selected=[r for r in rows if r['count']==value.indexCount and r['views'][0]['object']==hex(value.view)]
                    (folder/'draws.json').write_text(json.dumps(selected,indent=2),encoding='utf-8')
                    mesh_outputs.append((folder,read))
            audit['meshResourceReads']=results;save()
            if any(not v['gpuReadbackCompleted'] or not v['bytes'] for v in results):
                raise RuntimeError('Selected Mesh GPU readback was not completed; partial output preserved')
        if texture:
            value=TextureRead.from_buffer_copy(g.read(address+c.sizeof(Request),c.sizeof(TextureRead)))
            audit['textureRead']={n:getattr(value,n) for n,_ in value._fields_ if n!='output'}
            if any(getattr(value,n)!=getattr(texture,n) for n in ('magic','version','size','slot','psOwner','view','resource','output','capacity')):
                raise RuntimeError('Texture readback selection changed')
            if value.state!=2 or value.hresult or value.bytes!=texture.capacity:raise RuntimeError('Original GPU texture readback was not completed')
            payload=g.read(value.output,value.bytes)
            if payload is None:raise RuntimeError('Completed GPU texture receipt cannot be read')
            (args.out/('original-land-control-rgba.bin' if value.slot==3 else 'original-land-bc3-mips.bin')).write_bytes(payload)
            audit['textureRead'].update(sha256=digest(payload),gpuReadbackCompleted=True,originalGpuResourceModified=False)
            save()
        print(json.dumps(audit))
    except BaseException as error:
        audit['controllerError']=str(error);save();raise
    finally:
        for thread in threads:k32.CloseHandle(c.c_void_p(thread))
        # After completed detach/drain and signaled worker, late callbacks hold
        # only module globals and execute original; admission is permanently false.
        # Keep the module pinned, but release our data after saving the receipt.
        retained=[];released=[]
        for buffer_address,buffer_size in buffers:
            row=dict(address=hex(buffer_address),requestedBytes=buffer_size)
            if safe_to_release or (loader_done and buffer_address==path_buffer):
                if k32.VirtualFreeEx(c.c_void_p(handle),c.c_void_p(buffer_address),0,0x8000):row['memFreeReadback']=g.region(buffer_address)[2]==0x10000;released.append(row)
                else:row['releaseError']=c.get_last_error();retained.append(row)
            else:retained.append(row)
        audit['retainedAllocations']=retained;audit['releasedAllocations']=released
        save()
        k32.CloseHandle(c.c_void_p(handle))

if __name__=='__main__':main()
