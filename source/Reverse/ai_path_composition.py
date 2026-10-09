# Original path wrappers and segment chains, copied pages only.
# python ai_path_composition.py [--smoke] [--vectors ../Tests/AiPathCompositionVectors.json]
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import struct
import time
from ai_executor_audit import Harness
from ai_facility_special import read_clock
from ai_friend import HERE, vectors_text
from ai_native_snapshot import ListPoolSnapshot
from ai_native_trace import Trace
from game import Game
from image import Image
from oracle import OracleError
from unicorn.x86_const import UC_X86_REG_RAX, UC_X86_REG_RSP

WRAPPERS = (0x1843E0, 0x185620)
BACKENDS = (0x183A30, 0x184EB0)

class SegmentTrace(Trace):
    def enter(self, uc, address, size, user):
        super().enter(uc, address, size, user)
        e = self.events[-1]
        e['stackArgs'] = [self.oracle.q(e['rsp'] + 0x28 + i * 8) for i in range(4)]
        e['source'] = e['args'][1]
        e['target'] = struct.unpack('<I', self.oracle.read(e['args'][2], 4))[0]
        e['scratchClearBytes'] = e['stackArgs'][1]
        e['nodesBefore'] = []
        for index in dict.fromkeys((e['source'], e['target'])):
            if not 0 <= index < 48400:
                continue
            b = self.oracle.read(e['stackArgs'][0] + index * 40, 40)
            e['nodesBefore'].append({'id': index, 'flags': b[28], 'cost': struct.unpack_from('<i', b, 32)[0],
                                     'next': struct.unpack_from('<Q', b, 16)[0]})

    def leave(self, uc, address, size, user):
        pending = self.pending.get(address, [])
        e = pending[-1] if pending and uc.reg_read(UC_X86_REG_RSP) == pending[-1]['rsp'] + 8 else None
        super().leave(uc, address, size, user)
        if e is None:
            return
        pointer, nodes, seen = uc.reg_read(UC_X86_REG_RAX), [], set()
        while pointer:
            if pointer in seen or len(nodes) >= 48400:
                raise AssertionError('cyclic native chain')
            seen.add(pointer)
            b = self.oracle.read(pointer, 40)
            nodes.append({'id': struct.unpack_from('<I', b, 24)[0], 'flags': b[28],
                          'cost': struct.unpack_from('<i', b, 32)[0]})
            pointer = struct.unpack_from('<Q', b, 16)[0]
        e['chain'] = nodes

def plans(smoke):
    rows = []
    for rva in WRAPPERS:
        for count in (-2147483648, -1, 0):
            rows.append(dict(rva=rva, targets=[], count=count, options=1, slot=2, limit=20000, context='force'))
        for slot in (0, 1, 2):
            for options in (0, 1, 4, 8, 16, 33, 64, 255):
                rows.append(dict(rva=rva, targets=[12781], count=1, options=options, slot=slot,
                                 limit=20000, context='force'))
        for context in ('neutral', 'transport', 'army'):
            rows.append(dict(rva=rva, targets=[12781], count=1, options=33, slot=2, limit=20000, context=context))
        for targets in ([0], [12780], [12781, 0], [12781, 13001], [12780, 12781], [12781, 12780], [12781, 12781]):
            rows.append(dict(rva=rva, targets=targets, count=len(targets), options=1, slot=2,
                             limit=20000, context='force'))
    for limit in (-1, 0, 1, 299, 300, 301, 599, 600):
        rows.append(dict(rva=0x185620, targets=[12781], count=1, options=1, slot=2, limit=limit, context='force'))
    return rows[:8] if smoke else rows

def verify(game, smoke):
    before = read_clock(game)
    h = Harness(game); o = h.o; o.insn_limit = 600_000_000
    force = o.q(h.raw + 0xDF30 + 29 * 8)
    if not o.call32(0x32F500, force):
        raise AssertionError('valid force 29 is required')
    army = h.valid_table(0x7E838, 501)[0]
    context, out, points = o.alloc(0x30), o.alloc(16), o.alloc(32)
    cases, failures = [], []
    for index, config in enumerate(plans(smoke)):
        if read_clock(game) != before:
            raise AssertionError('game clock/world changed')
        a = army if config['context'] == 'army' else 0
        f = 0 if config['context'] == 'neutral' else force
        o.poke(context, bytes(0x30))
        o.poke(context, struct.pack('<QQI', a, f, int(config['context'] == 'transport')))
        o.poke(points, struct.pack('<8I', *config['targets'], *([0] * (8 - len(config['targets'])))))
        trace = SegmentTrace(o, {rva: None for rva in BACKENDS})
        row = dict(config)
        def output_ranges():
            pointer, count, cost = struct.unpack('<Qii', o.read(out, 16))
            if not 0 <= count <= 1320:
                raise AssertionError('path exceeds the original output buffer')
            return [(out, 16), (pointer, count * 4)]
        try:
            args = (config['count'], config['options'], config['slot'])
            if config['rva'] == 0x185620:
                args += (config['limit'],)
            row['native'] = o.call32(config['rva'], out, context, 12780, points, stack_args=args, read_after=output_ranges)
            pointer, row['count'], row['cost'] = struct.unpack('<Qii', o.after[0])
            row['path'] = list(struct.unpack('<' + 'I' * row['count'], o.after[1]))
            row['outputOffset'] = pointer - o.exe
            expected_path, expected_cost = [], 0
            for segment, e in enumerate(trace.events):
                chain = e['chain'][int(segment > 0):]
                if e['chain'] and not chain:
                    raise AssertionError('completed wrapper read a null skipped head')
                if chain:
                    expected_cost = (expected_cost + chain[0]['cost'] + 2**31) % 2**32 - 2**31
                    expected_path.extend(n['id'] for n in chain)
            if (row['native'], row['path'], row['cost']) != (int(bool(expected_path)), expected_path, expected_cost):
                raise AssertionError('wrapper and independently observed chain composition disagree')
            cases.append(row)
        except OracleError as error:
            row['error'] = str(error)
            failures.append(row)
        finally:
            row['events'] = trace.events.copy(); trace.close()
        print(index, hex(config['rva']), config['targets'], row.get('native', row.get('error')), flush=True)
    after = read_clock(game)
    im = Image()
    return dict(utc=datetime.now(timezone.utc).isoformat(), clockBefore=before, clockAfter=after,
        clockStable=before == after, listPoolSnapshot=game.pool_snapshot, instructionLimit=o.insn_limit,
        method='original complete wrappers and segment backends; return and scratch-node observers; copied inputs only; no game writes or helper replacement',
        oracle_sha256=hashlib.sha256((HERE/'oracle.py').read_bytes()).hexdigest(),
        static={hex(rva): im.flowtext(rva) for rva in (*WRAPPERS, *BACKENDS, 0x18AFA0)}, cases=cases, failures=failures)

def main():
    p=argparse.ArgumentParser();p.add_argument('--smoke',action='store_true');p.add_argument('--vectors',type=Path);args=p.parse_args()
    game=Game();clock=read_clock(game);time.sleep(3)
    if read_clock(game)!=clock: raise SystemExit('Game clock changed before copying')
    snapshot=ListPoolSnapshot()
    if read_clock(snapshot)!=clock: raise SystemExit('Game clock changed while copying')
    result=verify(snapshot,args.smoke)
    folder=HERE/'.cache'/'ai_path_composition';folder.mkdir(parents=True,exist_ok=True)
    path=folder/(datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.json')
    text=vectors_text(result);path.write_text(text,encoding='utf-8')
    if args.vectors and result['clockStable']: args.vectors.write_text(text,encoding='utf-8')
    print(f"{len(result['cases'])} completed; {len(result['failures'])} separate faults; {path}")
    if not result['clockStable']: raise SystemExit('Clock changed; diagnostics only')
if __name__=='__main__': main()
