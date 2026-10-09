# Resolve UI labels through actual option copies, column metadata and native formatters.
# python ai_field_names.py [--vectors ../Tests/AiFieldNameVectors.json]
import argparse
from datetime import datetime, timezone
import hashlib
from pathlib import Path
import struct
from ai_executor_audit import Harness
from ai_facility_special import read_clock
from ai_friend import HERE, vectors_text
from ai_native_snapshot import ListPoolSnapshot
from image import Image

def wide(o, pointer):
    b=bytearray()
    for n in range(1024):
        s=o.read(pointer+n*2,2)
        if s==b'\0\0':return b.decode('utf-16le')
        b.extend(s)
    raise AssertionError('unterminated native text')

def verify(game):
    before=read_clock(game);h=Harness(game);o=h.o;im=Image();cases=[]
    options=o.call(0x3E9C90);copy=o.alloc(0x700)
    identities=[dict(predicate=148,custom=0x404,preset=0x10C,stack=0xF4,labelId=0x7F3,formatter=0x30B130),
                dict(predicate=149,custom=0x41C,preset=0x110,stack=0xF8,labelId=0x7F4,formatter=0x30B0B0)]
    for identity in identities:
        identity['label']=wide(o,o.call(0x30CF80,identity['labelId']))
        identity['choices']=[wide(o,o.call(identity['formatter'],v)) for v in (0,1)]
        for value in (-1,0,1,2,0x5148):
            # UI 5D18DE..5D18F2 copies Options+8 to rbp-10, preserving relative offsets.
            o.poke(options+identity['preset'],struct.pack('<i',value))
            source=o.call(0x7FEB90,options)
            o.call(0x3E47D0,copy,source,keep=True)
            relative=identity['stack']+0x10
            actual=struct.unpack('<i',o.read(copy+relative,4))[0]
            if source!=options+8 or actual!=value or relative+8!=identity['preset']:
                raise AssertionError('option UI field translation')
            cases.append(dict(kind='optionCopy',predicate=identity['predicate'],input=value,
                              native=actual,relative=relative,optionsOffset=identity['preset']))
    metadata=[]
    for role,record,formatter in (('han',0x1A2B850+188*0x60,0x35B400),('tradeFriendship',0x1A38F00+0x60,0x35ABE0)):
        data=o.read(o.exe+record,0x60);function=struct.unpack_from('<Q',data,0x18)[0]-o.exe
        label_id=struct.unpack_from('<I',data,0x48)[0]
        if function!=formatter: raise AssertionError('actual column formatter differs from static code')
        metadata.append(dict(role=role,record=record,bytes=data.hex(),formatter=function,labelId=label_id,
                             label=wide(o,o.call(0x30CF80,label_id))))
    person=game.officer(736)
    for value in (-32768,-20,-10,0,10,20,32767):
        o.poke(person+0x17A,struct.pack('<h',value))
        # Preserve copied writes so the returned temporary string can be read before it is recycled.
        text=wide(o,o.call(0x35B400,person,keep=True))
        cases.append(dict(kind='hanDisplay',input=value,native=int(text)))
    countries=h.valid_table(0x85A20,5);force=o.q(h.raw+0xDF30+29*8);out=o.alloc(16)
    if int.from_bytes(o.read(o.exe+0x277E23C,4),'little')!=29:
        raise AssertionError('formatter must select actual player force 29')
    for country in countries:
        kind=o.call32(0x22DCB0,country);at=max(0,min(4,kind))
        for value in (0,1,999,1000,1001,65535):
            o.poke(force+0x184+at*2,struct.pack('<H',value))
            o.call(0x3489D0,out,country,read_after=[(out,12)])
            number=struct.unpack_from('<I',o.after[0],8)[0]
            text=wide(o,o.call(0x35ABE0,country,keep=True))
            if number!=value or text!=str(value): raise AssertionError('native trade numeric/text formatter')
            cases.append(dict(kind='tradeDisplay',countryKind=kind,input=value,native=number,text=text))
    after=read_clock(game)
    return dict(utc=datetime.now(timezone.utc).isoformat(),clockBefore=before,clockAfter=after,clockStable=before==after,
        method='native option structure copy and game message getters; actual live UI column records and native formatters; copied field inputs only',
        oracle_sha256=hashlib.sha256((HERE/'oracle.py').read_bytes()).hexdigest(),
        identities=identities,metadata=metadata,cases=cases,
        static={hex(rva):im.flowtext(rva) for rva in (0x7FEB90,0x3E47D0,0x3E4A10,0x3E9C60,0x3E9C00,
            0x30B130,0x30B0B0,0x35B400,0x35ABE0,0x3489D0)},
        optionUI=im.text(0x5D3222,0x5D329D),optionCopyUI=im.text(0x5D18DE,0x5D18F7))

def main():
    p=argparse.ArgumentParser();p.add_argument('--vectors',type=Path);args=p.parse_args()
    result=verify(ListPoolSnapshot());folder=HERE/'.cache'/'ai_field_names';folder.mkdir(parents=True,exist_ok=True)
    path=folder/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.json');text=vectors_text(result)
    path.write_text(text,encoding='utf-8')
    if args.vectors and result['clockStable']:args.vectors.write_text(text,encoding='utf-8')
    print(f"{len(result['cases'])} native cases; labels {[r['label'] for r in result['identities']+result['metadata']]}; {path}")
    if not result['clockStable']:raise SystemExit('Game clock changed; diagnostics only')
if __name__=='__main__':main()
