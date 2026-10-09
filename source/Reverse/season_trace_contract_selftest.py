"""Offline tests; do not open or modify the running game."""
import copy
import struct
import unittest
from season_trace_contract import TraceLayout, HEADER_SIZE, RECORD_SIZE, CANARY, check_identity, restore_slots
from graphics_probe import observe
from game import BASE_POINTER_RVA, BASE_ADJUST


def snapshot(year=194,month=10,day=1):
    return dict(pid=27992,exe='0x10000',world='0x20000',state='0x30000',
                manager='0x40000',manager_mode=0,manager_scene='0x50000',
                driver='0x60000',device='0x70000',immediate='0x80000',
                date=[year,month,day,0],clock=struct.pack('<HBBB',year,month,day,0).hex())


class FakeFrame:
    def __init__(self):
        self.g=self;self.slots={1:101,2:102};self.writes=[];self.protections=[]
        self.fail_write=None;self.fail_protect=None;self.bad_readback=None
    def q(self,address):return self.slots.get(address)
    def write(self,address,data):
        self.writes.append(address)
        if address==self.fail_write:raise OSError('write rejected')
        self.slots[address]=struct.unpack('<Q',data)[0]+int(address==self.bad_readback)
    def protect(self,address,size,value):
        self.protections.append(address)
        if address==self.fail_protect:raise OSError('protection rejected')


class ContractTests(unittest.TestCase):
    def test_full_allocation_includes_header_and_guard(self):
        layout=TraceLayout(8192)
        self.assertEqual(48,HEADER_SIZE)
        self.assertEqual(0x10000+48+8192*344+len(CANARY),layout.allocation_size)
        layout.check_range(layout.records_end-RECORD_SIZE,RECORD_SIZE)
        layout.check_range(layout.records_end,len(CANARY))
        for offset,size in ((-1,1),(0,-1),(layout.allocation_size,1),(layout.allocation_size+1,0),(0,layout.allocation_size+1)):
            with self.assertRaises(ValueError):layout.check_range(offset,size)

    def test_invalid_capacities_are_rejected_before_allocation(self):
        for capacity in (0,-1,8193,0x100000000,True,1.5):
            with self.assertRaises(ValueError):TraceLayout(capacity)

    def test_native_count_and_capacity_contract(self):
        layout=TraceLayout(8192)
        def header(count,capacity=8192,storage=8192):return struct.pack('<4Q4I',0,0,0,0,0,count,capacity,storage)
        self.assertEqual((0,False),layout.decode_count(header(0)))
        self.assertEqual((8192,True),layout.decode_count(header(8192)))
        for value in (header(8193),header(0xffffffff),header(0,8191),header(0,storage=0),bytes(47)):
            with self.assertRaises(ValueError):layout.decode_count(value)

    def test_valid_date_progress_and_backward_load_change_only_calendar(self):
        baseline=snapshot(194,12,30)
        for current in (snapshot(195,1,1),snapshot(193,3,1)):
            self.assertTrue(check_identity(baseline,current,check_clock=False))
            with self.assertRaises(ValueError):check_identity(baseline,current)
        self.assertFalse(check_identity(baseline,copy.deepcopy(baseline),check_clock=False))

    def test_invalid_calendar_is_not_accepted_as_progress(self):
        for year,month,day in ((194,0,1),(194,13,1),(194,1,0),(194,1,31),(100,1,1),(400,1,1)):
            with self.assertRaises(ValueError):check_identity(snapshot(),snapshot(year,month,day),check_clock=False)
        state=snapshot();state['date'][2]=2
        with self.assertRaises(ValueError):check_identity(snapshot(),state,check_clock=False)

    def test_scene_device_process_world_change_requires_new_connection(self):
        for key in ('pid','exe','world','state','manager','manager_scene','driver','device','immediate'):
            current=snapshot();current[key]=current[key]+1 if key=='pid' else '0xffff'
            with self.subTest(key=key),self.assertRaises(ValueError):check_identity(snapshot(),current,check_clock=False)
        current=snapshot();current['manager_mode']=1
        with self.assertRaises(ValueError):check_identity(snapshot(),current,check_clock=False)

    def test_world_observation_rereads_global_instead_of_cached_attachment(self):
        class Memory:
            pid=27992;exe=0x10000;base=0xdead00
            def q(self,address):
                return {self.exe+BASE_POINTER_RVA:0x20000,0x20000+BASE_ADJUST+0x86f58:0x30000,
                        self.exe+0x1b9fb70:0x40000,0x40000+0x210:0x50000}.get(address)
            def read(self,address,size):
                return {0x30000+0x34:bytes.fromhex('c2000a0100'),0x40000+8:bytes(4)}.get(address)
        game=Memory();state=observe(game)
        self.assertEqual(hex(0x20000+BASE_ADJUST),state['world'])
        self.assertEqual('0x30000',state['state']);self.assertEqual(0xdead00,game.base)

    def test_successful_restore_requires_readback_for_every_slot(self):
        frame=FakeFrame();errors=restore_slots(frame,[(1,11,101,4),(2,12,102,4)])
        self.assertFalse(errors);self.assertEqual({1:11,2:12},frame.slots)
        self.assertEqual([2,1],frame.protections)

    def test_restore_continues_after_individual_write_or_protection_failure(self):
        for failure in ('fail_write','fail_protect','bad_readback'):
            frame=FakeFrame();setattr(frame,failure,2)
            errors=restore_slots(frame,[(1,11,101,4),(2,12,102,4)])
            self.assertTrue(errors);self.assertEqual(11,frame.slots[1]);self.assertEqual([2,1],frame.protections)

    def test_unexpected_dispatch_is_not_overwritten(self):
        frame=FakeFrame();frame.slots[2]=999
        errors=restore_slots(frame,[(1,11,101,4),(2,12,102,4)])
        self.assertTrue(errors);self.assertEqual(999,frame.slots[2]);self.assertNotIn(2,frame.writes)
        self.assertEqual(11,frame.slots[1])

    def test_already_restored_slot_is_verified_without_another_write(self):
        frame=FakeFrame();frame.slots={1:11,2:12}
        self.assertFalse(restore_slots(frame,[(1,11,101,4),(2,12,102,4)]));self.assertFalse(frame.writes)


if __name__=='__main__':unittest.main(verbosity=2)
