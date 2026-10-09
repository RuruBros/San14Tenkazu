"""Bounded actual engine Draw/Present chronology. Default is read-only preflight.

Uses authored native code, never the retired command probe or D3D11 dispatch
vtable. Only a signaled worker and independently verified full recovery permit
freeing its CPU transfer buffer. A Present interval is not automatically a
complete frame or a verified terrain/tree/UI classification.
"""
import argparse
import ctypes as c
import hashlib
import json
import time
from pathlib import Path
from collections import Counter
from ctypes import wintypes as w
from game import Game,k32,psapi
from image import Image
from season_watch import read_snapshot
from season_trace_contract import check_identity
from season_shader_snapshot import EXE_HASH
from season_draw_observe import export_rva,modules,remote_load_library

CAPACITY=8192
TARGETS=[
    (0x960e50,52,'4a1653de5006945e8945504e51158704032d997f08c4c2b03ed0b8a7ff705fdf'),
    (0x960ee0,82,'dae9d45952c317094fd857fbce61bfd7e3f40696404ac5cfb213d0569a4a18b2'),
    (0x960e90,67,'ed619f25c921d2e81ce43933df8798febdac7bdd432a3378c4163fc562c8f5ff'),
    (0x960f90,70,'782647f22a91f452bddacbef5c8d341bef5f2bea7d22584162ae1a0c213fe41b'),
    (0x960fe0,87,'f1661504d65a5e54580f6ee42476f2483b0fb08887998fa646659f250264f7b6'),
    (0x961040,75,'d055bb98fb245ec048b9285499f0ca0b12cd4d76c14ea7d7d88cc83faa012bb1'),
    (0x960e20,43,'5cd336ea9cc3e3beb543c86eb6adad0b7903f95b503994afc96ae6d91ad3e266'),
    (0x961090,387,'e6ca1ff950779a01e8b42269ef06bb753e465952af2e9ad380f515fb0c46f5e6'),
    (0x9625f0,443,'75f7fe6b0e9c27de55869454f1e20c533b8e34615c2872a2e95df949062e009b'),
    (0x963c60,533,'8a6edf8fc9bdf6daf9356ba7ed78adb312ad41d48c97651308298078a9e6d044'),
    (0x954730,433,'8249f288ba68659fadd02963ae90da53902de1ac9f49613fded0e85c49e92153')]

class Row(c.Structure):
    _fields_=[(n,c.c_uint64) for n in ('sequence','qpcBegin','qpcEnd','driver','context','psOwner','vsOwner','ps','vs')]+[
        ('views',c.c_uint64*12),('targets',c.c_uint64*8),('depth',c.c_uint64),('vertexBuffers',c.c_uint64*8),
        ('indexBuffer',c.c_uint64),('inputLayout',c.c_uint64),('strides',c.c_uint32*8),('offsets',c.c_uint32*8)]+[
        (n,c.c_uint32) for n in ('indexFormat','indexOffset','topology')]+[('viewport',c.c_float*6)]+[
        (n,c.c_uint32) for n in ('kind','thread','mode','start','count','instances','counterBefore','counterAfter','complete',
            'presentEnabled','syncInterval','presentFlags')]+[('swapchain',c.c_uint64),('bindingSampleSequence',c.c_uint64),
            ('bindingsCaptured',c.c_uint32),('reserved',c.c_uint32)]
class Patch(c.Structure):
    _fields_=[('address',c.c_uint64),('trampoline',c.c_uint64)]+[(n,c.c_uint32) for n in
        ('size','originalProtection','finalProtection','codeRestored','protectionRestored','reserved')]
class Request(c.Structure):
    _fields_=[(n,c.c_uint32) for n in ('magic','version','size','durationMs')]+[(n,c.c_uint64) for n in
        ('moduleBase','context','device','sceneDriver','swapchain','qpcFrequency')]+[(n,c.c_int32) for n in
        ('cancel','status','error','records','active','rejected','skipped','saturated')]+[(n,c.c_uint32) for n in
        ('callbacksDrained','referencesReleased','failedThread','failedStage','sparseBindings','bindingSamples','renderThread','otherDrawThreads','targetPresentReturns','presentReturns')]+[('patches',Patch*11),('rows',Row*CAPACITY)]
assert c.sizeof(Row)==496 and c.sizeof(Patch)==40 and c.sizeof(Request)==4063808

class BoundaryFrame(c.Structure):
    _fields_=Request._fields_[:-2]+[('patches',Patch*3),('rows',Row*64)]
class BoundaryImage(c.Structure):
    _fields_=[(n,c.c_uint64) for n in ('resource','view','backbuffer','qpc')]+[(n,c.c_uint32) for n in ('width','height','format','offset','bytes','reserved')]
class BoundaryData(c.Structure):
    _fields_=[(n,c.c_uint32) for n in ('magic','version','size','state')]+[(n,c.c_uint64) for n in
        ('driver','scenePsOwner','sceneVsOwner','scenePs','sceneVs','target','uiPsOwner','uiVsOwner','uiPs','uiVs','inputLayout','output')]+[
        (n,c.c_uint32) for n in ('capacity','bytes','images','hresult','failureStage','thread','counterBefore','counterAfter')]+[('image',BoundaryImage*3)]
class BoundaryRequest(c.Structure):
    _fields_=[('frame',BoundaryFrame),('data',BoundaryData)]

def boundary_selection(g,folder,baseline,render_thread):
    prior=json.loads((folder/'audit.json').read_text());check_identity(prior['baseline'],baseline,check_clock=True)
    if not prior['actualDrawDispatched'] or not prior['independentCodeAndProtectionReadback'] or prior['renderThreads']!=[render_thread]:
        raise ValueError('A recovered current-process chronology is required')
    md=json.loads((folder/'bound-shader-metadata.json').read_text());check_identity(md['baseline'],baseline,check_clock=True)
    owners={(e['stage'],e['owner']):e for e in md['entries']}
    rows=[r for r in json.loads((folder/'draws.json').read_text()) if r['bindingsCaptured']==1]
    def retained(row):return owners.get(('vs',row['vsOwner']),{}).get('retainedBytecodeSha256')
    def resources(row):return sorted((r['name'],r['slot']) for r in owners.get(('ps',row['psOwner']),{}).get('metadata',{}).get('resources',[]) if r['kind']==2)
    scenes=[r for r in rows if r['kind']==3 and r['count']==3 and retained(r)=='46e389a47d684c17a47ea14e0b1f0d7b5d7427b3ef177122c2842cb4dfe2fd76' and resources(r)==[('g_tSceneMap',0)]]
    ui=[r for r in rows if r['kind']==0 and r['count']==6 and retained(r)=='98a371435daa4a2c8fb531bf39b00be0e9af20077050d99f7aa384963440b201' and resources(r)==[('sStage0',0),('sStage1',1),('sStage2',2)]]
    if len(scenes)!=1 or len(ui)!=1:raise ValueError('One exact scene/UI candidate pair required')
    scene,ui= scenes[0],ui[0]
    if scene['driver']!=ui['driver'] or scene['targets'][0]=='0x0' or scene['targets'][0]!=ui['targets'][0] or scene['sequence']>=ui['sequence']:
        raise ValueError('Observed scene/UI target or order differs')
    for row in (scene,ui):
        for stage in ('ps','vs'):
            owner=int(row[stage+'Owner'],16);shader=int(row[stage],16)
            if g.q(owner)!=g.exe+(0x14beb90 if stage=='ps' else 0x14beb58) or g.q(owner+40)!=shader:
                raise ValueError('Scene/UI native shader identity changed')
            if stage=='vs':
                length=g.q(owner+56)
                if not 32<=length<=65536:raise ValueError('Scene/UI retained VS length outside bounds')
                data=g.read(g.q(owner+48),length)
                if data is None or sha(data)!=retained(row):raise ValueError('Scene/UI retained VS changed')
    return dict(driver=int(scene['driver'],16),scenePsOwner=int(scene['psOwner'],16),sceneVsOwner=int(scene['vsOwner'],16),
        scenePs=int(scene['ps'],16),sceneVs=int(scene['vs'],16),target=int(scene['targets'][0],16),
        uiPsOwner=int(ui['psOwner'],16),uiVsOwner=int(ui['vsOwner'],16),uiPs=int(ui['ps'],16),uiVs=int(ui['vs'],16),
        inputLayout=int(ui['inputLayout'],16))

def sha(data):return hashlib.sha256(data).hexdigest()
def row_json(row):
    pointers={'driver','context','psOwner','vsOwner','ps','vs','depth','indexBuffer','inputLayout','swapchain'}
    arrays={'views','targets','vertexBuffers'}
    result={}
    for name,kind in row._fields_:
        value=getattr(row,name)
        if name in pointers:result[name]=hex(value)
        elif name in arrays:result[name]=[hex(x) for x in value]
        elif name in ('strides','offsets','viewport'):result[name]=list(value)
        else:result[name]=value
    return result

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--build',type=Path,required=True);p.add_argument('--out',type=Path,required=True)
    p.add_argument('--milliseconds',type=int,default=300);p.add_argument('--live',action='store_true')
    p.add_argument('--sparse-bindings',action='store_true',help='Record every actual engine Draw, capture GPU bindings once per CPU owner/path group; repeated rows do not prove identical GPU bindings')
    p.add_argument('--render-thread-from',type=Path,required=True,help='Completed current-process actual Draw receipt with one authenticated render thread')
    p.add_argument('--target-present-returns',type=int,default=0,help='Close admission after 1..3 engine Present returns, or at hard deadline; default uses deadline only')
    p.add_argument('--scene-boundary-from',type=Path,help='New GPU readback capability: three engine hooks and one exact scene/UI candidate pair from completed chronology')
    args=p.parse_args()
    if not 50<=args.milliseconds<=(10000 if args.sparse_bindings else 500) or not 0<=args.target_present_returns<=3:raise ValueError('Duration/boundary exceeds selected bounded mode')
    if args.scene_boundary_from and (args.milliseconds>1000 or not args.sparse_bindings or args.target_present_returns):
        raise ValueError('Scene GPU readback requires sparse mode, <=1000ms and no Present hook')
    args.out.mkdir(parents=True,exist_ok=False)
    g=Game();im=Image();baseline=read_snapshot(g);driver=int(baseline['driver'],16)
    prior=json.loads((args.render_thread_from/'audit.json').read_text());check_identity(prior['baseline'],baseline,check_clock=True)
    if not prior['actualDrawDispatched'] or not prior['independentCodeAndProtectionReadback'] or len(prior['renderThreads'])!=1:
        raise ValueError('One verified current-process render thread and completed Draw/recovery required')
    render_thread=prior['renderThreads'][0]
    selection=boundary_selection(g,args.scene_boundary_from,baseline,render_thread) if args.scene_boundary_from else None
    if sha(im.img)!='aa85c23361c67cd085f3101d2ecb9bf1579f79d46d081effa0184044f1c48e59':raise ValueError('Saved image differs')
    exe=c.create_unicode_buffer(32768)
    if not psapi.GetModuleFileNameExW(c.c_void_p(g.h),c.c_void_p(g.exe),exe,len(exe)) or sha(Path(exe.value).read_bytes())!=EXE_HASH:raise ValueError('Game executable differs')
    chain=g.q(driver+0x3080)
    default_present=g.read(driver+0x2fc0,16)
    if not chain or default_present is None:raise ValueError('Default swapchain/settings are unreadable')
    originals=[]
    targets=[TARGETS[i] for i in (3,0,9)] if selection else TARGETS
    for rva,size,digest in targets:
        data=im.img[rva:rva+size];protection=g.region(g.exe+rva)[3]
        if sha(data)!=digest or g.read(g.exe+rva,size)!=data or protection!=0x20:raise ValueError(f'Engine code/protection differs at {rva:x}')
        originals.append((rva,size,data,protection))
    dll=(args.build/('SceneBoundaryRead.dll' if selection else 'FrameObserver.dll')).resolve();manifest=json.loads((args.build/'build.json').read_text(encoding='utf-8-sig'));dll_data=dll.read_bytes()
    if sha(dll_data)!=manifest['dllSha256']:raise ValueError('Observer DLL differs from its build receipt')
    native=Path(__file__).resolve().parent.parent/'Seasons/Native'
    for file,digest in manifest['hashes'].items():
        if sha((native/file).read_bytes())!=digest:raise ValueError(f'Observer source changed after build: {file}')
    loaded=modules(g)
    if any(Path(file).name.lower()==dll.name.lower() for file in loaded):
        raise ValueError('This process already loaded the one-shot observer; changing build path does not permit another trial')
    if any(Path(file).name.lower()=='renderdoc.dll' for file in loaded):raise ValueError('RenderDoc-wrapped process is not accepted')
    export=export_rva(dll_data,'SeasonSceneBoundaryRead' if selection else 'SeasonFrameObserve')
    audit=dict(mode='Bounded engine Draw/Present chronology' if args.live else 'Read-only preflight',baseline=baseline,
        durationMs=args.milliseconds,targetPresentReturns=args.target_present_returns,dllSha256=sha(dll_data),swapchain=hex(chain),defaultPresentSettingsHex=default_present.hex(),targets=[dict(rva=hex(rva),size=size,sha256=sha(data),protection=protection) for rva,size,data,protection in originals],
        retiredCommandProbeRetried=False,nativeDrawVtableModified=False,gameBytecodeIdentityVerified=False,
        completeFrame=False,drawAutoObserved=False,sceneUiOrderVerified=False,seasonApplied=False,productionReady=False)
    audit['bindingSampling']=dict(sparse=bool(args.sparse_bindings),actualDrawRecordsUnfiltered=True,
        repeatedGpuBindingsEqualityClaimed=False)
    if selection:
        audit.update(mode='GPU scene/UI boundary readback' if args.live else 'Read-only scene/UI preflight',
            boundarySelection={k:hex(v) for k,v in selection.items()},originalGpuContentsModified=False,
            gpuBindingsModified=False,sceneBeforeUiReadbackVerified=False)
        audit['bindingSampling']['actualDrawRecordsUnfiltered']=False
        audit['bindingSampling']['selection']='Exact scene candidate and first exact indexed UI candidate only'
    def save():(args.out/'audit.json').write_text(json.dumps(audit,indent=2),encoding='utf-8')
    save()
    if not args.live:print(json.dumps(audit));return
    handle=k32.OpenProcess(0x43a,False,g.pid)
    if not handle:raise c.WinError(c.get_last_error())
    k32.VirtualAllocEx.restype=c.c_void_p;k32.CreateRemoteThread.restype=c.c_void_p
    buffers=[];threads=[];safe=False;loader_done=False;path_address=None
    def allocate(data):
        address=k32.VirtualAllocEx(c.c_void_p(handle),None,c.c_size_t(len(data)),0x3000,0x04)
        if not address:raise c.WinError(c.get_last_error())
        buffers.append((address,len(data)));buffer=c.create_string_buffer(data);written=c.c_size_t()
        if not k32.WriteProcessMemory(c.c_void_p(handle),c.c_void_p(address),buffer,len(data),c.byref(written)) or written.value!=len(data) or g.read(address,len(data))!=data:
            raise RuntimeError('Private data allocation/readback failed')
        return address
    def launch(address,parameter):
        thread_id=w.DWORD()
        thread=k32.CreateRemoteThread(c.c_void_p(handle),None,0,c.c_void_p(address),c.c_void_p(parameter),0,c.byref(thread_id))
        if not thread:raise c.WinError(c.get_last_error())
        threads.append(thread);return thread,thread_id.value
    try:
        check_identity(baseline,read_snapshot(g),check_clock=True)
        loader_address,loader_code,loader=remote_load_library(g);audit['loader']=loader;save()
        path_address=allocate((str(dll)+'\0').encode('utf-16le'))
        if g.read(loader_address,64)!=loader_code:raise ValueError('Public loader entry changed')
        for hop in loader.get('relaySnapshots',[]):
            at=int(hop['address'],16);expected=bytes.fromhex(hop['bytesHex'])
            if g.read(at,len(expected))!=expected or g.region(at)[3]!=hop['protection']:raise ValueError('Existing public loader relay changed')
        thread,loader_id=launch(loader_address,path_address);audit['loaderThreadId']=loader_id;save()
        if k32.WaitForSingleObject(c.c_void_p(thread),5000)!=0:raise TimeoutError('Diagnostic DLL loading is unconfirmed')
        loader_done=True;remote=modules(g).get(str(dll).lower())
        if not remote or sha(dll.read_bytes())!=audit['dllSha256']:raise ValueError('Diagnostic DLL load/hash differs')
        check_identity(baseline,read_snapshot(g),check_clock=True)
        request_type=BoundaryFrame if selection else Request
        request=request_type(magic=0x53465231,version=2,size=c.sizeof(request_type),durationMs=args.milliseconds,moduleBase=g.exe,
            context=int(baseline['immediate'],16),device=int(baseline['device'],16),sceneDriver=driver,swapchain=chain,
            sparseBindings=int(args.sparse_bindings),renderThread=render_thread,targetPresentReturns=args.target_present_returns)
        output_address=None
        if selection:
            output_address=allocate(bytes(32*1024*1024))
            request=BoundaryRequest(frame=request,data=BoundaryData(magic=0x53425231,version=1,size=c.sizeof(BoundaryData),
                output=output_address,capacity=32*1024*1024,**selection))
        address=allocate(bytes(request));audit.update(requestAddress=hex(address),moduleAddress=hex(remote),residentDiagnosticModule=True,allocatedRequestBytes=c.sizeof(request));save()
        worker,worker_id=launch(remote+export,address);audit['nativeWorkerThreadId']=worker_id;save()
        # Admission closes at the native deadline; a previously admitted original
        # game callback still has to finish. Keep its exact worker handle alive
        # while checking recovery, without repeating the Hook or freeing data.
        started=time.monotonic();audit['recoveryWaits']=[]
        while True:
            wait=k32.WaitForSingleObject(c.c_void_p(worker),1000)
            if wait==0:audit['nativeWorkerSignaled']=True;save();break
            if wait!=0x102:raise c.WinError(c.get_last_error())
            elapsed=time.monotonic()-started
            audit['recoveryWaits'].append(dict(elapsedSeconds=elapsed));save()
            if elapsed>=30:raise TimeoutError('Native worker recovery is unconfirmed; do not free/unload')
        data=g.read(address,c.sizeof(request))
        if data is None:raise RuntimeError('Cannot read completed recovery receipt')
        (args.out/'native-receipt.bin').write_bytes(data);result=type(request).from_buffer_copy(data)
        if selection:
            result,bd=result.frame,result.data
            boundary_audit={n:getattr(bd,n) for n in ('state','bytes','images','hresult','failureStage','thread','counterBefore','counterAfter')}
            boundary_audit['image']=[];audit['boundaryReadback']=boundary_audit
            if not 0<=bd.bytes<=32*1024*1024 or not 0<=bd.images<=3:raise ValueError('Boundary output bounds invalid')
            payload=g.read(output_address,bd.bytes) if bd.bytes else b''
            if payload is None:raise ValueError('Boundary GPU payload unavailable')
            (args.out/'gpu-boundary-rgba.bin').write_bytes(payload)
            boundary_audit['sha256']=sha(payload)
            for i in range(bd.images):
                im=bd.image[i]
                if im.bytes!=im.width*im.height*4 or im.offset+im.bytes>bd.bytes:raise ValueError('Boundary image bounds invalid')
                boundary_audit['image'].append(dict(resource=hex(im.resource),view=hex(im.view),backbuffer=hex(im.backbuffer),qpc=im.qpc,
                    width=im.width,height=im.height,format=im.format,offset=im.offset,bytes=im.bytes,
                    sha256=sha(payload[im.offset:im.offset+im.bytes])))
            boundary_audit['completed']=bd.state==2 and not bd.hresult and bd.images==3 and bd.thread==render_thread and bd.counterAfter==bd.counterBefore+1 and all(
                im.resource==im.backbuffer and im.view==selection['target'] for im in bd.image)
            save()
        if not 0<=result.records<=(64 if selection else CAPACITY):raise ValueError('Record count exceeds allocated capacity')
        rows=[row_json(result.rows[i]) for i in range(result.records)];(args.out/'draws.json').write_text(json.dumps(rows,indent=2))
        recoveries=[]
        for index,(rva,size,original,protect) in enumerate(originals):
            native_row=result.patches[index]
            recoveries.append(dict(rva=hex(rva),codeEqual=g.read(g.exe+rva,size)==original,protectionEqual=g.region(g.exe+rva)[3]==protect,
                nativeCodeRestored=native_row.codeRestored,nativeProtectionRestored=native_row.protectionRestored,
                nativeTarget=hex(native_row.address),nativeSize=native_row.size))
        safe=result.status==2 and not result.error and result.callbacksDrained and result.referencesReleased and all(
            r['codeEqual'] and r['protectionEqual'] and r['nativeCodeRestored'] and r['nativeProtectionRestored'] and r['nativeTarget']==hex(g.exe+originals[i][0]) and r['nativeSize']==originals[i][1] for i,r in enumerate(recoveries))
        valid=all(row['complete']==1 and row['sequence']==i and row['context']==baseline['immediate'] and row['qpcEnd']>=row['qpcBegin'] and row['kind']<=10 and
            (row['counterAfter']==((row['counterBefore']+1)&0xffffffff) if row['kind']<8 else row['counterAfter']==row['counterBefore'] if row['kind']==8 else True) for i,row in enumerate(rows))
        samples_valid=all(row['kind']>=9 or row['reserved']==1 or row['bindingsCaptured']==1 or (
            row['bindingSampleSequence']<row['sequence'] and rows[row['bindingSampleSequence']]['bindingsCaptured']==1 and
            all(row[k]==rows[row['bindingSampleSequence']][k] for k in ('driver','psOwner','vsOwner','kind','mode'))) for row in rows)
        audit.update(native={name:getattr(result,name) for name in ('status','error','records','active','rejected','skipped','saturated','callbacksDrained','referencesReleased','failedThread','failedStage','qpcFrequency','sparseBindings','bindingSamples','renderThread','otherDrawThreads','targetPresentReturns','presentReturns')},
            recovery=recoveries,independentCodeAndProtectionReadback=bool(safe),recordIdentityVerified=valid,
            after=read_snapshot(g),kindCounts=dict(Counter(row['kind'] for row in rows)),renderThreads=sorted({row['thread'] for row in rows if row['kind']<9}),
            presentThreads=sorted({row['thread'] for row in rows if row['kind']>=9}),gpuCompletionVerified=False)
        audit['actualDrawDispatched']=any(row['kind']<9 and row['complete']==1 for row in rows) and valid
        audit['bindingSampleReferencesVerified']=samples_valid
        check_identity(baseline,audit['after'],check_clock=True);save()
        if not safe:raise RuntimeError('Native code/protection/drain recovery is incomplete; no further live trial')
        if not valid or not samples_valid:raise RuntimeError('Chronology has incomplete or mismatched Draw/sample records')
        if selection:
            audit['sceneBeforeUiReadbackVerified']=audit['boundaryReadback']['completed'] and valid and safe and not result.saturated
            save()
            if not audit['sceneBeforeUiReadbackVerified']:raise RuntimeError('Scene/UI boundary GPU readback incomplete')
        audit['presentReturnCount']=sum(row['kind']==10 and row['presentEnabled'] and row['presentFlags']==0 and row['swapchain']==hex(chain) for row in rows)
        audit['presentBoundedIntervalObserved']=audit['presentReturnCount']>=2 and not result.saturated
        save();print(json.dumps(audit))
    except BaseException as error:audit['controllerError']=str(error);save();raise
    finally:
        for thread in threads:k32.CloseHandle(c.c_void_p(thread))
        released=[];retained=[]
        for address,size in buffers:
            row=dict(address=hex(address),requestedBytes=size)
            if safe or (loader_done and address==path_address):
                if k32.VirtualFreeEx(c.c_void_p(handle),c.c_void_p(address),0,0x8000):
                    row['memFreeReadback']=g.region(address)[2]==0x10000;released.append(row)
                else:row['releaseError']=c.get_last_error();retained.append(row)
            else:retained.append(row)
        audit.update(releasedAllocations=released,retainedAllocations=retained);save();k32.CloseHandle(c.c_void_p(handle))

if __name__=='__main__':main()
