"""Audit saved engine Draw paths and the coverage of a saved binding trace.

This module never imports Game or a process API. It does not install a callback,
replay a command in the game, or propose an alternative to the retired probe.
Engine paths are static evidence; a binding event is not a completed Draw pass.
"""
import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import re
import struct

from image import Image


IMAGE_SHA256 = 'aa85c23361c67cd085f3101d2ecb9bf1579f79d46d081effa0184044f1c48e59'
COMMAND_TABLE = 0x14bd2d0
RECORD_SIZE = 344
MAX_RECORDS = 8192
# These are observed engine call paths, never callback installation locations.
# ID -> (packet decoder, engine implementation, native context vtable offset).
DRAW_PATHS = {
    45: (0x957570, 0x960f90, 0x68),
    46: (0x9575a0, 0x960e50, 0x60),
    47: (0x9575e0, 0x960ee0, 0xa0),
    48: (0x957610, 0x960e90, 0xa0),
    49: (0x957650, 0x960fe0, 0xa8),
    50: (0x957690, 0x961040, 0x140),
    51: (0x9576d0, 0x960e20, 0x130),
    52: (0x9576f0, 0x961090, 0x68),
    55: (0x957790, 0x960560, 0x148),
}
SDK_METHODS = {
    8: 'PSSetShaderResources', 9: 'PSSetShader',
    12: 'DrawIndexed', 13: 'Draw', 17: 'IASetInputLayout', 20: 'DrawIndexedInstanced',
    21: 'DrawInstanced', 38: 'DrawAuto',
    40: 'DrawInstancedIndirect', 41: 'Dispatch',
    24: 'IASetPrimitiveTopology', 35: 'OMSetBlendState',
    36: 'OMSetDepthStencilState', 43: 'RSSetState',
    14: 'Map', 15: 'Unmap',
}
ENGINE_STAGES = ('VS', 'HS', 'DS', 'GS', 'PS', 'CS')
BINDING_TABLES = {'ShaderResources': 0x14bd6d0, 'Samplers': 0x14bd700,
                  'ConstantBuffers': 0x14bd730}


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def sdk_methods(text):
    match = re.search(r'typedef struct ID3D11DeviceContextVtbl\s*\{(.*?)\}\s*ID3D11DeviceContextVtbl;', text, re.S)
    if not match:
        raise ValueError('SDK context vtable declaration is missing')
    methods = re.findall(r'\(\s*STDMETHODCALLTYPE\s*\*(\w+)\s*\)', match[1])
    if len(methods) != 115 or methods[:3] != ['QueryInterface', 'AddRef', 'Release']:
        raise ValueError('SDK context vtable layout is not verified')
    for slot, name in SDK_METHODS.items():
        if methods[slot] != name:
            raise ValueError(f'SDK context slot {slot} differs from {name}')
    return methods


def audit_trace(document, raw):
    """Require exact agreement with raw records; do not promote event coverage."""
    records = document.get('records')
    if not isinstance(records, list) or not 1 <= len(records) <= MAX_RECORDS:
        raise ValueError('Saved trace record count is invalid')
    if len(raw) != len(records) * RECORD_SIZE:
        raise ValueError('Saved binary and JSON record counts differ')
    if document.get('indices') != [8, 9]:
        raise ValueError('This audit requires the historical PS binding observer')
    base = int(document['state']['exe'], 16)
    callers = Counter()
    kinds = Counter()
    sequences = set()
    for index, record in enumerate(records):
        data = raw[index * RECORD_SIZE:(index + 1) * RECORD_SIZE]
        values = struct.unpack_from('<36Q', data)
        arguments = struct.unpack_from('<3Ii2I', data, 288)
        viewport = list(struct.unpack_from('<6f', data, 312))
        complete = struct.unpack_from('<I', data, 336)[0]
        sequence = record['sequence']
        if type(sequence) is not int or sequence != index or sequence in sequences or complete != 1:
            raise ValueError('Saved record sequence/completion is invalid')
        sequences.add(sequence)
        qwords = [record['caller'], record['ps'], record['vs'], *record['targets'], record['depth'],
                  *record['views'], *record['psConstants'], *record['vsConstants']]
        if tuple(int(value, 16) for value in qwords) != values:
            raise ValueError(f'Saved record {index} pointer fields differ from raw data')
        fields = ('kind', 'count', 'start', 'base', 'instances', 'firstInstance')
        if any(type(record[key]) is not int for key in fields):
            raise ValueError('Saved record integer fields are invalid')
        if tuple(record[key] for key in fields) != arguments or record['viewport'] != viewport:
            raise ValueError(f'Saved record {index} draw-shaped fields differ from raw data')
        rva = int(record['callerRva'], 16)
        if int(record['caller'], 16) - base != rva:
            raise ValueError('Saved caller RVA and historical module base disagree')
        if record['kind'] not in (4, 5):
            raise ValueError('A non-binding record cannot be interpreted by this observer contract')
        callers[rva, record['kind']] += 1
        kinds[record['kind']] += 1
    return dict(recordCount=len(records), binaryJsonExact=True,
                saturated=len(records) == MAX_RECORDS,
                eventCounts={'PSSetShaderResources': kinds[4], 'PSSetShader': kinds[5]},
                # These fields are intentionally independent of nonzero event counts.
                drawCallsRecorded=0, frameBoundariesRecorded=0,
                completeFrame=False, finalDrawStateVerified=False, gamePassVerified=False,
                renderThreadRecorded=False, resourceOwnershipVerified=False,
                callers=[dict(returnRva=hex(rva), kind=kind, events=count)
                         for (rva, kind), count in sorted(callers.items())])


def direct_calls(instructions):
    return [int(i.op_str, 16) for i in instructions
            if i.mnemonic == 'call' and i.op_str.startswith('0x')]


def context_site(instructions, offset):
    pattern = re.compile(r'qword ptr \[(rax|r10) \+ ' + hex(offset) + r'\]')
    result = []
    for index, instruction in enumerate(instructions):
        if instruction.mnemonic != 'call' or not (match := pattern.fullmatch(instruction.op_str)):
            continue
        before = instructions[max(0, index - 12):index]
        context = next((i for i in before if i.mnemonic == 'mov' and
                        re.fullmatch(r'rcx, qword ptr \[(rbx|rdi|rsi) \+ 0x10\]', i.op_str)), None)
        vtable = next((i for i in before if i.mnemonic == 'mov' and
                       i.op_str == f'{match[1]}, qword ptr [rcx]'), None)
        if context is None or vtable is None or not context.address < vtable.address < instruction.address:
            raise ValueError('Native context call load sequence was not verified')
        between = [i for i in before if context.address < i.address < instruction.address]
        if any(i.mnemonic.startswith('j') or i.mnemonic == 'call' for i in between):
            raise ValueError('Context load crosses a branch or another call')
        result.append(dict(callRva=hex(instruction.address), returnRva=hex(instruction.address + instruction.size),
                           loadContextRva=hex(context.address), loadVtableRva=hex(vtable.address),
                           instruction=f'{instruction.mnemonic} {instruction.op_str}'))
    if len(result) != 1:
        raise ValueError('Expected exactly one native Draw/Dispatch call')
    return result[0]


def binding_tables(image, methods, flow):
    """Resolve engine stage indices from tiny native tail-call adapters."""
    result = []
    for kind, table in BINDING_TABLES.items():
        for stage, prefix in enumerate(ENGINE_STAGES):
            start = image.u64(table + stage * 8) - image.base
            instructions = flow(start)
            if len(instructions) != 2 or (instructions[0].mnemonic, instructions[0].op_str) != \
                    ('mov', 'rax, qword ptr [rcx]') or instructions[1].mnemonic != 'jmp':
                raise ValueError('Engine binding adapter is not a pure context tail call')
            match = re.fullmatch(r'qword ptr \[rax \+ (0x[0-9a-f]+)\]', instructions[1].op_str)
            if not match or int(match[1], 16) % 8:
                raise ValueError('Engine binding adapter slot is invalid')
            slot = int(match[1], 16) // 8
            if not 0 <= slot < len(methods) or methods[slot] != prefix + 'Set' + kind:
                raise ValueError('Engine stage index and SDK binding method disagree')
            result.append(dict(engineStage=stage, stage=prefix, binding=kind,
                               tableRva=hex(table), adapterRva=hex(start),
                               nativeSlot=slot, nativeMethod=methods[slot],
                               ownedContextAcquired=False))
    return result


def audit(image, document, raw, sdk):
    if sha256(image.img) != IMAGE_SHA256:
        raise ValueError('Saved engine image hash differs from the recorded image')
    methods = sdk_methods(sdk)
    report = audit_trace(document, raw)
    evidence = {}

    def flow(start):
        instructions = image.flow(start)
        if not instructions or any(i.mnemonic == '.byte' for i in instructions):
            raise ValueError(f'Incomplete engine disassembly at {start:#x}')
        pieces = [image.img[i.address:i.address + i.size] for i in instructions]
        evidence[start] = dict(rva=hex(start), instructionCount=len(instructions),
                               reachableCodeSha256=sha256(b''.join(pieces)))
        return instructions

    worker = flow(0x95b0a0)
    if not any(i.mnemonic == 'lea' and i.op_str.startswith('r15, [rip ') and
               i.address + i.size + int(i.op_str.split('rip ')[1][:-1].replace(' ', ''), 0) == COMMAND_TABLE for i in worker):
        raise ValueError('Saved worker command table load was not verified')
    dispatch = [i for i in worker if i.mnemonic == 'call' and i.op_str == 'qword ptr [r15 + r9*8]']
    if len(dispatch) != 1:
        raise ValueError('Saved worker indirect command call was not verified')
    paths = []
    for command_id, (decoder, implementation, offset) in DRAW_PATHS.items():
        if image.u64(COMMAND_TABLE + command_id * 8) - image.base != decoder:
            raise ValueError(f'Engine command {command_id} entry differs')
        if direct_calls(flow(decoder)) != [implementation]:
            raise ValueError(f'Engine command {command_id} decoder call differs')
        instructions = flow(implementation)
        calls = direct_calls(instructions)
        preparation = [i for i in instructions if i.mnemonic == 'call' and i.op_str == '0x963c60']
        expected = (0 if command_id == 55 else 1)
        if len(preparation) != expected:
            raise ValueError('Engine pre-Draw preparation call differs')
        site = context_site(instructions, offset)
        if preparation and preparation[0].address >= int(site['callRva'], 16):
            raise ValueError('Engine preparation is not before the native Draw')
        rejection = None
        if preparation:
            following = [i for i in instructions if i.address > preparation[0].address]
            if command_id == 52:
                if (following[0].mnemonic, following[0].op_str) != ('mov', 'rbx, qword ptr [rsp + 0x70]'):
                    raise ValueError('Inline vertex Draw register restoration differs')
                following = following[1:]
            if len(following) < 2 or (following[0].mnemonic, following[0].op_str) != ('test', 'al, al') or \
                    following[1].mnemonic != 'je' or not following[1].op_str.startswith('0x') or \
                    int(following[1].op_str, 16) <= int(site['callRva'], 16):
                raise ValueError('Failed preparation does not have the expected Draw rejection branch')
            rejection = dict(testRva=hex(following[0].address), branchRva=hex(following[1].address),
                             rejectedDrawTarget=following[1].op_str)
        paths.append(dict(commandId=command_id, decoderRva=hex(decoder), implementationRva=hex(implementation),
                          nativeMethod=methods[offset // 8], nativeSlot=offset // 8, **site,
                          category='Dispatch' if command_id == 55 else 'Draw',
                          preparationRva='0x963c60' if preparation else None,
                          preparationFailureSkipsDraw=rejection,
                          directCallees=[hex(rva) for rva in calls], liveDrawObserved=False,
                          terrainTreeOrUiPass=None, callbackEligible=False))
    preparation = flow(0x963c60)
    # Preserve the engine operations that can happen after the recorded binding event.
    native_preparation = [dict(rva=hex(i.address), instruction=f'{i.mnemonic} {i.op_str}')
                          for i in preparation if i.mnemonic == 'call']
    adapters = binding_tables(image, methods, flow)
    flush = flow(0x9625f0)
    flush_paths = []
    for offset, prepare_rva in ((0x68, 0x96270f), (0x60, 0x962751)):
        site = context_site(flush, offset)
        at = {i.address: i for i in flush}
        prepare = at[prepare_rva]
        test = at[prepare.address + prepare.size]
        branch = at[test.address + test.size]
        if (prepare.mnemonic, prepare.op_str) != ('call', '0x963c60') or \
                (test.mnemonic, test.op_str) != ('test', 'al, al') or branch.mnemonic != 'je' or \
                int(branch.op_str, 16) <= int(site['callRva'], 16):
            raise ValueError('Pending batch flush Draw preparation guard differs')
        flush_paths.append(dict(engineFunctionRva='0x9625f0', nativeMethod=methods[offset // 8],
                               **site, preparationCallRva=hex(prepare_rva),
                               rejectionBranchRva=hex(branch.address),
                               liveDrawObserved=False, gamePassVerified=False, callbackEligible=False))
    shader_set = flow(0x965a80)
    shader_at = {i.address: i for i in shader_set}
    ordering = {0x965aa7: ('call', '0x9625f0'),
                0x965ac2: ('call', 'qword ptr [rax + 0x48]'),
                0x965ac5: ('mov', 'qword ptr [rbx + 0xb8], rdi'),
                0x965b14: ('jmp', '0x95d960')}
    if any((shader_at[rva].mnemonic, shader_at[rva].op_str) != expected for rva, expected in ordering.items()):
        raise ValueError('Pixel shader binding/owner/dirty-mark order differs')
    flow(0x95d960)
    upload = flow(0x9627b0)
    upload_at = {i.address: i for i in upload}
    upload_facts = {
        0x9627bc: ('lea', 'rax, [rcx + 0x2628]'),
        0x9627cd: ('mov', 'r8d, dword ptr [r8 + 0x1c]'),
        0x962810: ('mov', 'rdi, qword ptr [r13 + 0x20]'),
        0x962817: ('mov', 'esi, dword ptr [rdi + 0xc]'),
        0x96284d: ('call', 'qword ptr [rax + 0x70]'),
        0x962850: ('mov', 'r8d, dword ptr [rdi + 8]'),
        0x962859: ('imul', 'rax, r12, 0xe'),
        0x962868: ('mov', 'rdx, qword ptr [rbp + rax*8 + 0x2388]'),
        0x962870: ('call', '0x101ade0'),
        0x962887: ('call', 'qword ptr [rax + 0x78]'),
        0x9628cd: ('call', 'qword ptr [rax + r12*8]'),
        0x9628fd: ('add', 'rbx, 0x10'),
        0x962924: ('mov', 'dword ptr [rax], r15d')}
    if any((upload_at[rva].mnemonic, upload_at[rva].op_str) != expected for rva, expected in upload_facts.items()):
        raise ValueError('Engine constant buffer upload layout differs')
    between_map_copy = [i for i in upload if 0x96284d < i.address < 0x962870]
    if any(i.mnemonic != 'mov' and i.mnemonic not in ('imul', 'add') for i in between_map_copy):
        raise ValueError('Map-to-copy path is no longer the recorded unchecked sequence')
    constant_upload = dict(rva='0x9627b0', countOffset='0x1c', descriptorArrayOffset='0x20',
                           descriptorStride=16, nativeBufferOffset=0, byteLengthOffset=8, nativeSlotOffset=12,
                           dirtyMask='driver + 0x2628 + engineStage*4',
                           shadowSource='[driver + 0x2388 + (engineStage*14 + descriptor.nativeSlot)*8]',
                           mapMethod=methods[14], mapCallRva='0x96284d', copyCallRva='0x962870',
                           unmapMethod=methods[15], unmapCallRva='0x962887',
                           mapHresultCheckedBeforeCopy=False, bufferContentsObserved=False,
                           bufferLifetimeVerified=False, coordinatesVerified=False,
                           instructionFacts=[dict(rva=hex(rva), instruction=' '.join(expected))
                                             for rva, expected in upload_facts.items()])
    for caller in report['callers']:
        rva = int(caller['returnRva'], 16)
        bounds = image.func(rva - 1)
        if bounds is None:
            raise ValueError('Saved binding caller is not in an engine function')
        instructions = flow(bounds[0])
        sites = [i for i in instructions if i.address + i.size == rva and i.mnemonic == 'call']
        if len(sites) != 1:
            raise ValueError('Saved binding return address does not follow an engine call')
        caller.update(engineFunctionRva=hex(bounds[0]), callRva=hex(sites[0].address),
                      instruction=f'{sites[0].mnemonic} {sites[0].op_str}',
                      eventMeaning='PS binding event; resource ownership and final Draw state unresolved')
    return dict(mode='Saved image and historical trace only', imageSha256=IMAGE_SHA256,
                imageBase=hex(image.base), historicalTraceModuleBase=document['state']['exe'],
                sdkHeaderSha256=sha256(sdk.encode('utf-8')), trace=report,
                commandWorker=dict(rva='0x95b0a0', indirectCallRva=hex(dispatch[0].address)),
                enginePathsCoverage='Selected commands plus pending-batch flush; not an exhaustive engine graph',
                enginePaths=paths, drawPreparation=dict(rva='0x963c60', calls=native_preparation),
                bindingAdapters=adapters, pendingBatchFlushDraws=flush_paths,
                shaderSetOrdering=[dict(rva=hex(rva), instruction=' '.join(expected))
                                   for rva, expected in ordering.items()], constantUpload=constant_upload,
                engineCode=list(evidence.values()), gameProcessAccess=False, liveHookInstalled=False,
                retiredCommandProbeRetried=False, gameRenderModified=False,
                callbackBootstrapVerified=False, gameUiOrderVerified=False,
                readyForNextGameTrial=False, productionReady=False)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--trace', required=True, type=Path)
    parser.add_argument('--raw', required=True, type=Path)
    parser.add_argument('--sdk-header', required=True, type=Path)
    parser.add_argument('--out', required=True, type=Path)
    args = parser.parse_args()
    image = Image()
    trace = args.trace.read_bytes()
    raw = args.raw.read_bytes()
    sdk = args.sdk_header.read_bytes()
    report = audit(image, json.loads(trace), raw, sdk.decode('utf-8-sig'))
    report['inputHashes'] = dict(trace=sha256(trace), raw=sha256(raw), sdk=sha256(sdk),
                                 source=sha256(Path(__file__).read_bytes()))
    args.out.mkdir(parents=True, exist_ok=False)
    (args.out / 'summary.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    starts = [int(row['rva'], 16) for row in report['engineCode']]
    (args.out / 'engine-paths.txt').write_text('\n\n'.join(
        f'== {start:#x}\n{image.flowtext(start)}' for start in starts), encoding='utf-8')
    print(json.dumps(dict(enginePaths=len(report['enginePaths']),
                          pendingBatchDraws=len(report['pendingBatchFlushDraws']),
                          bindingAdapters=len(report['bindingAdapters']), trace=report['trace']['eventCounts'],
                          drawRecords=report['trace']['drawCallsRecorded'],
                          callbackBootstrapVerified=False, readyForNextGameTrial=False)))


if __name__ == '__main__':
    main()
