# Replay reason4/mode0 without either attacker, before composing the verified C# dependencies.
from context import *
from corps_commands import generate as collect
from free_officer import LOYALTY

COVER = [0x258EE0,0x2A1F00,0x2A2100,0x2EA6D0,0x230B10,0x2E75C0,0x31ECC0]


def fixtures(ctx,snapshot,source,destination):
    active={pid for command in snapshot['commands'] for pid in command['officers']}
    unit=next(i for i,b in enumerate(ctx.rec[UNITS]) if i and b[0x10] and ctx.u16(UNITS,i,0x12) and
              ctx.officer_force(ctx.officer_by_id(ctx.u16(UNITS,i,0x12))) in range(1,46) and
              ctx.u16(UNITS,i,0x12) not in active)
    commander=ctx.officer_by_id(ctx.u16(UNITS,unit,0x12))
    def word(table,index,at,value): return [hex(table),index,at,struct.pack('<H',value&0xFFFF).hex()]
    def dword(table,index,at,value): return [hex(table),index,at,struct.pack('<i',value).hex()]
    def option(at,value): return ['settings',0,at,struct.pack('<i',value).hex()]
    prefix=[['worldstate',0,0x3A,'00'],['exe','challenge_active',0,'00000000']]
    result=[]
    def case(name,pokes=()):
        result.append(dict(name=name,source=source,destination=destination,person=[hex(OFFICERS),commander],
            unit=[hex(UNITS),unit],attacker=None,attacker_base=None,mode=0,reason=4,pokes=prefix+list(pokes)))
    case('ordinary_mode4_no_attacker')
    case('invalid_unit_guard',[[hex(UNITS),unit,0x10,'00']])
    # This branch only reads the caller list and returns before any pool operation.
    case('already_pending_guard',[['listdata','0x70',snapshot['lists']['0x70']['items']+[[[hex(UNITS),unit]]],'']])
    home_id=ctx.u16(OFFICERS,commander,0xE2)
    home=next(n[0] for n in snapshot['lists']['0x80']['items']
              if ctx.u16(int(n[0][0],16),n[0][1],0x4E)==home_id)
    htable,hindex=int(home[0],16),home[1]
    wounded=[word(UNITS,unit,0x18,1001),dword(htable,hindex,0x40,0)]
    case('preset_home_wounded',wounded+[option(0xE58,1),option(0xCC,0)])
    case('preset_home_wounded_disabled',wounded+[option(0xE58,1),option(0xCC,1)])
    case('custom_home_wounded_total_cap',wounded+[word(UNITS,unit,0x16,11),
         option(0xE58,0),option(0x29C,1000)])
    case('custom_home_wounded_disabled',wounded+[option(0xE58,0),option(0x29C,0)])
    case('home_wounded_signed_capacity_clamp',wounded+[dword(htable,hindex,0x40,2147483640),
         option(0xE58,1),option(0xCC,0)])
    case('loyalty_loss_byte_wrap',[word(OFFICERS,commander,0x16C,-1300),[hex(OFFICERS),commander,0xE8,'50']])
    case('loyalty_flag_forces_255',[word(OFFICERS,commander,0x15E,ctx.u16(OFFICERS,commander,0x15E)|0x2000),
         word(OFFICERS,commander,0x16C,-1300),[hex(OFFICERS),commander,0xE8,'50']])
    case('lost_home_unmarked_commander',[word(OFFICERS,commander,0xE2,0),
         word(OFFICERS,commander,0x15E,ctx.u16(OFFICERS,commander,0x15E)&~0x4000)])
    registered=[n[0][1] for n in snapshot['lists']['0x60']['items'] if n[0][1]!=unit]
    peer,invalid,other=registered[:3]
    taunts=[]
    for i,target in [(peer,unit),(invalid,unit),(other,unit+1)]:
        taunts += [[hex(UNITS),i,0x25,'7b'],[hex(UNITS),i,0x3C,'00'],word(UNITS,i,0x3E,target),
                   [hex(UNITS),i,0x37,'07'],[hex(UNITS),i,0x38,'1b'],word(UNITS,i,0x3A,target)]
    case('registered_taunt_and_order_cleanup',taunts+[[hex(UNITS),invalid,0x10,'00']])
    leaders={ctx.u16(UNITS,i,0x12) for i,b in enumerate(ctx.rec[UNITS]) if b[0x10]}
    captive=next(n[0][1] for n in snapshot['lists']['0x100']['items']
        if ctx.rec[OFFICERS][n[0][1]][0xE6]==4 and ctx.u16(OFFICERS,n[0][1],0x10) not in active|leaders
        and ctx.officer_force(n[0][1]) in range(1,46))
    case('release_registered_prisoner',[[hex(OFFICERS),captive,0xE6,'06'],word(OFFICERS,captive,0xE4,unit+61),
         word(OFFICERS,captive,0x15E,ctx.u16(OFFICERS,captive,0x15E)&~0x4000)])
    case('force_51_reset_commander',[[hex(OFFICERS),commander,0xE0,'33'],[hex(CORPS),51,0x10,'3301']])
    case('tribe_outside_reset_commander',[[hex(OFFICERS),commander,0xE0,'2e'],[hex(CORPS),46,0x10,'2e01'],
         word(OFFICERS,commander,0xE2,0)])
    ally=next(i for i in registered if ctx.rec[UNITS][i][0x10] and
              ctx.officer_force(ctx.officer_by_id(ctx.u16(UNITS,i,0x12)))==ctx.officer_force(commander))
    ally_commander=ctx.officer_by_id(ctx.u16(UNITS,ally,0x12))
    center=ctx.u16(UNITS,unit,0x2A)
    # Direction2 is the north neighbor in both column parities.
    near=center-220
    if center<220 or ctx.u16(TILES,near,0x1C):
        raise RuntimeError('A vacant north neighbor is required for the recorded local-effect input')
    old_hex=ctx.u16(UNITS,ally,0x2A)
    nearby=[word(TILES,old_hex,0x1C,0),word(TILES,near,0x1C,ally),word(UNITS,ally,0x2A,near),
            [hex(UNITS),ally,0x1A,'64']]
    office=next(i for i,b in enumerate(ctx.rec[OFFICES]) if i and b[0x37]<=8)
    case('nearby_office_morale_loss',nearby+[[hex(OFFICERS),commander,0xE7,bytes([office]).hex()]])
    force=ctx.officer_force(commander)
    case('nearby_ruler_morale_floor',nearby+[word(FORCES,force,0x10,ctx.u16(OFFICERS,commander,0x10)),
         [hex(UNITS),ally,0x1A,'02']])
    positive,negative=[i for i,b in enumerate(ctx.rec[0x7DD18]) if i and b[0xA4]==0x27][:2]
    case('nearby_condition_27_duplicate_slots',nearby+[[hex(OFFICERS),ally_commander,0x118,
         struct.pack('<5H',positive,positive,negative,0,0).hex()],
         word(0x7DD18,positive,0xB8,7),word(0x7DD18,positive,0xBC,3),
         word(0x7DD18,negative,0xB8,-9),word(0x7DD18,negative,0xBC,-2)])
    return result


def generate(ctx):
    return collect(ctx,fixtures,0x258EE0,'unit_destruction',capture_free_lists=True,extra_tables=[(LOYALTY,0x38,7)])
