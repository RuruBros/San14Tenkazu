"""Attach to SAN14CE_KO with QUERY_INFORMATION | VM_READ only."""
import ctypes as c
from ctypes import wintypes as w
from datetime import datetime, timedelta, timezone
from pathlib import Path
import game

def connect():
    pid = game._find_pid()
    g = game.Game.__new__(game.Game)
    g.pid = pid
    g.h = game.k32.OpenProcess(0x0410, False, pid)
    if not g.h:
        raise c.WinError(c.get_last_error())
    path = c.create_unicode_buffer(32768)
    size = w.DWORD(len(path))
    if not game.k32.QueryFullProcessImageNameW(c.c_void_p(g.h), 0, path, c.byref(size)):
        raise c.WinError(c.get_last_error())
    if Path(path.value).name.upper() != 'SAN14CE_KO.EXE':
        raise ValueError(path.value)
    times = [w.FILETIME() for _ in range(4)]
    if not game.k32.GetProcessTimes(c.c_void_p(g.h), *(c.byref(t) for t in times)):
        raise c.WinError(c.get_last_error())
    created = (times[0].dwHighDateTime << 32) | times[0].dwLowDateTime
    start = datetime(1601, 1, 1, tzinfo=timezone.utc) + timedelta(microseconds=created // 10)
    module = c.c_void_p()
    needed = w.DWORD()
    if not game.psapi.EnumProcessModulesEx(c.c_void_p(g.h), c.byref(module), c.sizeof(module), c.byref(needed), 3):
        raise c.WinError(c.get_last_error())
    g.exe = module.value
    return g, Path(path.value), start
