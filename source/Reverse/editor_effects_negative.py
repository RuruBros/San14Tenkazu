"""Fault-injection checks for the offline Native effects auditor; no game access."""
import argparse
import json
from pathlib import Path
from unittest.mock import patch

from unicorn import UcError, UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RIP, UC_X86_REG_RSP
import struct
from editor_effects_audit import EffectsNative, mappings
from editor_effects_cases import policy_cases, policy_fixture
from editor_consumer_audit import sha
from runtime_profile_audit import LEGACY_IMAGE, NOVEMBER_IMAGE, require
from version_audit import MemoryImage


def check(old, new):
    require(sha(old.data) == LEGACY_IMAGE and sha(new.data) == NOVEMBER_IMAGE, 'Pinned images')
    functions, globals_, _, _ = mappings(old, new)
    passed = []
    for image, november in ((old, False), (new, True)):
        n = EffectsNative(image, november, functions, globals_)
        try:
            n.put(image.base + n.rva(0x258D90), b'\x90')
        except ValueError as error:
            require('Native executable' in str(error), 'Unexpected rejection')
            passed.append(dict(build=november, fault='executable fixture mutation', rejected=True))
        else:
            raise AssertionError('Modified executable accepted')
        with patch.object(EffectsNative, 'on_read', lambda *args: None):
            no_reads = EffectsNative(image, november, functions, globals_)
            try:
                policy_cases(no_reads)
            except ValueError as error:
                require('byte witness' in str(error), 'Unexpected missing-read failure')
                passed.append(dict(build=november, fault='correct result without read evidence', rejected=True))
            else:
                raise AssertionError('Missing Native read accepted')
        policy_fixture(n, {42: dict(level=3, status=1)})
        baseline = (n.policy_effect(1, True), n.policy_effect(1))
        require(baseline == (3, 103), 'Native baseline for fault injection')
        def no_visitor(cpu, address, size, user):
            sp = cpu.reg_read(UC_X86_REG_RSP)
            cpu.reg_write(UC_X86_REG_RIP, struct.unpack('<Q', cpu.mem_read(sp, 8))[0])
            cpu.reg_write(UC_X86_REG_RSP, sp + 8)
        entry = image.base + n.rva(0x258D90)
        hook = n.cpu.hook_add(UC_HOOK_CODE, no_visitor, begin=entry, end=entry)
        try:
            skipped = (n.policy_effect(1, True), n.policy_effect(1))
            require(skipped == (0, 0) and skipped != baseline, 'Visitor replacement was not detected')
            try:
                policy_cases(n)
            except ValueError as error:
                require('byte witness' in str(error), 'Unexpected visitor-boundary failure')
            else:
                raise AssertionError('A gameplay visitor replacement passed the auditor')
            passed.append(dict(build=november, fault='old observation-only policy visitor',
                               baseline=baseline, substituted=skipped, rejected=True))
        finally:
            n.cpu.hook_del(hook)
        policy_fixture(n, {42: dict(level=3, status=1)})
        n.put(n.root + 0x871E8, bytes(8))
        try:
            n.policy_effect(1)
        except UcError:
            require(n.last_fault is not None, 'Missing-state fault witness')
            passed.append(dict(build=november, fault='missing mutable world input', rejected=True, nativeFault=n.last_fault))
        else:
            raise AssertionError('Missing mutable input accepted')
    return dict(passed=True, checks=passed, count=len(passed), gameAccess=False, liveWrites=0,
                testOnlyFaultInjection=True)


def main():
    p = argparse.ArgumentParser(description=__doc__)
    for name in ('legacy-image', 'legacy-base', 'november-image', 'november-base', 'output'):
        p.add_argument('--' + name, required=True)
    args = p.parse_args()
    output = Path(args.output)
    require(not output.exists(), 'Output must be new')
    result = check(MemoryImage.load(args.legacy_image, int(args.legacy_base, 0)),
                   MemoryImage.load(args.november_image, int(args.november_base, 0)))
    output.parent.mkdir(exist_ok=True, parents=True)
    output.write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(f'PASS {result["count"]} offline fault-injection checks; gameAccess=false')


if __name__ == '__main__':
    main()
