"""Verify saved Pixel Shader creation inputs in isolated emulated memory.

No game process, GPU, COM implementation, callback, or retired probe is used.
COM calls are test doubles. Input agreement proves a saved code path, not the
identity or lifetime of a shader captured during an actual game Draw.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import struct
import uuid

from image import Image
from season_render_path import IMAGE_SHA256
from unicorn import Uc, UC_ARCH_X86, UC_MODE_64, UC_HOOK_CODE
from unicorn.x86_const import (UC_X86_REG_RAX, UC_X86_REG_RCX, UC_X86_REG_RDX,
                              UC_X86_REG_R8, UC_X86_REG_R9, UC_X86_REG_RSP,
                              UC_X86_REG_RIP)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def imports(image):
    """Use PE import names, never the already-resolved saved IAT pointer."""
    pe = image.u32(0x3c)
    start, size = struct.unpack_from('<II', image.img, pe + 24 + 112 + 8)
    result = {}
    for offset in range(start, start + size, 20):
        original, _, _, name, iat = struct.unpack_from('<5I', image.img, offset)
        if not any(image.img[offset:offset + 20]):
            return result
        if not original:
            raise ValueError('Import original thunk is missing')
        library = image.img[name:name + 256].split(b'\0')[0].decode('ascii')
        index = 0
        while value := image.u64(original + index * 8):
            if not value & (1 << 63):
                symbol = image.img[value + 2:value + 258].split(b'\0')[0].decode('ascii')
                result[iat + index * 8] = (library, symbol)
            index += 1
            if index > 4096:
                raise ValueError('Import thunk count exceeds the audit limit')
    raise ValueError('Import directory terminator is missing')


def device_methods(sdk):
    match = re.search(r'typedef struct ID3D11DeviceVtbl\s*\{(.*?)\}\s*ID3D11DeviceVtbl;', sdk, re.S)
    if not match:
        raise ValueError('SDK device declaration is missing')
    methods = re.findall(r'\(\s*STDMETHODCALLTYPE\s*\*(\w+)\s*\)', match[1])
    if len(methods) != 43 or methods[:3] != ['QueryInterface', 'AddRef', 'Release'] or \
            methods[15] != 'CreatePixelShader':
        raise ValueError('SDK device creation slot is not verified')
    return methods


def emulate(image, mode, byte_length=137):
    """Execute original saved instructions; external calls remain synthetic."""
    if mode not in ('creation-failure', 'reflection-failure', 'reflection-entry'):
        raise ValueError('Unknown isolated scenario')
    if not 1 <= byte_length <= 4096:
        raise ValueError('Isolated input length is invalid')
    uc = Uc(UC_ARCH_X86, UC_MODE_64)
    # Only the bounded creation/failure paths, IID and cookie data are mapped.
    # A path reaching any other game implementation fails instead of running it.
    for page in (0x948000, 0x94c000, 0xff9000, 0xffb000, 0x1730000, 0x1a8b000):
        uc.mem_map(image.base + page, 4096)
        uc.mem_write(image.base + page, image.img[page:page + 4096])
    scratch = 0x30000000
    uc.mem_map(scratch, 0x20000)
    driver, device, table, shader, shader_table = [scratch + n for n in (0, 0x4000, 0x5000, 0x6000, 0x7000)]
    bytecode, stack = scratch + 0x8000, scratch + 0x1f008
    create, release, returned = [scratch + n for n in (0x9000, 0x9010, 0x9020)]
    uc.mem_write(create, b'\xc3')
    uc.mem_write(release, b'\xc3')
    uc.mem_write(returned, b'\xc3')

    def put(address, value):
        uc.mem_write(address, struct.pack('<Q', value))

    def qword(address):
        return struct.unpack('<Q', uc.mem_read(address, 8))[0]

    put(driver + 0x3078, device)
    put(device, table)
    put(table + 15 * 8, create)
    put(shader, shader_table)
    put(shader_table + 2 * 8, release)
    put(stack, returned)
    payload = bytes((index * 37 + 11) & 255 for index in range(byte_length))
    uc.mem_write(bytecode, payload)
    for register, value in ((UC_X86_REG_RCX, driver), (UC_X86_REG_RDX, bytecode),
                            (UC_X86_REG_R8, byte_length), (UC_X86_REG_RSP, stack)):
        uc.reg_write(register, value)
    calls = []
    finished = []

    def ret(result):
        sp = uc.reg_read(UC_X86_REG_RSP)
        uc.reg_write(UC_X86_REG_RAX, result & 0xffffffff)
        uc.reg_write(UC_X86_REG_RSP, sp + 8)
        uc.reg_write(UC_X86_REG_RIP, qword(sp))

    def on_code(_, address, size, user):
        if address == create:
            calls.append(dict(method='CreatePixelShader', device=hex(uc.reg_read(UC_X86_REG_RCX)),
                              bytecode=hex(uc.reg_read(UC_X86_REG_RDX)),
                              byteLength=uc.reg_read(UC_X86_REG_R8),
                              classLinkage=uc.reg_read(UC_X86_REG_R9)))
            if mode == 'creation-failure':
                ret(0x80004005)
            else:
                put(qword(uc.reg_read(UC_X86_REG_RSP) + 0x28), shader)
                ret(0)
        elif address == image.base + 0xffba78:
            calls.append(dict(method='D3DReflect', bytecode=hex(uc.reg_read(UC_X86_REG_RCX)),
                              byteLength=uc.reg_read(UC_X86_REG_RDX),
                              iid=bytes(uc.mem_read(uc.reg_read(UC_X86_REG_R8), 16)).hex()))
            if mode == 'reflection-entry':
                finished.append('Stopped before synthetic reflection execution')
                uc.emu_stop()
            else:
                ret(0x80004005)
        elif address == release:
            calls.append(dict(method='Release', object=hex(uc.reg_read(UC_X86_REG_RCX))))
            ret(0)
        elif address == image.base + 0xff9d80:
            calls.append(dict(method='SyntheticSecurityCookieCheck'))
            ret(0)
        elif address == returned:
            finished.append('Returned')
            uc.emu_stop()

    uc.hook_add(UC_HOOK_CODE, on_code)
    uc.emu_start(image.base + 0x94cb20, returned + 1, count=10000)
    if len(finished) != 1:
        raise ValueError('Isolated engine path did not finish within the instruction limit')
    creation = [call for call in calls if call['method'] == 'CreatePixelShader']
    reflection = [call for call in calls if call['method'] == 'D3DReflect']
    releases = [call for call in calls if call['method'] == 'Release']
    if len(creation) != 1 or creation[0]['device'] != hex(device) or \
            creation[0]['bytecode'] != hex(bytecode) or creation[0]['byteLength'] != byte_length or \
            creation[0]['classLinkage'] != 0:
        raise ValueError('Native creation input differs from the isolated caller input')
    if mode == 'creation-failure':
        if reflection or releases or uc.reg_read(UC_X86_REG_RAX) != 0:
            raise ValueError('Creation failure did not return null without reflection')
    else:
        if len(reflection) != 1 or reflection[0]['bytecode'] != hex(bytecode) or \
                reflection[0]['byteLength'] != byte_length:
            raise ValueError('Creation and reflection inputs differ')
        if reflection[0]['iid'] != image.img[0x17306b0:0x17306c0].hex():
            raise ValueError('Reflection IID differs from the saved image')
        if mode == 'reflection-failure' and (len(releases) != 1 or
                releases[0]['object'] != hex(shader) or uc.reg_read(UC_X86_REG_RAX) != 0):
            raise ValueError('Reflection failure did not release the created shader and return null')
        if mode == 'reflection-entry' and releases:
            raise ValueError('Shader was released before reflection entry')
    if bytes(uc.mem_read(bytecode, byte_length)) != payload:
        raise ValueError('Creation path changed isolated bytecode input')
    return dict(scenario=mode, byteLength=byte_length, payloadSha256=digest(payload),
                calls=calls, finished=finished[0], externalCallsSynthetic=True,
                gameBytecodeIdentityVerified=False, liveGpuOwnershipVerified=False)


def audit(image, sdk):
    if digest(image.img) != IMAGE_SHA256:
        raise ValueError('Saved image hash differs')
    if image.base != image.u64(image.u32(0x3c) + 24 + 24):
        raise ValueError('Saved image metadata base differs from its PE header')
    if [image.u64(0x14beb90 + index * 8) - image.base for index in range(4)] != \
            [0x969a30, 0xdced90, 0x937090, 0x969ab0]:
        raise ValueError('Saved Pixel Shader owner vtable differs')
    methods = device_methods(sdk)
    if imports(image).get(0x1340078) != ('D3DCOMPILER_43.dll', 'D3DReflect'):
        raise ValueError('Reflection import differs')
    facts = {
        0x94cb52: ('call', 'qword ptr [rax + 0x78]'),
        0x94cb55: ('test', 'eax, eax'), 0x94cb57: ('jns', '0x94cb86'),
        0x94cba0: ('mov', 'dword ptr [rsp + 0x20], 4'),
        0x94cba8: ('call', '0x948400'),
        0x94846a: ('call', '0xffba78'),
        0x948560: ('mov', 'qword ptr [rdi + 0x28], r12'),
        0x948569: ('mov', 'qword ptr [rdi + 0x10], rcx'),
        0x948579: ('mov', 'dword ptr [rdi + 0x18], ecx'),
        0x969a55: ('call', 'qword ptr [rax + 0x10]'),
        0x96969a: ('call', 'qword ptr [rax + 0x10]'),
        0x969aba: ('jmp', '0x952b60'),
        0x952b87: ('call', 'qword ptr [rax]'),
        0x952ba0: ('jmp', 'qword ptr [rax + 0x58]')}
    starts = (0x94cb20, 0x948400, 0x969a00, 0x969a30, 0x969650, 0x969670, 0x969ab0, 0x952b60)
    flows = {start: image.flow(start) for start in starts}
    at = {instruction.address: instruction for instructions in flows.values() for instruction in instructions}
    if any(rva not in at or (at[rva].mnemonic, at[rva].op_str) != expected for rva, expected in facts.items()):
        raise ValueError('Saved Pixel Shader owner path differs')
    cases = [emulate(image, mode, length) for mode in
             ('creation-failure', 'reflection-failure', 'reflection-entry') for length in (1, 137, 4096)]
    return dict(mode='Saved engine instructions in isolated Unicorn memory; synthetic external calls',
                imageSha256=IMAGE_SHA256, imageBase=hex(image.base),
                nativeCreationMethod=methods[15], nativeCreationSlot=15,
                engineStage=4, reflectionImport='D3DCOMPILER_43.dll!D3DReflect',
                reflectionIid=str(uuid.UUID(bytes_le=image.img[0x17306b0:0x17306c0])),
                gpuCreationAndReflectionShareInputs=True,
                ownerFields={'reflection': '0x10', 'byteLength': '0x18', 'gpuShader': '0x28'},
                byteLengthIsNotBytecodeHash=True, ownerNativeShaderReleaseRva='0x969a55',
                ownerReflectionReleaseRva='0x96969a',
                ownerAllocationAndConstantCreationEmulated=False, cases=cases,
                engineCode=[dict(rva=hex(start), reachableCodeSha256=digest(b''.join(
                    image.img[i.address:i.address + i.size] for i in instructions)))
                    for start, instructions in flows.items()],
                gameProcessAccess=False, gameRenderModified=False, liveHookInstalled=False,
                retiredCommandProbeRetried=False, gameBytecodeIdentityVerified=False,
                actualDrawShaderVerified=False, callbackBootstrapVerified=False,
                readyForNextGameTrial=False, productionReady=False)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--sdk-header', required=True, type=Path)
    parser.add_argument('--out', required=True, type=Path)
    args = parser.parse_args()
    image, sdk = Image(), args.sdk_header.read_bytes()
    report = audit(image, sdk.decode('utf-8-sig'))
    report['inputHashes'] = dict(sdk=digest(sdk), source=digest(Path(__file__).read_bytes()))
    args.out.mkdir(parents=True, exist_ok=False)
    (args.out / 'summary.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    (args.out / 'engine-paths.txt').write_text('\n\n'.join(image.flowtext(int(row['rva'], 16))
        for row in report['engineCode']), encoding='utf-8')
    print(json.dumps(dict(isolatedCases=len(report['cases']), sameBytecodeInputs=True,
                          gameBytecodeIdentityVerified=False, readyForNextGameTrial=False)))


if __name__ == '__main__':
    main()
