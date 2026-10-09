"""Actual early-self-restoration regression plus foreign/incomplete receipts."""
import unittest
from season_dispatch_receipt import verify_receipt

class ReceiptTests(unittest.TestCase):
    def setUp(self):
        self.control=(0x111,0x222,0x333,0x444,0x555,0x666,1000,2,0,0x777,0x888,0x999,0,0,42,1,bytes(16))
        self.args=dict(original=0x111,slot=0x222,installed=0x333,driver_vtable=0x444,current_pointer=0x111,protection=2)
    def test_native_restored_before_controller_readback_is_success(self):
        self.assertTrue(all(verify_receipt(self.control,**self.args).values()))
    def test_installed_or_foreign_pointer_is_not_restored(self):
        for pointer in (0x333,0xdead):
            with self.assertRaises(ValueError):verify_receipt(self.control,**dict(self.args,current_pointer=pointer))
    def test_incomplete_failed_or_wrong_context_receipts_are_rejected(self):
        for index,value in ((0,0xdead),(7,0),(7,1),(8,-1),(9,0),(10,0),(11,0),(12,1),(13,1),(14,0),(15,0)):
            changed=list(self.control);changed[index]=value
            with self.assertRaises(ValueError):verify_receipt(tuple(changed),**self.args)
    def test_pending_page_protection_is_not_complete(self):
        with self.assertRaises(ValueError):verify_receipt(self.control,**dict(self.args,protection=4))

if __name__=='__main__':unittest.main()
