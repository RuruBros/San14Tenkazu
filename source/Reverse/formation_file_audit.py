"""Check formation fields on private decoded copies. Never writes an installed game file."""
import argparse
import hashlib
import json
import struct
import tempfile
from pathlib import Path

from lwc_decode import decode


FIELDS = {"mobility": 0x6F, "attack": 0x73, "siege": 0x74, "break": 0x75, "defense": 0x76}
HEADER = struct.pack("<II", 11, 21)
NEXT_TABLE = struct.pack("<II", 12, 201)
STRIDE = 152


def locate(data):
    start = data.find(HEADER)
    candidates = []
    while start >= 0:
        first = start + len(HEADER)
        if data.startswith(NEXT_TABLE, first + 21 * STRIDE):
            candidates.append(first)
        start = data.find(HEADER, start + 1)
    if len(candidates) != 1:
        raise ValueError("Expected exactly one CE formation table")
    return candidates[0]


def digest(data):
    return hashlib.sha256(data).hexdigest()


def audit(path, reference):
    raw = path.read_bytes()
    if path.suffix.lower() == ".decoded":
        data = raw
    else:
        if raw[:19] != struct.pack("<I", 0x66) + b"SN14SCEXVER0001":
            raise ValueError("Unrecognized CE fixed-data header")
        data = decode(raw, raw.index(b"LWC\x1a"))
    start = locate(data)
    fields = {(i, field): start + i * STRIDE + offset
              for i in range(1, 21) for field, offset in FIELDS.items()}
    if set(reference) != set(fields):
        raise ValueError("Incomplete comparison profile")
    changed = bytearray(data)
    differences = []
    for (record, name), at in fields.items():
        if data[at] != reference[record, name]:
            differences.append(dict(record=record, field=name, profile=reference[record, name], file=data[at]))
        changed[at] = data[at] - 1 if data[at] else 1
    with tempfile.TemporaryDirectory() as folder:
        copy = Path(folder, "formation.decoded")
        copy.write_bytes(changed)
        readback = bytearray(copy.read_bytes())
        if locate(readback) != start or readback != changed:
            raise ValueError("Decoded copy readback failed")
        actual = {i for i, (old, new) in enumerate(zip(data, readback)) if old != new}
        if len(readback) != len(data) or actual != set(fields.values()):
            raise ValueError("Unselected bytes changed")
        for at in actual:
            readback[at] = data[at]
        copy.write_bytes(readback)
        if copy.read_bytes() != data or path.read_bytes() != raw:
            raise ValueError("Restoration or source preservation failed")
    return dict(input=str(path), inputSha256=digest(raw), decodedSha256=digest(data), offset=start,
                verifiedFields=len(fields), comparedWithProfile=len(reference), differences=differences)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("files", nargs="+", type=Path)
    args = parser.parse_args()
    if args.output.resolve() in {p.resolve() for p in [*args.files, args.profile]}:
        raise ValueError("Report must be separate from inputs")
    baseline = json.loads(args.profile.read_text(encoding="utf-8-sig"))
    reference = {(d["Key"]["Record"], d["Key"]["Field"]): d["Value"] for d in baseline["Defaults"]
                 if d["Key"]["Table"] == "formation"}
    result = dict(gameAccess=False, testedRepresentation="decoded CE master data",
                  profileSha256=digest(args.profile.read_bytes()), files=[audit(p, reference) for p in args.files])
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
