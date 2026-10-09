"""Bounded signed-byte rules read from the pinned game's CSV loader.

Object key: inline loop at F51137..F51172.
Group key: ABC290, called at F5111A, adds the first signed byte once more.
These keys are distinct; no general KTID hashing rule is asserted.
"""


def name_hash(data,count=None,group=False):
    if count is None:count=len(data)
    if not 0<=count<=len(data):raise ValueError('Hash byte count is outside the snapshot')
    result=(data[0] if data[0]<128 else data[0]-256) if group and data else 0
    power=1
    for value in data[:count]:
        if value==0:break
        power=power*31&0xFFFFFFFF
        result=(result+(value if value<128 else value-256)*power)&0xFFFFFFFF
    return result&0xFFFFFFFF
