# Run the entire original path queue removal helper in independent memory.
# python ai_path_queue.py [--vectors ../Tests/AiPathQueueVectors.json.gz]
# Saved image.bin only; no Game, Oracle, live memory, helper stubs or code patches.
import argparse
from datetime import datetime, timezone
import gzip
import hashlib
from itertools import permutations
import json
from pathlib import Path
import random
import struct

from image import Image
from unicorn import Uc, UC_ARCH_X86, UC_MODE_64, UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_RCX, UC_X86_REG_RDX, UC_X86_REG_RSP, UC_X86_REG_RIP

HERE = Path(__file__).resolve().parent
ENTRY = 0x18AE60


def cases():
    yield [], [], -1
    rng = random.Random(1864)
    for count in range(1, 9):
        datasets = [sorted((-2147483648, -1, 0, 1, 2147483647, -300, 300, 15)[:count]),
            [0] * count, [(-1, 1)[i % 2] for i in range(count)],
            [(-2147483648, 2147483647, 0)[i % 3] for i in range(count)]]
        orders = list(permutations(range(count))) if count <= 5 else [tuple(rng.sample(range(count), count)) for _ in range(32)]
        for priorities in datasets:
            for order in orders:
                for target in range(count): yield priorities, order, target


def inputs(priorities, order):
    left, right = [-1] * len(priorities), [-1] * len(priorities)
    root = order[0] if order else -1
    # Same signed comparison as backend insertion: equality follows the right link.
    for node in order[1:]:
        parent = root
        while True:
            links = left if priorities[parent] > priorities[node] else right
            if links[parent] == -1:
                links[parent] = node; break
            parent = links[parent]
    return root, left, right


def verify(im):
    im.md.detail = True
    instructions = list(im.flow(ENTRY))
    if any(i.mnemonic == 'call' or 'rip' in i.op_str for i in instructions):
        raise AssertionError('helper acquired an external call or global/image data dependency')
    branches = {i.address: i for i in instructions if i.mnemonic.startswith('j') and i.mnemonic != 'jmp'}
    start = ENTRY & ~0xFFF
    end = (max(i.address + i.size for i in instructions) + 0xFFF) & ~0xFFF
    machine = Uc(UC_ARCH_X86, UC_MODE_64)
    machine.mem_map(im.base + start, end - start)
    machine.mem_write(im.base + start, im.img[start:end])
    data, stack, stop = 0x300000000, 0x400000000, 0x500000000
    machine.mem_map(data, 0x1000); machine.mem_map(stack, 0x2000); machine.mem_map(stop, 0x1000)
    machine.mem_write(stop, b'\xF4')
    previous, edges = [None], set()
    def observe(uc, address, size, user):
        pc = address - im.base
        if previous[0] in branches: edges.add((previous[0], pc))
        previous[0] = pc
    machine.hook_add(UC_HOOK_CODE, observe, None, im.base + start, im.base + end - 1)
    answers = []
    for priorities, order, target in cases():
        root, left, right = inputs(priorities, order)
        node = lambda n: 0 if n == -1 else data + 0x100 + n * 40
        def index(pointer):
            if pointer == 0: return -1
            offset = pointer - data - 0x100
            if offset < 0 or offset % 40 or offset // 40 >= len(priorities):
                raise AssertionError('native link points outside input nodes')
            return offset // 40
        before = bytearray(0x1000)
        struct.pack_into('<Q', before, 0, node(root))
        for n, priority in enumerate(priorities):
            struct.pack_into('<QQQ', before, 0x100 + n * 40, node(left[n]), node(right[n]), 0)
            struct.pack_into('<i', before, 0x100 + n * 40 + 36, priority)
        machine.mem_write(data, bytes(before))
        machine.mem_write(stack + 0x1000, struct.pack('<Q', stop))
        machine.reg_write(UC_X86_REG_RSP, stack + 0x1000)
        machine.reg_write(UC_X86_REG_RCX, data)
        machine.reg_write(UC_X86_REG_RDX, node(target))
        previous[0] = None
        machine.emu_start(im.base + ENTRY, stop, count=10000)
        if machine.reg_read(UC_X86_REG_RIP) != stop or machine.reg_read(UC_X86_REG_RSP) != stack + 0x1008:
            raise AssertionError('helper did not return on its original stack')
        after = bytes(machine.mem_read(data, 0x1000))
        updated = [struct.unpack_from('<QQ', after, 0x100 + n * 40) for n in range(len(priorities))]
        # Only head and the two pointer links may change. All stored priorities/payload bytes stay equal.
        mutable = set(range(8)) | {0x100 + n * 40 + b for n in range(len(priorities)) for b in range(16)}
        if any(a != b and i not in mutable for i, (a, b) in enumerate(zip(before, after))):
            raise AssertionError('unexpected payload modification')
        answers.append({'priorities': priorities, 'insertionOrder': order, 'target': target,
            'rootBefore': root, 'leftBefore': left, 'rightBefore': right,
            'rootAfter': index(struct.unpack_from('<Q', after)[0]),
            'leftAfter': [index(a) for a, b in updated], 'rightAfter': [index(b) for a, b in updated]})
    coverage = [{'rva': pc, 'text': i.mnemonic + ' ' + i.op_str,
        'taken': (pc, i.operands[0].imm) in edges, 'fallthrough': (pc, pc + i.size) in edges}
        for pc, i in sorted(branches.items())]
    return {'entry': ENTRY, 'instructionCount': len(instructions), 'cases': answers, 'branchCoverage': coverage,
        'static': im.flowtext(ENTRY), 'payloadUnchanged': True,
        'method': 'complete original leaf function in independent code/data/stack mappings; valid backend insertion order; no game access or replacements'}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--vectors', type=Path)
    args = parser.parse_args()
    im = Image()
    result = {'utc': datetime.now(timezone.utc).isoformat(), 'imageSha256': hashlib.sha256(im.img).hexdigest(), **verify(im)}
    folder = HERE / '.cache' / 'ai_path_queue'; folder.mkdir(parents=True, exist_ok=True)
    path = folder / (datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ') + '.json')
    text = json.dumps(result, ensure_ascii=False, indent=2) + '\n'
    path.write_text(text, encoding='utf-8')
    if args.vectors: args.vectors.write_bytes(gzip.compress(text.replace('\n', '\r\n').encode('utf-8'), mtime=0))
    both = sum(b['taken'] and b['fallthrough'] for b in result['branchCoverage'])
    print(len(result['cases']), 'complete calls;', both, '/', len(result['branchCoverage']), 'branches in both directions;', path)


if __name__ == '__main__': main()
