# Read-only snapshots for the save/load checks in notes/ai.md. Every invocation resolves current addresses.
#   python ai_save_probe.py before --officer 동탁
#   python ai_save_probe.py after-tree-load --officer 동탁
# Redirect stdout to .cache/*.json when keeping evidence. Nothing writes to the game or executes code in it.
import argparse
import hashlib
import json
import os
import struct
from datetime import datetime, timezone
from pathlib import Path

import ai
from game import Game

TRAITS = list(range(0x16A, 0x182, 2))  # all twelve; +0x17A is the game's Han inclination


def summarize_trial(folder, cleanup_confirmed, trial_offset=None):
    phases = ["before", "written", "saved", "undone", "loaded", "restored"]
    if cleanup_confirmed:
        phases.append("final")
    records = {phase: json.loads((folder / f"save-{phase}.json").read_text(encoding="utf-8-sig")) for phase in phases}
    first = records["before"]
    for record in records.values():
        for key in ["pid", "file_sha256", "tree_roots"]:
            if record[key] != first[key]:
                raise AssertionError((key, "trial identity changed"))
        if (record["officer"]["id"], record["officer"]["name"]) != (first["officer"]["id"], first["officer"]["name"]):
            raise AssertionError("officer identity changed")
        if record["return_morale"]["game"] != first["return_morale"]["game"]:
            raise AssertionError("unrelated tree threshold changed")
    values = {phase: record["officer"]["traits"] for phase, record in records.items()}
    changed = [key for key in values["before"] if values["before"][key] != values["written"][key]]
    expected = [hex(trial_offset)] if trial_offset is not None else [hex(o) for o in TRAITS if o != 0x17A]
    if changed != expected:
        raise AssertionError((changed, expected, "trial changed offsets differ"))
    for phase in ["saved", "loaded"]:
        if values[phase] != values["written"]:
            raise AssertionError((phase, "trial values differ"))
    for phase in ["undone", "restored"] + (["final"] if cleanup_confirmed else []):
        if values[phase] != values["before"]:
            raise AssertionError((phase, "original values not restored"))
    return {"pid": first["pid"], "officer": first["officer"]["name"], "officer_id": first["officer"]["id"],
            "method": "real-game save -> Undo -> load -> read -> Undo; user performed save/load",
            "cleanup": "user reported overwriting the trial slot" if cleanup_confirmed else "not yet reported",
            "phases": phases, "utc": {phase: record["utc"] for phase, record in records.items()},
            "file_sha256": first["file_sha256"], "changed_offsets": [int(key, 16) for key in changed],
            "cases": [[offset] + [values[phase][hex(offset)] for phase in phases] for offset in TRAITS]}


def officer(game, requested):
    # world + 0x158 is the ID table (bases.py). Scan up to ai.py's lookup limit, 6000; identity +0x10, names +0x12/+0x20.
    candidates = [int(requested)] if requested.isdecimal() else range(6001)
    matches = []
    for id_ in candidates:
        if not 0 <= id_ <= 6000:
            raise SystemExit("장수 ID 범위: 0~6000")
        address = game.q(game.base - 0x290 + 0x158 + id_ * 8)
        if not address:
            continue
        record = game.read(address, 0x182)
        if record is None or struct.unpack_from("<H", record, 0x10)[0] != id_:
            continue
        name = game.utf16(address + 0x12, 7) + game.utf16(address + 0x20, 7)
        if requested.isdecimal() or name == requested:
            matches.append({"id": id_, "name": name, "address": hex(address),
                            "traits": {hex(o): struct.unpack_from("<h", record, o)[0] for o in TRAITS}})
    if len(matches) != 1:
        raise SystemExit(f"장수를 하나로 고르지 못했습니다: {requested} ({len(matches)}명)")
    return matches[0]


def main():
    parser = argparse.ArgumentParser(description="AI 기준값·장수 성향의 저장/로드 읽기 스냅샷")
    parser.add_argument("label", help="시험 단계 이름")
    parser.add_argument("--officer", default="동탁", help="정확한 장수 이름 또는 ID")
    parser.add_argument("--trial-directory", type=Path, help="저장 시험 단계 JSON을 검증하고 요약")
    parser.add_argument("--cleanup-confirmed", action="store_true", help="사용자가 시험 슬롯 덮어 저장을 보고한 경우만 사용")
    parser.add_argument("--trial-offset", type=lambda value: int(value, 0), choices=[0x17A], help="단독 한조 저장 시험의 offset 0x17A")
    parser.add_argument("--vectors", type=Path, help="검증한 단계별 값들을 xUnit 정답 파일로 남김")
    args = parser.parse_args()
    if args.trial_directory:
        result = summarize_trial(args.trial_directory, args.cleanup_confirmed, args.trial_offset)
        text = json.dumps(result, ensure_ascii=False, indent=2) + "\n"
        if args.vectors:
            args.vectors.write_text(text, encoding="utf-8")
        print(text)
        return
    game = Game()
    file = os.path.join(ai.game_dir(), ai.FILE)
    trees = ai.read_file(file)
    live = ai.live_trees(game)
    node, address = trees[1][10132], live[1][1][10132]
    if len(trees[1]) != len(live[1][1]) or node.cls != ai.CONDITION:
        raise SystemExit("조사한 귀환 조건 트리와 다릅니다")
    if struct.unpack("<I", game.read(address + 8, 4))[0] != node.cls:
        raise SystemExit("귀환 노드 클래스가 다릅니다")
    raw = ai.live_params(game, node, address, {})  # Condition terms do not need a dumped image or class metadata.
    first, current = ai.terms(node.params)[0], ai.terms(raw)[0]
    if (first[:4] != (2, 1, 20, 0) or first[:4] != current[:4] or len(first[4]) != 2
            or len(current[4]) != 2 or first[4][0] != current[4][0]):
        raise SystemExit("조사한 귀환 사기 조건과 다릅니다")
    print(json.dumps({"utc": datetime.now(timezone.utc).isoformat(), "label": args.label, "pid": game.pid,
                      "world": hex(game.base - 0x290), "tree_roots": [hex(root) for root, _ in live],
                      "file_sha256": hashlib.sha256(open(file, "rb").read()).hexdigest(),
                      "return_morale": {"anchor": "1:10132:0:1", "node": hex(address),
                                        "file": first[4][1], "game": current[4][1]},
                      "officer": officer(game, args.officer)}, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
