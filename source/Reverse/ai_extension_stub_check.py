"""Execute the C# emitted wrapper and installer in Unicorn, checking ABI and branch ordering.

Native callees are explicit test doubles here. These checks establish the wrapper's contract,
not native pathfinding results. The disabled live-game probe is logged separately by RenderChecks.
"""
import argparse,json,struct
from pathlib import Path
from unicorn import Uc,UC_ARCH_X86,UC_MODE_64,UC_HOOK_CODE
from unicorn.x86_const import *

FUNCTIONS={'original':0xB2BC40,'agent':0xB6070,'valid':0x32F500,'corps':0x22BAE0,
           'direct':0x233960,'evade':0x201E0,'recover':0x20060,'retreat':0x61120,'hold':0x304700}
NONVOL=[UC_X86_REG_RBX,UC_X86_REG_RSI,UC_X86_REG_RDI,UC_X86_REG_R12,UC_X86_REG_R13,
        UC_X86_REG_R14,UC_X86_REG_R15,UC_X86_REG_RBP]
VOL=[UC_X86_REG_RCX,UC_X86_REG_RDX,UC_X86_REG_R8,UC_X86_REG_R9,UC_X86_REG_R10,UC_X86_REG_R11]

def run(fixture,c):
    exe,code,data=(fixture[k] for k in ('exe','code','data'))
    u=Uc(UC_ARCH_X86,UC_MODE_64); mapped=set()
    def map_page(p):
        p &= ~0xfff
        if p not in mapped:u.mem_map(p,0x1000);mapped.add(p)
    def put(p,b):
        for page in range(p&~0xfff,(p+len(b)+0xfff)&~0xfff,0x1000):map_page(page)
        u.mem_write(p,b)
    def q(p,v):put(p,struct.pack('<Q',v))
    def value(p):return struct.unpack('<Q',u.mem_read(p,8))[0]
    world,unit,corp,state,node,context,evade,recover,retreat=range(0x30000000,0x30009000,0x1000)
    put(code,bytes.fromhex(fixture['hex']));put(data,bytes(0x200))
    q(data+32,c.get('flags',5));q(data+40,recover);q(data+48,evade);q(data+56,retreat)
    put(unit,bytes(0x200));put(corp,bytes(0x200));put(state,bytes(0x200))
    put(unit+0x10,bytes([c.get('kind',1)]));put(unit+0x26,bytes([c.get('supply',0)]))
    put(unit+0x37,bytes([c.get('order',4)]));put(corp+0x10,bytes([c.get('force',45)]))
    put(state+0x3a,bytes([c.get('player',1)]));q(exe+0x277DF18,world if not c.get('no_world') else 0)
    q(world+0xe0d0+8,corp);q(world+0x871e8,state if not c.get('no_state') else 0)
    names={exe+rva:name for name,rva in FUNCTIONS.items()}
    for address in names:put(address,b'\xc3')
    stack=0x50000000
    for page in range(stack,stack+0x4000,0x1000):map_page(page)
    rsp=stack+0x3008;stop=code+0xf00;q(rsp,stop)
    u.reg_write(UC_X86_REG_RSP,rsp)
    for i,r in enumerate(NONVOL):u.reg_write(r,0xABC000+i)
    u.reg_write(UC_X86_REG_RCX,node);u.reg_write(UC_X86_REG_RDX,context);u.reg_write(UC_X86_REG_R8,unit)
    calls=[]
    def hook(u,address,size,_):
        name=names.get(address)
        if not name:return
        args=[u.reg_read(r) for r in (UC_X86_REG_RCX,UC_X86_REG_RDX,UC_X86_REG_R8)]
        assert u.reg_read(UC_X86_REG_RSP)%16==8,(name,'stack alignment')
        calls.append(name)
        if name=='original':assert args==[node,context,unit];result=2
        elif name=='agent':assert args[:2]==[context,unit];result=0 if c.get('no_unit') else unit
        elif name=='valid':result=int(args[0]!=0 and not(c.get('invalid_unit') and args[0]==unit))
        elif name=='corps':assert args[0]==unit;result=c.get('corps',1)
        elif name=='direct':assert args[0]==corp;result=c.get('direct',0)
        elif name in ('evade','recover','retreat'):
            assert args==[{'evade':evade,'recover':recover,'retreat':retreat}[name],context,unit],(name,args)
            result=c.get(name,2)
            if name=='recover' and result==1:put(unit+0x37,b'\x01')
            if name=='retreat' and result==1:put(unit+0x37,b'\x02')
        elif name=='hold':assert args[0]==unit;put(unit+0x37,b'\x0d');result=0xDEAD
        for r in VOL:u.reg_write(r,0xDEADBEEF)
        # Callees may freely overwrite all four shadow slots. The original R8 must survive outside them.
        s=u.reg_read(UC_X86_REG_RSP);u.mem_write(s+8,bytes([0xAD])*32)
        u.reg_write(UC_X86_REG_RAX,result)
    u.hook_add(UC_HOOK_CODE,hook);u.emu_start(code,stop,count=10000)
    assert u.reg_read(UC_X86_REG_RIP)==stop
    assert u.reg_read(UC_X86_REG_RSP)==rsp+8
    for i,r in enumerate(NONVOL):assert u.reg_read(r)==0xABC000+i,(c['name'],'nonvolatile')
    assert calls==c['calls'],(c['name'],calls,c['calls'])
    expected=c.get('result',2 if 'original' in calls else 1)
    assert u.reg_read(UC_X86_REG_RAX)==expected,(c['name'],'return',u.reg_read(UC_X86_REG_RAX))
    assert value(data+112)==1
    checks=int('evade' in calls and c.get('evade',2)==2)
    assert value(data+64)==checks
    return {'name':c['name'],'calls':calls,'result':expected,'checks':checks,'recovered':value(data+72),
            'returns':value(data+80),'holds':value(data+88),'order':u.mem_read(unit+0x37,1)[0],'passed':True}

def installer(fixture,expected_matches,cancelled=False):
    code,data=fixture['code'],fixture['data'];node=0x30000000;old,new=0x1111,0x2222
    u=Uc(UC_ARCH_X86,UC_MODE_64)
    u.mem_map(code,0x2000);u.mem_map(node,0x1000);u.mem_map(0x50000000,0x2000)
    u.mem_write(code+0x800,bytes.fromhex(fixture['installer']));u.mem_write(node,struct.pack('<Q',old))
    tag=0xDEAD000;prior=4
    u.mem_write(data+32,struct.pack('<Q',prior if cancelled else tag|prior))
    u.mem_write(data+128,struct.pack('<Q',tag|5))
    rsp=0x50001008;stop=code+0xf00;u.mem_write(rsp,struct.pack('<Q',stop));u.reg_write(UC_X86_REG_RSP,rsp)
    for r,v in [(UC_X86_REG_RCX,node),(UC_X86_REG_RDX,old if expected_matches else old+1),(UC_X86_REG_R8,new),(UC_X86_REG_R9,tag|prior)]:u.reg_write(r,v)
    u.emu_start(code+0x800,stop,count=100)
    assert u.reg_read(UC_X86_REG_RAX)==int(expected_matches and not cancelled)
    assert struct.unpack('<Q',u.mem_read(node,8))[0]==(new if expected_matches else old)
    assert struct.unpack('<Q',u.mem_read(data+32,8))[0]==(prior if cancelled else tag|(5 if expected_matches else prior))
    return {'name':'installer-'+str(expected_matches)+'-cancelled-'+str(cancelled),'passed':True}

def main():
    p=argparse.ArgumentParser();p.add_argument('fixture');p.add_argument('--output',default='source/RenderChecks/artifacts/ai-extensions/abi.jsonl');args=p.parse_args()
    fixture=json.loads(Path(args.fixture).read_text()); prefix=['agent','valid','corps','valid']
    rows=[]
    def case(name,calls,**kw):rows.append(run(fixture,dict(name=name,calls=calls,**kw)))
    case('disabled',['original'],flags=4)
    case('agent-missing',['agent','valid','original'],no_unit=True)
    case('unit-invalid',['agent','valid','original'],invalid_unit=True)
    case('transport',['agent','valid','original'],kind=2)
    case('supplied',['agent','valid','original'],supply=2)
    case('corps-zero',['agent','valid','corps','original'],corps=0)
    case('corps-outside-table',['agent','valid','corps','original'],corps=52)
    case('world-missing',['agent','valid','corps','original'],no_world=True)
    case('state-missing',prefix+['original'],no_state=True)
    case('neutral',prefix+['original'],force=0)
    case('direct-player',prefix+['direct','original'],force=1,direct=1)
    case('delegated-excluded',prefix+['original'],force=1,flags=1)
    case('delegated-recovery',prefix+['direct','evade','recover'],force=1,recover=1)
    case('ai-recovery',prefix+['evade','recover'],recover=1)
    case('fire-priority',prefix+['evade'],evade=1)
    case('fire-running',prefix+['evade'],evade=0,result=0)
    for order in [2,3,11,12]:case('return-kept-'+str(order),prefix+['evade'],order=order)
    case('failed-recovery-return',prefix+['evade','recover','retreat'],retreat=1)
    case('failed-retreat-hold',prefix+['evade','recover','retreat','hold'])
    case('failed-recovery-hold',prefix+['evade','recover','hold'],flags=7)
    case('held-retries-recovery',prefix+['evade','recover'],flags=7,order=13)
    rows += [installer(fixture,True),installer(fixture,False),installer(fixture,True,True)]
    Path(args.output).parent.mkdir(parents=True,exist_ok=True)
    Path(args.output).write_text('\n'.join(json.dumps(r) for r in rows)+'\n')
    print(f'PASS {len(rows)} wrapper / installer ABI scenarios')

if __name__=='__main__':main()
