import copy
import unittest
from verify_provocation_hold import sha, verify

class HeldProofTests(unittest.TestCase):
    def fixture(self):
        original = bytearray([0xA5] * 0x200)
        original[0x25] = 0; original[0x3C] = 255; original[0x3E:0x40] = b'\0\0'
        held = bytearray(original); held[0x25] = 12; held[0x3C] = 27; held[0x3E:0x40] = b'\x05\0'
        decreased = bytearray(held); decreased[0x25] = 11
        plan = dict(beforeHex=original.hex(), heldHex=held.hex(), sourceSlot=5, address=100, nativeReplay=True, liveNativeCall=False)
        receipt = dict(plan=plan, records=[dict(address=100, sha256=sha(original))], arrays={'array':sha(b'a')})
        native = dict(before=original.hex(), after=held.hex(), immunity={'21':0,'24':0},
                      consumer=dict(matchingTargetPassed=True, wrongTargetRejected=True))
        def row(stage, value): return dict(stage=stage, actualHex=value.hex(), expectedHex=value.hex())
        applied = [row('before-apply', original), row('held-readback', held)]
        finished = [row('decreased-readback', decreased), row('undo-readback', held), row('restored-readback', original),
                    dict(stage='record-final',address=100,matches=True,beforeSha256=sha(original),afterSha256=sha(original)),
                    dict(stage='array-final',name='array',matches=True,beforeSha256=sha(b'a'),afterSha256=sha(b'a'))]
        return [receipt,native,applied,finished,dict(confirmed=True, observed='visible')]

    def test_complete(self):
        self.assertEqual([0,12,11,12,0], verify(*self.fixture())['durationSequence'])

    def test_requires_user_confirmation_and_both_native_target_outcomes(self):
        for mutation in ('confirmation','native-target'):
            data=self.fixture()
            if mutation=='confirmation': data[4]['confirmed']=False
            else: data[1]['consumer']['wrongTargetRejected']=False
            with self.subTest(mutation=mutation),self.assertRaises(ValueError): verify(*data)

    def test_cannot_hide_neighbor_corruption_in_claimed_expected_bytes(self):
        data=self.fixture(); row=data[2][1]; value=bytearray.fromhex(row['actualHex']); value[0x3D]=0
        row['actualHex']=row['expectedHex']=value.hex()
        with self.assertRaises(ValueError): verify(*data)

    def test_requires_undo_and_full_global_comparison(self):
        for mutation in ('undo','record','array'):
            data=self.fixture()
            data[3]=[row for row in data[3] if row['stage']!={'undo':'undo-readback','record':'record-final','array':'array-final'}[mutation]]
            with self.subTest(mutation=mutation),self.assertRaises(ValueError): verify(*data)

if __name__=='__main__': unittest.main()
