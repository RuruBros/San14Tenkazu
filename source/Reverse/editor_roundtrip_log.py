"""Independently verify complete-editor JSONL bytes; never access a game process."""
import argparse
import hashlib
import json
from pathlib import Path

NOVEMBER = "782CF229A56CDDE2B95399E0E7536740B3F5E96BEA702679365E4DAD061ABDCE"


def require(condition, reason):
    if not condition:
        raise ValueError(reason)


def digest(data):
    return hashlib.sha256(data).hexdigest().upper()


def encode(value, size):
    return int(value).to_bytes(size, "little", signed=value < 0)


def verify_rows(rows, native):
    manifests = [r for r in rows if r.get("stage") == "target-manifest"]
    require(len(manifests) == 1, "Expected one target manifest")
    manifest = manifests[0]
    require(manifest["schemaVersion"] == 1 and manifest["exeHash"].upper() == NOVEMBER, "Unknown schema/build")
    cases = {c["key"]: c for c in manifest["trials"]}
    require(len(cases) == len(manifest["trials"]), "Duplicate trial keys")
    seeded = []
    for case in cases.values():
        if case.get("seededProvocation", False):
            require(case["key"] == "unit-25" and len(case["steps"]) == 2, "Unknown injected fixture")
            address = None
            for step, values in zip(case["steps"], ((0, 1), (1, 0)), strict=True):
                require(len(step) == 1, "Injected fixture must affect one unit")
                edit = step[0]
                require(edit["kind"] == "Unit" and not edit.get("holders") and len(edit["changes"]) == 1,
                        "Injected fixture must change only the duration byte")
                change = edit["changes"][0]
                require((change["offset"], change["size"], change["before"], change["after"]) == (0x25, 1, *values),
                        "Injected duration must be exactly 0->1->0")
                require(address is None or address == edit["address"], "Injected fixture changed units")
                address = edit["address"]
            seeded.append(case["key"])
    groups = {}
    for row in rows:
        if "records" in row and "key" in row:
            require(row["key"] in cases, "Unexpected trial")
            groups.setdefault(row["key"], []).append(row)
    if manifest["gameAccess"]:
        worlds = {r["key"]: r for r in rows if r.get("stage") == "trial-world"}
        require(set(groups).issubset(worlds), "Trial world/paused evidence missing")
        for key in groups:
            world = worlds[key]
            require(world["date"] == manifest["date"] and world["hour"] == manifest["hour"]
                    and world["playerForce"] == manifest["playerForce"]
                    and world["pace"] == "Paused before thread freeze", "Game date/pace/player changed")
    verified, recovered_incomplete = [], []
    for key, evidence in groups.items():
        case = cases[key]
        require(evidence[0]["stage"] == "prepared", f"{key}: no durable baseline")
        baseline = {r["address"]: bytes.fromhex(r["before"]) for r in evidence[0]["records"]}
        require(len(baseline) == len(evidence[0]["records"]), f"{key}: duplicate record")
        expected = dict(baseline)
        allowed = {address: set() for address in baseline}
        for step in case["steps"]:
            for edit in step:
                address = edit["address"]
                require(address in baseline, f"{key}: edited record absent")
                for change in edit["changes"]:
                    offset, size = change["offset"], change["size"]
                    kind = edit["kind"]
                    if kind == "PlayRecord":
                        require((offset, size) in {(0x19C, 4), (0x1A0, 4), (0x274, 1)}, "Wrong play field")
                    else:
                        name = kind.lower() if kind in ("Officer", "Force") else kind
                        versions = native["records"][name]
                        require(all(v["fields"].get(hex(offset)) == size for v in versions), f"Wrong Native field width: {kind}+{offset:X}")
                    allowed[address].update(range(offset, offset + size))
                    if kind == "Officer" and offset in range(0x118, 0x12A, 2):
                        slot = (offset - 0x118) // 2
                        original = baseline[address]
                        if int.from_bytes(original[0x184:0x186], "little") >> slot & 1:
                            allowed[address].update(range(0x184, 0x186))
                        time = 0x186 + slot * 2
                        if any(original[time:time + 2]):
                            allowed[address].update(range(time, time + 2))
                if edit["kind"] == "PlayRecord":
                    allowed[address].add(0x274)
                for holder in edit.get("holders") or []:
                    require(holder["officer"] in baseline, "Holder baseline absent")
                    allowed[holder["officer"]].update(range(0x184, 0x186))

        def apply(step, undo=False):
            for edit in step:
                address = edit["address"]
                before = expected[address]
                after = bytearray(before)
                for change in edit["changes"]:
                    offset, size = change["offset"], change["size"]
                    value = change["before"] if undo else change["after"]
                    if not undo:
                        require(before[offset:offset + size] == encode(change["before"], size), f"{key}: wrong original value")
                    after[offset:offset + size] = encode(value, size)
                    if edit["kind"] == "Officer" and offset in range(0x118, 0x12A, 2) and before[offset:offset + size] != encode(value, size):
                        slot = (offset - 0x118) // 2
                        marks = int.from_bytes(after[0x184:0x186], "little") & ~(1 << slot)
                        after[0x184:0x186] = encode(marks, 2)
                        after[0x186 + slot * 2:0x188 + slot * 2] = b"\0\0"
                if edit["kind"] == "PlayRecord":
                    after[0x274] = 1
                expected[address] = bytes(after)
                for holder in edit.get("holders") or []:
                    address = holder["officer"]
                    image = bytearray(expected[address])
                    offset = 0x118 + holder["slot"] * 2
                    if int.from_bytes(image[offset:offset + 2], "little") == holder["personality"]:
                        marks = int.from_bytes(image[0x184:0x186], "little") & ~(1 << holder["slot"])
                        image[0x184:0x186] = encode(marks, 2)
                    expected[address] = bytes(image)

        stages = ["prepared"] + [f"apply-{i}-readback" for i in range(len(case["steps"]))]
        stages += ["changed-readback"] + [f"undo-{i}-readback" for i in reversed(range(len(case["steps"])))]
        stages += ["ordinary-undo-readback", "restored-byte-exact"]
        actual_stages = [r["stage"] for r in evidence]
        full_trial = actual_stages == stages
        recovered_prefix = (actual_stages[-1:] == ["restored-byte-exact"]
                            and actual_stages[:-1] == stages[:len(actual_stages) - 1])
        require(full_trial or recovered_prefix, f"{key}: missing/failed/out-of-order trial stages: {actual_stages}")
        for row in evidence:
            stage = row["stage"]
            if stage.startswith("apply-"):
                apply(case["steps"][int(stage.split("-")[1])])
            if stage.startswith("undo-"):
                apply(case["steps"][int(stage.split("-")[1])], undo=True)
            if stage == "restored-byte-exact":
                expected = dict(baseline)
            require({r["address"] for r in row["records"]} == set(baseline), f"{key}: mixed record scope")
            for record in row["records"]:
                address = record["address"]
                actual = bytes.fromhex(record["actual"])
                require(record["before"].upper() == baseline[address].hex().upper(), f"{key}: baseline changed")
                require(set(record["allowedOffsets"]) == allowed[address], f"{key}: forged byte mask")
                require(digest(actual) == record["sha256"].upper(), f"{key}: wrong readback hash")
                require(actual == expected[address] == bytes.fromhex(record["expected"]), f"{key}: unexpected raw bytes")
                require(all(a == b or i in allowed[address] for i, (a, b) in enumerate(zip(baseline[address], actual))), f"{key}: neighbor overwritten")
        (verified if full_trial else recovered_incomplete).append(key)
    before = {r["address"]: r for r in rows if r.get("stage") == "suite-before"}
    after = {r["address"]: r for r in rows if r.get("stage") == "suite-after"}
    differences, not_compared = [], []
    for address, original in before.items():
        if address not in after:
            not_compared.append(address)
            continue
        result = after[address]
        same = original["sha256"] == result["beforeSha256"] == result["afterSha256"]
        require(result["matches"] == same, "False global restoration result")
        if not same:
            differences.append(address)
    differences.extend(r["address"] for r in rows if r.get("stage") == "suite-extra-record")
    before_arrays = {r["name"]: r for r in rows if r.get("stage") == "suite-array-before"}
    after_arrays = {r["name"]: r for r in rows if r.get("stage") == "suite-array-after"}
    if manifest["gameAccess"]:
        require(set(before_arrays) == {"tile-owners", "tile-counties", "corps-force", "corps-leaders"}, "World arrays missing")
    if before_arrays:
        for name, original in before_arrays.items():
            if name not in after_arrays:
                not_compared.append(name)
                continue
            result = after_arrays[name]
            same = original["sha256"] == result["beforeSha256"] == result["afterSha256"]
            require(result["matches"] == same, "False world-array restoration result")
            if not same:
                differences.append(name)
    finals = [r for r in rows if r.get("stage") in ("all-restored", "incomplete", "cancelled-restored")]
    failures = [r for r in rows if r.get("stage") in ("failed", "recovery-failed")]
    if finals:
        require(len(finals) == 1, "Duplicate final result")
        final = finals[0]
        require(final["passed"] == len(verified) and final["missing"] == len(manifest["missing"]), "Wrong trial counts")
        require(final["snapshotDifferences"] == len(differences), "Wrong global difference count")
        require(not not_compared, "Final global snapshot is incomplete")
    complete = bool(finals and finals[0]["stage"] == "all-restored" and not failures and not differences
                    and not manifest["missing"] and len(verified) == len(cases) and before and set(before) == set(after))
    require(not finals or finals[0]["stage"] != "all-restored" or complete, "False all-restored claim")
    return {"schemaVersion": 1, "validEvidence": True, "gameAccess": manifest["gameAccess"],
            "processId": manifest["processId"], "exeHash": manifest["exeHash"], "verifiedTrials": len(verified),
            "plannedTrials": len(cases), "unavailable": manifest["missing"], "snapshotDifferences": differences,
            "unexecutedTrials": sorted(set(cases) - set(groups)), "unverifiedTrials": sorted(set(cases) - set(verified)),
            "recoveredIncompleteTrials": recovered_incomplete, "snapshotNotComparedCount": len(not_compared),
            "allPlannedCompleted": len(verified) == len(cases), "complete": complete,
            "seededInputTrials": seeded,
            "ordinaryUndoSeparateFromExactTrialRecovery": True, "failures": failures}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--log", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--native", type=Path, default=Path(__file__).resolve().parents[1] / "Tests/Vectors/GameEditorCoverage20261111.json")
    args = parser.parse_args()
    try:
        rows = [json.loads(line) for line in args.log.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
        report = verify_rows(rows, json.loads(args.native.read_text(encoding="utf-8-sig")))
        report["logSha256"] = digest(args.log.read_bytes())
    except Exception as error:
        report = {"validEvidence": False, "complete": False, "error": str(error)}
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps({k: v for k, v in report.items() if k != "unavailable"}, ensure_ascii=False))
    return 0 if report["validEvidence"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
