# Whole copied native history storage, including fixed-field tails and the game-state request.
import copy, struct
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RCX, UC_X86_REG_RDX
from engine_audit_state import EngineAuditState
from unit_finalization import capture_native_pools

HISTORY = 0x79E90
COVER = [0x934400, 0x30DE00, 0x241780, 0x2417A0, 0x2412B0, 0x2ACD50]

def generate(ctx, snapshot, retained=()):
    if not ctx.frozen: raise RuntimeError('History storage requires a coherent frozen input')
    o = ctx.o; capture_native_pools(ctx); audit = EngineAuditState(ctx)
    audit.tables[HISTORY] = (ctx.addr(HISTORY,0),0x140,2001)
    audit.tables[0x85A90] = (ctx.addr(0x85A90,0),0x78,101)
    audit.wrappers.append(('registered_counties',ctx.raw+0xB0))
    baseline = audit.read(); requests = []
    def case(name, **kwargs):
        f=dict(name=name,kind=1,corps=0,hex=0,player=1,tick=325,primary='Primary',secondary='Secondary',officer=5012,
               immediate=0,flag4=0,flag5=0,flag6=0,pokes=[],suppressed=False,null_record=False)
        f.update(kwargs); requests.append(f)
    case('plain'); case('empty_primary',primary=''); case('both_empty',primary='',secondary='')
    for n in (54,55,56): case(f'primary_length_{n}',primary='P'*n)
    for n in (84,85,86): case(f'secondary_length_{n}',secondary='S'*n)
    case('embedded_terminators',primary='A\0Ignored',secondary='B\0Ignored')
    case('all_flags',kind=7,corps=52,hex=48400,player=-1,tick=65536,officer=6001,immediate=-1,flag4=-1,flag5=-1,flag6=-1)
    case('signed_low_bounds',kind=-1,corps=-1,hex=-1,player=0,tick=-1,officer=0,primary='')
    case('exact_high_bounds',kind=6,hex=48399,tick=65535,officer=6000)
    case('old_flag_bits',pokes=[['0x79e90',46,0x82,'ff']])
    case('valid_corps',corps=3)
    case('out_of_range_corps',corps=2147483647)
    case('counter_1999',pokes=[['worldstate',0,0x165A,struct.pack('<H',1999).hex()]])
    case('counter_2000',pokes=[['worldstate',0,0x165A,struct.pack('<H',2000).hex()]])
    case('counter_65535',pokes=[['worldstate',0,0x165A,'ffff']])
    case('suppressed',suppressed=True)
    case('missing_record',null_record=True)
    primary=o.alloc(512);secondary=o.alloc(512);initial=o.alloc(4)
    slot=ctx.g.exe+0x27842A0;controller=o.q(slot);scratch=o.alloc(0x200)
    prior={c['fixture']['name']:c for c in retained};cases=[];refreshes=[]
    refs={ctx.addr(HISTORY,i):i for i in range(2001)}
    def refresh(mu,address,size,user):
        pointer=mu.reg_read(UC_X86_REG_RDX)
        if pointer not in refs:raise RuntimeError('Unexpected refreshed history pointer')
        refreshes.append(refs[pointer])
    hook=o.mu.hook_add(UC_HOOK_CODE,refresh,begin=ctx.g.exe+0x43A630,end=ctx.g.exe+0x43A630)
    try:
        for f in requests:
            if f['name'] in prior:
                if f!=prior[f['name']]['fixture']:raise RuntimeError('Retained history fixture differs')
                cases.append(prior[f['name']]);continue
            refreshes.clear();saved=[(slot,o.read(slot,8))]
            target=controller or scratch;saved.append((target+0x1B0,o.read(target+0x1B0,4)))
            try:
                o.poke(slot,struct.pack('<Q',target));o.poke(target+0x1B0,struct.pack('<i',int(f['suppressed'])))
                o.poke(primary,f['primary'].encode('utf-16-le')+bytes(2));o.poke(secondary,f['secondary'].encode('utf-16-le')+bytes(2))
                def run():
                    index=(struct.unpack('<H',o.read(audit.ws+0x165A,2))[0]+1)%2001
                    table_slot=ctx.raw+HISTORY+index*8;original=o.read(table_slot,8)
                    before=audit.read();output=[];abbr=''
                    if f['corps']>=0 and f['primary'] and f['primary'][0]!='\0':
                        corps=ctx.addr(0xE0D0,f['corps'] if f['corps']<=51 else 0)
                        valid=o.call(0x22DC80,corps)!=0
                        force=o.read(corps+0x10,1)[0] if valid else 0
                        o.call(0x2ACD50,ctx.addr(0xDF30,force if force<=51 else 0),initial,read_after=[(initial,4)])
                        abbr=o.after[0].hex()
                    try:
                        if f['null_record']:o.poke(table_slot,bytes(8))
                        def after():output.append(audit.delta(before,audit.read()));return []
                        result=o.call(0x934400,ctx.g.exe+0x1BB35F8,f['kind'],f['corps'],f['hex'],
                            stack_args=[f['player'],f['tick'],primary,secondary,f['officer'],f['immediate'],f['flag4'],f['flag5'],f['flag6']],read_after=after)
                    finally:
                        if f['null_record']:o.poke(table_slot,original)
                    if set(output[0])-{'records','worldstate'}:raise RuntimeError('Unexpected history gameplay mutation')
                    return dict(fixture=f,index=index,after=output[0],force_initial=abbr,refreshes=list(refreshes),returned=None if result==0 else refs.get(result,'unknown'))
                c=ctx.run(f['pokes'],run)
                if c is None:raise RuntimeError(f'{f["name"]}: {ctx.failures[-1]}')
                if c['returned']=='unknown':raise RuntimeError('Unexpected native history return pointer')
                cases.append(c);ctx.save('history_log_progress.json.gz',dict(cases=cases));print('  history',f['name'],'complete',flush=True)
            finally:
                for address,data in reversed(saved):o.poke(address,data)
        if audit.read()!=baseline:raise RuntimeError('History output did not roll back')
        ctx.save('history_log_world.json.gz',copy.deepcopy(snapshot))
        ctx.save('history_log.json.gz',dict(world='history_log_world.json.gz',cases=cases,complete=True,gameplay_stubs=False,allocator_stubs=False,
          scope='Whole 934400 fixed history record storage, temporary native strings, and logical game-state refresh request; physical UI heap is separate.'))
        return len(cases)
    finally:
        o.mu.hook_del(hook);o.unstub()
