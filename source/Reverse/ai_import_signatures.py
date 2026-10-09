# Optional local SDK declaration evidence; no inferred signatures for unknown imports.
import hashlib
from pathlib import Path
import re


def load(path):
    path=Path(path);raw=path.read_bytes();text=raw.decode('utf-8-sig')
    matches=list(re.finditer(r'WINBASEAPI\s+VOID\s+WINAPI\s+EnterCriticalSection\s*\(\s*_Inout_\s+LPCRITICAL_SECTION\s+lpCriticalSection\s*\)\s*;',text))
    if len(matches)!=1:raise ValueError('Expected the exact one-parameter EnterCriticalSection SDK declaration')
    match=matches[0]
    return {('kernel32.dll','EnterCriticalSection'):dict(registerParameters=['rcx'],stackParameters=[],
        source=dict(path=str(path),sha256=hashlib.sha256(raw).hexdigest(),line=text[:match.start()].count('\n')+1,
                    declaration=match.group(0)),
        scope='declared Windows x64 import parameters; values left in other volatile registers are not declared arguments')}
