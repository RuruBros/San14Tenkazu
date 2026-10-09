# Reconstruct the previous-poll inputs of mismatched tactic queue/result replays in a frozen oracle.
# The running game and the frozen dump are never modified. This does not recover invocation-time inputs.
import argparse
import gzip
import json
import re
import struct
import sys
from pathlib import Path

root = Path(__file__).resolve().parent
sys.path.insert(0, str(root / 'vectors'))
from context import Context
import world
from tactic_targets import TargetOracle, TACTICS
from tactic_damage import DamageMaps
from unicorn import UC_HOOK_MEM_READ

parser = argparse.ArgumentParser(description='Replay ObservationReport tactic mismatches on recorded poll inputs using a frozen oracle.')
parser.add_argument('directory', type=Path, help='Observation directory containing world.json.gz, events.jsonl and report.txt')
parser.add_argument('--frozen', type=Path, default=root / '.cache/frozen.pkl', help='Read-only frozen oracle dump')
args = parser.parse_args()
directory = args.directory.resolve()
sys.stdout.reconfigure(line_buffering=True)
ctx = Context(str(args.frozen.resolve()))
snapshot = json.load(gzip.open(directory / 'world.json.gz', 'rt'))

def require(condition, message):
    if not condition:
        raise ValueError(message)

require(snapshot['byid'] == ctx.snap['byid'], 'Officer ID mappings differ')
commands_differ = snapshot['cmdactions'] != ctx.snap['cmdactions']
baseline_pokes = []
for key, table in snapshot['tables'].items():
    frozen = ctx.snap['tables'][key]
    require(table['address'] == frozen['address'], f'Table addresses differ: {key}')
    require(table['size'] == frozen['size'] and len(table['records']) == len(frozen['records']), f'Table layout differs: {key}')
    require(table['extra'] == frozen['extra'], f'External vector data differs: {key}')
    for index, (record, old) in enumerate(zip(table['records'], frozen['records'])):
        if record != old:
            baseline_pokes.append([key, index, 0, record])
for key, lst in snapshot['lists'].items():
    baseline_pokes.append(['listdata', key, lst['items'], ''])
print(json.dumps({'frozen': str(args.frozen.resolve()), 'layout': 'all table addresses/sizes/counts, external vectors and officer ID mappings match',
                  'command_metadata_differs': commands_differ, 'baseline_pokes': len(baseline_pokes)}))

events = {float(ev['t']): ev for ev in map(json.loads, (directory / 'events.jsonl').read_text(encoding='utf-8').splitlines())}
report_lines = (directory / 'report.txt').read_text(encoding='utf-8').splitlines()
selected = []
for index, line in enumerate(report_lines):
    match = re.match(r't=([0-9.]+): tactic flow queue/results match=False', line)
    if match:
        output = re.fullmatch(r'  computed queue (.*); rows (.*)', report_lines[index + 1])
        require(output is not None, f'Missing computed tactic output after report line {index + 1}')
        selected.append((events[float(match[1])], *map(json.loads, output.groups())))

o = ctx.o
target_oracle = TargetOracle(ctx)
manager, sides, nodes, output_vectors = o.alloc(0x700), o.alloc(64), o.alloc(16 * 64), o.alloc(0x20000)
maps = DamageMaps(ctx)
maps.maps = manager + 0xB8
for rva in [0x1A3A00, 0x1A3C50, 0x404080, 0x18ACF0, 0x18C270, 0x84DEF0, 0x1B4AB0, 0x168BB0]:
    o.stub(rva, 0)
tactic_refs = {ctx.addr(TACTICS, i): i for i in range(201)}
tile_start = ctx.addr(0xE270, 0)
command_reads = []
# Objects.Away (0x232240 -> 0x22AA70) obtains the command manager from this World slot.
# If different command metadata is read, this reconstruction must stop rather than substitute its results.
command_address = ctx.raw + 0x871E0
command_hook = o.mu.hook_add(UC_HOOK_MEM_READ, lambda mu, access, address, size, value, data: command_reads.append(address),
                            begin=command_address, end=command_address + 7)

def targets(wrapper):
    cell = o.q(wrapper + 8)
    slot = struct.unpack('<I', o.read(cell, 4))[0]
    pool_count = struct.unpack('<I', o.read(ctx.g.exe + world.POOL_RVA + 0x30, 4))[0]
    require(slot < pool_count, 'Native target-list pool index is out of range')
    node, result, seen = o.q(target_oracle.pool + slot * 8), [], set()
    while node:
        require(node not in seen and len(result) < 48400, 'Cyclic or oversized native target list')
        seen.add(node)
        pointer, node = struct.unpack('<QQ', o.read(node, 16))
        tile, remainder = divmod(pointer - tile_start, 32)
        require(remainder == 0 and 0 <= tile < 48400, 'Unknown native target tile pointer')
        result.append(tile)
    return result

for event, core_queue, core_rows in selected:
    captured = event['tactics_after']
    context = captured['context']
    def matches(side, unit, base):
        reference = unit if side[1] == 27 else base
        return reference == [{27: '0x7e838', 5: '0xdd38', 6: '0xded8'}.get(side[1]), side[0]]
    pairs = {(tuple(a[:4]), tuple(b[:4])) for a, b in event['records_before'] + event['records_after']
             if matches(a, context[0], context[1]) and matches(b, context[2], context[3])}
    require(len(pairs) == 1, f'Ambiguous source/target context at t={event["t"]}')
    a, b = next(iter(pairs))
    pokes = list(baseline_pokes)
    for key, records in event['input_tables'].items():
        pokes.extend([key, int(index), 0, record] for index, record in records.items())
    pokes.extend([['worldstate', 0, 0, event['worldstate']], ['settings', 0, 0, event['settings']]])
    for key, value in event['flags'].items():
        if world.EXE_VALUES[key][1] == 'set':
            require(value == ctx.snap['flags'][key], f'Set-valued flag differs: {key}')
        else:
            pokes.append(['exe', key, 0, struct.pack('<i', value).hex()])
    for key, rows in event['input_lists'].items():
        pokes.append(['listdata', key, rows, ''])

    def run():
        command_reads.clear()
        o.poke(manager, bytes(0x700))
        o.poke(manager + 0x48, o.read(ctx.g.exe + 0x27D1128 + 0x40, 0x50))
        for i in range(16):
            o.poke(nodes + 64 * i, bytes(64))
            o.poke(nodes + 64 * i + 48, struct.pack('<Q', nodes + 64 * (i + 1) if i < 15 else 0))
        o.poke(manager + 0x28, struct.pack('<Q', nodes))
        maps.prepare(None)
        for i in range(11):
            o.poke(manager + 0xD8 + 64 * i + 8, struct.pack('<I', 255))
            for slot in range(2):
                buffer = output_vectors + (2 * i + slot) * 0x1000
                o.poke(manager + 0xD8 + 64 * i + 16 + 24 * slot, struct.pack('<QQQ', buffer, buffer, buffer + 0x1000))
        for i, side in enumerate((a, b)):
            o.poke(sides + 32 * i, struct.pack('<H2xI4xIIB3x', *side, 0))
        native_queue, native_rows = [], []
        def inspect():
            node = o.q(manager + 0x18)
            seen = set()
            while node:
                require(node not in seen and len(native_queue) < 11, 'Cyclic or oversized native tactic queue')
                seen.add(node)
                kind, number, tactic = struct.unpack('<IH2xQ', o.read(node, 16))
                native_queue.append([kind, number, tactic_refs[tactic], [targets(node + 16 + 16 * slot) for slot in range(2)]])
                node = o.q(node + 48)
            for i in range(11):
                row = manager + 0xD8 + 64 * i
                tactic, kind, number = struct.unpack('<QII', o.read(row, 16))
                if kind == 255:
                    continue
                slots = []
                for slot in range(2):
                    begin, end, cap = struct.unpack('<QQQ', o.read(row + 16 + 24 * slot, 24))
                    require(begin <= end <= cap and (end - begin) % 16 == 0, 'Invalid native result vector')
                    outputs = []
                    for address in range(begin, end, 16):
                        k, n, amount, unused, tile = struct.unpack('<IIHHI', o.read(address, 16))
                        outputs.append([k, n, tile, amount])
                    slots.append(outputs)
                native_rows.append([i, tactic_refs[tactic], kind, number, slots])
            return []
        fired = o.call(0x172E20, manager, sides, sides + 32, read_after=inspect) & 0xFFFFFFFF
        return {'fired': fired, 'queue': native_queue, 'rows': native_rows}

    native = ctx.run(pokes, run)
    if native is None:
        raise RuntimeError(f'Oracle could not replay t={event["t"]}: {ctx.failures[-1]["error"]}')
    require(not commands_differ or not command_reads, 'Native read the unmatched command manager')
    matches_core = native['fired'] == int(bool(core_queue)) and native['queue'] == core_queue and native['rows'] == core_rows
    print(json.dumps({'t': event['t'], 'native': native, 'command_manager_reads': len(command_reads), 'matches_core_poll_input': matches_core,
                      'matches_observed_queue': native['queue'] == captured['queue'],
                      'matches_observed_rows': native['rows'] == captured['rows']}, separators=(',', ':')))
    require(matches_core, f'Native/Core differ on captured poll inputs at t={event["t"]}')
o.unstub()
o.mu.hook_del(command_hook)
print(json.dumps({'same_input_native_core_matches': len(selected), 'oracle_failures': len(ctx.failures),
                  'limitations': 'Reconstructs recorded previous-poll inputs; it does not identify actual invocation inputs or execute the running game.'}))
