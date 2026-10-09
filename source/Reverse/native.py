# The game's own CRT math (pow, ...) run natively on this CPU, as the game runs it (its FMA3 path where the CPU has
# FMA): the dumped image is copied into executable memory of this process and the leaf function called there. Its
# tables are reached rip-relative, so the copy needs no relocation. Used by the oracle in place of emulating it
# (unicorn has no AVX/FMA).
import ctypes

from image import Image

_k32 = ctypes.WinDLL("kernel32", use_last_error=True)
_k32.VirtualAlloc.restype = ctypes.c_void_p
_k32.VirtualAlloc.argtypes = [ctypes.c_void_p, ctypes.c_size_t, ctypes.c_uint32, ctypes.c_uint32]
_copy = None


def _image():
    global _copy
    if _copy is None:
        img = Image().img
        base = _k32.VirtualAlloc(None, len(img), 0x3000, 0x40)   # MEM_COMMIT|MEM_RESERVE, PAGE_EXECUTE_READWRITE
        ctypes.memmove(base, img, len(img))
        _copy = base
    return _copy


POW_RVA = 0x101EEE0


def pow(x, y):
    f = ctypes.CFUNCTYPE(ctypes.c_double, ctypes.c_double, ctypes.c_double)(_image() + POW_RVA)
    return f(x, y)
