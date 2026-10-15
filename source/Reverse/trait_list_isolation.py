"""Execute product-generated list hooks/callbacks against saved pages, never a live game.

Export payload.bin/json with RenderChecks --runtime-trait-list-export first. GUI rendering,
input dispatch and the identity of a user's open screen are outside this Native audit.
"""
import hashlib
import json
from pathlib import Path
import struct
import sys

sys.argv.append('--tenkazu')
from mod_patch_isolation import FrozenMemory, Oracle, ROOT, HERE
from unicorn import UC_PROT_ALL, UC_PROT_READ, UC_PROT_WRITE, UC_PROT_EXEC
from unicorn.x86_const import *

OUT = ROOT / '.cache/traits9-list'
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest()
def main():
    inputs = [HERE / '.cache/frozen.pkl', HERE / '.cache/image.bin',
              ROOT / 'source/Core/RuntimeRules/trait-list.json', OUT / 'payload.bin']
    hashes = {str(p): sha(p) for p in inputs}
    g = FrozenMemory(inputs[0]); o = Oracle(g)
    metadata = json.loads((OUT / 'payload.json').read_text()); address = metadata['allocation']
    assert metadata['module'] == g.exe
    definition = json.loads(inputs[2].read_text()); payload = inputs[3].read_bytes()
    cases = []
    def check(name, actual, expected, **context):
        cases.append(dict(name=name, actual=actual, expected=expected, passed=actual == expected, **context))
        if actual != expected: print('FAIL', cases[-1], flush=True)
    def rebased(block):
        b = bytearray.fromhex(block['Bytes'])
        for at in block['Fixups']:
            struct.pack_into('<Q', b, at, struct.unpack_from('<Q', b, at)[0] - definition['OriginalBase'] + g.exe)
        return bytes(b)
    blocks = [definition['Columns'], *definition['Tables'], *definition['Guards']]
    for block in blocks:
        expected = rebased(block)
        # A missing saved static data page is supplied only from the same verified image.
        for page in range((g.exe + block['Rva']) & ~4095, g.exe + block['Rva'] + len(expected), 4096):
            if page not in g.pages:
                g.pages[page] = bytes(g.image[page-g.exe:page-g.exe+4096])
        actual = o.read(g.exe + block['Rva'], len(expected))
        check('original-static-block', actual.hex(), expected.hex(), rva=hex(block['Rva']))
    assert all(c['passed'] for c in cases), 'Source/static snapshot mismatch'
    check('saved-native-code-pages', all(c['equal'] for c in g.code_page_comparison), True)
    o.mu.mem_map(address, len(payload), UC_PROT_ALL)
    o.pages.update(range(address, address + len(payload), 4096)); o.own.update(range(address, address + len(payload), 4096))
    o.poke(address, payload)
    o.mu.mem_protect(address, 4096, UC_PROT_READ | UC_PROT_EXEC)
    for page in range(4096, len(payload), 4096):
        o.mu.mem_protect(address + page, 4096, UC_PROT_READ | (UC_PROT_WRITE if page in metadata['writablePages'] else 0))
    original = o.read(g.exe + metadata['site'], 15)
    jump = bytes.fromhex('ff2500000000') + struct.pack('<Q', address) + b'\x90'
    o.poke(g.exe + metadata['site'], jump)
    obj, frame = o.alloc(4096), o.alloc(4096)
    copied = []
    complete = False
    try:
        # Execute the real hook site, the emitted code, and the original column-pointer store.
        for table in definition['Tables'] + [dict(Rva=0x123450, TraitIndex=-1)]:
            for correct_columns in (True, False):
                pageptr = g.exe + table['Rva']; colptr = g.exe + definition['Columns']['Rva'] + (0 if correct_columns else 96)
                o.poke(frame + 0x77, struct.pack('<Q', pageptr)); o.poke(frame + 0x7f, struct.pack('<Q', colptr))
                sp = o.stack + 0x80000
                initial = {UC_X86_REG_RBP: frame, UC_X86_REG_RSI: obj, UC_X86_REG_RSP: sp,
                           UC_X86_REG_RCX: 0x11112222, UC_X86_REG_R11: 0x33334444, UC_X86_REG_RDX: 0x55556666,
                           UC_X86_REG_EFLAGS: 0x246}
                for reg, value in initial.items(): o.mu.reg_write(reg, value)
                o.mu.emu_start(g.exe + metadata['site'], g.exe + 0x2bf049, count=2000)
                check('hook-return', o.mu.reg_read(UC_X86_REG_RIP), g.exe + 0x2bf049, table=hex(table['Rva']))
                for reg, value in initial.items(): check('hook-preserves-register', o.mu.reg_read(reg), value, register=reg)
                got_page, got_cols = o.q(obj+0x150), o.q(obj+0x158)
                matches = correct_columns and table['TraitIndex'] >= 0
                if not matches:
                    check('unrelated-list-untouched', [got_page, got_cols], [pageptr, colptr]); continue
                check('copied-columns-connected', got_cols, address + 4096)
                old = rebased(table); expected = bytearray(old)
                listptr = o.q(got_page + table['TraitIndex'] * 24)
                struct.pack_into('<Q', expected, table['TraitIndex'] * 24, listptr)
                check('other-tabs-preserved', o.read(got_page, len(old)).hex(), expected.hex())
                first=table['TraitBase']; added=217 if first==170 else 213
                check('nine-trait-columns', list(struct.unpack('<11i', o.read(listptr, 44))), [0,*range(first,first+5),*range(added,added+4),-1])
                copied.append((got_page, len(old), o.read(got_page, len(old))))
        # The new callbacks must return the native slot's name and use the native comparison.
        p = o.q(g.base - 0x290 + 0x73a50 + 8); other = o.q(g.base - 0x290 + 0x73a50 + 16)
        # The capture lacks the base-officer database pages. Supply an explicit synthetic
        # database with no matching base records: the original helper's documented null
        # branch uses current traits. Do not fake a return value or any UI/game function.
        base_database = o.alloc(0x3000)
        o.poke(g.exe + 0x2143620, struct.pack('<Q',base_database))
        o.poke(p+0x10, struct.pack('<H',1)); o.poke(other+0x10, struct.pack('<H',2))
        patches = json.loads((ROOT / 'source/Core/RuntimeRules/patches.json').read_text())
        for r in patches: o.poke(g.exe+r['Rva'], bytes.fromhex(r['Traits']))
        o.poke(p + 0x118, bytes(18)); o.poke(other + 0x118, bytes(18))
        for traitid in (256, 257):
            trait = o.q(g.base - 0x290 + 0x7dd18 + traitid*8)
            o.poke(trait + 0xb6, b'\x05'); o.poke(trait + 0xbf, b'\x10')
            o.poke(trait + 0x10, ('Alpha' if traitid==256 else 'Bravo').encode('utf-16le') + bytes(2))
        for style,slot in ((style,slot) for style in (0,1) for slot in range(5,9)):
            rec = address + 4096 + (213+style*4+slot-5)*96
            check('header-id', struct.unpack('<I', o.read(rec+0x48,4))[0], 0x3e2+slot)
            value, ascending, descending = (o.q(rec+k) for k in (0x18,0x30,0x38))
            for traitid in (0,256,257,355,356,65535):
                o.poke(p+0x118+2*slot, struct.pack('<H',traitid)); o.poke(p+0x118+8,struct.pack('<H',traitid))
                # Compare with the original fifth-column callback on identical trait input.
                expected = o.call(0x35ef10 if style else 0x35eec0,p)
                actual = o.call(value-g.exe,p)
                check('name-callback-native-equivalence',actual,expected,slot=slot+1,trait=traitid,style=style)
            for left,right in ((256,257),(257,256),(256,256),(0,256),(256,0)):
                o.poke(p+0x118+2*slot,struct.pack('<H',left));o.poke(other+0x118+2*slot,struct.pack('<H',right))
                for fn,order in ((ascending,1),(descending,0)):
                    expected=o.call(0x353810,p,other,slot,style,stack_args=(order,))
                    actual=o.call(fn-g.exe,p,other)
                    check('sort-native-equivalence',actual,expected,slot=slot+1,left=left,right=right,order=order,style=style)
        # OFF removes only the hook. Existing screen references and callbacks stay readable.
        o.poke(g.exe + metadata['site'], original)
        check('hook-restored',o.read(g.exe+metadata['site'],15).hex(),original.hex())
        for ptr,size,old in copied: check('old-screen-retained-after-off',o.read(ptr,size).hex(),old.hex())
        complete = True
    finally:
        o.poke(g.exe + metadata['site'], original)
        for r in json.loads((ROOT / 'source/Core/RuntimeRules/patches.json').read_text()):
            o.poke(g.exe+r['Rva'], bytes.fromhex(r['Before']))
        unchanged = {str(p): sha(p) == hashes[str(p)] for p in inputs}
        summary = dict(complete=complete,passed=sum(c['passed'] for c in cases),failed=sum(not c['passed'] for c in cases),
                       alternate_name_fixture='synthetic base database, no matching records; native fallback to current traits',
                       tables=len(definition['Tables']),live_game_access=False,input_files_unchanged=unchanged)
        (OUT/'native-results.json').write_text(json.dumps(dict(summary=summary,cases=cases),ensure_ascii=False,indent=2),encoding='utf8')
        (OUT/'native-summary.json').write_text(json.dumps(summary,indent=2))
        print(json.dumps(summary,indent=2),flush=True)
    assert not summary['failed'] and all(unchanged.values())

if __name__=='__main__': main()
