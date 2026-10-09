# Package retained complete native execution; never replay a case while publishing it.
import copy


def publish(ctx, proof, stem='force_fall_body', allow_fixture=False):
    if proof.get('complete') is not True or not proof.get('input_sha256'):
        raise RuntimeError('A complete independently guarded native trace is required')
    snapshot = copy.deepcopy(proof['world'])
    case = copy.deepcopy(proof['case'])
    if len(snapshot['tables']) != 32 or case['result'] != 0 or case['fixture']['pokes'] and not allow_fixture:
        raise RuntimeError('This proof must preserve the complete 32-table NPC scene and explicitly declare copied fixtures')
    if any('descriptors' not in command for command in case['created']):
        raise RuntimeError('Allocated return commands require their native destination descriptors')
    if not case.get('county_vectors'):
        raise RuntimeError('Officer county vectors require their native before/after contents')
    for command in snapshot['commands']:
        pointer = int(command['address'], 16)
        if ctx.o.read(pointer, len(bytes.fromhex(command['data']))).hex() != command['data']:
            raise RuntimeError('Command catalog belongs to a different frozen input')
        vtable = ctx.o.q(pointer)
        command['handlers'] = {hex(at): hex(ctx.o.q(vtable + at) - ctx.g.exe)
                               for at in (0x68, 0x90, 0xA8, 0xB0, 0xC8, 0xD8, 0xF0, 0x110, 0x118)}
    snapshot['fall_sound_suppressed'] = not any(e['kind'] == 'sound' and e['value'] == 8 for e in case['events'])
    snapshot['strategy_refresh_available'] = any(e['kind'] == 'strategy_refresh' for e in case['callbacks'])
    ctx.save(stem+'_world.json.gz', snapshot)
    ctx.save(stem+'.json.gz', dict(world=stem+'_world.json.gz', cases=[case],
        input_sha256=proof['input_sha256'], complete=True, gameplay_stubs=False, allocator_stubs=False,
        scope='Complete nonterminal NPC root execution; physical presentation/playback heap is an explicit host boundary',
        physical_fields=[[0, 8], [0x18, 16], [0x30, 16]],
        # The 8KB read window also covers the independently modelled native destination-list pool header.
        dirty_storage_aliases=[dict(rva=0x2143270, offset=0x2143270-0x2141AE0, length=18*8,
                                   kind='native_destination_list_pool')]))
    return 1
