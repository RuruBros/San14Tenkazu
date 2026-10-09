# Publish the retained native allocation -> queue registration -> heap slot consumption harness.
import copy


def publish(ctx, snapshot, probe, report, batch=None, stem='free_heap_consumer'):
    if not report['complete'] or not probe['complete'] or report['input_sha256'] != probe['input_sha256']:
        raise RuntimeError('A complete independently guarded heap-queue input is required')
    if probe['gameplay_stubs'] or probe['allocator_stubs'] or probe['entry'] != '0x33bc00':
        raise RuntimeError('The complete native consumer must execute without gameplay/allocator stubs')
    if probe['free_officers'] or len(probe['slot_inputs']) != 1 or probe['frees'] != [probe['name']['allocation']]:
        raise RuntimeError('This proof requires one registered heap slot and one actual name allocation release')
    snapshot = copy.deepcopy(snapshot)
    for key, table in snapshot['tables'].items():
        table['address'] = hex(ctx.addr(int(key, 16), 0))
    fixture = dict(person=probe['person'], count=int.from_bytes(bytes.fromhex(probe['free_manager'])[8:12], 'little', signed=True),
                   pokes=[['0x73a50', probe['person'], 0, probe['input_record']]])
    cases = [dict(fixture=fixture, buffer=probe['name'], result=probe['result'], after=probe['after'],
                  slot_input=probe['slot_inputs'][0], frees=probe['frees'], free_manager=probe['free_manager'])]
    if batch is not None:
        if batch['input_sha256'] != report['input_sha256']:
            raise RuntimeError('The multi-name cases must use the same independently guarded input')
        for case in batch['complete_cases']:
            if len(case['buffers']) != len(case['slot_inputs']) or case['frees'] != [b['allocation'] for b in case['buffers']]:
                raise RuntimeError('Every queued heap name requires its actual reset and allocation release')
            cases.append(copy.deepcopy(case))
    ctx.save(stem+'_world.json.gz', snapshot)
    ctx.save(stem+'.json.gz', dict(world=stem+'_world.json.gz', complete=True,
        input_sha256=report['input_sha256'], gameplay_stubs=False, allocator_stubs=False,
        cases=cases))
    return len(cases)
