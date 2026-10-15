"""Reproduce the native column-cache store using saved code; never accesses a game."""
import hashlib
import json
from pathlib import Path
import struct
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / '.cache/registered-officers/pydeps'))
from unicorn import Uc, UcError, UC_ARCH_X86, UC_MODE_64, UC_PROT_ALL, UC_PROT_READ, UC_PROT_WRITE, UC_PROT_EXEC, UC_ERR_WRITE_PROT
from unicorn.x86_const import UC_X86_REG_R13, UC_X86_REG_RDI, UC_X86_REG_RAX

image = (ROOT / 'source/Reverse/.cache/image.bin').read_bytes()
assert hashlib.sha256(image).hexdigest() == 'aa85c23361c67cd085f3101d2ecb9bf1579f79d46d081effa0184044f1c48e59'
manifest = json.loads((ROOT / 'source/Core/RuntimeRules/trait-list.json').read_text())
columns = bytes.fromhex(manifest['Columns']['Bytes'])
column, record_size = 107, 96
callback = struct.unpack_from('<Q', columns, column * record_size + 0x40)[0]
assert callback - manifest['OriginalBase'] == 0x341CB0
table = next(t for t in manifest['Tables'] if t['Rva'] == 0x1A31680)
ids = struct.unpack_from('<Q', bytes.fromhex(table['Bytes']))[0] - manifest['OriginalBase']
entries = []
while (value := struct.unpack_from('<i', image, ids + len(entries) * 4)[0]) != -1:
    entries.append(value)
assert column in entries

results = []
for readonly in (False, True):
    mu = Uc(UC_ARCH_X86, UC_MODE_64)
    mu.mem_map(0x2BF000, 4096, UC_PROT_ALL)
    mu.mem_write(0x2BF000, image[0x2BF000:0x2C0000])
    address = 0x20000000
    mu.mem_map(address, 0x8000, UC_PROT_ALL)
    mu.mem_write(address + 4096, columns)
    if readonly:
        mu.mem_protect(address + 4096, 0x7000, UC_PROT_READ)
    mu.reg_write(UC_X86_REG_R13, address + 4096)
    mu.reg_write(UC_X86_REG_RDI, column * record_size)
    # Explicit synthetic post-callback input, not a claimed real-game callback result.
    mu.reg_write(UC_X86_REG_RAX, 0)
    error = None
    try:
        mu.emu_start(0x2BF138, 0x2BF13D, count=1)
    except UcError as failure:
        error = failure.errno
    actual = struct.unpack('<I', mu.mem_read(address + 4096 + column * record_size + 0x5C, 4))[0]
    passed = error == UC_ERR_WRITE_PROT if readonly else error is None and actual == 0
    results.append(dict(readonly=readonly, error=error, cachedValue=actual, reproduced=passed))
    assert passed

metadata = json.loads((ROOT / '.cache/traits9-list/payload.json').read_text())
payload = (ROOT / '.cache/traits9-list/payload.bin').read_bytes()
assert metadata['mutableColumns'] == [i for i in range(213) if struct.unpack_from('<Q', columns, i*96+0x40)[0]]
fixed = []
for column in metadata['mutableColumns']:
    for value in (0, 1, 0xFFFFFFFF):
        mu = Uc(UC_ARCH_X86, UC_MODE_64)
        mu.mem_map(0x2BF000, 4096, UC_PROT_ALL)
        mu.mem_write(0x2BF000, image[0x2BF000:0x2C0000])
        address = metadata['allocation']
        mu.mem_map(address, len(payload), UC_PROT_ALL)
        mu.mem_write(address, payload)
        mu.mem_protect(address, 4096, UC_PROT_READ | UC_PROT_EXEC)
        for page in range(4096, len(payload), 4096):
            mu.mem_protect(address + page, 4096, UC_PROT_READ | (UC_PROT_WRITE if page in metadata['writablePages'] else 0))
        mu.reg_write(UC_X86_REG_R13, address + 4096)
        mu.reg_write(UC_X86_REG_RDI, column * record_size)
        mu.reg_write(UC_X86_REG_RAX, value)
        mu.emu_start(0x2BF138, 0x2BF13D, count=1)
        expected = bytearray(payload)
        struct.pack_into('<I', expected, 4096 + column * record_size + 0x5C, value)
        assert bytes(mu.mem_read(address, len(payload))) == expected
        fixed.append(dict(column=column, syntheticCallbackReturn=value, entireAllocationMatches=True))

output = ROOT / '.cache/trait-list-mutability-fixed.json'
output.write_text(json.dumps(dict(gameAccess=False, codeRva='0x2BF138', column=column, offset='0x5C',
    supportedTable='0x1A31680', syntheticCallbackReturn=0, originalBug=results, fixedProductCases=fixed), indent=2))
print(output.read_text())
