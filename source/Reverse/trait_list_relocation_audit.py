"""Audit saved/live diagnostic addresses against saved native initialization; no process access."""
import hashlib, json, struct, sys
from pathlib import Path
ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / '.cache/registered-officers/pydeps'))
from capstone import Cs, CS_ARCH_X86, CS_MODE_64
from unicorn import Uc, UC_ARCH_X86, UC_MODE_64, UC_PROT_ALL
from unicorn.x86_const import UC_X86_REG_RSP
image = (ROOT / 'source/Reverse/.cache/image.bin').read_bytes()
assert hashlib.sha256(image).hexdigest() == 'aa85c23361c67cd085f3101d2ecb9bf1579f79d46d081effa0184044f1c48e59'
definition = json.loads((ROOT / 'source/Core/RuntimeRules/trait-list.json').read_text())
path = Path(sys.argv[1])
diagnostic = json.loads(path.read_text(encoding='utf-8-sig'))
e = diagnostic['evidence']; module = int(e['ModuleBase'], 16)
columns = bytes.fromhex(definition['Columns']['Bytes'])
md = Cs(CS_ARCH_X86, CS_MODE_64)
targets = {int(d['Rva'],16): d for d in e['Differences']}
references = {r: [] for r in targets}
# RIP-relative LEA/MOV references, then disassemble a bounded context for examination.
for at in range(0x1000, 0x133fa00-7):
    if image[at] not in (0x48,0x4c) or image[at+1] not in (0x89,0x8b,0x8d) or image[at+2]&0xc7 != 5: continue
    target = at + 7 + struct.unpack_from('<i', image, at+3)[0]
    if target in references:
        references[target].append(dict(rva=hex(at), context=[f'{i.address:x} {i.mnemonic} {i.op_str}' for i in md.disasm(image[at-21:at+14], at-21)]))
results=[]
for target,d in targets.items():
    offset=target-definition['Columns']['Rva']
    saved=struct.unpack_from('<Q',columns,offset)[0]
    actual=int.from_bytes(bytes.fromhex(d['ActualHex']),'little')
    results.append(dict(column=d['ColumnIndex'],offset=d['RecordOffset'],fieldRva=hex(target),
        savedRva=hex(saved-definition['OriginalBase']), actualRva=hex(actual-module),
        equalRelative=saved-definition['OriginalBase']==actual-module,
        hasManifestFixup=offset in definition['Columns']['Fixups'], references=references[target]))
native=[]
runtime_fields=[c*96+o for c in (22,69,99,100) for o in (0x18,0x28)]+[212*96+8]
for base in (definition['OriginalBase'], 0x140000000, module):
    mu=Uc(UC_ARCH_X86,UC_MODE_64)
    for page in [0x2000,0x1a47000,*range(0x1a2b000,0x1a31000,4096)]:
        mu.mem_map(base+page,4096,UC_PROT_ALL);mu.mem_write(base+page,image[page:page+4096])
    # The initializer's input is a genuine PE-relocated global pointer.
    pe=struct.unpack_from('<I',image,0x3c)[0]; start,size=struct.unpack_from('<II',image,pe+24+112+5*8)
    relocations=[]; at=start
    while at<start+size:
        page,n=struct.unpack_from('<II',image,at)
        assert n>=8
        for p in range(at+8,at+n,2):
            v=struct.unpack_from('<H',image,p)[0]
            if v>>12==10:relocations.append(page+(v&4095))
        at+=n
    assert 0x1a47b98 in relocations
    assert all(definition['Columns']['Rva']+offset not in relocations for offset in runtime_fields)
    original_input=struct.unpack_from('<Q',image,0x1a47b98)[0]
    mu.mem_write(base+0x1a47b98,struct.pack('<Q',original_input-definition['OriginalBase']+base))
    stack=0x30000000000;mu.mem_map(stack,4096,UC_PROT_ALL);mu.reg_write(UC_X86_REG_RSP,stack+0x808)
    # The initializer copies an unwritten high dword beside the 32-bit type field.
    # Supply the captured stack-padding value explicitly; it is not pointer output.
    assert all(columns[c*96+0x24:c*96+0x28]==b'\xff'*4 for c in (22,69,99,100))
    mu.mem_write(stack+0x808-8-0x3c,b'\xff'*4)
    mu.emu_start(base+0x2260,base+0x24f0,count=500)
    actual=bytes(mu.mem_read(base+definition['Columns']['Rva'],len(columns)))
    expected=bytearray(columns)
    for offset in runtime_fields:
        struct.pack_into('<Q',expected,offset,struct.unpack_from('<Q',columns,offset)[0]-definition['OriginalBase']+base)
    assert actual==expected, f'Native initializer at {base:x} differs: '+str([(hex(i),actual[i:i+8].hex(),expected[i:i+8].hex()) for i in range(0,len(actual),8) if actual[i:i+8]!=expected[i:i+8]])
    native.append(dict(module=hex(base),initializerRva='0x2260',pointerFields=9,entireColumnsMatch=True))
# Replay the user's two saved reads without touching the process. Correct relocation must
# match every byte, not merely mask the nine addresses reported by the comparison.
expected=bytearray(columns)
for offset in set(definition['Columns']['Fixups'])|set(runtime_fields):
    struct.pack_into('<Q',expected,offset,struct.unpack_from('<Q',columns,offset)[0]-definition['OriginalBase']+module)
assert expected==bytes.fromhex(e['ActualHex'])==bytes.fromhex(e['SecondReadHex'])
out=ROOT/'.cache/trait-list-relocation-audit.json'
out.write_text(json.dumps(dict(gameAccess=False, diagnostic=str(path),module=hex(module), results=results,
    nativeInitializerCases=native, entireDiagnosticColumnsMatch=True),indent=2))
print(out.read_text())
