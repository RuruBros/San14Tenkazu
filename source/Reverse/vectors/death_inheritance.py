# Replay the entire item-inheritance body on a copied, coherent input. Gameplay functions are never stubbed.
import struct
from context import *
from corps_commands import generate as collect

COUNTIES=0x6CAF0
COVER=[0x2BDD50,0x24C590,0x254160,0x2A5EA0,0x323ED0,0x32B0E0,0x229FD0,0x2231D0]


def fixtures(ctx,snapshot,source,destination):
    registered=[n[0][1] for n in snapshot['lists']['0x100']['items']]
    owners={ctx.u16(ITEMS,i,0x4A) for i in ctx.owned}
    person=next(i for i in registered if ctx.rec[OFFICERS][i][0xE6]==4
        and ctx.u16(OFFICERS,i,0x10) not in owners and ctx.officer_force(i) in range(1,46))
    oid=ctx.u16(OFFICERS,person,0x10)
    corpse=ctx.rec[OFFICERS][person][0xE0]
    a,b,c=[i for i in registered if i!=person and ctx.officer_force(i) in range(1,46)][:3]
    receiver=a
    item,other=ctx.owned[:2]
    def word(t,i,at,value):return [hex(t),i,at,struct.pack('<H',value&0xFFFF).hex()]
    def byte(t,i,at,value):return [hex(t),i,at,bytes([value]).hex()]
    # Clear every candidate's incoming relationship to the dying officer, then explicitly add case inputs.
    clear=[]
    for i in registered:
        if i==person:continue
        for at in [0xB4,0xB6,0xBA]:
            if ctx.u16(OFFICERS,i,at)==oid:clear.append(word(OFFICERS,i,at,0))
    clear += [word(OFFICERS,i,0xBC,0) for i in registered if ctx.u16(OFFICERS,i,0xBC)==oid]
    clear += [word(OFFICERS,person,0xBC,0),[hex(OFFICERS),person,0xC0,'00'*32]]
    base=([['worldstate',0,0x3A,'00']]+clear+[word(ITEMS,i,0x4A,oid) for i in [item,other]]+
        [byte(ITEMS,item,0x4F,8),byte(ITEMS,other,0x4F,9)])
    result=[]
    def case(name,pokes=(),receiver=None,command=None,duplicates=()):
        result.append(dict(name=name,source=source,destination=destination,mode=0,person=[hex(OFFICERS),person],
            receiver=[hex(OFFICERS),receiver] if receiver is not None else None,command=command,
            duplicates=list(duplicates),pokes=base+list(pokes)))
    case('serving_fallback_ruler_id')
    case('explicit_receiver_uses_ruler',receiver=receiver)
    case('explicit_receiver_free',pokes=[byte(OFFICERS,receiver,0xE6,5)],receiver=receiver)
    case('explicit_free_receiver_corps_zero',pokes=[byte(OFFICERS,receiver,0xE6,5),byte(OFFICERS,receiver,0xE0,0)],receiver=receiver)
    case('receiver_ruler_id_clamp',receiver=receiver,
        pokes=[word(OFFICERS,ctx.officer_by_id(ctx.u16(FORCES,ctx.officer_force(receiver),0x10)),0x10,65000)])
    case('invalid_receiver_selects_child',receiver=receiver,pokes=[byte(OFFICERS,receiver,0xE6,0),
        byte(OFFICERS,b,0xE0,corpse),word(OFFICERS,b,0xB4,oid)])
    case('free_drops_items_with_rng',pokes=[byte(OFFICERS,person,0xE6,5)])
    case('ruler_without_heir_drops_items',pokes=[byte(OFFICERS,person,0xE6,1)])
    case('no_parent_counties_still_removes_items',pokes=[byte(OFFICERS,person,0xE6,5),word(OFFICERS,person,0xE2,0)])
    # County validity is not part of the iterator filter.
    home=ctx.u16(OFFICERS,person,0xE2)
    if not 1<=home<=51:raise RuntimeError('Inheritance fixture requires a real city home')
    tile=ctx.u16(0x6DA98,home,0x10)
    county=ctx.u16(TILES,tile,0x12)
    parent=ctx.rec[COUNTIES][county][0x35]
    county_ids=[n[0][1] for n in snapshot['lists']['0xb0']['items'] if ctx.rec[COUNTIES][n[0][1]][0x35]==parent]
    if len(county_ids)<2:raise RuntimeError('Inheritance fixture requires at least two registered parent counties')
    case('one_county_no_rng_draw',pokes=[byte(OFFICERS,person,0xE6,5)]+[
        byte(COUNTIES,i,0x35,0) for i in county_ids if i!=county])
    case('invalid_counties_remain_candidates',pokes=[byte(OFFICERS,person,0xE6,5)]+[
        byte(COUNTIES,i,0x34,0) for i in county_ids if i!=county])
    case('owned_item_aliases_all_removed',pokes=[byte(OFFICERS,person,0xE6,5)],duplicates=[item,item])
    def relative(i,at):return [byte(OFFICERS,i,0xE0,corpse),byte(OFFICERS,i,0xE6,4),
        [hex(OFFICERS),i,0xB4,'00'*10],word(OFFICERS,i,at,oid)]
    case('child_before_spouse',pokes=relative(a,0xBA)+relative(b,0xB4))
    case('spouse_after_child',pokes=relative(a,0xB4)+relative(b,0xBA))
    case('same_clan_before_other_child',pokes=relative(a,0xB4)+relative(b,0xB6)+[
        word(OFFICERS,person,0xB2,123),word(OFFICERS,a,0xB2,124),word(OFFICERS,b,0xB2,123)])
    case('other_clan_after_same_clan_child',pokes=relative(a,0xB4)+relative(b,0xB6)+[
        word(OFFICERS,person,0xB2,123),word(OFFICERS,a,0xB2,123),word(OFFICERS,b,0xB2,124)])
    case('sworn_insertion_order',pokes=relative(a,0xBC)+relative(b,0xBC)+relative(c,0xBC)+[word(OFFICERS,person,0xBC,oid)])
    case('liked_spouse_before_other',pokes=relative(a,0xBA)+relative(b,0xBA)+[
        word(OFFICERS,person,0xC0,ctx.u16(OFFICERS,b,0x10))])
    case('older_spouse_then_id',pokes=relative(a,0xBA)+relative(b,0xBA)+[
        word(OFFICERS,person,0xB2,0),word(OFFICERS,a,0xB2,0),word(OFFICERS,b,0xB2,0),
        word(OFFICERS,a,0xAC,200),word(OFFICERS,b,0xAC,180),['worldstate',0,0x44,'00000000']])
    case('spouse_age_tie_unsigned_id',pokes=relative(a,0xBA)+relative(b,0xBA)+[
        word(OFFICERS,person,0xB2,0),word(OFFICERS,a,0xB2,0),word(OFFICERS,b,0xB2,0),
        word(OFFICERS,a,0xAC,200),word(OFFICERS,b,0xAC,200),['worldstate',0,0x44,'00000000']])
    case('disliked_child_excluded',pokes=relative(a,0xBA)+relative(b,0xB4)+[
        word(OFFICERS,person,0xD0,ctx.u16(OFFICERS,b,0x10))])
    case('dead_prisoner_retired_relatives_excluded',pokes=relative(a,0xB4)+relative(b,0xB6)+relative(c,0xBA)+[
        byte(OFFICERS,a,0xE6,9),byte(OFFICERS,b,0xE6,6),byte(OFFICERS,c,0xE6,7)])
    tribe=next(i for i in range(1,52) if ctx.rec[CORPS][i][0x10] in range(46,51) and ctx.u16(CORPS,i,0x12))
    case('tribe_explicit_receiver_drops_items',receiver=receiver,pokes=[byte(OFFICERS,receiver,0xE0,tribe)])
    diplomatic=next(i for i,c in enumerate(snapshot['commands']) if c['kind'] in [4,5,6,7,8,9,19])
    case('diplomatic_reserved_item_preserved',command=dict(index=diplomatic,pokes=[[0x76,struct.pack('<H',item).hex()]]))
    case('invalid_command_uses_general_inheritance',command=dict(index=diplomatic,pokes=[
        [0x76,struct.pack('<H',item).hex()],[0x58,'ffffffff']]))
    for kind in [17,18]:
        command=dict(kind=kind,officers=[oid,ctx.u16(OFFICERS,b,0x10),ctx.u16(OFFICERS,a,0x10)],item=item,id=0)
        case(f'kind{kind}_first_serving_member',command=command,pokes=[byte(OFFICERS,b,0xE6,6),byte(OFFICERS,a,0xE6,4)])
        case(f'kind{kind}_member_owner_transition',command=command,pokes=[byte(ITEMS,item,0x50,2),
            byte(OFFICERS,b,0xE6,4)],duplicates=[other])
        case(f'kind{kind}_ruler_fallback',command=dict(command,officers=[oid,65000]),pokes=[])
    case('kind17_member_uses_list_id',command=dict(kind=17,officers=[oid,ctx.u16(OFFICERS,b,0x10)],item=item,id=0),
        pokes=[word(OFFICERS,b,0x10,65000),byte(OFFICERS,b,0xE6,4)])
    case('kind17_without_member_or_ruler',command=dict(kind=17,officers=[oid,65000],item=item,id=0),
        pokes=[word(FORCES,ctx.officer_force(person),0x10,0)])
    return result


def native_driver(ctx,fixture,commands):
    o=ctx.o
    code=bytearray(b'\x48\x83\xec\x28')
    def emit(rva,args):
        for opcode,value in zip([b'\x48\xb9',b'\x48\xba',b'\x49\xb8',b'\x49\xb9'],args):
            code.extend(opcode+struct.pack('<Q',value))
        code.extend(b'\x48\xb8'+struct.pack('<Q',ctx.g.exe+rva)+b'\xff\xd0')
    command=fixture['command']; pointer=0;undo=[]
    if command is not None:
        if 'index' in command:
            pointer=int(commands[command['index']]['address'],16)
            for at,data in command['pokes']:
                data=bytes.fromhex(data);undo.append((pointer+at,o.read(pointer+at,len(data))))
                o.poke(pointer+at,data)
        else:
            # Vtables and validity follow the native class constructors 0x1D7B50/0x1D7B90.
            data=bytearray(0x88)
            struct.pack_into('<Q',data,0,ctx.g.exe+(0x13A28B8 if command['kind']==17 else 0x13A2A58))
            struct.pack_into('<i',data,0x58,command['id']);struct.pack_into('<H',data,0x72,command['item'])
            pointer=o.alloc(len(data));o.poke(pointer,bytes(data))
            emit(0x18B60,[ctx.g.exe+0x1B5C9D8,0,0])
            code.extend(b'\x48\xba'+struct.pack('<Q',pointer+0x20)+b'\x48\x89\x02')
            for identifier in command['officers']:
                code.extend(b'\x48\xba'+struct.pack('<Q',pointer+0x20)+b'\x48\x8b\x12\x8b\x12')
                code.extend(b'\x48\xb9'+struct.pack('<Q',ctx.g.exe+0x1B5C990)+b'\x41\xb8\x01\x00\x00\x00')
                code.extend(b'\x48\xb8'+struct.pack('<Q',ctx.g.exe+0x18DF0)+b'\xff\xd0')
                code.extend(b'\xc7\x00'+struct.pack('<I',identifier))
    for item in fixture['duplicates']:emit(0x31F2E0,[ctx.raw,ctx.addr(ITEMS,item)])
    person=fixture['person']; receiver=fixture['receiver']
    emit(0x2BDD50,[ctx.addr(int(person[0],16),person[1]),ctx.addr(int(receiver[0],16),receiver[1]) if receiver else 0,pointer])
    code.extend(b'\x48\x83\xc4\x28\xc3')
    driver=o.alloc(len(code));o.poke(driver,bytes(code))
    return driver,pointer,undo


def generate(ctx):
    return collect(ctx,fixtures,0x2BDD50,'death_inheritance')
