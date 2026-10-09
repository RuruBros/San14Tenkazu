# Watches demo play (read-only) and records polling windows around the game's resolution list:
#   python observe.py [seconds]      → .cache/observe/<stamp>/world.json.gz (the baseline world)
#                                      and events.jsonl, one line per observed change:
#   {"t": s, "list": [[attacker unit, target base id, target unit], ...], "unit": {index: before hex, ...},
#    "base": {id: before hex, ...}, "after_unit": {index: hex}, "after_base": {id: hex}, "building": {id: before},
#    "after_building": {id: hex}}
# "Before" bytes are the previous poll's. Format 4 retains raw combat records, cached troop words and explicit
# record-list completeness. It also includes format 3's tactic commands and result rows as well as format 2's
# dynamic input-table deltas from the baseline, combat lists, split counts and the current seed/settings. Polling cannot prove which records
# ran within a window; ObservationReport retains the differences and reports unattributed changes explicitly.
import gzip, json, os, struct, sys, time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import world
from game import Game

UNITS, CITIES, GATES, BUILDINGS = 0x7E838, 0xDD38, 0xDED8, 0x6DA98
MANAGER_RVA = 0x1BB3610  # 0x16D8E0 returns this address; 0x169C50 follows manager +0xA0, then node +0x30.
TACTIC_MANAGER_RVA = 0x1BB38A0  # 0x16DA30; command queue +0x18, sources +0x98, result rows +0xD8.
DYNAMIC_TABLES = (UNITS, CITIES, GATES, BUILDINGS, 0x73A50, 0xDF30, 0xE0D0, 0xE270, 0x6CAF0)


class Direct:
    # what world.snapshot needs, straight from the process (no copies; the game must hold still for a snapshot)
    def __init__(self, g):
        self.g = g
        self.read = g.read
        self.q = g.q

    def prefetch(self, addr, size):
        pass


def read_resolution(g, manager):
    state = {'complete': False, 'records': []}
    try:
        head_data = g.read(manager + 0xA0, 8)
        if head_data is None: raise RuntimeError('Unreadable resolution-list head')
        node = struct.unpack('<Q', head_data)[0]
        state['head'] = hex(node)
        seen, originals = set(), []
        def side(data):
            return [struct.unpack_from('<H', data)[0], struct.unpack_from('<I', data, 4)[0],
                    struct.unpack_from('<I', data, 0xC)[0], struct.unpack_from('<I', data, 0x10)[0], data[0x14]]
        while node:
            if node in seen or len(seen) >= 300: raise RuntimeError('Cyclic or oversized resolution list')
            seen.add(node)
            data = g.read(node, 0x38)
            if data is None: raise RuntimeError('Unreadable resolution record')
            source = struct.unpack_from('<Q', data)[0]
            source_data = g.read(source, 0x18) if source else None
            if source and source_data is None: raise RuntimeError('Unreadable resolution source')
            target = data[8:32]
            state['records'].append({'source': side(source_data) if source_data else None, 'target': side(target),
                                     'source_troops': struct.unpack_from('<H', source_data, 0xA)[0] if source_data else None,
                                     'target_troops': struct.unpack_from('<H', target, 0xA)[0], 'node': hex(node),
                                     'source_raw': source_data.hex() if source_data else None, 'record_raw': data.hex()})
            originals.append((node, data, source, source_data))
            node = struct.unpack_from('<Q', data, 0x30)[0]
        if g.read(manager + 0xA0, 8) != head_data: raise RuntimeError('Resolution-list head changed during reading')
        for address, data, source, source_data in originals:
            if g.read(address, 0x38) != data or source and g.read(source, 0x18) != source_data:
                raise RuntimeError('Resolution record/source changed during reading')
        state['complete'] = True
    except (RuntimeError, struct.error, TypeError) as error:
        state['error'] = str(error)
    return state


def resolution_pairs(state):
    # Retain the earlier five-field API while raw records also preserve null source pointers.
    return [[record['source'], record['target']] for record in state['records'] if record['source'] is not None]


def main():
    seconds = float(sys.argv[1]) if len(sys.argv) > 1 else 600
    g = Game()
    raw = g.base - 0x290
    out = os.path.join(HERE, ".cache", "observe", time.strftime("%Y%m%d-%H%M%S"))
    os.makedirs(out, exist_ok=True)
    units0, cities0, gates0, buildings0 = g.q(raw + UNITS), g.q(raw + CITIES), g.q(raw + GATES), g.q(raw + BUILDINGS)
    spec = world.LISTS[0x140]
    table_starts = {key: g.q(raw + key) for key in DYNAMIC_TABLES}
    tactics0 = g.q(raw + 0x774D0)
    ws = g.q(raw + world.WORLDSTATE_RAW)

    def pending_targets():
        # Keep the historical three-field JSON key, with the native target unit/base and attacking unit roles.
        head = g.q(raw + 0x140)
        if not head: return []
        pool = g.q(g.exe + spec["pool"])
        count = struct.unpack("<I", g.read(g.exe + spec["pool"] + 0x30, 4))[0]
        idx = struct.unpack("<I", g.read(head, 4))[0]
        if idx >= count: return []
        node = g.q(pool + idx * 8)
        out, seen = [], set()
        while node and node not in seen and len(out) < 300:
            seen.add(node)
            a, tb, tu = g.q(node), g.q(node + 8), g.q(node + 0x10)
            out.append([(a - units0) // 0x200 if a else 0, base_id(tb), (tu - units0) // 0x200 if tu else 0])
            node = g.q(node + 8 * spec["next"])
        return out

    def base_id(p):
        if not p: return 0
        if cities0 <= p < cities0 + 52 * 0x168: return (p - cities0) // 0x168
        if gates0 <= p < gates0 + 11 * 0xC0: return 0x33 + (p - gates0) // 0xC0
        return 0

    def tactic_state():
        # Read without pausing or touching the game. Reject a changed queue header or malformed list explicitly.
        manager = g.exe + TACTIC_MANAGER_RVA
        header = g.read(manager + 8, 0x38)
        state = {"complete": False, "queue": [], "rows": []}
        try:
            if header is None: raise RuntimeError('Unreadable tactic queue header')
            head = struct.unpack_from('<Q', header, 0x10)[0]
            count = struct.unpack_from('<Q', header, 0x30)[0]
            if count > 11: raise RuntimeError(f'Tactic queue count {count} exceeds eleven commands')
            pool = g.q(g.exe + world.POOL_RVA)
            pool_count = struct.unpack('<I', g.read(g.exe + world.POOL_RVA + 0x30, 4))[0]

            def object_ref(pointer):
                if pointer == 0: return None
                for table in (UNITS, CITIES, GATES):
                    size, count = world.TABLES[table][1:]
                    index, remainder = divmod(pointer - table_starts[table], size)
                    if remainder == 0 and 0 <= index < count: return [hex(table), index]
                raise RuntimeError('Unknown tactic context pointer')

            context = g.read(manager + 0x98, 32)
            if context is None: raise RuntimeError('Unreadable tactic source/target context')
            state['context'] = [object_ref(p) for p in struct.unpack('<4Q', context)]

            def tactic_id(pointer):
                index, remainder = divmod(pointer - tactics0, 0x90)
                if pointer == 0: return None
                if remainder or not 0 <= index <= 200: raise RuntimeError('Unknown tactic pointer')
                return index

            def targets(wrapper):
                cell = g.q(wrapper + 8)
                index = struct.unpack('<I', g.read(cell, 4))[0] if cell else 0x14000
                if index >= pool_count: raise RuntimeError('Target list pool index is out of range')
                node, result, seen = g.q(pool + index * 8), [], set()
                while node:
                    if node in seen or len(result) >= 48400: raise RuntimeError('Cyclic or oversized target list')
                    seen.add(node)
                    data = g.read(node, 16)
                    if data is None: raise RuntimeError('Unreadable target node')
                    pointer, node = struct.unpack('<QQ', data)
                    h, remainder = divmod(pointer - table_starts[0xE270], 32)
                    if remainder or not 0 <= h < 48400: raise RuntimeError('Unknown target tile pointer')
                    result.append(h)
                return result

            seen, node = set(), head
            while node:
                if node in seen or len(state['queue']) >= 11: raise RuntimeError('Cyclic or oversized tactic queue')
                seen.add(node)
                data = g.read(node, 64)
                if data is None: raise RuntimeError('Unreadable tactic command')
                kind, number, tactic = struct.unpack_from('<IH2xQ', data)
                state['queue'].append([kind, number, tactic_id(tactic), [targets(node + 16 + 16 * slot) for slot in range(2)]])
                node = struct.unpack_from('<Q', data, 0x30)[0]
            if len(state['queue']) != count: raise RuntimeError('Tactic queue count changed during reading')
            for i in range(11):
                row = g.read(manager + 0xD8 + i * 64, 64)
                if row is None: raise RuntimeError('Unreadable tactic result row')
                tactic, kind, number = struct.unpack_from('<QII', row)
                if kind == 255: continue
                slots = []
                for slot in range(2):
                    begin, end, cap = struct.unpack_from('<QQQ', row, 16 + slot * 24)
                    if not begin <= end <= cap or (end - begin) % 16 or end - begin > 48400 * 16:
                        raise RuntimeError('Malformed tactic result vector')
                    data = g.read(begin, end - begin) if end > begin else b''
                    if data is None: raise RuntimeError('Unreadable tactic result vector')
                    output = []
                    for at in range(0, len(data), 16):
                        k, number_, amount, unused, h = struct.unpack_from('<IIHHI', data, at)
                        output.append([k, number_, h, amount])
                    slots.append(output)
                state['rows'].append([i, tactic_id(tactic), kind, number, slots])
            if g.read(manager + 8, 0x38) != header: raise RuntimeError('Tactic queue header changed during reading')
            state['complete'] = True
        except (RuntimeError, struct.error, TypeError) as error:
            state['error'] = str(error)
        return state

    def read_all():
        tables = {key: g.read(table_starts[key], world.TABLES[key][1] * world.TABLES[key][2]) for key in DYNAMIC_TABLES}
        split_counts, seen = [], set()
        node = g.q(g.exe + MANAGER_RVA + 0x10)
        while node and node not in seen and len(split_counts) < 1000:
            seen.add(node)
            data = g.read(node, 0x28)
            if data is None: raise RuntimeError('Cannot read the manager split-count list.')
            split_counts.append([struct.unpack_from('<H', data)[0], struct.unpack_from('<I', data, 4)[0], data[0x14]])
            node = struct.unpack_from('<Q', data, 0x20)[0]
        if node: raise RuntimeError('The manager split-count list is cyclic or too long.')
        direct = Direct(g)
        input_lists = {f'{key:#x}': world.list_items(direct, raw, g.exe, key, world.LISTS[key]) for key in (0x70, 0x140, 0xF0)}
        flags = {}
        for name, (rva, how) in world.EXE_VALUES.items():
            address = world.exe_value_address(direct, g.exe, rva, how)
            flags[name] = (int(g.q(address) != 0) if how == 'set' else
                           g.read(address, 1)[0] if how.startswith('ptrbyte') else struct.unpack('<i', g.read(address, 4))[0]) if address else 0
        return {"units": tables[UNITS], "cities": tables[CITIES], "gates": tables[GATES], "buildings": tables[BUILDINGS],
                "tables": tables, "worldstate": g.read(ws, world.WORLDSTATE_SIZE),
                "settings": g.read(g.exe + world.SETTINGS_RVA, world.SETTINGS_SIZE),
                "split_counts": split_counts, "input_lists": input_lists, "flags": flags, "tactics": tactic_state()}

    def unit(b, i): return b["units"][i * 0x200:(i + 1) * 0x200]
    def base(b, i): return b["cities"][i * 0x168:(i + 1) * 0x168] if i < 0x34 else b["gates"][(i - 0x33) * 0xC0:(i - 0x32) * 0xC0]
    def building(b, i): return b["buildings"][i * 0x20:(i + 1) * 0x20]

    # Save a baseline before polling. Every event overlays all dynamic records that differ from this baseline,
    # so later replay does not use the first battle's officers, seed, tiles or nearby allies for the whole run.
    snap = world.snapshot(Direct(g))
    world.save(snap, os.path.join(out, "world.json.gz"))
    prev, prev_list, prev_combat = read_all(), pending_targets(), read_resolution(g, g.exe + MANAGER_RVA)
    prev_records = resolution_pairs(prev_combat)
    with open(os.path.join(out, 'initial.json'), 'w', encoding='utf-8') as file:
        json.dump({'format': 4, 'combat': prev_combat, 'tactics': prev['tactics'], 'split_counts': prev['split_counts']}, file)
    t0 = time.time()
    events = open(os.path.join(out, "events.jsonl"), "w", encoding="utf-8")
    n, polls, incomplete_tactics = 0, 0, 0
    incomplete_combat = 0
    max_records = len(prev_combat['records'])
    max_commands = len(prev['tactics']['queue'])
    print(f"observing into {out}", flush=True)
    while time.time() - t0 < seconds:
        time.sleep(0.05)
        cur, lst, combat = read_all(), pending_targets(), read_resolution(g, g.exe + MANAGER_RVA)
        records = resolution_pairs(combat)
        polls += 1
        incomplete_tactics += not cur['tactics']['complete']
        incomplete_combat += not combat['complete']
        max_records = max(max_records, len(combat['records']))
        max_commands = max(max_commands, len(cur['tactics']['queue']))
        touched_units = {i for e in prev_list + lst for i in (e[0], e[2]) if 1 <= i <= 500}
        touched_bases = {e[1] for e in prev_list + lst if 1 <= e[1] <= 0x3D}
        record_units, record_bases = set(), set()
        for pair in prev_records + records:
            for side in pair:
                i, kind = side[:2]
                if kind == 0x1B and 1 <= i <= 500: record_units.add(i)
                if kind == 5 and 1 <= i <= 0x33: record_bases.add(i)
                if kind == 6 and 1 <= i <= 10: record_bases.add(i + 0x33)
        touched_units |= record_units
        touched_bases |= record_bases
        changed_u = {i for i in range(1, 501) if unit(prev, i)[0x16:0x1B] != unit(cur, i)[0x16:0x1B]}
        changed_b = {i for i in range(1, 0x3E) if base(prev, i)[0x3C:0x45] != base(cur, i)[0x3C:0x45]}
        changed_o = {i for i in range(1, 3001) if building(prev, i)[0x14:0x16] != building(cur, i)[0x14:0x16]}
        touched_units |= changed_u
        touched_bases |= changed_b
        tactic_changed = prev['tactics'] != cur['tactics']
        if ((prev_records or records or prev_list or lst) and (changed_u or changed_b or changed_o)) or tactic_changed or not combat['complete'] or not prev_combat['complete']:
            inputs = {}
            for key, data in prev["tables"].items():
                size, count = world.TABLES[key][1:]
                baseline = snap["tables"][f"{key:#x}"]["records"]
                delta = {i: data[i * size:(i + 1) * size].hex() for i in range(count)
                         if data[i * size:(i + 1) * size].hex() != baseline[i]}
                inputs[f"{key:#x}"] = delta
            ev = {"format": 4, "t": round(time.time() - t0, 2), "list": prev_list,
                  "records": prev_records or records, "records_before": prev_records, "records_after": records,
                  "combat_before": prev_combat, "combat_after": combat,
                  "records_changed": prev_records != records, "input_tables": inputs,
                  "split_counts": prev["split_counts"], "input_lists": prev["input_lists"],
                  "worldstate": prev["worldstate"].hex(), "settings": prev["settings"].hex(),
                  "flags": prev['flags'], "tactics_before": prev['tactics'], "tactics_after": cur['tactics'],
                  "unit": {i: unit(prev, i).hex() for i in touched_units}, "after_unit": {i: unit(cur, i).hex() for i in changed_u},
                  "base": {i: base(prev, i).hex() for i in touched_bases}, "after_base": {i: base(cur, i).hex() for i in changed_b},
                  "building": {i: building(prev, i).hex() for i in changed_o}, "after_building": {i: building(cur, i).hex() for i in changed_o}}
            events.write(json.dumps(ev) + "\n"); events.flush()
            n += 1
            print(f"  event {n} at {ev['t']} s: {len(ev['records'])} records, {len(changed_u)} units, {len(changed_b)} bases, {len(changed_o)} buildings", flush=True)
        prev, prev_list, prev_records, prev_combat = cur, lst, records, combat
    events.close()
    summary = {"format": 4, "seconds": round(time.time() - t0, 2), "polls": polls, "events": n,
               "incomplete_tactic_polls": incomplete_tactics, "max_tactic_commands": max_commands,
               "incomplete_combat_polls": incomplete_combat, "max_combat_records": max_records}
    with open(os.path.join(out, 'summary.json'), 'w', encoding='utf-8') as file: json.dump(summary, file, indent=2)
    print(json.dumps(summary), flush=True)
    print(f"{n} events")


if __name__ == "__main__":
    main()
