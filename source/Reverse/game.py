# The running SAN14CE_KO.exe, opened for reading only (PROCESS_QUERY_INFORMATION | PROCESS_VM_READ).
# Nothing here writes to the game: the oracle copies the pages it needs and writes to its copies.
import ctypes, ctypes.wintypes as wt, struct, sys

PROCESS_NAME = "SAN14CE_KO.exe"
BASE_POINTER_RVA = 0x277DF18   # the game's own pointer to its world; code adds 0x290 + table offset
BASE_ADJUST = 0x290

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
psapi = ctypes.WinDLL("psapi", use_last_error=True)
ntdll = ctypes.WinDLL("ntdll")
k32.OpenProcess.restype = ctypes.c_void_p
k32.OpenThread.restype = ctypes.c_void_p
k32.CreateToolhelp32Snapshot.restype = ctypes.c_void_p


class _PE32(ctypes.Structure):
    _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD), ("th32ProcessID", wt.DWORD),
                ("th32DefaultHeapID", ctypes.c_void_p), ("th32ModuleID", wt.DWORD), ("cntThreads", wt.DWORD),
                ("th32ParentProcessID", wt.DWORD), ("pcPriClassBase", ctypes.c_long), ("dwFlags", wt.DWORD),
                ("szExeFile", ctypes.c_wchar * 260)]


class _TE32(ctypes.Structure):
    _fields_ = [("dwSize", wt.DWORD), ("cntUsage", wt.DWORD), ("th32ThreadID", wt.DWORD),
                ("th32OwnerProcessID", wt.DWORD), ("tpBasePri", ctypes.c_long), ("tpDeltaPri", ctypes.c_long),
                ("dwFlags", wt.DWORD)]


class _MODINFO(ctypes.Structure):
    _fields_ = [("base", ctypes.c_void_p), ("size", wt.DWORD), ("entry", ctypes.c_void_p)]


class _MBI(ctypes.Structure):
    _fields_ = [("BaseAddress", ctypes.c_void_p), ("AllocationBase", ctypes.c_void_p), ("AllocationProtect", wt.DWORD),
                ("PartitionId", wt.WORD), ("RegionSize", ctypes.c_size_t), ("State", wt.DWORD), ("Protect", wt.DWORD),
                ("Type", wt.DWORD)]


class _TBI(ctypes.Structure):  # THREAD_BASIC_INFORMATION
    _fields_ = [("ExitStatus", ctypes.c_long), ("TebBaseAddress", ctypes.c_void_p), ("UniqueProcess", ctypes.c_void_p),
                ("UniqueThread", ctypes.c_void_p), ("AffinityMask", ctypes.c_size_t), ("Priority", ctypes.c_long),
                ("BasePriority", ctypes.c_long)]


def _find_pid():
    snap = k32.CreateToolhelp32Snapshot(2, 0)
    e = _PE32(); e.dwSize = ctypes.sizeof(_PE32)
    ok = k32.Process32FirstW(ctypes.c_void_p(snap), ctypes.byref(e))
    try:
        while ok:
            if e.szExeFile.lower() == PROCESS_NAME.lower():
                return e.th32ProcessID
            ok = k32.Process32NextW(ctypes.c_void_p(snap), ctypes.byref(e))
    finally:
        k32.CloseHandle(ctypes.c_void_p(snap))
    raise SystemExit("게임이 실행 중이 아닙니다 (SAN14CE_KO.exe)")


class Game:
    def __init__(self):
        self.pid = _find_pid()
        self.h = k32.OpenProcess(0x0410, False, self.pid)
        mods = (ctypes.c_void_p * 1024)(); need = wt.DWORD()
        psapi.EnumProcessModulesEx(ctypes.c_void_p(self.h), mods, ctypes.sizeof(mods), ctypes.byref(need), 3)
        self.exe = mods[0]
        mi = _MODINFO()
        psapi.GetModuleInformation(ctypes.c_void_p(self.h), ctypes.c_void_p(self.exe), ctypes.byref(mi), ctypes.sizeof(mi))
        self.exe_size = mi.size
        self.base = self.q(self.exe + BASE_POINTER_RVA) + BASE_ADJUST

    # --- raw reads
    def read(self, addr, n):
        buf = ctypes.create_string_buffer(n); got = ctypes.c_size_t()
        if not k32.ReadProcessMemory(ctypes.c_void_p(self.h), ctypes.c_void_p(addr), buf, n, ctypes.byref(got)) or got.value != n:
            return None
        return buf.raw

    def q(self, addr):
        b = self.read(addr, 8)
        return None if b is None else struct.unpack("<Q", b)[0]

    def memory_exports(self):
        # CRT internals can call their own memory routines without using the exe's import table.
        # Resolve the loaded DLL exports from the read-only process handle; forwarded exports are excluded.
        mods = (ctypes.c_void_p * 1024)(); need = wt.DWORD()
        psapi.EnumProcessModulesEx(ctypes.c_void_p(self.h), mods, ctypes.sizeof(mods), ctypes.byref(need), 3)
        result = {}
        for base in mods[:need.value // ctypes.sizeof(ctypes.c_void_p)]:
            name = ctypes.create_unicode_buffer(260)
            psapi.GetModuleBaseNameW(ctypes.c_void_p(self.h), ctypes.c_void_p(base), name, 260)
            dll = name.value.lower()
            if dll not in ('ntdll.dll', 'ucrtbase.dll'): continue
            hdr = self.read(base, 0x1000)
            pe = struct.unpack_from('<I', hdr, 0x3C)[0]
            rva, size = struct.unpack_from('<II', hdr, pe + 24 + 112)
            if not rva: continue
            e = struct.unpack('<IIHHIIIIIII', self.read(base + rva, 40))
            for k in range(e[7]):
                nrva = struct.unpack('<I', self.read(base + e[9] + 4 * k, 4))[0]
                symbol = self.read(base + nrva, 128).split(b'\0')[0].decode('ascii')
                if symbol not in ('memset', 'memcpy', 'memmove', 'memcmp'): continue
                ordinal = struct.unpack('<H', self.read(base + e[10] + 2 * k, 2))[0]
                target = struct.unpack('<I', self.read(base + e[8] + 4 * ordinal, 4))[0]
                if not rva <= target < rva + size: result[f'{dll}!{symbol}'] = base + target
        return result

    def region(self, addr):
        # (base, size, state, protect) of the region holding addr
        m = _MBI()
        if not k32.VirtualQueryEx(ctypes.c_void_p(self.h), ctypes.c_void_p(addr), ctypes.byref(m), ctypes.sizeof(m)):
            return None
        return (m.BaseAddress or 0, m.RegionSize, m.State, m.Protect)

    def regions(self, private_only=True):
        addr = 0; m = _MBI()
        while k32.VirtualQueryEx(ctypes.c_void_p(self.h), ctypes.c_void_p(addr), ctypes.byref(m), ctypes.sizeof(m)):
            b = m.BaseAddress or 0; size = m.RegionSize
            if m.State == 0x1000 and m.Protect in (0x04, 0x02, 0x40, 0x20, 0x08) and not (m.Protect & 0x100):
                if not private_only or m.Type in (0x20000, 0x1000000):
                    yield b, size, m.Type
            addr = b + size
            if addr >= 0x7FFFFFFFFFFF: break

    def threads(self):
        snap = k32.CreateToolhelp32Snapshot(4, 0)
        e = _TE32(); e.dwSize = ctypes.sizeof(_TE32)
        out = []
        ok = k32.Thread32First(ctypes.c_void_p(snap), ctypes.byref(e))
        while ok:
            if e.th32OwnerProcessID == self.pid: out.append(e.th32ThreadID)
            ok = k32.Thread32Next(ctypes.c_void_p(snap), ctypes.byref(e))
        k32.CloseHandle(ctypes.c_void_p(snap))
        return out

    def teb(self, tid):
        th = k32.OpenThread(0x0800, False, tid)   # THREAD_QUERY_LIMITED_INFORMATION
        if not th: return None
        tbi = _TBI()
        st = ntdll.NtQueryInformationThread(ctypes.c_void_p(th), 0, ctypes.byref(tbi), ctypes.sizeof(tbi), None)
        k32.CloseHandle(ctypes.c_void_p(th))
        return tbi.TebBaseAddress if st == 0 else None

    def main_teb(self):
        # The first thread of the process is the game's main (logic) thread.
        return self.teb(self.threads()[0])

    # --- game records
    def table(self, offset, i):
        return self.q(self.base + offset + i * 8)

    def officer(self, i):
        return self.table(0x737C0, i)

    def utf16(self, addr, chars):
        b = self.read(addr, chars * 2) or b""
        return b.decode("utf-16le", "ignore").split("\0")[0]

    def officer_name(self, i):
        p = self.officer(i)
        return (self.utf16(p + 0x12, 7) + self.utf16(p + 0x20, 7)) if p else "?"


if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")


# A game frozen in a page dump (Oracle.save_pages): the same reads the live game answered, without the game. Pages
# the dump lacks read as unreadable, so a module that touches new memory needs a live run (which saves them).
class FrozenGame:
    def __init__(self, path):
        import pickle
        d = pickle.load(open(path, "rb"))
        self.exe, self.exe_size, self.base, self._teb = d["exe"], d["exe_size"], d["base"], d["teb"]
        self._imports = d["imports"]
        self.pages = d["pages"]
        self.immutable_code_evidence = d.get("immutable_code_evidence")
        self.pid = None

    def read(self, addr, n):
        out = bytearray()
        a = addr
        while a < addr + n:
            page = a & ~0xFFF
            data = self.pages.get(page)
            if data is None: return None
            k = min(addr + n - a, page + 0x1000 - a)
            out += data[a - page: a - page + k]
            a += k
        return bytes(out)

    def q(self, addr):
        b = self.read(addr, 8)
        return None if b is None else struct.unpack("<Q", b)[0]

    def region(self, addr):
        # a dumped page is a committed region of its own; anything else counts as free up to the next dumped page
        # (at least a 4 GiB slot, so the oracle's free-range search ends)
        page = addr & ~0xFFF
        if page in self.pages: return (page, 0x1000, 0x1000, 0x04)
        end = page + 0x1_0000_0000
        for p in self.pages:
            if page < p < end: end = p
        return (page, end - page, 0x10000, 0)

    def main_teb(self):
        return self._teb

    def imports(self):
        return self._imports
