# Native queue registration and consumption share one ABI harness, so no state is replaced by outputs.
import struct
from context import *
from corps_commands import generate as collect

COVER = [0x32B5A0, 0x33BC00, 0x226300, 0x335060, 0x242690]


def fixtures(ctx, snapshot, source, destination):
    registered = {r[0][1] for r in snapshot['lists']['0x100']['items']}
    serving = [i for i,b in enumerate(ctx.rec[OFFICERS]) if b[0xE0] == source and b[0xE6] == 4
               and i in registered and struct.unpack_from('<Q',b,0x1B0)[0] <= 7]
    a,b = serving[:2]
    missing = next(i for i,data in enumerate(ctx.rec[OFFICERS]) if i and i not in registered
                   and struct.unpack_from('<Q',data,0x1B0)[0] <= 7)
    count = struct.unpack_from('<i',bytes.fromhex(snapshot['free_manager']),8)[0]
    if snapshot['nested']['free_officers']: raise RuntimeError('Queue composition requires an initially empty free list')
    def f(name, queue, extra=(), initial=count):
        pokes = [['worldstate',0,0x3A,'00']]
        for person in dict.fromkeys(queue):
            pokes += [[hex(OFFICERS),person,0x15E,struct.pack('<H',ctx.u16(OFFICERS,person,0x15E)|0x4000).hex()],
                      [hex(OFFICERS),person,0x1C5,'01']]
        return dict(name=name,source=source,destination=destination,mode=0,person=[hex(OFFICERS),a],
            queue=[[hex(OFFICERS),i] for i in queue],count=initial,pokes=pokes+list(extra))
    dirty = [[hex(OFFICERS),a,at,'a5'*length] for at,length in [
        (0xC0,32),(0x104,20),(0x118,18),(0x12C,14),(0x13A,18),(0x152,12),(0x160,9),
        (0xF2,5),(0xF8,10),(0x16A,24),(0x184,20),(0x1BE,6),(0x1C8,1),(0x1CC,4),(0x1EE,2)]]
    dirty += [[hex(OFFICERS),a,0x198,'41004200430044004500460047000000'],
              [hex(OFFICERS),a,0x1A8,struct.pack('<Q',7).hex()],
              [hex(OFFICERS),a,0x15E,struct.pack('<H',ctx.u16(OFFICERS,a,0x15E)|0x6000).hex()]]
    return [f('empty',[]),f('one_registered',[a]),f('two_registered',[a,b]),f('duplicate_registration',[a,a]),
            f('unregistered_slot',[missing],[[hex(OFFICERS),missing,0xE0,bytes([source]).hex()],
                [hex(OFFICERS),missing,0xE6,'04']]),f('reset_dirty_inline_slot',[a],dirty),
            f('counter_signed_wrap',[a],initial=2147483647)]


def generate(ctx):
    return collect(ctx,fixtures,0x33BC00,'free_consumer')
