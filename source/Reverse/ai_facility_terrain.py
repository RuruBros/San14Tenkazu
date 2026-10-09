# Reproduce both facility terrain-mask formats from the saved game image only.
# python ai_facility_terrain.py --vectors ../Tests/AiFacilityTerrainVectors.json
# No live game, result stubs, code patches, or game hooks are used.
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import struct

from image import Image, IMAGE
from unicorn import Uc, UcError, UC_ARCH_X86, UC_MODE_64, UC_HOOK_CODE, UC_HOOK_MEM_UNMAPPED
from unicorn.x86_const import UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_RSP, UC_X86_REG_RAX


HERE = Path(__file__).resolve().parent
FUNCTION = 0x42B40
VTABLE = 0x13A6EF0
SENTINEL = 0xD0000000
SCRATCH = 0x100000000


class TerrainImage:
    def __init__(self):
        self.image = Image()
        self.base = self.image.base
        self.mu = Uc(UC_ARCH_X86, UC_MODE_64)
        self.mu.mem_map(SCRATCH, 0x100000)
        self.mu.mem_map(SENTINEL, 0x1000)
        self.image_pages = set()
        self.mu.hook_add(UC_HOOK_MEM_UNMAPPED, self.fault)
        self.mu.hook_add(UC_HOOK_CODE, self.observe)
        self.world = SCRATCH
        self.definitions = SCRATCH + 0x80000
        self.parameters = SCRATCH + 0x90000
        self.stack = SCRATCH + 0xF0000
        self.virtual = self.image.u64(VTABLE + 0x48) - self.base
        if self.virtual != 0xDB00:
            raise AssertionError('unexpected facility virtual byte decoder')
        self.write(self.base + 0x277DF18, struct.pack('<Q', self.world))
        self.write(self.world + 0x73958, struct.pack('<Q', self.definitions))
        self.frame = None

    def fault(self, uc, access, address, size, value, user_data):
        for page in range(address & ~0xFFF, ((address + size - 1) & ~0xFFF) + 0x1000, 0x1000):
            if page in self.image_pages:
                continue
            offset = page - self.base
            if not 0 <= offset < len(self.image.img):
                return False
            uc.mem_map(page, 0x1000)
            uc.mem_write(page, self.image.img[offset:offset + 0x1000].ljust(0x1000, b'\0'))
            self.image_pages.add(page)
        return True

    def write(self, address, data):
        # Explicit image-data input, not an instruction or helper replacement.
        if address >= self.base:
            for page in range(address & ~0xFFF, ((address + len(data) - 1) & ~0xFFF) + 0x1000, 0x1000):
                try:
                    self.mu.mem_read(page, 1)
                except UcError:
                    if not self.fault(self.mu, 0, page, 0x1000, 0, None):
                        raise
        self.mu.mem_write(address, data)

    def observe(self, uc, address, size, user_data):
        rva = address - self.base
        if rva == 0xDB00:
            self.frame['decoderCalls'] += 1
        elif rva == 0x42BD8:
            stack = uc.reg_read(UC_X86_REG_RSP)
            self.frame['mask'] = int.from_bytes(uc.mem_read(stack + 0x50, 4), 'little')
            self.frame['mode'] = 'encoded'
        elif rva == 0x42BF3:
            obj = uc.reg_read(UC_X86_REG_RCX)
            self.frame['mask'] = int.from_bytes(uc.mem_read(obj + 0x3C, 4), 'little')
            self.frame['mode'] = 'legacy'

    def run(self, kind, parameter_bytes, legacy_mask, encoded_bytes, terrain):
        obj = self.definitions + kind * 0x68
        self.write(obj, bytes(0x68))
        for offset, data in (
                (0, struct.pack('<Q', self.base + VTABLE)),
                (0x3C, struct.pack('<I', legacy_mask)),
                (0x50, struct.pack('<QQ', self.parameters, self.parameters + parameter_bytes))):
            self.write(obj + offset, data)
        for index, byte in enumerate(encoded_bytes):
            self.write(self.parameters + 0x34 + index * 4, bytes([byte, 0xA7, 0xDB, 0xF1]))
        rsp = self.stack - 8
        self.write(rsp, struct.pack('<Q', SENTINEL))
        for register, value in ((UC_X86_REG_RSP, rsp), (UC_X86_REG_RCX, obj), (UC_X86_REG_RDX, terrain)):
            self.mu.reg_write(register, value)
        self.frame = {'decoderCalls': 0}
        self.mu.emu_start(self.base + FUNCTION, SENTINEL, count=10000)
        if self.mu.reg_read(UC_X86_REG_RSP) != rsp + 8:
            raise AssertionError('original function did not return')
        return self.mu.reg_read(UC_X86_REG_RAX) & 0xFFFFFFFF, dict(self.frame)


def verify():
    engine = TerrainImage()
    terrains = list(range(33)) + [255, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF]
    masks = sorted({0, 0xFFFFFFFF, 0x7FFFFFFF, 0xAAAAAAAA, 0x55555555, *(1 << bit for bit in range(32))})
    cases = []
    for length in (0, 72, 75, 76, 77, 78, 79, 80):
        encoded = length & ~3 == 76
        for kind in ((0, 1, 30, 31, 255, 256) if encoded else (0,)):
            for mask in masks:
                legacy = mask ^ 0xA5A55A5A
                stored = []
                for byte in mask.to_bytes(4, 'little'):
                    value = byte ^ (kind & 255)
                    stored.append(((value << 1) | (value >> 7)) & 255)
                native, observation = [], None
                for terrain in terrains:
                    result, frame = engine.run(kind, length, legacy, stored, terrain)
                    expected_mask = mask if encoded else legacy
                    expected = int(terrain <= 30 and bool(expected_mask & (1 << terrain)))
                    if result != expected or frame != {'decoderCalls': 4 if encoded else 0,
                            'mask': expected_mask, 'mode': 'encoded' if encoded else 'legacy'}:
                        raise AssertionError((kind, length, mask, terrain, result, frame))
                    native.append(result)
                    observation = frame
                cases.append({'kind': kind, 'parameterBytes': length, 'legacyMask': legacy,
                    'encodedBytes': stored, 'observation': observation, 'native': native})
    return {'utc': datetime.now(timezone.utc).isoformat(),
        'method': 'saved image only; original 42B40/22DC50/DB00; synthetic data, no stubs or patches',
        'imageSha256': hashlib.sha256(Path(IMAGE).read_bytes()).hexdigest(),
        'function': FUNCTION, 'vtable': VTABLE, 'decoder': engine.virtual,
        'terrains': terrains, 'nativeCalls': len(cases) * len(terrains), 'cases': cases}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--vectors', type=Path)
    parser.add_argument('--static', action='store_true')
    args = parser.parse_args()
    folder = HERE / '.cache' / 'ai_facility_terrain'
    folder.mkdir(parents=True, exist_ok=True)
    if args.static:
        image = Image()
        result = {f'{rva:X}': image.flowtext(rva) for rva in (0x279190, FUNCTION, 0x22DC50, 0xDB00, 0x27BD50)}
        path = folder / 'static.json'
    else:
        result = verify()
        path = folder / (datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ') + '.json')
    text = json.dumps(result, ensure_ascii=False, indent=2)
    path.write_text(text, encoding='utf-8')
    if args.vectors:
        args.vectors.write_text(text.replace('\n', '\r\n') + '\r\n', encoding='utf-8', newline='')
    print(f"{result.get('nativeCalls', 0)} native calls; {path}")


if __name__ == '__main__':
    main()
