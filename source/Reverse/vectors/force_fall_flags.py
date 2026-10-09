# Execute the complete force-fall scenario-bit helper on coherent copied memory, without stubs.
import hashlib
import struct
from context import Context, FORCES, world

COVER = [0x279A50, 0x22E970, 0x2337B0, 0x233F10]


def generate(ctx):
    o = ctx.o
    a, b = [i for i in range(1, 46) if ctx.u16(FORCES, i, 0x10)][:2]
    fixtures = []

    def case(name, left=521, right=952, flags=0, player=b, first=a, second=b):
        fixtures.append(dict(name=name, defeated=[hex(FORCES), first] if first is not None else None,
            victor=[hex(FORCES), second] if second is not None else None,
            pokes=[[hex(FORCES), a, 0x10, struct.pack('<H', left).hex()],
                [hex(FORCES), b, 0x10, struct.pack('<H', right).hex()],
                ['worldstate', 0, 0x169C, struct.pack('<I', flags).hex()],
                ['worldstate', 0, 0x3A, bytes([player]).hex()]]))

    for bit, (left, right) in enumerate([(521, 952), (521, 35), (521, 769), (35, 266)]):
        case(f'match_bit_{bit}', left, right)
        case(f'preserve_other_bits_{bit}', left, right, flags=0x80000100)
        case(f'already_set_{bit}', left, right, flags=0x80000100 | (1 << bit))
    case('not_player', player=a)
    case('no_player', player=0)
    case('player_lookup_51', player=51)
    case('out_of_range_player', player=52)
    case('wrong_defeated_ruler', left=520)
    case('wrong_victor_ruler', right=951)
    case('reversed_ruler_pair', left=952, right=521)
    case('null_defeated', first=None)
    case('null_victor', second=None)
    case('invalid_defeated', left=0)
    case('invalid_victor', right=0)
    case('defeated_zero_slot', first=0)
    case('victor_zero_slot', second=0)
    case('all_four_bits_already_set', flags=0xFFFFFFFF)
    ws = o.q(ctx.raw + world.WORLDSTATE_RAW)
    force_start = ctx.addr(FORCES, 0)
    force_size = ctx.snap['tables'][hex(FORCES)]['size'] * len(ctx.rec[FORCES])
    cases = []
    try:
        for fixture in fixtures:
            def run():
                before = o.read(force_start, force_size)
                first = fixture['defeated']
                second = fixture['victor']
                o.call(0x279A50, ctx.addr(FORCES, first[1]) if first else 0,
                    ctx.addr(FORCES, second[1]) if second else 0,
                    read_after=[(ws, world.WORLDSTATE_SIZE), (force_start, force_size)])
                if o.after[1] != before:
                    raise RuntimeError('The native scenario helper unexpectedly changed a force record')
                return dict(fixture, worldstate=o.after[0].hex(), forces_unchanged=True)
            result = ctx.run(fixture['pokes'], run)
            if result is None:
                raise RuntimeError(f'Native scenario helper failed: {ctx.failures[-1]}')
            cases.append(result)
    finally:
        o.unstub()
    ctx.save('force_fall_flags_world.json.gz', ctx.snap)
    ctx.save('force_fall_flags.json.gz', dict(entry=hex(0x279A50),
        world='force_fall_flags_world.json.gz', image_sha256=hashlib.sha256(ctx.im.img).hexdigest(),
        no_stubs=True, cases=cases))
    return len(cases)
