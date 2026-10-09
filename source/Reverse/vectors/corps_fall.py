# Real retained-force corps reevaluation, including active-command mergers and main-corps replacement.
import struct
from context import *
from corps_commands import generate as collect

COVER = [0x2E8E70,0x293050,0x2543D0,0x2ACB50,0x2301F0,0x230410]


def fixtures(ctx,snapshot,source,destination):
    force=ctx.rec[CORPS][source][0x10]
    group=[n[0][1] for n in snapshot['lists']['0xd0']['items']
           if ctx.rec[CORPS][n[0][1]][0x10]==force and ctx.rec[CORPS][n[0][1]][0x11] and ctx.u16(CORPS,n[0][1],0x12)]
    ruler=ctx.u16(FORCES,force,0x10)
    main=next(i for i in group if ctx.u16(CORPS,i,0x12)==ruler or ctx.rec[CORPS][i][0x11]==1)
    if main==source or len(group)<3: raise RuntimeError('A secondary corps and multiple actual main-corps candidates are required')
    leader=ctx.officer_by_id(ctx.u16(CORPS,main,0x12))
    if ctx.rec[OFFICERS][leader][0xE6]!=1: raise RuntimeError('This main-corps fixture requires its actual ruler')
    def p(t,i,at,v,fmt='B'): return [hex(t),i,at,struct.pack('<'+fmt,v).hex()]
    def people(c): return [i for i,b in enumerate(ctx.rec[OFFICERS]) if b[0xE0]==c and b[0xE6] in [1,2,3,4]]
    common=[['worldstate',0,0x3A,'00']]
    def f(name,corp=source,pokes=None,mode=0):
        return dict(name=name,source=corp,destination=0,mode=mode,pokes=common+(pokes or []))
    main_prisoners=[p(OFFICERS,i,0xE6,6) for i in people(main) if i!=leader]
    return [f('retained_secondary'),f('null_corps',None),f('zero_corps',0),
        f('invalid_leader_rejected',pokes=[p(CORPS,source,0x12,0,'H')]),
        f('invalid_leader_structural_override',pokes=[p(CORPS,source,0x12,0,'H')],mode=1),
        f('secondary_without_cities',pokes=[p(0xDD38,i,0x30,destination) for i,b in enumerate(ctx.rec[0xDD38]) if b[0x30]==source]),
        f('secondary_without_serving',pokes=[p(OFFICERS,i,0xE6,6) for i in people(source)]),
        f('main_without_cities',main,main_prisoners+[p(0xDD38,i,0x30,destination) for i,b in enumerate(ctx.rec[0xDD38]) if b[0x30]==main]),
        f('main_without_counted_officers',main,main_prisoners+[p(OFFICERS,leader,0x15E,ctx.u16(OFFICERS,leader,0x15E)|0x4000,'H')])]


def generate(ctx):
    return collect(ctx,fixtures,0x2E8E70,'corps_fall')
