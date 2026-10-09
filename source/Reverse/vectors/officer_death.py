# Replay ordinary/no-item death and marked deferred removal through the entire native 0x295AA0 body.
import struct
from context import *
from corps_commands import generate as collect
from free_officer import LOYALTY

COVER=[0x295AA0,0x2BDD50,0x226300,0x241C30,0x32B1E0,0x32B5A0,0x258EE0]


def fixtures(ctx,snapshot,source,destination):
    active={pid for c in snapshot['commands'] for pid in c['officers']}
    owners={ctx.u16(ITEMS,n[0][1],0x4A) for n in snapshot['lists']['0x110']['items']}
    registered={n[0][1] for n in snapshot['lists']['0x100']['items']}
    ordinary=[i for i,b in enumerate(ctx.rec[OFFICERS]) if i in registered and b[0xE6]==4
        and ctx.u16(OFFICERS,i,0x10) not in active|owners and ctx.rec[OFFICES][b[0xE7]][0x37]>=2
        and struct.unpack_from('<Q',b,0x1B0)[0]<=7 and ctx.officer_force(i) in range(1,46)
        and not(ctx.u16(OFFICERS,i,0x15E)&0x4000)]
    if not ordinary:raise RuntimeError('This snapshot has no ordinary no-item officer outside the death-notice grade')
    person=ordinary[0]
    oid=ctx.u16(OFFICERS,person,0x10)
    prefix=[['worldstate',0,0x3A,'00'],['exe','challenge_active',0,'00000000']]
    def word(t,i,at,value):return [hex(t),i,at,struct.pack('<H',value&0xFFFF).hex()]
    result=[]
    def case(name,pokes=(),destroy=0,target=person):
        result.append(dict(name=name,source=source,destination=destination,mode=0,person=[hex(OFFICERS),target],
            destroy_own_unit=destroy,pokes=prefix+list(pokes)))
    case('ordinary_no_items')
    case('popularity_fame_zero',[[hex(OFFICERS),person,0x1BC,'00']])
    force=ctx.officer_force(person)
    case('popularity_signed_floor',[word(FORCES,force,0x1BE,-999),[hex(OFFICERS),person,0x1BC,'ff']])
    a,b,c=[i for i in range(1,len(ctx.rec[OFFICERS])) if i!=person][:3]
    relations=[]
    for i in [a,b,c]:relations += [word(OFFICERS,i,0xBA,oid),[hex(OFFICERS),i,0xC0,struct.pack('<8H',oid,0,oid,0,0,0,0,oid).hex()],
                                  [hex(OFFICERS),i,0xD0,struct.pack('<8H',0,oid,0,oid,0,0,0,0).hex()]]
    case('relationship_aliases_all_slots',relations)
    # Ensure other original groups do not alias the fixture's group ID; all inputs are explicit record pokes.
    clear_group=[word(OFFICERS,i,0xBC,0) for i in range(len(ctx.rec[OFFICERS])) if ctx.u16(OFFICERS,i,0xBC)==oid and i!=person]
    leader=[word(OFFICERS,person,0xBC,oid)]
    case('sworn_singleton_cleared',clear_group+leader+[word(OFFICERS,a,0xBC,oid)])
    case('sworn_leader_birthyear_then_id',clear_group+leader+[word(OFFICERS,i,0xBC,oid) for i in [a,b,c]]+
         [word(OFFICERS,a,0xAC,201),word(OFFICERS,b,0xAC,200),word(OFFICERS,c,0xAC,200)])
    case('marked_serving_deferred',[word(OFFICERS,person,0x15E,ctx.u16(OFFICERS,person,0x15E)|0x4000),
         [hex(OFFICERS),person,0x1C5,'01']])
    case('marked_free_actual_death',[word(OFFICERS,person,0x15E,ctx.u16(OFFICERS,person,0x15E)|0x4000),
         [hex(OFFICERS),person,0x1C5,'01'],[hex(OFFICERS),person,0xE6,'05']])
    case('prisoner_no_items',[[hex(OFFICERS),person,0xE6,'06']])
    home=next(n[0] for n in snapshot['lists']['0x80']['items']
        if ctx.u16(int(n[0][0],16),n[0][1],0x4E)==ctx.u16(OFFICERS,person,0xE2))
    case('governor_reselected',[[hex(OFFICERS),person,0xE6,'03'],word(int(home[0],16),home[1],0x2E,oid)])
    return result


def generate(ctx):
    return collect(ctx,fixtures,0x295AA0,'officer_death',capture_free_lists=True,extra_tables=[(LOYALTY,0x38,7)])
