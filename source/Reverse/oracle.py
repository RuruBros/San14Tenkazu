# The oracle: runs one of the game's own functions in the unicorn x64 emulator, against the running game's memory.
# Pages are copied from the game the first time the emulated code touches them (read-only access to the game);
# the code's writes land in those copies, so the game never sees them. Tests poke inputs the same way (poke),
# which is how a formula is checked against the game's code on inputs the save does not hold.
# Every call leaves the copies as it found them: the copied pages start read-only to the emulated code; the first
# write to a page keeps a pristine copy and makes the page writable for good, and every call ends by putting the
# pristine copies back (keep=True instead makes the call's writes the new pristine state, for state a test follows
# from call to call, such as the random seed). So each case runs on the snapshot and its pokes alone, whatever
# earlier cases did or failed to finish; pokes change the pristine copies.
import struct

from unicorn import (Uc, UcError, UC_ARCH_X86, UC_MODE_64, UC_PROT_ALL, UC_PROT_READ, UC_PROT_EXEC, UC_HOOK_MEM_UNMAPPED,
                     UC_HOOK_INSN, UC_HOOK_MEM_WRITE_PROT)
from unicorn.x86_const import *

from game import Game

PAGE = 0x1000
STACK_SIZE = 0x100000
SENTINEL = 0x0000_0DEA_D000_0000   # a return address no code lives at
CALLEE_SAVED = [UC_X86_REG_RBX, UC_X86_REG_RBP, UC_X86_REG_RSI, UC_X86_REG_RDI, UC_X86_REG_R12, UC_X86_REG_R13,
                UC_X86_REG_R14, UC_X86_REG_R15, UC_X86_REG_XMM6, UC_X86_REG_XMM7, UC_X86_REG_XMM8, UC_X86_REG_XMM9,
                UC_X86_REG_XMM10, UC_X86_REG_XMM11, UC_X86_REG_XMM12, UC_X86_REG_XMM13, UC_X86_REG_XMM14, UC_X86_REG_XMM15]
INT_ARGS = (UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_R8, UC_X86_REG_R9)
XMM_ARGS = (UC_X86_REG_XMM0, UC_X86_REG_XMM1, UC_X86_REG_XMM2, UC_X86_REG_XMM3)


class OracleError(Exception):
    pass


class Oracle:
    def __init__(self, game=None):
        self.g = game or Game()
        self.exe = self.g.exe
        self.mu = Uc(UC_ARCH_X86, UC_MODE_64)
        self.pages = set()
        self.stubs = {}                       # address -> python function(oracle) returning rax, run instead of the code
        self.mu.hook_add(UC_HOOK_MEM_UNMAPPED, self._fault)
        self.pristine = {}                    # page -> its bytes as the snapshot (and pokes) left them
        self.mu.hook_add(UC_HOOK_MEM_WRITE_PROT, self._first_write)
        # A stack in a range the game does not use.
        self.own = set()                      # pages of the stack and scratch, not the game's
        self.stack = self._free_range(STACK_SIZE + 2 * PAGE)
        self.mu.mem_map(self.stack, STACK_SIZE + 2 * PAGE, UC_PROT_ALL)
        self.pages.update(range(self.stack, self.stack + STACK_SIZE + 2 * PAGE, PAGE))
        self.own.update(range(self.stack, self.stack + STACK_SIZE + 2 * PAGE, PAGE))
        self.mu.mem_map(SENTINEL, PAGE, UC_PROT_ALL)
        # The emulated code runs as the game's main thread: gs points at its TEB (TLS, the stack limits are faulted in).
        teb = self.g.main_teb()
        self.mu.msr_write(0xC0000101, teb)
        # The copy of the TEB gets the emulated stack's limits (StackBase +0x8, StackLimit +0x10), so __chkstk probes
        # pages of this stack and not the real thread's.
        self.poke(teb + 0x8, struct.pack("<QQ", self.stack + STACK_SIZE + 2 * PAGE, self.stack))
        self.mu.hook_add(UC_HOOK_INSN, self._syscall, None, 1, 0, UC_X86_INS_SYSCALL)
        # FMA3 instructions (the CRT's pow, exp, log pick them on this CPU, as the game does) are not in unicorn:
        # they are done here, fused, exactly as the CPU does them.
        from unicorn import UC_HOOK_INSN_INVALID
        import capstone
        self._cs = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        self._cs.detail = True
        self.mu.hook_add(UC_HOOK_INSN_INVALID, self._invalid)
        # Imported memory routines (ntdll's run AVX-256 paths unicorn lacks) are done in Python at their entry.
        self._install_import_stubs()
        # The CRT's __isa_available (exe + 0x1A8BBB0, 5 = AVX2 here) picks AVX2 string and memory routines; their
        # results do not depend on the path, so the copy says SSE2 (1). The math routines' FMA flag is left alone:
        # their results do depend on it, so they run natively instead (pow) or stop the oracle.
        self.poke(self.exe + 0x1A8BBB0, struct.pack("<i", 1))
        self.insn_limit = 60_000_000
        self.last_fault = None

    def _free_range(self, size):
        # free in the game and not yet used by the emulator (each call takes a 4 GiB slot of its own)
        a = getattr(self, "_next_free", 0x10_0000_0000)
        while True:
            r = self.g.region(a)
            if r and r[2] == 0x10000 and r[0] + r[1] - a >= size:  # MEM_FREE
                self._next_free = a + 0x1_0000_0000
                return a
            a += 0x1_0000_0000

    def _map_page(self, page):
        if page in self.pages: return True
        data = self.g.read(page, PAGE)
        if data is None: return False
        self.mu.mem_map(page, PAGE, UC_PROT_READ | UC_PROT_EXEC)
        self.mu.mem_write(page, data)
        self.pages.add(page)
        return True

    def _first_write(self, uc, access, address, size, value, _):
        for page in range(address & ~(PAGE - 1), (address + max(size, 1) - 1 & ~(PAGE - 1)) + PAGE, PAGE):
            if page in self.pages and page not in self.pristine:
                self.pristine[page] = bytes(uc.mem_read(page, PAGE))
                uc.mem_protect(page, PAGE, UC_PROT_ALL)
        # unicorn lets the instruction go on but drops the write that faulted: done here (stores of 16 bytes come
        # as two of 8, so `value` always fits)
        uc.mem_write(address, (value & ((1 << (8 * size)) - 1)).to_bytes(size, "little"))
        return True

    def _settle(self, keep):
        # after a call: the pristine copies back, or the call's writes made pristine
        if keep:
            for page in self.pristine: self.pristine[page] = bytes(self.mu.mem_read(page, PAGE))
        else:
            for page, data in self.pristine.items(): self.mu.mem_write(page, data)

    def _fault(self, uc, access, address, size, value, _):
        ok = True
        for page in range(address & ~(PAGE - 1), (address + max(size, 1) - 1 & ~(PAGE - 1)) + PAGE, PAGE):
            ok = self._map_page(page) and ok
        if not ok: self.last_fault = (address, uc.reg_read(UC_X86_REG_RIP))
        return ok

    def imports(self):
        # {name: resolved address} of the exe's imports, read from the running game (or the frozen one's record)
        if hasattr(self, '_imports'): return self._imports
        g = self.g
        if hasattr(g, "imports"): return g.imports()
        hdr = g.read(g.exe, 0x1000)
        pe = struct.unpack_from("<I", hdr, 0x3C)[0]
        imp_rva, imp_size = struct.unpack_from("<II", hdr, pe + 24 + 112 + 8)
        out = {}
        d = imp_rva
        while True:
            oft, _, _, name_rva, ft = struct.unpack("<IIIII", g.read(g.exe + d, 20))
            if not name_rva: break
            k = 0
            while True:
                thunk = struct.unpack("<Q", g.read(g.exe + (oft or ft) + 8 * k, 8))[0]
                if not thunk: break
                if not thunk >> 63:
                    nm = g.read(g.exe + (thunk & 0x7FFFFFFF) + 2, 64).split(b"\0")[0].decode()
                    out[nm] = struct.unpack("<Q", g.read(g.exe + ft + 8 * k, 8))[0]
                k += 1
            d += 20
        out.update(g.memory_exports())
        self._imports = out
        return out

    def _ret(self, value):
        # returns from the function just entered (rsp at its return address) with rax = value
        rsp = self.mu.reg_read(UC_X86_REG_RSP)
        self.mu.reg_write(UC_X86_REG_RAX, value & 0xFFFFFFFFFFFFFFFF)
        self.mu.reg_write(UC_X86_REG_RIP, struct.unpack("<Q", self.read(rsp, 8))[0])
        self.mu.reg_write(UC_X86_REG_RSP, rsp + 8)

    def stub(self, rva, value=0, fn=None):
        # replaces the game's function at exe + rva for the calls that follow: it returns `value`, or what fn()
        # (which may read the registers) returns — for bookkeeping the formulas do not need (message queues, maps).
        # unstub() removes them: a module's stubs must not reach the next module (a stubbed constructor leaves
        # objects without their vtable, and the next formula's calls then jump through null)
        from unicorn import UC_HOOK_CODE
        def hook(uc, address, size, user):
            self._ret(fn() if fn else value)
        self._stub_hooks = getattr(self, "_stub_hooks", [])
        self._stub_hooks.append(self.mu.hook_add(UC_HOOK_CODE, hook, None, self.exe + rva, self.exe + rva))

    def unstub(self):
        for h in getattr(self, "_stub_hooks", []): self.mu.hook_del(h)
        self._stub_hooks = []

    def _install_import_stubs(self):
        from unicorn import UC_HOOK_CODE
        R = lambda r: self.mu.reg_read(r)
        ret = self._ret
        def memset():
            dst, val, n = R(UC_X86_REG_RCX), R(UC_X86_REG_RDX) & 0xFF, R(UC_X86_REG_R8)
            if n: self.emu_write(dst, bytes([val]) * n)
            ret(dst)
        def memcpy():
            dst, src, n = R(UC_X86_REG_RCX), R(UC_X86_REG_RDX), R(UC_X86_REG_R8)
            if n: self.emu_write(dst, self.read(src, n))
            ret(dst)
        def memcmp():
            a, b, n = R(UC_X86_REG_RCX), R(UC_X86_REG_RDX), R(UC_X86_REG_R8)
            x, y = self.read(a, n), self.read(b, n)
            ret(0 if x == y else (-1 if x < y else 1))
        # The emulator runs one thread: a critical section is always free to it (the snapshot may have caught the
        # game's own thread holding one, which would send the copy into the wait path).
        stubs = {"memset": memset, "memcpy": memcpy, "memmove": memcpy, "memcmp": memcmp,
                 "EnterCriticalSection": lambda: ret(0), "LeaveCriticalSection": lambda: ret(0),
                 "TryEnterCriticalSection": lambda: ret(1),
                 "AcquireSRWLockExclusive": lambda: ret(0), "ReleaseSRWLockExclusive": lambda: ret(0),
                 "AcquireSRWLockShared": lambda: ret(0), "ReleaseSRWLockShared": lambda: ret(0),
                 "WakeAllConditionVariable": lambda: ret(0), "SetEvent": lambda: ret(1), "ResetEvent": lambda: ret(1),
                 "WaitForSingleObject": lambda: ret(0), "WaitForSingleObjectEx": lambda: ret(0), "Sleep": lambda: ret(0)}
        self.stub_addrs = {}
        for name, addr in self.imports().items():
            name = name.rsplit('!', 1)[-1]
            if name in stubs:
                self.stub_addrs[addr] = stubs[name]
        # The game's CRT pow runs natively (its FMA path; unicorn cannot), with the game's own code (native.py).
        def native_pow():
            import native
            x = struct.unpack("<d", struct.pack("<Q", R(UC_X86_REG_XMM0) & 0xFFFFFFFFFFFFFFFF))[0]
            y = struct.unpack("<d", struct.pack("<Q", R(UC_X86_REG_XMM1) & 0xFFFFFFFFFFFFFFFF))[0]
            r = native.pow(x, y)
            self.mu.reg_write(UC_X86_REG_XMM0, struct.unpack("<Q", struct.pack("<d", r))[0])
            ret(R(UC_X86_REG_RAX))
        self.stub_addrs[self.exe + 0x101EEE0] = native_pow
        for addr, fn in self.stub_addrs.items():
            def hook(uc, address, size, user, fn=fn):
                fn()
            self.mu.hook_add(UC_HOOK_CODE, hook, None, addr, addr)

    def _invalid(self, uc, _):
        rip = uc.reg_read(UC_X86_REG_RIP)
        code = bytes(uc.mem_read(rip, 16))
        ins = next(self._cs.disasm(code, rip), None)
        if ins is None or not ins.mnemonic.startswith(("vfmadd", "vfmsub", "vfnmadd", "vfnmsub")):
            return False
        done = fma_insn(self, uc, ins)
        if done: uc.reg_write(UC_X86_REG_RIP, rip + ins.size)
        return done

    def _syscall(self, uc, _):
        raise OracleError(f"syscall at {uc.reg_read(UC_X86_REG_RIP) - self.exe:#x} (eax {uc.reg_read(UC_X86_REG_RAX):#x})")

    # --- memory, through the copies
    def read(self, addr, n):
        for page in range(addr & ~(PAGE - 1), addr + n, PAGE):
            if not self._map_page(page): raise OracleError(f"unreadable {page:#x}")
        return bytes(self.mu.mem_read(addr, n))

    def prefetch(self, addr, size):
        # copy these pages now (whatever is readable), so later calls see them as they were at this moment
        for page in range(addr & ~(PAGE - 1), addr + size, PAGE):
            self._map_page(page)

    def emu_write(self, addr, data):
        # a write on the game's behalf (a stubbed memset/memcpy): kept like the code's own writes, so the call's
        # end puts the page back (poke, by contrast, changes the pristine state for good)
        self.read(addr, len(data))
        for page in range(addr & ~(PAGE - 1), addr + len(data), PAGE):
            if page in self.pages and page not in self.pristine and page not in self.own:
                self.pristine[page] = bytes(self.mu.mem_read(page, PAGE))
                self.mu.mem_protect(page, PAGE, UC_PROT_ALL)
        self.mu.mem_write(addr, data)

    def poke(self, addr, data):
        self.read(addr, len(data))
        self.mu.mem_write(addr, data)
        for page in range(addr & ~(PAGE - 1), addr + len(data), PAGE):
            if page in self.pristine: self.pristine[page] = bytes(self.mu.mem_read(page, PAGE))

    def q(self, addr): return struct.unpack("<Q", self.read(addr, 8))[0]

    def alloc(self, size):
        # scratch memory for arguments the call needs (structs passed by pointer)
        a = self._free_range(size + PAGE)
        n = (size + PAGE - 1) & ~(PAGE - 1) or PAGE
        self.mu.mem_map(a, n, UC_PROT_ALL)
        self.pages.update(range(a, a + n, PAGE))
        self.own.update(range(a, a + n, PAGE))
        return a

    # --- freezing: every page copied so far, as a FrozenGame later runs on (game.py)
    def save_pages(self, path):
        import pickle
        # the pristine copy where one exists: the dump is the game as the snapshot saw it, not as the last call
        # left it
        pages = {}
        for p in self.pages:
            if p in self.own: continue
            pages[p] = self.pristine.get(p) or bytes(self.mu.mem_read(p, PAGE))
        pickle.dump({"exe": self.exe, "exe_size": self.g.exe_size, "base": self.g.base, "teb": self.g.main_teb(),
                     "imports": self.imports(), "pages": pages}, open(path, "wb"))
        return len(pages)

    # --- coverage: which instructions of the given functions (flow sets from image.Image.flow) the calls ran
    def track(self, image, rvas):
        # starts counting (again) for these functions; untrack() removes the hooks, which slow every call
        from unicorn import UC_HOOK_CODE
        self.untrack()
        self.cov_all = {}
        self.cov_hit = set()
        self._cov_hooks = []
        for r in rvas:
            addrs = {self.exe + i.address for i in image.flow(r)}
            self.cov_all[r] = addrs
            lo, hi = min(addrs), max(addrs)
            def hook(uc, address, size, user, addrs=addrs):
                if address in addrs: self.cov_hit.add(address)
            self._cov_hooks.append(self.mu.hook_add(UC_HOOK_CODE, hook, None, lo, hi))

    def untrack(self):
        for h in getattr(self, "_cov_hooks", []): self.mu.hook_del(h)
        self._cov_hooks = []

    def coverage(self):
        out = {}
        for r, addrs in self.cov_all.items():
            missed = sorted(a - self.exe for a in addrs - self.cov_hit)
            out[r] = (len(addrs) - len(missed), len(addrs), missed)
        return out

    # --- calls
    def call(self, rva, *args, xmm=(), stack_args=(), ret_float=False, keep=False, read_after=()):
        """Run the function at exe + rva with the Windows x64 convention; returns rax (or xmm0's float/double).
        read_after: [(addr, size), ...], or a callable producing it after the call, before writes are put back (self.after)."""
        mu = self.mu
        top = self.stack + STACK_SIZE
        sp = top - 0x100 - 8 * len(stack_args)
        sp &= ~0xF
        # the home slots and the caller's frame above them read as zeros: a function that reads beyond its own
        # locals (Wounded.Ratio's force table, 0x17D1D0) then answers the same every time, and the C# models 0
        mu.mem_write(sp + 8, bytes(top - sp - 8))
        for k, v in enumerate(stack_args):
            mu.mem_write(sp + 0x28 + 8 * k, struct.pack("<Q", v & 0xFFFFFFFFFFFFFFFF))
        sp -= 0  # rsp after the call pushes the return address: 8 (mod 16)
        mu.mem_write(sp, struct.pack("<Q", SENTINEL))
        mu.reg_write(UC_X86_REG_RSP, sp)
        for k, v in enumerate(args):
            mu.reg_write(INT_ARGS[k], v & 0xFFFFFFFFFFFFFFFF)
        for k, v in enumerate(xmm):
            if isinstance(v, float):
                v = struct.unpack("<I", struct.pack("<f", v))[0]
            mu.reg_write(XMM_ARGS[k], v)
        # callee-saved registers start from zero every call: a call that failed midway would otherwise leave its
        # values in them (a function reading beyond its locals, as 0x17D1D0 does, must see the same every time)
        for r in CALLEE_SAVED: mu.reg_write(r, 0)
        self.last_fault = None
        try:
            try:
                mu.emu_start(self.exe + rva, SENTINEL, count=self.insn_limit)
            except UcError as e:
                rip = mu.reg_read(UC_X86_REG_RIP)
                where = f"rip {rip:#x} (rva {rip - self.exe:#x})"
                raise OracleError(f"{e} at {where}; last fault {self.last_fault and tuple(hex(x) for x in self.last_fault)}")
            if mu.reg_read(UC_X86_REG_RIP) != SENTINEL:
                raise OracleError(f"stopped at {mu.reg_read(UC_X86_REG_RIP) - self.exe:#x} (instruction limit?)")
            # A dynamic list's nodes are known only after the native function has built it.
            reads = read_after() if callable(read_after) else read_after
            self.after = [self.read(a, n) for a, n in reads]
        finally:
            self._settle(keep)
        if ret_float:
            x = mu.reg_read(UC_X86_REG_XMM0)
            return struct.unpack("<f", struct.pack("<I", x & 0xFFFFFFFF))[0]
        return mu.reg_read(UC_X86_REG_RAX)

    def call32(self, rva, *args, **kw):
        v = self.call(rva, *args, **kw) & 0xFFFFFFFF
        return v - (1 << 32) if v >= 1 << 31 else v


# --- FMA3 scalar instructions (vfmadd/vfmsub/vfnmadd/vfnmsub 132/213/231 sd/ss), fused like the CPU
import math
from fractions import Fraction

_XMM = {f"xmm{i}": getattr(__import__("unicorn.x86_const", fromlist=["x"]), f"UC_X86_REG_XMM{i}") for i in range(16)}


def _f32_nearest(r):
    # the float32 nearest to the exact value r, ties to even
    import numpy as np
    f = np.float32(float(r))
    best = f
    for g in (np.nextafter(f, np.float32(-np.inf)), np.nextafter(f, np.float32(np.inf))):
        if not np.isfinite(g): continue
        d, db = abs(Fraction(float(g)) - r), abs(Fraction(float(best)) - r)
        if d < db or (d == db and (int(np.float32(g).view(np.uint32)) & 1) == 0):
            best = g
    return best


def fma_insn(o, uc, ins):
    m = ins.mnemonic
    single = m.endswith("ss")
    if not (m.endswith("sd") or single): return False
    order = m[-5:-2]           # 132, 213, 231
    ops = ins.op_str.split(", ")
    if len(ops) != 3: return False

    def read(op):
        if op in _XMM:
            v = uc.reg_read(_XMM[op])
            raw = v & (0xFFFFFFFF if single else 0xFFFFFFFFFFFFFFFF)
        else:
            # a memory operand: compute its address through capstone's detail
            mem = [x for x in ins.operands if x.type == 3][0].mem
            a = mem.disp
            if mem.base:
                reg = ins.reg_name(mem.base)
                a += (ins.address + ins.size) if reg == "rip" else uc.reg_read(getattr(__import__("unicorn.x86_const", fromlist=["x"]), "UC_X86_REG_" + reg.upper()))
            if mem.index:
                a += mem.scale * uc.reg_read(getattr(__import__("unicorn.x86_const", fromlist=["x"]), "UC_X86_REG_" + ins.reg_name(mem.index).upper()))
            raw = int.from_bytes(bytes(uc.mem_read(a & 0xFFFFFFFFFFFFFFFF, 4 if single else 8)), "little")
        return struct.unpack("<f" if single else "<d", raw.to_bytes(4 if single else 8, "little"))[0]

    d, s2, s3 = ops
    a, b, c = read(d), read(s2), read(s3)
    x, y, z = {"132": (a, c, b), "213": (b, a, c), "231": (b, c, a)}[order]   # x*y + z in the operand order
    neg_prod = m.startswith("vfn")
    sub = "msub" in m
    if any(math.isnan(v) or math.isinf(v) for v in (x, y, z)):
        p = (-x * y) if neg_prod else (x * y)
        r = p - z if sub else p + z
    elif single:
        exact = Fraction(x) * Fraction(y) * (-1 if neg_prod else 1) + (Fraction(z) * (-1 if sub else 1))
        r = float(_f32_nearest(exact))
    else:
        r = math.fma(-x if neg_prod else x, y, -z if sub else z)
    old = uc.reg_read(_XMM[d])
    if single:
        bits = struct.unpack("<I", struct.pack("<f", r))[0]
        new = (old & ~0xFFFFFFFF) & ((1 << 128) - 1) | bits
    else:
        bits = struct.unpack("<Q", struct.pack("<d", r))[0]
        new = (old & ~0xFFFFFFFFFFFFFFFF) & ((1 << 128) - 1) | bits
    # VEX scalar ops zero the upper 128 bits of the ymm register; unicorn's xmm view keeps the low 128
    uc.reg_write(_XMM[d], new)
    return True
