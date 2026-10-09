# Validate corps-leader death, preserving the native merge-before-reset and reevaluate-after-reset order.
import struct
from context import *
from corps_commands import generate as collect
from free_officer import LOYALTY
from officer_death import fixtures as no_item_fixtures

COVER=[0x295AA0,0x2301F0,0x293050,0x2E8E70,0x2BDD50,0x226300]
CITY=0xDD38


def fixtures(ctx,snapshot,source,destination):
    base=no_item_fixtures(ctx,snapshot,source,destination)[0]
    registered=[n[0][1] for n in snapshot['lists']['0x100']['items']]
    active={pid for c in snapshot['commands'] for pid in c['officers']}
    def people(c):return [i for i in registered if ctx.rec[OFFICERS][i][0xE0]==c and ctx.rec[OFFICERS][i][0xE6] in [1,2,3,4]]
    eligible=[]
    for c,b in enumerate(ctx.rec[CORPS]):
        leader=ctx.officer_by_id(ctx.u16(CORPS,c,0x12))
        if c and b[0x10] in range(1,46) and b[0x11]>1 and leader in registered:
            p=ctx.rec[OFFICERS][leader]
            if p[0xE6]==2 and ctx.rec[OFFICES][p[0xE7]][0x37]>=2 and not(ctx.u16(OFFICERS,leader,0x15E)&0x4000) and struct.unpack_from('<Q',p,0x1B0)[0]<=7:
                eligible.append((c,leader))
    corps,leader=min(((c,p) for c,p in eligible if ctx.u16(OFFICERS,p,0x10) not in active),key=lambda v:len(people(v[0])))
    force=ctx.rec[CORPS][corps][0x10]
    main=next(n[0][1] for n in snapshot['lists']['0xd0']['items'] if ctx.rec[CORPS][n[0][1]][0x10]==force
        and ctx.rec[CORPS][n[0][1]][0x11] and ctx.u16(CORPS,n[0][1],0x12)
        and (ctx.u16(CORPS,n[0][1],0x12)==ctx.u16(FORCES,force,0x10) or ctx.rec[CORPS][n[0][1]][0x11]==1))
    if main==corps:raise RuntimeError('This fixture requires a distinct surviving main corps')
    def word(t,i,at,v):return [hex(t),i,at,struct.pack('<H',v&0xFFFF).hex()]
    def items(p):return [entry for i in ctx.owned[:2] for entry in
        [word(ITEMS,i,0x4A,ctx.u16(OFFICERS,p,0x10)),[hex(ITEMS),i,0x4F,'09']]]
    single=[[hex(OFFICERS),i,0xE0,bytes([main]).hex()] for i in people(corps) if i!=leader]
    no_cities=[[hex(CITY),i,0x30,bytes([main]).hex()] for i,b in enumerate(ctx.rec[CITY]) if b[0x30]==corps]
    marked=[word(OFFICERS,leader,0x15E,ctx.u16(OFFICERS,leader,0x15E)|0x4000),[hex(OFFICERS),leader,0x1C5,'01']]
    result=[]
    def case(name,p=leader,c=corps,pokes=(),merges=0,checks=1,attacker=None):
        result.append(dict(base,name=name,source=c,person=[hex(OFFICERS),p],destroy_own_unit=0,inherits=True,
            attacker=[hex(OFFICERS),attacker] if attacker is not None else None,
            expected_merges=merges,expected_checks=checks,pokes=base['pokes']+list(pokes)))
    case('retained_corps_replaces_dead_leader')
    case('retained_corps_leader_items',pokes=items(leader))
    case('single_leader_merges_before_death',pokes=single,merges=1,checks=0)
    case('single_leader_items_after_merge',pokes=single+items(leader),merges=1,checks=0)
    case('marked_leader_defers_without_recheck',pokes=marked+items(leader),checks=0)
    case('single_marked_leader_merges_then_defers',pokes=single+marked+items(leader),merges=1,checks=0)
    case('no_cities_merges_after_death',pokes=no_cities,merges=1)
    case('no_cities_items_then_merge',pokes=no_cities+items(leader),merges=1)
    enemy=next(i for i in registered if ctx.rec[OFFICERS][i][0xE6]==4 and ctx.officer_force(i) in range(1,46)
        and ctx.officer_force(i)!=force and not(ctx.u16(OFFICERS,i,0x15E)&0x4000))
    case('enemy_relation_and_leader_inheritance',pokes=items(leader),attacker=enemy)
    ac,ap=next((c,p) for c,p in eligible if ctx.u16(OFFICERS,p,0x10) in active)
    case('active_leader_removed_from_command',p=ap,c=ac)
    return result


def generate(ctx):return collect(ctx,fixtures,0x295AA0,'officer_death_corps',capture_free_lists=True,extra_tables=[(LOYALTY,0x38,7)])
