"""Execute saved Native ability-growth code for zero/below-baseline XP, never a live game."""
import json
import struct
import sys

# The audit harness uses the product manifest and denies live-process discovery.
if '--tenkazu' not in sys.argv:
    sys.argv.append('--tenkazu')
from mod_patch_isolation import Audit, FrozenMemory, HERE, Oracle, PATCHES, ROOT, sha, source_bytes


def main():
    g = FrozenMemory(HERE / '.cache/frozen.pkl')
    o = Oracle(g)
    for rows in PATCHES.values():
        for _, _, _, guard_rva, guard in rows:
            if o.read(g.exe + guard_rva, len(guard)) != guard:
                raise RuntimeError(f'Original patch guard mismatch at {guard_rva:#x}')
    audit = Audit(o, g)
    for mode in ('original', 'stats200', 'growth200'):
        audit.mode_set(mode)
        for level in ((100,) if mode == 'original' else (100, 101, 150, 199, 200)):
            for ability in range(5):
                original = (level ** 2 if ability == 4 else level ** 3) + level * 5000 + max(level - 70, 0) * 5000
                low_ceiling = min(original, audit.threshold(level, ability)) - 1
                for experience in (0, 1, low_ceiling):
                    with audit.fixture() as put:
                        audit.setup(put)
                        put(audit.person + 0xEC + ability, bytes([level]))
                        put(audit.person + 0x104 + ability * 4, struct.pack('<i', experience))
                        o.call32(0x2B9D50, audit.person, ability, 1, 0, read_after=[(audit.person, 0x1F0)])
                        record = o.after[0]
                        actual = [record[0xEC + ability], struct.unpack_from('<i', record, 0x104 + ability * 4)[0]]
                        expected = [level, experience + (1 if mode == 'growth200' and level < 200 else 0)]
                        audit.check('below_baseline_native_growth', actual, expected,
                                    level=level, ability=ability, experience=experience, amount=1)
    report = dict(live_game_access=False, image_sha256=g.image_sha256, frozen_sha256=g.digest,
                  patches_sha256=sha(source_bytes), native_entry='0x2B9D50',
                  limitation='Saved-memory Native growth function only; post-level-up notification is stubbed. No full save or game load.',
                  cases=audit.cases, passed=all(c['passed'] for c in audit.cases))
    output = ROOT / '.cache/low-experience/native-results.json'
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps(dict(cases=len(audit.cases), passed=report['passed'], report=str(output))))
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
