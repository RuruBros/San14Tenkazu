# The game's exe as it sits in memory (its .text is encrypted on disk by the Steam stub), dumped once per run
# into .cache, with the static helpers over it: functions from .pdata, disassembly, cross references, RTTI.
#   python image.py dump            dump the running game's image (read-only)
#   python image.py dis <rva>...    disassemble the functions holding these RVAs
#   python image.py callers <rva>   direct calls to a function
#   python image.py refs <rva>      instructions whose rip-relative operand points at rva
import bisect, os, pickle, struct, sys
from collections import defaultdict

import capstone

HERE = os.path.dirname(os.path.abspath(__file__))
CACHE = os.path.join(HERE, ".cache")
IMAGE = os.path.join(CACHE, "image.bin")
META = os.path.join(CACHE, "image.meta")
XREF = os.path.join(CACHE, "xref.pkl")


def dump():
    from game import Game
    g = Game()
    hdr = g.read(g.exe, 0x1000)
    pe = struct.unpack_from("<I", hdr, 0x3C)[0]
    nsec = struct.unpack_from("<H", hdr, pe + 6)[0]
    optsz = struct.unpack_from("<H", hdr, pe + 20)[0]
    image = bytearray(g.exe_size)
    image[0:0x1000] = hdr
    for i in range(nsec):
        o = pe + 24 + optsz + i * 40
        vsize, va = struct.unpack_from("<II", hdr, o + 8)
        for p in range(0, vsize, 0x1000):
            b = g.read(g.exe + va + p, min(0x1000, vsize - p))
            if b: image[va + p: va + p + len(b)] = b
    os.makedirs(CACHE, exist_ok=True)
    open(IMAGE, "wb").write(image)
    open(META, "w").write(f"{g.exe:#x}\n")
    if os.path.exists(XREF): os.remove(XREF)
    print(f"dumped {len(image):#x} bytes at base {g.exe:#x}")


def make_pe():
    # image.bin laid out as a PE file (raw offset = virtual address) for Ghidra; the image base is the run's.
    img = bytearray(open(IMAGE, "rb").read())
    pe = struct.unpack_from("<I", img, 0x3C)[0]
    nsec = struct.unpack_from("<H", img, pe + 6)[0]
    optsz = struct.unpack_from("<H", img, pe + 20)[0]
    struct.pack_into("<II", img, pe + 24 + 112 + 8 * 4, 0, 0)   # the certificate table is a file offset
    vas = []
    for i in range(nsec):
        o = pe + 24 + optsz + i * 40
        vsize, va = struct.unpack_from("<II", img, o + 8)
        vas.append((o, va, vsize))
    for k, (o, va, vsize) in enumerate(vas):
        end = vas[k + 1][1] if k + 1 < len(vas) else len(img)
        raw = min((vsize + 0x1FF) & ~0x1FF, end - va)
        struct.pack_into("<II", img, o + 16, raw, va)
    out = os.path.join(CACHE, "SAN14CE_KO.mem.exe")
    open(out, "wb").write(img)
    print("wrote", out)


class Image:
    def __init__(self):
        if not os.path.exists(IMAGE):
            raise SystemExit("image.bin 없음: python image.py dump 먼저")
        self.img = open(IMAGE, "rb").read()
        self.base = int(open(META).read().split()[0], 16)
        img = self.img
        pe = struct.unpack_from("<I", img, 0x3C)[0]
        nsec = struct.unpack_from("<H", img, pe + 6)[0]
        optsz = struct.unpack_from("<H", img, pe + 20)[0]
        self.sections = {}
        for i in range(nsec):
            o = pe + 24 + optsz + i * 40
            name = img[o:o + 8].rstrip(b"\0").decode()
            vsize, va = struct.unpack_from("<II", img, o + 8)
            self.sections[name] = (va, vsize)
        pva, psize = self.sections[".pdata"]
        fs = [struct.unpack_from("<III", img, pva + i * 12) for i in range(psize // 12)]
        self.funcs = sorted(f for f in fs if f[0])
        self.starts = [f[0] for f in self.funcs]
        self.md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
        self.md.skipdata = True
        self._xref = None

    # --- functions
    def func(self, rva):
        # (start, end) of the .pdata entry holding rva (a chained part of a function is an entry of its own)
        i = bisect.bisect_right(self.starts, rva) - 1
        while i >= 0:
            b, e, _ = self.funcs[i]
            if b <= rva < e: return b, e
            if rva - b > 0x40000: break
            i -= 1
        return None

    def insns(self, b, e):
        return self.md.disasm(self.img[b:e], b)

    def i32(self, rva): return struct.unpack_from("<i", self.img, rva)[0]
    def u32(self, rva): return struct.unpack_from("<I", self.img, rva)[0]
    def u64(self, rva): return struct.unpack_from("<Q", self.img, rva)[0]
    def f32(self, rva): return struct.unpack_from("<f", self.img, rva)[0]
    def f64(self, rva): return struct.unpack_from("<d", self.img, rva)[0]

    def text(self, b, e, annotate=True):
        out = []
        for i in self.insns(b, e):
            extra = ""
            s = i.op_str
            if annotate and "rip" in s:
                try:
                    disp = int(s.split("rip ")[1].split("]")[0].replace(" ", ""), 0)
                    t = i.address + i.size + disp
                    extra = f"   ; -> {t:#x}"
                    if "dword ptr" in s and t >= self.sections[".data"][0]:
                        extra += f" = {self.i32(t)}"
                    elif "qword ptr" in s and s.startswith(("movsd", "mulsd", "addsd", "divsd", "subsd", "comisd")):
                        extra += f" = {self.f64(t)!r}"
                    elif "dword ptr" in s and s.split()[0] in ("movss", "mulss", "addss", "divss", "subss", "comiss", "ucomiss", "maxss", "minss"):
                        extra += f" = {self.f32(t)!r}"
                    elif "dword ptr" in s:
                        extra += f" = {self.i32(t)} / {self.f32(t)!r}"
                except Exception:
                    pass
            out.append(f"{i.address:8x}  {i.mnemonic:8} {s}{extra}")
        return "\n".join(out)

    def flow(self, start, limit=0x20000):
        # Every instruction reachable from start by jumps (not calls): the whole function, chained parts included.
        # A jmp to another function's start is a tail call and ends the path.
        seen, todo, out = set(), [start], {}
        starts = set(self.starts)
        while todo:
            a = todo.pop()
            while a not in seen and abs(a - start) < limit:
                seen.add(a)
                ins = next(self.md.disasm(self.img[a:a + 16], a), None)
                if ins is None: break
                out[a] = ins
                m, s = ins.mnemonic, ins.op_str
                if m == "ret" or m == "int3" or (m == "jmp" and not s.startswith("0x")):
                    break
                if m.startswith("j") and s.startswith("0x"):
                    t = int(s, 16)
                    if m == "jmp":
                        if t in starts and t != start: break
                        a = t
                        continue
                    todo.append(t)
                a += ins.size
        return [out[k] for k in sorted(out)]

    def flowtext(self, start):
        lines, prev = [], None
        for i in self.flow(start):
            if prev is not None and i.address != prev:
                lines.append("        ...")
            lines.append(self.text(i.address, i.address + i.size))
            prev = i.address + i.size
        return "\n".join(lines)

    # --- cross references (one pass over all functions, cached)
    def xref(self):
        if self._xref is None:
            if os.path.exists(XREF):
                self._xref = pickle.load(open(XREF, "rb"))
            else:
                refs, calls = [], []
                for b, e, _ in self.funcs:
                    for i in self.insns(b, e):
                        s = i.op_str
                        if "rip" in s:
                            try:
                                disp = int(s[s.index("rip ") + 4:].split("]")[0].replace(" ", ""), 0)
                                refs.append((i.address + i.size + disp, i.address, b, f"{i.mnemonic} {s}"))
                            except Exception:
                                pass
                        elif i.mnemonic in ("call", "jmp") and s.startswith("0x"):
                            calls.append((i.address, int(s, 16), b))
                self._xref = (refs, calls)
                pickle.dump(self._xref, open(XREF, "wb"))
        return self._xref

    def refs_to(self, target):
        return [r for r in self.xref()[0] if r[0] == target]

    def callers(self, target):
        return [(s, f) for s, t, f in self.xref()[1] if t == target]

    def callees(self, rva):
        b, e = self.func(rva)
        return [(s, t) for s, t, f in self.xref()[1] if f == b and not (b <= t < e)]

    # --- RTTI
    def rtti_vtables(self):
        rd, rs = self.sections[".rdata"]
        out = {}
        for slot in range(rd, rd + rs - 8, 8):
            v = self.u64(slot) - self.base
            if rd <= v < rd + rs - 24:
                sig, off, cd, td, ch, me = struct.unpack_from("<IIIIII", self.img, v)
                if sig == 1 and me == v:
                    out[slot + 8] = self.img[td + 0x10: td + 0x100].split(b"\0")[0].decode("ascii", "ignore")
        return out


if __name__ == "__main__":
    cmd = sys.argv[1]
    if cmd == "dump":
        dump()
    elif cmd == "pe":
        make_pe()
    else:
        im = Image()
        if cmd == "dis":
            for a in sys.argv[2:]:
                f = im.func(int(a, 16))
                print(f"== {f[0]:#x}..{f[1]:#x}"); print(im.text(*f))
        elif cmd == "flow":
            for a in sys.argv[2:]:
                print(f"== flow {a}"); print(im.flowtext(int(a, 16)))
        elif cmd == "callers":
            for s, f in im.callers(int(sys.argv[2], 16)): print(f"{s:#x} in {f:#x}")
        elif cmd == "refs":
            for t, a, f, txt in im.refs_to(int(sys.argv[2], 16)): print(f"{a:#x} in {f:#x}: {txt}")
