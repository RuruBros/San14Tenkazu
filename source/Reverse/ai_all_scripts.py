# Audit every CSymboScript slot and execute all entries in copied pages, including unused formulas.
#   python ai_all_scripts.py --vectors ../Tests/AiAllScriptVectors.json
import argparse
from collections import Counter
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import random
import struct

import ai
from ai_friend import ARGS, EXECUTE, HERE, TABLE, divide, expression, i32, read, vectors_text
from game import Game

EXPECTED = {1: '((arg0 * (100 + arg1)) / 100)', 2: '((arg0 - arg1) + arg2)',
            3: '((arg0 * 100) / arg1)',
            4: '((((((arg0 + arg1) + arg2) + arg3) + arg4) + arg5) + arg6)',
            5: '((arg0 * (100 + arg1)) / 100)', 6: '(90 + ((arg0 * 100) / 100))',
            7: '(arg0 * (100 - arg1))', 8: '(arg0 * arg1)', 9: '(arg0 - arg1)',
            10: '(arg0 + arg1)', 11: '(arg0 / arg1)',
            12: '((((((100 * arg0) / arg1) - arg2) - arg3) + arg4) + arg5)',
            13: '((arg0 * (100 - arg1)) / 100)'}


def model(script, a):
    if script in (1, 5): return divide(i32(a[0] * i32(100 + a[1])), 100)
    if script == 2: return i32(a[0] - a[1] + a[2])
    if script == 3: return divide(i32(a[0] * 100), a[1])
    if script == 4: return i32(sum(a[:7]))
    if script == 6: return i32(90 + divide(i32(a[0] * 100), 100))
    if script == 7: return i32(a[0] * i32(100 - a[1]))
    if script == 8: return i32(a[0] * a[1])
    if script == 9: return i32(a[0] - a[1])
    if script == 10: return i32(a[0] + a[1])
    if script == 11: return divide(a[0], a[1])
    if script == 12: return i32(divide(i32(100 * a[0]), a[1]) - a[2] - a[3] + a[4] + a[5])
    if script == 13: return divide(i32(a[0] * i32(100 - a[1])), 100)
    return 0


def verify(game):
    from oracle import Oracle, OracleError
    from unicorn import UC_HOOK_INTR
    from unicorn.x86_const import UC_X86_REG_RIP, UC_X86_REG_EAX, UC_X86_REG_EBX, UC_X86_REG_EDX
    oracle = Oracle(game=game)
    interrupts = []
    def observe_interrupt(mu, number, _):
        interrupts.append(number)
        mu.emu_stop()
    oracle.mu.hook_add(UC_HOOK_INTR, observe_interrupt)
    count, capacity, buffer = struct.unpack('<IIQ', read(game, game.exe + TABLE + 8, 16))
    if count != 896 or capacity < count: raise AssertionError((count, capacity))
    entries, tokens_by_script = [], {}
    for script in range(count):
        vt, ev, kind, depth, token = struct.unpack('<QQIIQ', read(game, buffer + script * 32, 32))
        tokens, seen = [], set()
        while token:
            if token in seen or len(tokens) >= 128: raise AssertionError('invalid token chain')
            seen.add(token)
            tv, op, dep, next_token = struct.unpack('<QIIQ', read(game, token, 24))
            if tv - game.exe != 0x1352C88: raise AssertionError('unexpected token vtable')
            tokens.append({'opcode': op, 'depth': dep, 'address': hex(token)})
            token = next_token
        expr = expression(tokens) if tokens else None
        expected = EXPECTED.get(script, '0' if 14 <= script <= 100 else None)
        if expr != expected: raise AssertionError((script, expr, expected))
        entries.append([script, kind, depth, expr])
        if script in EXPECTED: tokens_by_script[str(script)] = tokens
    # A native call for every slot distinguishes constant-zero entries from empty entries without guessing.
    cases = [(script, [0] * 8) for script in range(count)]
    rng = random.Random(0x93BA0)
    boundaries = [0, 1, -1, 20, -20, 2147483647, -2147483648, 536870912]
    for script in EXPECTED:
        cases += [(script, [a, b, 30, -20, 75, 30, 9, 7]) for a in boundaries for b in boundaries]
        cases += [(script, [rng.randrange(-0x80000000, 0x80000000) for _ in range(8)]) for _ in range(64)]
    answers = []
    for script, a in cases:
        interrupts.clear()
        for index, value in enumerate(a):
            oracle.poke(game.exe + ARGS + index * 32, struct.pack('<i', value))
        try:
            expected, trap = model(script, a), False
        except OverflowError:
            expected, trap = None, True
        try:
            actual = oracle.call32(EXECUTE, buffer + script * 32, 0)
            if trap or actual != expected: raise AssertionError((script, a, actual, expected))
            answers.append({'script': script, 'args': a, 'native': actual})
        except OracleError as error:
            registers = [oracle.mu.reg_read(reg) for reg in (UC_X86_REG_RIP, UC_X86_REG_EAX, UC_X86_REG_EBX, UC_X86_REG_EDX)]
            if (not trap or interrupts != [0] or registers !=
                    [game.exe + 0x92ED3, 0x80000000, 0xFFFFFFFF, 0xFFFFFFFF]):
                raise AssertionError((script, a, trap, interrupts, registers, str(error))) from error
            answers.append({'script': script, 'args': a, 'trap': 'signed-division-overflow',
                            'interrupts': list(interrupts), 'registers': registers, 'error': str(error)})
            # Unicorn retains exception delivery state after #DE; use a new copied-page CPU for the next case.
            oracle = Oracle(game=game)
            oracle.mu.hook_add(UC_HOOK_INTR, observe_interrupt)
    uses = Counter(node.params[0] for tree in ai.read_file() for node in tree if node.cls == 0x72E99C29)
    return {'utc': datetime.now(timezone.utc).isoformat(), 'pid': game.pid,
            'method': 'native 0x93BA0 on all script entries in Unicorn copied pages; no game writes',
            'oracle_sha256': hashlib.sha256((HERE / 'oracle.py').read_bytes()).hexdigest(),
            'count': count, 'capacity': capacity, 'slots': entries, 'tokens': tokens_by_script,
            'stock_uses': dict(sorted(uses.items())), 'cases': answers}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--vectors', type=Path)
    args = parser.parse_args()
    result = verify(Game())
    folder = HERE / '.cache' / 'ai_all_scripts'
    folder.mkdir(parents=True, exist_ok=True)
    path = folder / (datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ') + '.json')
    text = vectors_text(result)
    path.write_text(text, encoding='utf-8')
    if args.vectors: args.vectors.write_text(text, encoding='utf-8')
    print(f"{result['count']} slots audited; {len(result['cases'])} native cases including "
          f"{sum('trap' in c for c in result['cases'])} division traps matched; {path}")


if __name__ == '__main__': main()
