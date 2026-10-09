"""Pure validation used before any native observer dispatch.

These checks prevent known buffer/identity/restore errors. They do not prove
that a context can be safely hooked while the game destroys a scene/device.
"""
from dataclasses import dataclass
import struct

HEADER_SIZE = struct.calcsize('<4Q4I')
RECORD_SIZE = 344
CODE_SIZE = 0x10000
MAX_CAPACITY = 8192
CANARY = b'SAN14-SEASON-TRACE-END-GUARD-2026!!'
IDENTITY_KEYS = ('pid', 'exe', 'world', 'state', 'manager', 'manager_mode',
                 'manager_scene', 'driver', 'device', 'immediate')


@dataclass(frozen=True)
class TraceLayout:
    capacity: int

    def __post_init__(self):
        if type(self.capacity) is not int or not 1 <= self.capacity <= MAX_CAPACITY:
            raise ValueError('Trace capacity must be 1..8192')

    @property
    def records_offset(self): return CODE_SIZE + HEADER_SIZE

    @property
    def records_end(self): return self.records_offset + self.capacity * RECORD_SIZE

    @property
    def allocation_size(self): return self.records_end + len(CANARY)

    def check_range(self, offset, size):
        if type(offset) is not int or type(size) is not int or offset < 0 or size < 0 or offset > self.allocation_size - size:
            raise ValueError('Observer access exceeds its complete allocation')

    def decode_count(self, header):
        if len(header) != HEADER_SIZE: raise ValueError('Incomplete trace header')
        *_, enabled, count, capacity, storage_capacity = struct.unpack('<4Q4I', header)
        if capacity != self.capacity or storage_capacity != self.capacity or enabled not in (0, 1) or count > self.capacity:
            raise ValueError('Invalid trace header')
        return count, count == capacity


def validate_snapshot(state):
    date = state.get('date')
    if not isinstance(date, list) or len(date) != 4 or any(type(v) is not int for v in date):
        raise ValueError('Game calendar is unreadable')
    year, month, day, hour = date
    if not (100 < year < 400 and 1 <= month <= 12 and 1 <= day <= 30 and 0 <= hour <= 255):
        raise ValueError('Invalid game calendar')
    if state.get('clock') != struct.pack('<HBBB', *date).hex():
        raise ValueError('Inconsistent game calendar read')
    if state.get('manager_mode') != 0:
        raise ValueError('Loaded scene manager is required')
    for key in IDENTITY_KEYS:
        if key == 'manager_mode': continue
        value = state.get(key)
        if key == 'pid':
            if type(value) is not int or value <= 0: raise ValueError('Process identity is unavailable')
        elif not isinstance(value, str) or not value.startswith('0x') or int(value, 16) <= 0:
            raise ValueError('Scene/device identity is unavailable: ' + key)


def check_identity(baseline, current, check_clock=True):
    validate_snapshot(baseline); validate_snapshot(current)
    changed = [key for key in IDENTITY_KEYS if current[key] != baseline[key]]
    if changed: raise ValueError('Game render identity changed: ' + ', '.join(changed))
    if check_clock and current['clock'] != baseline['clock']:
        raise ValueError('Game calendar changed during a fixed-date probe')
    return current['clock'] != baseline['clock']


def restore_slots(frame, leases):
    """Try every slot, verify readback, and never overwrite an unexpected target.

    Leases are (slot, original, installed, original_protection). This is a
    diagnostic recovery procedure, not a substitute for native ownership.
    """
    errors = []
    for slot, original, installed, protection in reversed(leases):
        try:
            current = frame.g.q(slot)
            if current == installed:
                frame.write(slot, struct.pack('<Q', original))
            elif current != original:
                raise RuntimeError('API slot changed ownership')
            if frame.g.q(slot) != original: raise RuntimeError('API restore readback mismatch')
        except Exception as error:
            errors.append(dict(slot=hex(slot), error=str(error)))
        finally:
            try: frame.protect(slot, 8, protection)
            except Exception as error: errors.append(dict(slot=hex(slot), error=str(error)))
    return errors
