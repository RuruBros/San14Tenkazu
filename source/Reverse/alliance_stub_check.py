"""Execute C#-generated alliance frame stubs in Unicorn; never opens or writes the game."""
import argparse
import json
from pathlib import Path
import struct

from unicorn import Uc, UC_ARCH_X86, UC_MODE_64, UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RAX, UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_R9, UC_X86_REG_RSP
from stub import PAGE, STUB_RESULT, STUB_NEXT, DONE, UPDATE, STACK, REGS, XMMS, RESULT


def check(case):
    uc = Uc(UC_ARCH_X86, UC_MODE_64)
    mapped = set()

    def page(address):
        address &= ~(PAGE - 1)
        if address not in mapped:
            uc.mem_map(address, PAGE)
            mapped.add(address)

    code, data = case['code'], case['data']
    page(code); page(data); page(UPDATE)
    uc.mem_write(code, bytes.fromhex(case['hex']))
    uc.mem_write(data + STUB_NEXT, struct.pack('<Q', UPDATE))
    for call in case['calls']:
        page(call['function']); uc.mem_write(call['function'], b'\xc3')
    for store in case['stores']:
        page(store['Address']); uc.mem_write(store['Address'], b'\xaa')
    uc.mem_map(STACK, 0x10000)
    calls, landings = [], []
    targets = {c['function']: c for c in case['calls']}

    def hook(mu, address, size, unused):
        if address in targets:
            expected = targets[address]
            actual = [mu.reg_read(r) for r in (UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_R9)][:len(expected['args'])]
            assert actual == expected['args'], (case['name'], hex(address), actual, expected['args'])
            assert mu.reg_read(UC_X86_REG_RSP) % 16 == 8
            calls.append(address)
            # A native callee may destroy every volatile argument register, including all four XMM inputs.
            for r in REGS.values(): mu.reg_write(r, 0xA000 + len(calls))
            for x in XMMS: mu.reg_write(x, 0xB000 + len(calls))
            mu.reg_write(UC_X86_REG_RAX, RESULT + len(calls) - 1)
        elif address == UPDATE:
            landings.append(dict(regs={n: mu.reg_read(r) for n, r in REGS.items()},
                                 xmm=[mu.reg_read(x) for x in XMMS], rsp=mu.reg_read(UC_X86_REG_RSP)))
            mu.emu_stop()

    uc.hook_add(UC_HOOK_CODE, hook)
    incoming = dict(rcx=0x1111, rdx=0x2222, r8=0x3333, r9=0x4444)
    for entry in range(2):
        rsp = STACK + 0x8000 - 8 - 0x10 * entry
        for n, r in REGS.items(): uc.reg_write(r, incoming[n])
        for i, x in enumerate(XMMS): uc.reg_write(x, 0x55550000 + i)
        uc.reg_write(UC_X86_REG_RSP, rsp)
        uc.emu_start(code, 0, count=10000)
        assert len(landings) == entry + 1
        assert landings[-1] == dict(regs=incoming, xmm=[0x55550000 + i for i in range(4)], rsp=rsp)
        assert calls == [c['function'] for c in case['calls']]
        assert uc.mem_read(data, 1)[0] == DONE
        assert struct.unpack('<I', uc.mem_read(data + STUB_RESULT, 4))[0] == RESULT
        for store in case['stores']: assert uc.mem_read(store['Address'], 1)[0] == store['Value']
    return dict(name=case['name'], passed=True, calls=len(calls), entries=2, aligned=True,
                arguments_verified=True, volatile_registers_restored=True, raw_flags_restored=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('stubs', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    result = [check(c) for c in json.loads(args.stubs.read_text(encoding='utf-8'))]
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(result))
