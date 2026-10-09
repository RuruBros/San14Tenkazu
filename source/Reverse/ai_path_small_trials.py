# Three planned complete native searches; observers run in copied Unicorn memory only.
# python ai_path_small_trials.py --plan <offline-plan.json> --vectors ../Tests/AiPathSmallTrialVectors.json.gz
import argparse
from datetime import datetime, timezone
import gzip
import hashlib
import json
from pathlib import Path
import struct

from ai_facility_special import read_clock
from ai_friend import HERE, vectors_text
from ai_native_snapshot import ListPoolSnapshot
from ai_path_global_branch import capture_global
from ai_path_search_audit import verify
from game import Game
from image import Image
from unicorn import UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RSP, UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_RDI, UC_X86_REG_RBX


def list_state(o, index_pointer, node):
    index = struct.unpack('<I', o.read(index_pointer, 4))[0]
    capacity = struct.unpack('<I', o.read(o.exe + 0x27D1120, 4))[0]
    if index >= capacity: raise AssertionError('Observed index is outside the native pool')
    def at(rva): return o.q(o.q(o.exe + rva) + index * 8)
    return dict(indexPointer=index_pointer, index=index, node=node, payload=o.q(node),
                next=o.q(node + 8), previous=o.q(node + 16), head=at(0x27D10F0),
                tail=at(0x27D10F8), count=at(0x27D1108))


class Observers:
    def __init__(self, o):
        self.o, self.pending, self.handles = o, {}, []
        self.result = dict(queueRemovals=[], globalListCalls=[])
        for pc in (0x18AE60, 0x1841FD, 0x18544A, 0x185B30, 0x183EC4):
            self.handles.append(o.mu.hook_add(UC_HOOK_CODE, self.observe, None, o.exe + pc, o.exe + pc))

    def observe(self, uc, address, size, user):
        o, pc = self.o, address - self.o.exe
        rsp = uc.reg_read(UC_X86_REG_RSP)
        if pc in (0x18AE60, 0x185B30):
            returned_to = o.q(rsp) - o.exe
            if pc == 0x18AE60 and returned_to in (0x1841FD, 0x18544A):
                head_address = uc.reg_read(UC_X86_REG_RCX)
                node = uc.reg_read(UC_X86_REG_RDX)
                event = dict(returnAddress=returned_to, rsp=rsp, headAddress=head_address,
                             node=node, hex=struct.unpack('<i', o.read(node + 24, 4))[0],
                             headBefore=o.q(head_address))
                self.result['queueRemovals'].append(event)
            elif pc == 0x185B30 and returned_to == 0x183EC4:
                event = dict(returnAddress=returned_to, rsp=rsp,
                             before=list_state(o, uc.reg_read(UC_X86_REG_RBX), uc.reg_read(UC_X86_REG_RDI)))
                self.result['globalListCalls'].append(event)
            else: return
            self.pending.setdefault(returned_to, []).append(event)
        else:
            pending = self.pending.get(pc, [])
            if not pending or rsp != pending[-1]['rsp'] + 8: return
            event = pending.pop()
            if pc == 0x183EC4:
                before = event['before']
                event['after'] = list_state(o, before['indexPointer'], before['node'])
            else: event['headAfter'] = o.q(event['headAddress'])


def configurations(plan):
    trials = plan['trials']
    if len(trials) != 2 or {t['rva'] for t in trials} != {0x183A30, 0x184EB0}:
        raise ValueError('Expected exactly the two planned backends')
    rows = []
    for trial in trials:
        row = {k: trial[k] for k in ('rva', 'source', 'goals', 'options', 'context')}
        row.update(clearBytes=48400, limit=trial.get('limit', 20000), armyPointer=trial['army']['pointer'],
                   plannedTarget=trial['target'], plannedPredictions=trial['predictions'], tracedHelpers=[0x18AE60])
        if 'excludedHexes' in trial: row['excludedHexes'] = trial['excludedHexes']
        rows.append(row)
    first = trials[0]
    rows.append(dict(rva=0x183A30, source=first['source'], goals=[first['source']], options=0,
                     context='army', armyPointer=first['army']['pointer'], clearBytes=48400,
                     imageFields=[(0x2141A84, 4, 1)], tracedHelpers=[0x185B30], plannedTarget=0x183ECB))
    return rows


def validate_identities(h, plan):
    valid = set(h.valid_table(0x7E838, 501)); records = []
    for trial in plan['trials']:
        expected = trial['army']; pointer = expected['pointer']
        if pointer not in valid: raise ValueError('Planned actual army is no longer valid')
        actual = dict(pointer=pointer, owner=h.o.call32(0x22CC40, pointer),
                      source=struct.unpack('<H', h.game.read(pointer + 0x2A, 2))[0])
        if actual != expected: raise ValueError(f'Planned army identity changed: {actual}')
        records.append(actual)
    return records


def main():
    parser = argparse.ArgumentParser(); parser.add_argument('--plan', type=Path, required=True)
    parser.add_argument('--vectors', type=Path); args = parser.parse_args()
    raw = args.plan.read_bytes(); plan = json.loads(raw); rows = configurations(plan)
    game = ListPoolSnapshot(); before = read_clock(game)
    if before != plan['source']['clock']: raise SystemExit('World identity or clock differs from the prepared plan; no searches started')
    im = Image()
    if hashlib.sha256(im.img).hexdigest() != plan['imageSha256']: raise SystemExit('Saved code differs from the prepared plan')
    global_snapshot = capture_global(game)
    flag_before = Game.read(game, game.exe + 0x2141A84, 4).hex()
    def prepare(h):
        identities = validate_identities(h, plan)
        observer = Observers(h.o); observer.result['actualArmies'] = identities
        return observer.result
    result = verify(game, False, rows, prepare)
    result.update(planSource=dict(path=str(args.plan), sha256=hashlib.sha256(raw).hexdigest()),
                  globalContextSnapshot=global_snapshot, liveFlagBefore=flag_before,
                  liveFlagAfter=Game.read(game, game.exe + 0x2141A84, 4).hex(),
                  staticGlobalList=im.flowtext(0x185B30),
                  observerSha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest())
    folder = HERE / '.cache' / 'ai_path_small_trials'; folder.mkdir(parents=True, exist_ok=True)
    path = folder / (datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ') + '.json')
    text = vectors_text(result); path.write_text(text, encoding='utf-8')
    print('queue removal observations', result['preparation']['queueRemovals'])
    print('global list observations', result['preparation']['globalListCalls'])
    print(len(result['cases']), 'completed;', len(result['failures']), 'faults;', path)
    if not result['clockStable'] or result['liveFlagBefore'] != result['liveFlagAfter'] or result['failures']:
        raise SystemExit('Unstable or faulted execution; diagnostics only')
    if args.vectors: args.vectors.write_bytes(gzip.compress(text.replace('\n', '\r\n').encode('utf-8'), mtime=0))


if __name__ == '__main__': main()
