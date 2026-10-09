# Select small native-trial inputs from saved complete-search evidence.
# python ai_path_reinsert_plan.py --output .cache/ai_path_reinsert_plan.json
# Predictions are input planning, not native coverage. No Game imports or process reads.
import argparse
from collections import Counter
import gzip
import hashlib
import heapq
from itertools import count
import json
from pathlib import Path
import struct

from image import Image
from unicorn import Uc, UC_ARCH_X86, UC_MODE_64
from unicorn.x86_const import UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_RAX, UC_X86_REG_RSP, UC_X86_REG_RIP

HERE = Path(__file__).resolve().parent


class Neighbors:
    # Entire original 2A7720, including its switch table, in independent memory.
    def __init__(self, im):
        begin, end = im.func(0x2A7720)
        instructions = list(im.insns(begin, end))
        if begin != 0x2A7720 or any(i.mnemonic == 'call' for i in instructions):
            raise ValueError('Neighbor helper acquired a call or changed entry')
        start, stop = begin & ~0xFFF, (end + 28 + 0xFFF) & ~0xFFF
        self.machine = Uc(UC_ARCH_X86, UC_MODE_64)
        self.machine.mem_map(im.base + start, stop - start)
        self.machine.mem_write(im.base + start, im.img[start:stop])
        self.stack, self.stop = 0x400000000, 0x500000000
        self.machine.mem_map(self.stack, 0x2000)
        self.machine.mem_map(self.stop, 0x1000)
        self.machine.mem_write(self.stop, b'\xF4')
        self.entry, self.cache, self.calls = im.base + begin, {}, 0

    def __call__(self, tile):
        if tile not in self.cache:
            values = []
            for direction in range(1, 7):
                self.machine.mem_write(self.stack + 0x1000, struct.pack('<Q', self.stop))
                self.machine.reg_write(UC_X86_REG_RSP, self.stack + 0x1000)
                self.machine.reg_write(UC_X86_REG_RCX, tile)
                self.machine.reg_write(UC_X86_REG_RDX, direction)
                self.machine.emu_start(self.entry, self.stop, count=1000)
                if self.machine.reg_read(UC_X86_REG_RIP) != self.stop or self.machine.reg_read(UC_X86_REG_RSP) != self.stack + 0x1008:
                    raise ValueError('Original neighbor helper did not return')
                value = self.machine.reg_read(UC_X86_REG_RAX) & 0xFFFFFFFF
                if value < 48400: values.append(value)
                self.calls += 1
            self.cache[tile] = values
        return self.cache[tile]


def inputs(run):
    if len(run['goals']) != 1 or run['changes'] or not run['options'] & 5:
        raise ValueError('Planner requires one goal, unchanged world inputs and heuristic mode')
    step, heuristic = {}, {}
    for row in run['relaxations']:
        tile, cost = row['hex'], row['stepCost']
        if cost < 0 or tile in step and step[tile] != cost:
            raise ValueError('Observed destination cost is negative or changes')
        step[tile] = cost
        if 'priority' in row:
            value = row['priority'] - row['newCost']
            if value < 0 or tile in heuristic and heuristic[tile] != value:
                raise ValueError('Observed destination heuristic is negative or changes')
            heuristic[tile] = value
    return step, heuristic


def model(run, neighbors, allowed=None, limit=None):
    step, heuristic = inputs(run)
    if run['rva'] == 0x184EB0 and allowed is not None:
        raise ValueError('Bounded backend does not consult the exclusion list')
    allowed = set(step) if allowed is None else allowed
    nodes = {g: [0, 1, None] for g in run['goals']}
    heap, serial, versions = [], count(), Counter()
    for goal in run['goals']: heapq.heappush(heap, (0, next(serial), goal, 0))
    queued, events, empty = set(run['goals']), [], []
    bounded = run['rva'] == 0x184EB0
    limit = run['limit'] if limit is None else limit
    while heap:
        _, _, parent, version = heapq.heappop(heap)
        if version != versions[parent] or parent not in queued: continue
        queued.remove(parent)
        if parent == run['source']: break
        parent_cost = nodes[parent][0]
        for tile in neighbors(parent):
            if tile not in allowed or tile not in step: continue
            old, flags, _ = nodes.get(tile, [0, 0, None])
            cost = parent_cost + step[tile]
            if not 0 <= cost <= 2147483647: raise ValueError('Planning cost is outside observed signed range')
            accepted = flags & 3 == 0 or old > cost
            events.append(dict(hex=tile, parentHex=parent, parentCost=parent_cost, stepCost=step[tile],
                               newCost=cost, oldCost=old, flags=flags, accepted=accepted))
            if not accepted: continue
            if flags & 1:
                queued.remove(tile)
                if not queued: empty.append(dict(hex=tile, parentHex=parent, oldCost=old, newCost=cost))
            nodes[tile] = [cost, flags & 0xFD, parent]
            if not bounded or cost <= limit:
                if tile not in heuristic: raise ValueError('No observed heuristic for a planned queued node')
                nodes[tile][1] |= 1
                queued.add(tile); versions[tile] += 1
                heapq.heappush(heap, (cost + heuristic[tile], next(serial), tile, versions[tile]))
        nodes[parent][1] = nodes[parent][1] & 0xFE | 2
    return dict(events=events, emptyQueuePredictions=empty)


def corridors(run):
    previous = {g: None for g in run['goals']}
    def path(tile):
        seen = []
        while tile is not None:
            if tile in seen or tile not in previous: return None
            seen.append(tile); tile = previous[tile]
        return seen
    for row in run['relaxations']:
        if not row['accepted']: continue
        if row['flags'] & 1:
            old, new = path(row['hex']), path(row['parentHex'])
            if old and new: yield set(old + new + [run['source']])
        previous[row['hex']] = row['parentHex']


def build(path, im):
    raw = path.read_bytes(); root = json.loads(gzip.decompress(raw))
    if not root['clockStable'] or root['clockBefore'] != root['clockAfter'] or root['failures']:
        raise ValueError('Unstable or faulted evidence')
    neighbors = Neighbors(im)
    validated, predictions = [], []
    for case, run in enumerate(root['cases']):
        if not run['options'] & 5: continue
        replay = model(run, neighbors)['events']
        original = run['relaxations']
        if len(replay) != len(original) or any(any(row[key] != value for key, value in expected.items()) for row, expected in zip(original, replay)):
            raise ValueError(f'Planner does not reproduce the complete logged decisions in case {case}')
        validated.append(dict(case=case, decisions=len(replay)))
    attempts, found = 0, set()
    for case, run in enumerate(root['cases']):
        if run['options'] not in (1, 65) or run['limit'] != 20000 or run['rva'] in found: continue
        recipe = dict(case=case, rva=run['rva'], source=run['source'], goals=run['goals'], options=run['options'],
                      context=run['context'], army=run['actualRoute']['army'], nativeObserved=False)
        if run['rva'] == 0x183A30:
            for allowed in corridors(run):
                attempts += 1
                result = model(run, neighbors, allowed)
                if result['emptyQueuePredictions']:
                    # A boundary around the retained corridor suffices; do not allocate 48400 exclusions.
                    boundary = sorted({n for tile in allowed for n in neighbors(tile)} - allowed)
                    predictions.append(dict(recipe, target=0x184230, allowedHexes=sorted(allowed), excludedHexes=boundary,
                                            predictions=result['emptyQueuePredictions']))
                    found.add(run['rva']); break
        else:
            # 184EB0 does not consult the context+20 exclusion list. Only change its genuine limit.
            for limit in sorted({r['oldCost'] for r in run['relaxations'] if r['accepted'] and r['flags'] & 1}):
                attempts += 1
                result = model(run, neighbors, limit=limit)
                if result['emptyQueuePredictions']:
                    predictions.append(dict(recipe, target=0x18547D, limit=limit, predictions=result['emptyQueuePredictions']))
                    found.add(run['rva']); break
        if len(found) == 2: break
    return dict(method='offline input planning from actual complete decision sequences; original pure neighbor leaf only; modeled cutsets and limits are predictions, not native backend coverage',
                source=dict(path=str(path), sha256=hashlib.sha256(raw).hexdigest(), clock=root['clockBefore']),
                imageSha256=hashlib.sha256(im.img).hexdigest(), validatedSearches=validated,
                validatedDecisions=sum(r['decisions'] for r in validated), candidateAttempts=attempts,
                originalNeighborCalls=neighbors.calls, trials=predictions,
                limits=['Step costs and heuristics are from the recorded world; reread actual input identities and compare native results before accepting a prediction.',
                        'Only complete logged decision replay is validated; reduced corridors and changed limits have not run in either native backend.',
                        'The model includes observed admissible tiles, not a substitute for native world admission helpers.',
                        'Equal priorities use insertion order, matching the tested native signed BST insertion/removal ordering.'],
                globalListTrial=dict(rva=0x183A30, target=0x183ECB, nativeObserved=False,
                                     method='one source=goal search with copied global flag enabled; read list head and next before and after original185B30; do not invent another list node'))


def main():
    parser = argparse.ArgumentParser(); parser.add_argument('--output', type=Path, required=True); args = parser.parse_args()
    result = build(HERE.parent / 'Tests' / 'AiPathReinsertVectors.json.gz', Image())
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(len(result['validatedSearches']), 'reproduced searches;', result['validatedDecisions'], 'decisions;',
          result['candidateAttempts'], 'offline candidate attempts;', len(result['trials']), 'native trials pending')
    for trial in result['trials']: print(hex(trial['target']), trial['source'], trial['goals'], 'exclusions', len(trial.get('excludedHexes', [])), 'limit', trial.get('limit'), trial['predictions'])


if __name__ == '__main__': main()
