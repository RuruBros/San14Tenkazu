# Compose item-bearing death and its own-unit removal through the full native death entry.
import struct
from context import *
from corps_commands import generate as collect
from free_officer import LOYALTY
from officer_death import fixtures as no_item_fixtures

COVER=[0x295AA0,0x2BDD50,0x258EE0,0x2EA6D0,0x1D7DA0,0x31ECC0,0x226300]


def fixtures(ctx,snapshot,source,destination):
    base=no_item_fixtures(ctx,snapshot,source,destination)[0]
    person=base['person'][1]
    owned={ctx.u16(ITEMS,i,0x4A) for i in ctx.owned}
    active={pid for c in snapshot['commands'] for pid in c['officers']}
    pending={n[0][1] for n in snapshot['lists']['0x70']['items']}
    unit=next(i for i,b in enumerate(ctx.rec[UNITS]) if i and b[0x10] and ctx.u16(UNITS,i,0x12)
        and i not in pending and (p:=ctx.officer_by_id(ctx.u16(UNITS,i,0x12))) is not None
        and ctx.rec[OFFICERS][p][0xE6]==4 and ctx.rec[OFFICES][ctx.rec[OFFICERS][p][0xE7]][0x37]>=2
        and ctx.officer_force(p) in range(1,46) and ctx.u16(OFFICERS,p,0x10) not in owned|active
        and not(ctx.u16(OFFICERS,p,0x15E)&0x4000) and struct.unpack_from('<Q',ctx.rec[OFFICERS][p],0x1B0)[0]<=7)
    commander=ctx.officer_by_id(ctx.u16(UNITS,unit,0x12))
    item,exclusive=ctx.owned[:2]
    def word(t,i,at,value):return [hex(t),i,at,struct.pack('<H',value&0xFFFF).hex()]
    def items(p):return [word(ITEMS,i,0x4A,ctx.u16(OFFICERS,p,0x10)) for i in [item,exclusive]]+[
        [hex(ITEMS),item,0x4F,'08'],[hex(ITEMS),exclusive,0x4F,'09']]
    result=[]
    def case(name,p=person,destroy=0,pokes=(),pending=()):
        result.append(dict(base,name=name,person=[hex(OFFICERS),p],destroy_own_unit=destroy,
            inherits=True,pending_units=list(pending),pokes=base['pokes']+list(pokes)))
    case('serving_death_items',pokes=items(person))
    case('free_death_items',pokes=items(person)+[[hex(OFFICERS),person,0xE6,'05']])
    marked=[word(OFFICERS,person,0x15E,ctx.u16(OFFICERS,person,0x15E)|0x4000),[hex(OFFICERS),person,0x1C5,'01']]
    case('marked_serving_items_deferred',pokes=items(person)+marked)
    case('marked_free_items_actual_death',pokes=items(person)+marked+[[hex(OFFICERS),person,0xE6,'05']])
    case('prisoner_death_items_home_ruler',pokes=items(person)+[[hex(OFFICERS),person,0xE6,'06']])
    case('own_unit_death',p=commander,destroy=1)
    case('own_unit_death_items',p=commander,destroy=1,pokes=items(commander))
    case('own_unit_removal_disabled',p=commander)
    case('own_unit_invalid_skips_engine',p=commander,destroy=1,pokes=[[hex(UNITS),unit,0x10,'00']])
    case('own_unit_pending_skips_engine',p=commander,destroy=1,pending=[unit])
    return result


def pending_driver(ctx,fixture):
    code=bytearray(b'\x48\x83\xec\x58')
    def emit(rva,args):
        for opcode,value in zip([b'\x48\xb9',b'\x48\xba',b'\x49\xb8',b'\x49\xb9'],args):
            code.extend(opcode+struct.pack('<Q',value))
        code.extend(b'\x48\xb8'+struct.pack('<Q',ctx.g.exe+rva)+b'\xff\xd0')
    for unit in fixture['pending_units']:emit(0x31ECC0,[ctx.raw,ctx.addr(UNITS,unit)])
    for at,value in zip([0x20,0x28,0x30,0x38,0x40],[1,0,1,1,0]):
        code.extend(b'\x48\xc7\x44\x24'+bytes([at])+struct.pack('<I',value))
    emit(0x295AA0,[ctx.addr(OFFICERS,fixture['person'][1]),0,1,fixture['destroy_own_unit']])
    code.extend(b'\x48\x83\xc4\x58\xc3')
    driver=ctx.o.alloc(len(code));ctx.o.poke(driver,bytes(code))
    return driver


def generate(ctx):
    return collect(ctx,fixtures,0x295AA0,'officer_death_extended',capture_free_lists=True,extra_tables=[(LOYALTY,0x38,7)])
