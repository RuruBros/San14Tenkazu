"""Reproduce the reviewed November native command bindings from captured PE images."""
import argparse
import json
from pathlib import Path

from runtime_profile_audit import LEGACY_IMAGE, NOVEMBER_IMAGE, NOVEMBER_EXE, require
from version_audit import MemoryImage, digest


PROFILE = Path(__file__).resolve().parents[1] / 'Core' / 'BuildProfiles' / 'NovemberCommands.json'
# Reviewed call entry points. The values for event variants are the targets of the
# matching RTTI descriptor, even if another function has the same instruction shape.
FUNCTIONS = {
    0x33A630: 0x33C890, 0x3C1A00: 0x3A0050, 0x3A9300: 0x3AC6A0,
    0x31F2E0: 0x321350, 0x32B0E0: 0x32D3A0,
    0x2EBCC0: 0x2EE080, 0x278270: 0x279BA0, 0x292760: 0x294670,
    0x2754A0: 0x276DD0, 0x33CED0: 0x33F140,
    0x31F4F0: 0x321560, 0x2341F0: 0x235B90,
    0x3BF120: 0x3C2510, 0x39FD00: 0x3A30A0, 0x2D7C80: 0x2DA030, 0x232400: 0x233DA0, 0x233110: 0x234AB0,
    0x233440: 0x234DE0, 0x241C90: 0x243630, 0x240CB0: 0x242650, 0x2408E0: 0x242280,
    0x2EA1B0: 0x2EC570, 0x2E8E70: 0x2EB230, 0x228710: 0x22A140,
    0x240C90: 0x242630, 0x2827C0: 0x284560, 0x26CC10: 0x26E530,
    0x333480: 0x335700, 0x31CBA0: 0x31EC10, 0x323ED0: 0x325F80,
    0x2E0CF0: 0x2E3020, 0x29EDE0: 0x2A0CF0,
}
GLOBALS = {0x13BAEF8: 0x13BDF48, 0x27D10E0: 0x27D4130, 0x27D10F0: 0x27D4140, 0x27D1120: 0x27D4170}
CHANGED = {
    0x2E0CF0: '7c354eec02412a01daf469ec9cafe4b12f5941565099df6a8380b3b91ce60041',
    0x29EDE0: '5e55c79538893b7233a1c7c5c08a29664f256149422ff823f20f7d244cadc89b',
}


def relative_pointer(image, rva):
    return int.from_bytes(image.data[rva:rva + 8], 'little') - image.base


def profile(old, new):
    from game_command_audit import event_types, image_pointers

    require(event_types(old, image_pointers(old), 0x3C1A00) ==
            event_types(new, image_pointers(new), FUNCTIONS[0x3C1A00]) == ['.?AUSExecSurrenderForceForEdit@S14Event@@'],
            'Surrender event identity')
    for old_rva, new_rva in [(0x13BAEF8, 0x13BDF48)]:
        old_instance, new_instance = relative_pointer(old, old_rva), relative_pointer(new, new_rva)
        require(relative_pointer(old, relative_pointer(old, old_instance)) == 0x39FD00 and
                relative_pointer(new, relative_pointer(new, new_instance)) == 0x3A30A0,
                'Independence event instance')
    for rva in (0x27D10E0, 0x27D10F0, 0x27D1120):
        refs = [(x['rva'], y['rva']) for old_entry in (0x31F2E0, 0x31F4F0, 0x32B0E0)
                for x, y in zip(old.describe(old_entry)['references'], new.describe(FUNCTIONS[old_entry])['references'], strict=True)]
        require({b for a, b in refs if a == rva} == {GLOBALS[rva]}, f'List pool global {rva:x}')
    functions = {}
    old_caller, new_caller = old.describe(0x284D60), new.describe(0x286B00)
    require(old_caller['shape_sha256'] == new_caller['shape_sha256'], 'Native capture/arrival call sites')
    for source, target in FUNCTIONS.items():
        description = new.describe(target)
        if source in CHANGED:
            old_edges = [edge['instruction'] for edge in old_caller['direct_edges'] if edge['rva'] == source]
            new_edges = [edge['instruction'] for edge in new_caller['direct_edges'] if edge['rva'] == target]
            require(old_edges and old_edges == new_edges and old.prefix(source) == new.prefix(target) and
                    description['shape_sha256'] == CHANGED[source], f'Changed native command at {target:x}')
        else:
            require(old.describe(source)['shape_sha256'] == description['shape_sha256'], f'Code shape {source:x}')
        size = description['code_end'] - target
        require(description['code_start'] == target and 16 <= size <= 0x4000 and
                not any(target <= at < target + size for at in new.relocations),
                f'Executable guard at {target:x}')
        raw = new.data[target:target + size]
        functions[str(source)] = dict(Rva=target, Start=raw[:min(size, 32)].hex().upper(),
                                      Length=size, Sha256=digest(raw).upper())
    return dict(ExeHash=NOVEMBER_EXE, Functions=functions, Globals={str(k): v for k, v in GLOBALS.items()})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for version in ('legacy', 'november'):
        parser.add_argument(f'--{version}-image', type=Path, required=True)
        parser.add_argument(f'--{version}-base', type=lambda v: int(v, 0), required=True)
    parser.add_argument('--write', action='store_true')
    args = parser.parse_args()
    old = MemoryImage.load(args.legacy_image, args.legacy_base)
    new = MemoryImage.load(args.november_image, args.november_base)
    require(digest(old.data) == LEGACY_IMAGE and digest(new.data) == NOVEMBER_IMAGE, 'Unregistered PE capture')
    result = profile(old, new)
    if args.write:
        PROFILE.write_bytes((json.dumps(result, indent=2) + '\n').encode())
    else:
        require(json.loads(PROFILE.read_text()) == result, 'Native command bindings differ')
    print(f'{len(FUNCTIONS)} native command entries and {len(GLOBALS)} globals verified')


if __name__ == '__main__':
    main()
