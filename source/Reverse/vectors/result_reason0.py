# Execute the complete reason-0 caller; project only morale requests and injury decisions into C#.
# Localization, history and injury UI run in native copies, without gameplay stubs.
# These cases retain positive morale; the failed zero-morale engine diagnostic is kept separately.
import copy, struct, time
from context import *
from unit_finalization import capture_native_pools
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_R9, UC_X86_REG_RSP

COVER = [0x3789E0, 0x270550, 0x27D4B0, 0x229E30]


def generate(ctx):
    o = ctx.o
    capture_native_pools(ctx)
    people = [ctx.officer_by_id(ctx.u16(UNITS,i,0x12)) for i in range(1,501)
              if ctx.rec[UNITS][i][0x10] and ctx.u16(UNITS,i,0x12)]
    people = [i for i in people if o.call32(0x32F500,ctx.addr(OFFICERS,i)) and 1 <= ctx.officer_force(i) <= 45
              and not o.call32(0x32F500,o.call(0x22AA70,ctx.addr(OFFICERS,i)))]
    first = people[0]
    second = next(i for i in people if ctx.officer_force(i) != ctx.officer_force(first))
    persons = [ctx.addr(OFFICERS,i) for i in (first,second)]
    units = [o.call(0x22A070,p,1) for p in persons]
    urefs = {ctx.addr(UNITS,i):i for i in range(501)}
    prefs = {ctx.addr(OFFICERS,i):i for i in range(len(ctx.rec[OFFICERS]))}
    tiles = {ctx.addr(TILES,i):i for i in range(0xBD10)}
    target = urefs[units[1]]
    def put(t,i,at,b): return [hex(t),i,at,b.hex()]
    def integer(kind,at,v): return [kind,0,at,struct.pack('<i',v).hex()]
    base = [['worldstate',0,0x3A,'00'],put(UNITS,target,0x1A,b'\x64'),put(OFFICERS,second,0xF1,b'\x4a'),
            put(OFFICERS,second,0x161,b'\x00'),integer('settings',0xE58,1),integer('settings',0xD0,1),
            put(OFFICERS,first,0x118,bytes(10))]
    fixtures = []
    def add(name,pokes=None,selector=0,officers=None):
        fixtures.append(dict(name=name,selector=selector,officers=officers or [first,second],pokes=base+(pokes or [])))
    add('baseline')
    add('selector_1',selector=1)
    add('health_75',[put(OFFICERS,second,0xF1,b'\x4b')])
    add('critical',[put(OFFICERS,second,0x161,b'\x03')])
    for kind in (0x3D,0x3E):
        add(f'personality_{kind:x}',[put(OFFICERS,first,0x118,struct.pack('<H',kind))])
    for age in (60,61):
        add(f'personality_90_age_{age}',[put(OFFICERS,first,0x118,b'\x90\x00'),integer('worldstate',0x44,0),
            put(OFFICERS,first,0xAC,struct.pack('<H',211-age)),['worldstate',0,0x34,'d200']])
    for difficulty in (0,1,2):
        add(f'player_difficulty_{difficulty}',[['worldstate',0,0x3A,bytes([ctx.officer_force(second)]).hex()],
                                               integer('settings',0x28,difficulty)])
    add('injury_enabled',[integer('settings',0xD0,0)])
    add('invalid_first',officers=[0,second])
    observed = [None]
    hooks = []
    def word(p): return struct.unpack('<i',o.read(p,4))[0]
    def root(mu,ret): return o.q(mu.reg_read(UC_X86_REG_RSP)) == ctx.g.exe+ret
    def morale(mu,address,size,data):
        if not root(mu,0x378C70): return
        delta = mu.reg_read(UC_X86_REG_RDX) & 0xFFFFFFFF
        if delta >= 0x80000000: delta -= 0x100000000
        observed[0]['morale'].append([urefs[mu.reg_read(UC_X86_REG_RCX)],delta,
            tiles[mu.reg_read(UC_X86_REG_R8)],mu.reg_read(UC_X86_REG_R9)&0xFFFFFFFF,
            word(mu.reg_read(UC_X86_REG_RSP)+0x28)])
    def setter(mu,address,size,data):
        if root(mu,0x27064C): observed[0]['setter'].append([urefs[mu.reg_read(UC_X86_REG_RCX)],mu.reg_read(UC_X86_REG_RDX)&0xFFFFFFFF])
    def set_done(mu,address,size,data):
        if observed[0]['setter']:
            index = observed[0]['setter'][-1][0]
            observed[0]['morale_after_set'].append(o.read(ctx.addr(UNITS,index)+0x1A,1)[0])
    def injury(mu,address,size,data):
        if root(mu,0x378CBB): observed[0]['injury'].append([prefs[mu.reg_read(UC_X86_REG_RCX)],mu.reg_read(UC_X86_REG_RDX)&0xFFFFFFFF,
                                                        prefs[mu.reg_read(UC_X86_REG_R8)],mu.reg_read(UC_X86_REG_R9)&0xFFFFFFFF])
    def player(mu,address,size,data):
        if root(mu,0x378B18): observed[0]['player'].append([mu.reg_read(UC_X86_REG_RCX)&0xFFFFFFFF,prefs[mu.reg_read(UC_X86_REG_RDX)]])
    for rva,hook in ((0x270550,morale),(0x241620,setter),(0x27064C,set_done),(0x27D4B0,injury),(0x378F00,player)):
        hooks.append(o.mu.hook_add(UC_HOOK_CODE,hook,begin=ctx.g.exe+rva,end=ctx.g.exe+rva))
    cases = []
    try:
        for f in fixtures:
            started = time.time()
            a,b = (ctx.addr(OFFICERS,i) for i in f['officers'])
            def execute():
                observed[0] = dict(morale=[],setter=[],morale_after_set=[],injury=[],player=[])
                o.call(0x3789E0,a,b,0,f['selector'],read_after=[(p,0x1F0) for p in persons]+[(p,0x200) for p in units])
                return dict(trace=copy.deepcopy(observed[0]),officers=[p.hex() for p in o.after[:2]],units=[p.hex() for p in o.after[2:]])
            result = ctx.run(f['pokes'],execute)
            if result is None: raise RuntimeError(f'Incomplete native reason-0 caller: {f["name"]}: {ctx.failures[-1]}')
            cases.append(dict(fixture=f,observed=result))
            print(f'  reason0 {f["name"]}: complete native ({time.time()-started:.2f}s)',flush=True)
    finally:
        for hook in hooks: o.mu.hook_del(hook)
        o.unstub()
    ctx.save('result_reason0_world.json.gz',ctx.snap)
    ctx.save('result_reason0.json.gz',dict(cases=cases,world='result_reason0_world.json.gz',officers=[first,second],
        units=[urefs[p] for p in units],native_full_body=True,native_stubs=[],scope='Positive-morale reason-0 request, setter value and injury decision/state; native full caller. Localization, UI and auxiliary storage are external C# boundaries. Zero-morale route engines remain unverified in this module.'))
    return f'Complete native positive-morale reason-0 callers: {len(cases)}'
