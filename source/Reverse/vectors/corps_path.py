# Native base-path search with a real pool constructor and one copied-memory call scope.
import copy
import struct
import time
from context import *
from engine_audit_state import EngineAuditState
from unit_finalization import capture_native_pools
from destruction_overflow import require_unchanged_snapshot

COVER = [0x2ACB50,0x1843E0,0x183A30,0x2AE6F0,0xBE120,0x21E2B0]


def generate(ctx):
    o=ctx.o; o.insn_limit=300_000_000
    capture_native_pools(ctx,[(0x1B5C990,24,8)])
    audit=EngineAuditState(ctx); snapshot=copy.deepcopy(audit.snapshot)
    original=audit.read(); initial_rng=original['rng']
    # Preserve ABI registers, run the original output-list constructor, then the original base-path body.
    # The harness contains no gameplay calculations and makes no writes to the live game.
    thunk=bytes.fromhex('41544155415641574989cc4989d54d89c64d89cf4883ec284c89f148b8')+struct.pack('<Q',ctx.g.exe+0x21E2B0)
    thunk+=bytes.fromhex('ffd04c89e14c89ea4d89f04d89f948b8')+struct.pack('<Q',ctx.g.exe+0x2ACB50)
    thunk+=bytes.fromhex('ffd04883c428415f415e415d415cc3')
    code=o.alloc(len(thunk)); o.poke(code,thunk)
    output=o.alloc(24); outcost=o.alloc(4)
    corps=[i for i,b in enumerate(ctx.rec[CORPS]) if i and b[0x10] and b[0x11] and ctx.u16(CORPS,i,0x12)]
    groups={}
    for i in corps: groups.setdefault(ctx.rec[CORPS][i][0x10],[]).append(i)
    force,group=max(groups.items(),key=lambda p:len(p[1]))
    bases=[i for i,b in enumerate(ctx.rec[0xDD38]) if i and b[0x30] in group]
    if len(bases)<2: raise RuntimeError('Two actual same-force bases are required')
    source,target=bases[:2]
    other=next(i for i,b in enumerate(ctx.rec[0xDD38]) if i and b[0x30] not in group)
    def p(t,i,at,v,fmt='B'): return [hex(t),i,at,struct.pack('<'+fmt,v).hex()]
    fixtures=[dict(name='same_base',source=source,target=source,pokes=[]),
              dict(name='actual_route',source=source,target=target,pokes=[]),
              dict(name='different_force',source=source,target=other,pokes=[])]
    center=20000; region=[center+row*220+col for row in range(3) for col in range(3)]
    terrain=[p(TERRAINS,i,0x2A,0,'H') for i in range(31)]+[p(TERRAINS,1,0x2A,600,'H'),p(TERRAINS,2,0x2A,225,'H')]
    grid=terrain+[p(TILES,i,0x10,1) for i in region]+[p(TILES,i,0x14,force) for i in region]+[p(TILES,i,0x16,0,'H') for i in region]
    grid += [p(0x6DA98,ctx.u16(0xDD38,source,0x4E),0x10,center,'H'),
             p(0x6DA98,ctx.u16(0xDD38,target,0x4E),0x10,center+2,'H')]
    for name,extra in [('uniform_ties',[]),('weighted_detour',[p(TILES,i,0x10,2) for i in region if i not in [center,center+1,center+2]]),
                       ('fire_detour',[p(TILES,center+1,0x16,100,'H')]),
                       ('hostile_detour',[p(TILES,center+1,0x14,0)]),
                       ('blocked_source',[p(TILES,center,0x10,0)]),
                       ('blocked_target',[p(TILES,center+2,0x10,0)])]:
        fixtures.append(dict(name=name,source=source,target=target,pokes=grid+extra))
    cases=[]
    for f in fixtures:
        began=time.time()
        # Own harness pages do not roll back: initialize their input bytes before every native call.
        o.poke(output,struct.pack('<QQQ',ctx.g.exe+0x1343670,0,0)); o.poke(outcost,b'\0'*4)
        def run():
            before,after=audit.read(),[]
            def inspect():
                path=[(int(p,16)-ctx.addr(TILES,0))//32 for p in audit.members(o.q(output+8))]
                after.append(dict(hexes=path,cost=struct.unpack('<i',o.read(outcost,4))[0],delta=audit.delta(before,audit.read())))
                return []
            result=o.call(code-ctx.g.exe,ctx.addr(0xDD38,f['source']),ctx.addr(0xDD38,f['target']),output,outcost,read_after=inspect)
            return dict(fixture=f,result=result&0xffffffff,**after[0])
        result=ctx.run(f['pokes'],run)
        if result is None: raise RuntimeError(f'{f["name"]}: {ctx.failures[-1]}')
        if result['delta']!={'records':[]}: raise RuntimeError('Base path search changed gameplay audit state')
        cases.append(result); print(f'  corps path {f["name"]}: {len(result["hexes"])} tiles, cost {result["cost"]} ({time.time()-began:.2f}s)',flush=True)
    steps=[]
    for terrain_index in [0,1,2,3,14,15,24,30]:
        for fire in [0,1,100]:
            pokes=[p(TILES,center,0x10,terrain_index),p(TILES,center,0x16,fire,'H')]
            result=ctx.run(pokes,lambda:o.call32(0x2AE6F0,ctx.addr(TILES,center),force,1))
            if result is None: raise RuntimeError(f'Step cost failed: {ctx.failures[-1]}')
            steps.append(dict(pokes=pokes,result=result))
    if audit.read()!=original: raise RuntimeError('Base path fixtures did not restore their input')
    if not ctx.frozen: require_unchanged_snapshot(snapshot,initial_rng)
    ctx.save('corps_path_world.json.gz',snapshot)
    ctx.save('corps_path.json.gz',dict(world='corps_path_world.json.gz',cases=cases,steps=steps,gameplay_stubs=[],
        native_output_constructor='0x21e2b0',contract=dict(army=None,force=force,options=4,workspace=0)))
    return f'corps paths: {len(cases)} paths, {len(steps)} step costs'
