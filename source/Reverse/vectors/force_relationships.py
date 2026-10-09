# Complete NPC relationship-body validation, with both directional fields and the native return value.
import struct
from context import *
from corps_commands import generate as collect

COVER=[0x2700A0,0x2586E0,0x2AD610]


def fixtures(ctx,snapshot,source,destination):
    a,b=[i for i in range(1,46) if ctx.u16(FORCES,i,0x10)][:2]
    cases=[]
    def case(name,left=80,right=80,delta=-5,notify=0,suppress=0,first=a,second=b,extra=()):
        cases.append(dict(name=name,source=source,destination=destination,mode=0,a=[hex(FORCES),first] if first is not None else None,
            b=[hex(FORCES),second] if second is not None else None,delta=delta,notify=notify,suppress=suppress,
            pokes=[['worldstate',0,0x3A,'00'],[hex(FORCES),a,0x13+b,bytes([left]).hex()],
                [hex(FORCES),b,0x13+a,bytes([right]).hex()]]+list(extra)))
    case('same_band_decrease')
    case('same_band_increase',delta=5,notify=1)
    case('asymmetric_fields',left=80,right=40,delta=-15)
    case('symmetric_lower_clamp',left=2,right=0,delta=-5)
    case('symmetric_upper_clamp',left=198,right=200,delta=5)
    case('zero_delta',delta=0)
    case('npc_notification_flags',notify=1,suppress=1)
    case('same_force_guard',second=a)
    case('null_first_guard',first=None)
    case('null_second_guard',second=None)
    case('invalid_first_guard',extra=[[hex(FORCES),a,0x10,'0000']])
    for threshold in [1,30,50,70,100]:
        case(f'cross_up_{threshold}',left=threshold-1,right=threshold-1,delta=1)
        case(f'cross_down_{threshold}',left=threshold,right=threshold,delta=-1)
    case('signed_add_overflow',left=1,right=100,delta=2147483647)
    return cases


def generate(ctx):return collect(ctx,fixtures,0x2700A0,'force_relationships')
