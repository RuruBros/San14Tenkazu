"""Usage: .venv/Scripts/python save_officers_emulate.py <decoded-stream.bin> <officer-table-offset-hex> <out.json>
(lwc_decode.py <save.s14> <decoded-stream.bin> produces the stream; the table offset is where int32 10, int32 count begins.)
Run the game's own officer deserializer (vtable+0x28, 0x2390B0) over a decompressed save stream in Unicorn.
Read-only on files; never touches a live game."""
import json, struct, sys
from pathlib import Path
REVERSE = Path(__file__).resolve().parent
sys.path.insert(0, str(REVERSE))
if '--tenkazu' not in sys.argv: sys.argv.append('--tenkazu')
from mod_patch_isolation import FrozenMemory, HERE, Oracle
from oracle import OracleError

SERIALIZER = 0x2390B0
OFFICER_TABLE = 0x73A50
STRIDE = 0x1F0

def main(stream_path, table_offset, out_path):
    data = Path(stream_path).read_bytes()
    count = struct.unpack_from('<I', data, table_offset)[0]
    pos = table_offset + 4
    g = FrozenMemory(HERE / '.cache/frozen.pkl')
    o = Oracle(g)
    buf = o.alloc(len(data))
    o.mu.mem_write(buf, data)
    stream = o.alloc(0x100)
    o.mu.mem_write(stream, bytes(0x100))
    o.mu.mem_write(stream + 0x20, struct.pack('<I', 1))            # read mode
    o.mu.mem_write(stream + 0x38, struct.pack('<Q', buf + len(data)))
    o.mu.mem_write(stream + 0x40, struct.pack('<Q', buf))
    o.mu.mem_write(stream + 0x88, struct.pack('<I', 0x66))         # file version
    person = o.q(g.base - 0x290 + OFFICER_TABLE + 1 * 8)
    records, failures = [], []
    for i in range(count):
        o.mu.mem_write(stream + 0x30, struct.pack('<Q', buf + pos))
        o.mu.mem_write(stream + 0x50, struct.pack('<I', 0))
        try:
            ok = o.call(SERIALIZER, person, stream, read_after=[(person, STRIDE), (stream, 0x100)]) & 0xFF
        except OracleError as e:
            failures.append(dict(index=i, pos=pos, error=str(e))); break
        rec, st = o.after
        cursor = struct.unpack_from('<Q', st, 0x30)[0] - buf
        error = struct.unpack_from('<I', st, 0x50)[0]
        if not ok or error or cursor <= pos:
            failures.append(dict(index=i, pos=pos, ok=ok, error=error, cursor=cursor)); break
        records.append(dict(index=i, start=pos, end=cursor, record=rec.hex()))
        pos = cursor
    marker = struct.unpack_from('<I', data, pos)[0] if pos + 4 <= len(data) else None
    summary = dict(count=count, parsed=len(records), end=pos, end_marker=marker, failures=failures)
    Path(out_path).write_text(json.dumps(dict(summary=summary, records=records)), encoding='utf-8')
    print(json.dumps(summary))

if __name__ == '__main__':
    main(sys.argv[1], int(sys.argv[2], 16), sys.argv[3])
