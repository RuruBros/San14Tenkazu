"""Run unmodified native formation getters in Unicorn; synthetic data, no live game writes.
The checked-in output is an oracle for managed encoders, clamps and the plain fallback.
"""
import gzip, hashlib, json, os, struct
from image import Image
from unicorn import Uc, UC_ARCH_X86, UC_MODE_64
from unicorn.x86_const import UC_X86_REG_RCX, UC_X86_REG_RSP, UC_X86_REG_RAX, UC_X86_REG_RIP

im = Image()
u = Uc(UC_ARCH_X86, UC_MODE_64)
u.mem_map(im.base, (len(im.img) + 4095) & ~4095)
u.mem_write(im.base, im.img)
root, array, vectors, stack, stop = 0x300000000, 0x301000000, 0x302000000, 0x303000000, 0x304000000
for at, size in ((root, 0x90000), (array, 0x2000), (vectors, 0x2000), (stack, 0x10000), (stop, 0x1000)):
    u.mem_map(at, size)
u.mem_write(im.base + 0x277DF18, struct.pack('<Q', root))
u.mem_write(root + 0x77428, b''.join(struct.pack('<Q', array + i * 0xC8) for i in range(21)))
# Real CFormationData vtable from the image, including its DecodeByte implementation.
vtable = im.base + 0x13A6E20
fields = [('mobility', 0x2B0210, 0x7F, 0, 5), ('attack', 0x2B2720, 0x83, 12, 1),
          ('siege', 0x2B2690, 0x84, 16, 3), ('defense', 0x2A9DC0, 0x86, 24, 7),
          ('break', 0x2B27B0, 0x85, 20, 5)]
cases = []
for id in range(1, 21):
    address, vec = array + id * 0xC8, vectors + id * 0x40
    record = bytearray(0xC8)
    struct.pack_into('<Q', record, 0, vtable)
    for mode in ('encoded', 'plain'):
        struct.pack_into('<QQQ', record, 0xA8, vec, vec + (32 if mode == 'encoded' else 0), vec + 32)
        for name, rva, plain, offset, rotate in fields:
            for raw in range(256):
                record[plain] = raw
                value = (raw ^ id) & 255
                encoded = ((value >> rotate) | (value << (8 - rotate))) & 255
                vector = bytearray(bytes.fromhex('00a5c37e') * 8)
                vector[offset] = encoded
                u.mem_write(address, bytes(record))
                u.mem_write(vec, bytes(vector))
                sp = stack + 0xF008
                u.mem_write(sp, struct.pack('<Q', stop))
                u.reg_write(UC_X86_REG_RSP, sp)
                u.reg_write(UC_X86_REG_RCX, address)
                u.emu_start(im.base + rva, stop, count=10000)
                if u.reg_read(UC_X86_REG_RIP) != stop:
                    raise RuntimeError('Native getter did not return')
                cases.append([id, name, mode, raw, encoded, u.reg_read(UC_X86_REG_RAX) & 0xFFFFFFFF])
out = os.path.join(os.path.dirname(__file__), '..', 'Tests', 'Vectors', 'formation_rules.json.gz')
result = {'imageSha256': hashlib.sha256(im.img).hexdigest(), 'getters': {f[0]: hex(f[1]) for f in fields},
          'liveGameAccess': False, 'nativeCodePatched': False, 'cases': cases}
with gzip.open(out, 'wt', encoding='utf-8') as file:
    json.dump(result, file, separators=(',', ':'))
print(json.dumps({'output': os.path.abspath(out), 'cases': len(cases), 'imageSha256': result['imageSha256'], 'liveGameAccess': False}))
