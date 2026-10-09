# Decode only the guarded two-table MSVC dispatch shape present in the saved image.
# No execution, patched instructions or guessed virtual targets.
from capstone.x86_const import X86_OP_IMM, X86_OP_MEM, X86_OP_REG, X86_REG_RIP


def executable(im, address):
    start, size = im.sections['.text']
    return start <= address < start + size


def register(ins, value):
    name = ins.reg_name(value)
    return {'eax': 'rax', 'ecx': 'rcx', 'edx': 'rdx', 'ebx': 'rbx', 'esi': 'rsi', 'edi': 'rdi'}.get(name,
        name[:-1] if name.startswith('r') and name.endswith('d') else name)


def guarded_tables(im, start, instructions):
    if not executable(im, start): return []
    # Small leaf functions may have no .pdata entry. Their target range is
    # still bounded below by the called entry and above by both table starts.
    owner = im.func(start) or (start, start + 0x20000)
    result = []
    for at in range(len(instructions) - 7):
        block = instructions[at:at + 8]
        if any(a.address + a.size != b.address for a, b in zip(block, block[1:])): continue
        compare, guard, base, extend, byte, table, add, jump = block
        if [i.mnemonic for i in block[:3]] != ['cmp', 'ja', 'lea']: continue
        if len(compare.operands) != 2 or compare.operands[0].type != X86_OP_REG or compare.operands[1].type != X86_OP_IMM: continue
        count = compare.operands[1].imm + 1
        if compare.operands[0].size != 4 or not 0 < count <= 4096: continue
        if base.operands[1].type != X86_OP_MEM or base.operands[1].mem.base != X86_REG_RIP: continue
        base_reg = register(base, base.operands[0].reg)
        origin = base.address + base.size + base.operands[1].mem.disp
        guarded = register(compare, compare.operands[0].reg)
        if extend.mnemonic == 'cdqe':
            if guarded != 'rax': continue
            index_reg = 'rax'
        elif extend.mnemonic == 'movsxd' and len(extend.operands) == 2 and all(o.type == X86_OP_REG for o in extend.operands):
            if register(extend, extend.operands[1].reg) != guarded: continue
            index_reg = register(extend, extend.operands[0].reg)
        else: continue
        if byte.mnemonic != 'movzx' or len(byte.operands) != 2 or byte.operands[0].type != X86_OP_REG or byte.operands[1].type != X86_OP_MEM: continue
        bmem = byte.operands[1].mem
        if byte.operands[1].size != 1 or register(byte, bmem.base) != base_reg or register(byte, bmem.index) != index_reg or bmem.scale != 1: continue
        byte_reg = register(byte, byte.operands[0].reg)
        if table.mnemonic != 'mov' or len(table.operands) != 2 or table.operands[0].type != X86_OP_REG or table.operands[0].size != 4 or table.operands[1].type != X86_OP_MEM: continue
        tmem = table.operands[1].mem
        if register(table, tmem.base) != base_reg or register(table, tmem.index) != byte_reg or tmem.scale != 4: continue
        target_reg = register(table, table.operands[0].reg)
        if base_reg in (index_reg, byte_reg, target_reg) or table.operands[1].size != 4: continue
        if add.mnemonic != 'add' or any(o.type != X86_OP_REG for o in add.operands): continue
        if any(o.size != 8 for o in add.operands): continue
        if [register(add, o.reg) for o in add.operands] != [target_reg, base_reg]: continue
        if jump.mnemonic != 'jmp' or jump.operands[0].type != X86_OP_REG or register(jump, jump.operands[0].reg) != target_reg: continue
        # No decoded direct jump may bypass the guard by entering its straight-line body.
        interior = {i.address for i in block[2:]}
        if any((i.mnemonic.startswith('j') or i.mnemonic == 'call') and i.operands and i.operands[0].type == X86_OP_IMM and i.operands[0].imm in interior for i in instructions): continue
        byte_address, table_address = origin + bmem.disp, origin + tmem.disp
        if not 0 <= byte_address <= len(im.img) - count: continue
        choices = im.img[byte_address:byte_address + count]
        targets = []
        for value in sorted(set(choices)):
            address = table_address + value * 4
            if not 0 <= address <= len(im.img) - 4: break
            target = origin + im.u32(address)
            if not executable(im, target) or not owner[0] <= target < owner[1] or target >= min(byte_address, table_address): break
            targets.append(dict(tableIndex=value, target=target))
        else:
            result.append(dict(entry=start, rva=jump.address, guard=guard.address, compare=compare.address,
                defaultTarget=guard.operands[0].imm, count=count, byteTable=byte_address,
                targetTable=table_address, selectorBytes=choices.hex(), targets=targets,
                anchors=[dict(rva=i.address, bytes=bytes(i.bytes).hex(), text=i.mnemonic + ' ' + i.op_str) for i in block]))
    return result


def flow(im, start):
    code = {i.address: i for i in im.flow(start)}; switches = {}
    while True:
        discovered = guarded_tables(im, start, [code[k] for k in sorted(code)])
        new = [row for row in discovered if row['rva'] not in switches]
        if not new: return [code[k] for k in sorted(code)], list(switches.values())
        for row in new:
            switches[row['rva']] = row
            for item in row['targets']:
                code.update((i.address, i) for i in im.flow(item['target']))
