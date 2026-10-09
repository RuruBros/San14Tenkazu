# Validate the ordinary NPC death caller with a real enemy officer and the complete relationship body.
import struct
from context import *
from corps_commands import generate as collect
from free_officer import LOYALTY
from officer_death import fixtures as no_item_fixtures

COVER=[0x295AA0,0x2700A0,0x2586E0,0x2BDD50,0x226300]


def fixtures(ctx,snapshot,source,destination):
    base=no_item_fixtures(ctx,snapshot,source,destination)[0]
    person=base['person'][1];force=ctx.officer_force(person)
    attacker=next(n[0][1] for n in snapshot['lists']['0x100']['items']
        if ctx.rec[OFFICERS][n[0][1]][0xE6]==4 and ctx.officer_force(n[0][1]) in range(1,46)
        and ctx.officer_force(n[0][1])!=force and not(ctx.u16(OFFICERS,n[0][1],0x15E)&0x4000)
        and not 5001<=ctx.u16(OFFICERS,n[0][1],0x10)<=5100)
    enemy=ctx.officer_force(attacker)
    items=[p for i in ctx.owned[:2] for p in [[hex(ITEMS),i,0x4A,struct.pack('<H',ctx.u16(OFFICERS,person,0x10)).hex()],
        [hex(ITEMS),i,0x4F,'09']]]
    relations=[[hex(FORCES),force,0x13+enemy,'50'],[hex(FORCES),enemy,0x13+force,'28']]
    result=[]
    def case(name,extra=()):
        result.append(dict(base,name=name,inherits=True,attacker=[hex(OFFICERS),attacker],pokes=base['pokes']+relations+list(extra)))
    case('enemy_attacker_relation_loss')
    case('enemy_attacker_inherits_items',items)
    case('free_officer_enemy_inherits_items',items+[[hex(OFFICERS),person,0xE6,'05']])
    case('invalid_attacker_uses_general_inheritance',items+[[hex(OFFICERS),attacker,0xE6,'00']])
    case('marked_serving_relation_then_deferral',items+[[hex(OFFICERS),person,0x15E,
        struct.pack('<H',ctx.u16(OFFICERS,person,0x15E)|0x4000).hex()],[hex(OFFICERS),person,0x1C5,'01']])
    case('relation_lower_clamp',[[hex(FORCES),force,0x13+enemy,'02'],[hex(FORCES),enemy,0x13+force,'00']])
    return result


def generate(ctx):return collect(ctx,fixtures,0x295AA0,'officer_death_attacker',capture_free_lists=True,extra_tables=[(LOYALTY,0x38,7)])
