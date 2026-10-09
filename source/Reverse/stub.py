# The editor's frame stub (GameMemory.FrameStub) run in an x64 emulator, nothing of the game needed: the stub is entered
# as CApp::Update would be, the function it calls is a hook that records what it was given, and Update is a hook that
# ends the run. Checks the call (once, with the arguments given, rsp 16-byte aligned at the call), the result and the
# status kept on the data page, that Update is reached with rsp, rcx, rdx, r8, r9 and xmm0~3 as they came in, and that a
# second entry goes straight on to Update.
#   python stub.py <stub hex> <code> <function> <argument>...
# <stub hex> is FrameStub's bytes (or a file holding them), <code> where they were made for (the data page follows),
# <function> the function they call; the arguments are rcx, rdx, then the stub's `more` (an argument with a "." is a
# double: it is looked for in xmm2/xmm3 too, or as its bits on the stack). Example (GameCallTests' camera stub):
#   python stub.py <hex> 0x200000000 0x7ff600451360 12345 0 500 -1.0 -1.0 1
import os, struct, sys

from unicorn import Uc, UC_ARCH_X86, UC_MODE_64, UC_HOOK_CODE
from unicorn.x86_const import (UC_X86_REG_RAX, UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_R9, UC_X86_REG_RSP,
                               UC_X86_REG_RIP, UC_X86_REG_XMM0, UC_X86_REG_XMM1, UC_X86_REG_XMM2, UC_X86_REG_XMM3)

PAGE = 0x1000
STUB_RESULT, STUB_NEXT = 4, 8
DONE = 2
UPDATE = 0x7FF6_0014_B600          # where Update is, put at the data page's StubNext
STACK = 0x1_0000_0000
RESULT = 0x1234_5678
REGS = {"rcx": UC_X86_REG_RCX, "rdx": UC_X86_REG_RDX, "r8": UC_X86_REG_R8, "r9": UC_X86_REG_R9}
XMMS = [UC_X86_REG_XMM0, UC_X86_REG_XMM1, UC_X86_REG_XMM2, UC_X86_REG_XMM3]


def bits(text):
    return struct.unpack("<Q", struct.pack("<d", float(text)))[0] if "." in text else int(text, 0) & (2**64 - 1)


def run(stub, code, function, expected):
    data = code + PAGE
    uc = Uc(UC_ARCH_X86, UC_MODE_64)
    uc.mem_map(code, 2 * PAGE)
    uc.mem_write(code, stub)
    uc.mem_write(data + STUB_NEXT, struct.pack("<Q", UPDATE))
    for page in (function & ~(PAGE - 1), UPDATE & ~(PAGE - 1)):
        uc.mem_map(page, PAGE)
        uc.mem_write(page, b"\xCC" * PAGE)
    uc.mem_write(function, b"\xC3")    # ret (the hook below sets eax first)
    uc.mem_map(STACK, 0x10000)
    calls, landings = [], []

    def hook(uc, address, size, _):
        if address == function:
            rsp = uc.reg_read(UC_X86_REG_RSP)
            stack = [struct.unpack("<Q", uc.mem_read(rsp + 0x28 + 8 * k, 8))[0] for k in range(4)]
            calls.append({"regs": {n: uc.reg_read(r) for n, r in REGS.items()}, "xmm": [uc.reg_read(x) & (2**64 - 1) for x in XMMS],
                          "rsp": rsp, "stack": stack})
            uc.reg_write(UC_X86_REG_RAX, RESULT)
        elif address == UPDATE:
            landings.append({"regs": {n: uc.reg_read(r) for n, r in REGS.items()}, "xmm": [uc.reg_read(x) for x in XMMS],
                             "rsp": uc.reg_read(UC_X86_REG_RSP)})
            uc.emu_stop()

    uc.hook_add(UC_HOOK_CODE, hook)
    problems = []
    for entry in range(2):
        # Entered as Update is: by a call, so rsp is 8 off a 16-byte line, with arguments of Update's own.
        rsp = STACK + 0x8000 - 8 - 0x10 * entry
        uc.mem_write(rsp, struct.pack("<Q", 0xDEAD_BEEF))
        came = {"rcx": 0x1111, "rdx": 0x2222, "r8": 0x3333, "r9": 0x4444}
        for n, r in REGS.items(): uc.reg_write(r, came[n])
        for k, x in enumerate(XMMS): uc.reg_write(x, 0x5555_0000 + k)
        uc.reg_write(UC_X86_REG_RSP, rsp)
        uc.emu_start(code, 0, count=10_000)
        if len(landings) != entry + 1:
            problems.append(f"entry {entry}: did not reach Update (rip {uc.reg_read(UC_X86_REG_RIP):#x})")
            break
        land = landings[-1]
        if land["rsp"] != rsp: problems.append(f"entry {entry}: rsp {land['rsp']:#x} at Update, came in {rsp:#x}")
        if land["regs"] != came: problems.append(f"entry {entry}: registers at Update {land['regs']}")
        if land["xmm"] != [0x5555_0000 + k for k in range(4)]: problems.append(f"entry {entry}: xmm0~3 at Update {land['xmm']}")
    if len(calls) != 1:
        problems.append(f"the function was called {len(calls)} times")
    else:
        call = calls[0]
        if call["rsp"] % 16 != 8: problems.append(f"rsp {call['rsp']:#x} at the function: not aligned at the call")
        for k, want in enumerate(expected):
            if k < 4:
                got = call["regs"][list(REGS)[k]]
                if got != want: problems.append(f"argument {k + 1}: {got:#x} in {list(REGS)[k]}, not {want:#x}")
                if k >= 2 and call["xmm"][k] != want: problems.append(f"argument {k + 1}: {call['xmm'][k]:#x} in xmm{k}, not {want:#x}")
            elif call["stack"][k - 4] != want:
                problems.append(f"argument {k + 1}: {call['stack'][k - 4]:#x} on the stack, not {want:#x}")
    status = uc.mem_read(data, 1)[0]
    result = struct.unpack("<i", uc.mem_read(data + STUB_RESULT, 4))[0]
    if status != DONE: problems.append(f"status {status}, not Done")
    if result != RESULT: problems.append(f"result {result:#x} kept, not {RESULT:#x}")
    return problems


if __name__ == "__main__":
    text = sys.argv[1]
    if os.path.exists(text): text = open(text).read()
    code, function = int(sys.argv[2], 0), int(sys.argv[3], 0)
    problems = run(bytes.fromhex(text.strip()), code, function, [bits(a) for a in sys.argv[4:]])
    print("\n".join(problems) if problems else "ok: called once as given, aligned, result and Done kept, Update reached as entered, twice")
    sys.exit(1 if problems else 0)
