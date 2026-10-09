# Adds immutable code pages from the captured image to the frozen dump. Runtime data and the live game are
# never accessed. PE relocations are adjusted, then every existing .text page is checked before saving.
# Usage: python freeze_code.py [--dump=<path>] <rva> [...] or --all-text for complete immutable code pages.
import os, pickle, shutil, struct, sys
from image import Image


def main():
    im = Image()
    path = next((a.split('=', 1)[1] for a in sys.argv[1:] if a.startswith('--dump=')),
                os.path.join(os.path.dirname(__file__), '.cache', 'frozen.pkl'))
    requested = [a for a in sys.argv[1:] if not a.startswith('--dump=')]
    with open(path, 'rb') as f: dump = pickle.load(f)
    if dump['exe_size'] != len(im.img): raise SystemExit('The captured and frozen image sizes differ.')
    start, size = im.sections['.text']
    end = start + size
    data = bytearray(im.img)
    pe = struct.unpack_from('<I', data, 0x3C)[0]
    reloc, reloc_size = struct.unpack_from('<II', data, pe + 24 + 112 + 5 * 8)
    delta = dump['exe'] - im.base
    at = reloc
    while at < reloc + reloc_size:
        page, block_size = struct.unpack_from('<II', data, at)
        if block_size < 8: raise SystemExit('Invalid PE relocation block.')
        for off in range(at + 8, at + block_size, 2):
            entry = struct.unpack_from('<H', data, off)[0]
            kind, rva = entry >> 12, page + (entry & 0xFFF)
            if kind == 0 or not start <= rva < end: continue
            if kind != 10: raise SystemExit(f'Unsupported code relocation type {kind} at {rva:#x}.')
            value = struct.unpack_from('<Q', data, rva)[0]
            struct.pack_into('<Q', data, rva, (value + delta) & 0xFFFFFFFFFFFFFFFF)
        at += block_size
    checked = 0
    for address, contents in dump['pages'].items():
        rva = address - dump['exe']
        if start <= rva and rva + 0x1000 <= end:
            if contents != data[rva:rva + 0x1000]: raise SystemExit(f'Code differs at {rva:#x}; frozen dump unchanged.')
            checked += 1
    if checked == 0: raise SystemExit('No frozen code pages are available to validate the captured image.')
    added = []
    args = [hex(r) for r in range((start + 0xFFF) & ~0xFFF, end - 0xFFF, 0x1000)] if requested == ['--all-text'] else requested
    for arg in args:
        rva = int(arg, 16) & ~0xFFF
        if not start <= rva or rva + 0x1000 > end: raise SystemExit(f'{rva:#x} is not a complete .text page.')
        address = dump['exe'] + rva
        if address in dump['pages'] and dump['pages'][address] != data[rva:rva + 0x1000]:
            raise SystemExit(f'Requested immutable page differs at {rva:#x}; frozen dump unchanged.')
        if address not in dump['pages']:
            dump['pages'][address] = bytes(data[rva:rva + 0x1000])
            added.append(hex(rva))
    if not requested: raise SystemExit('Specify the missing code RVA.')
    if added:
        backup = path + '.before-codefill'
        if not os.path.exists(backup): shutil.copyfile(path, backup)
        with open(path + '.tmp', 'wb') as f: pickle.dump(dump, f)
        os.replace(path + '.tmp', path)
    print(f'Validated {checked} relocated .text pages; added {len(added)} pages {added[:12]}; no runtime data copied.')


if __name__ == '__main__': main()
