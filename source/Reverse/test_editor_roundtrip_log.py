import copy
import unittest
from editor_roundtrip_log import digest, verify_rows, NOVEMBER


class RoundTripLogTests(unittest.TestCase):
    def fixture(self):
        before = bytes([0] * 0x44 + [10, 0xA5] + [0] * 10)
        changed = bytearray(before)
        changed[0x44] = 11
        changed = bytes(changed)
        case = {"key": "city-44", "steps": [[{"kind": "City", "address": 100, "changes": [
            {"offset": 0x44, "size": 1, "before": 10, "after": 11}], "holders": None}]]}
        rows = [{"stage": "target-manifest", "schemaVersion": 1, "gameAccess": False, "processId": 0,
                 "exeHash": NOVEMBER, "trials": [case], "missing": []},
                {"stage": "suite-before", "address": 100, "sha256": digest(before)}]
        for stage, actual in [("prepared", before), ("apply-0-readback", changed), ("changed-readback", changed),
                              ("undo-0-readback", before), ("ordinary-undo-readback", before), ("restored-byte-exact", before)]:
            rows.append({"stage": stage, "key": "city-44", "records": [{"address": 100, "before": before.hex(),
                         "expected": actual.hex(), "actual": actual.hex(), "sha256": digest(actual), "allowedOffsets": [0x44]}]})
        rows += [{"stage": "suite-after", "address": 100, "beforeSha256": digest(before), "afterSha256": digest(before), "matches": True},
                 {"stage": "all-restored", "passed": 1, "missing": 0, "snapshotDifferences": 0, "cancelled": False}]
        native = {"records": {"City": [{"fields": {"0x44": 1}}, {"fields": {"0x44": 1}}]}}
        return rows, native

    def test_exact_round_trip(self):
        rows, native = self.fixture()
        self.assertTrue(verify_rows(rows, native)["complete"])

    def seeded_fixture(self):
        before = bytearray(0x60)
        before[0x24] = 0x6D
        before[0x26] = 0xA5
        before[0x3C] = 255
        changed = bytearray(before)
        changed[0x25] = 1
        before, changed = bytes(before), bytes(changed)
        def edit(old, new):
            return {"kind": "Unit", "address": 100, "holders": None, "changes": [
                {"offset": 0x25, "size": 1, "before": old, "after": new}]}
        rows = [{"stage": "target-manifest", "schemaVersion": 1, "gameAccess": False, "processId": 0,
                 "exeHash": NOVEMBER, "missing": [], "trials": [{"key": "unit-25", "seededProvocation": True,
                 "steps": [[edit(0, 1)], [edit(1, 0)]]}]},
                {"stage": "suite-before", "address": 100, "sha256": digest(before)}]
        stages = [("prepared", before), ("apply-0-readback", changed), ("apply-1-readback", before),
                  ("changed-readback", before), ("undo-1-readback", changed), ("undo-0-readback", before),
                  ("ordinary-undo-readback", before), ("restored-byte-exact", before)]
        for stage, actual in stages:
            rows.append({"stage": stage, "key": "unit-25", "records": [{"address": 100, "before": before.hex(),
                         "expected": actual.hex(), "actual": actual.hex(), "sha256": digest(actual), "allowedOffsets": [0x25]}]})
        rows += [{"stage": "suite-after", "address": 100, "beforeSha256": digest(before), "afterSha256": digest(before), "matches": True},
                 {"stage": "all-restored", "passed": 1, "missing": 0, "snapshotDifferences": 0, "cancelled": False}]
        return rows, {"records": {"Unit": [{"fields": {"0x25": 1}}, {"fields": {"0x25": 1}}]}}

    def test_injected_input_is_explicit_and_requires_both_apply_readbacks(self):
        rows, native = self.seeded_fixture()
        self.assertEqual(["unit-25"], verify_rows(rows, native)["seededInputTrials"])
        rows.pop(3)
        with self.assertRaises(ValueError):
            verify_rows(rows, native)

    def test_injected_input_cannot_expand_to_target_fields_or_another_unit(self):
        original, native = self.seeded_fixture()
        for mutation in ("different-unit", "target-field", "longer-duration"):
            rows = copy.deepcopy(original)
            if mutation == "different-unit":
                rows[0]["trials"][0]["steps"][1][0]["address"] = 101
            else:
                change = rows[0]["trials"][0]["steps"][0][0]["changes"][0]
                change["offset" if mutation == "target-field" else "after"] = 0x3C if mutation == "target-field" else 2
            with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                verify_rows(rows, native)

    def test_neighbor_corruption_cannot_be_hidden_by_matching_claimed_hash(self):
        rows, native = self.fixture()
        image = rows[3]["records"][0]
        altered = bytearray.fromhex(image["actual"])
        altered[0x45] = 0
        image.update(actual=altered.hex(), expected=altered.hex(), sha256=digest(altered))
        with self.assertRaises(ValueError):
            verify_rows(rows, native)

    def test_wrong_field_width_rejected_by_native_evidence(self):
        rows, native = self.fixture()
        rows[0]["trials"][0]["steps"][0][0]["changes"][0]["size"] = 2
        with self.assertRaises(ValueError):
            verify_rows(rows, native)

    def test_missing_or_forged_restore_is_not_success(self):
        original, native = self.fixture()
        for mutation in ("missing", "wrong-hash", "wrong-before", "broaden-mask"):
            rows = copy.deepcopy(original)
            if mutation == "missing":
                rows.pop(7)
            elif mutation == "wrong-hash":
                rows[7]["records"][0]["sha256"] = "0" * 64
            elif mutation == "wrong-before":
                rows[7]["records"][0]["before"] = "00"
            else:
                rows[3]["records"][0]["allowedOffsets"].append(0x45)
            with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                verify_rows(rows, native)

    def test_failed_suite_can_report_recovered_trial_without_claiming_completion(self):
        rows, native = self.fixture()
        rows[-1] = {"stage": "failed", "error": "injected failure after trial restoration"}
        report = verify_rows(rows, native)
        self.assertEqual(1, report["verifiedTrials"])
        self.assertFalse(report["complete"])

    def test_prewrite_guard_failure_restoration_is_not_a_completed_trial(self):
        rows, native = self.fixture()
        rows = rows[:3] + [rows[7], {"stage": "failed", "error": "prewrite page guard"}]
        rows.append({"stage": "suite-array-before", "name": "tile-owners", "sha256": digest(b"a")})
        report = verify_rows(rows, native)
        self.assertEqual(0, report["verifiedTrials"])
        self.assertEqual(["city-44"], report["recoveredIncompleteTrials"])
        self.assertEqual(2, report["snapshotNotComparedCount"])
        self.assertEqual([], report["snapshotDifferences"])
        self.assertFalse(report["complete"])

    def test_live_evidence_requires_paused_world_and_world_arrays(self):
        rows, native = self.fixture()
        rows[0].update(gameAccess=True, date={"item1": 184, "item2": 2, "item3": 11}, hour=0, playerForce=1)
        with self.assertRaises(ValueError):
            verify_rows(rows, native)
        rows.append({"stage": "trial-world", "key": "city-44", "date": rows[0]["date"], "hour": 0,
                     "playerForce": 1, "pace": "Paused before thread freeze"})
        for name in ("tile-owners", "tile-counties", "corps-force", "corps-leaders"):
            rows.extend([{"stage": "suite-array-before", "name": name, "sha256": digest(b"a")},
                         {"stage": "suite-array-after", "name": name, "beforeSha256": digest(b"a"),
                          "afterSha256": digest(b"a"), "matches": True}])
        self.assertTrue(verify_rows(rows, native)["complete"])
        rows[-1]["afterSha256"] = digest(b"b")
        with self.assertRaises(ValueError):
            verify_rows(rows, native)


if __name__ == "__main__":
    unittest.main()
