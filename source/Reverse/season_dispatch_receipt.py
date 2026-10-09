"""Validate a retained native completion without assuming an installed pointer persists.

Regression contract for the 2026-10-06 real command probe: the native callback
restored the slot before Python's immediate write readback. An original pointer
and a verified native completion are success, not a write mismatch. No live
installation or retry is performed by this module.
"""
def verify_receipt(control, *, original, slot, installed, driver_vtable, current_pointer, protection):
    if len(control)!=17 or control[:4]!=(original,slot,installed,driver_vtable):
        raise ValueError('Native control belongs to another installation')
    if control[7]!=2:raise ValueError('Native callback has not completed')
    if control[8]!=0 or control[15]!=1:raise ValueError('Native capability/self-restoration failed')
    if not all(control[index]>0 for index in (9,10,11,14)) or control[12]&1 or control[13]!=0:
        raise ValueError('Native context/device capability is invalid')
    if current_pointer!=original:raise ValueError('Original command pointer is not restored')
    if protection!=2:raise ValueError('Read-only command-page protection is not restored')
    return dict(nativeCompleted=True,capabilityConfirmed=True,originalDispatchConfirmed=True,pageProtectionConfirmed=True)
