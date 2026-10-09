from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from ai_import_signatures import load
from ai_attack_boundaries import closure
from test_ai_attack_boundaries import BytesImage
from image import Image

SDK=Path('C:/Program Files (x86)/Windows Kits/10/Include/10.0.26100.0/um/synchapi.h')
DECLARATION='WINBASEAPI\nVOID\nWINAPI\nEnterCriticalSection(\n_Inout_ LPCRITICAL_SECTION lpCriticalSection\n);'


class ImportSignatureTests(unittest.TestCase):
    def profile(self,text=DECLARATION):
        with tempfile.TemporaryDirectory(prefix='ai-signature-test-') as folder:
            path=Path(folder)/'synchapi.h';path.write_text(text,encoding='utf-8');return load(path)

    def test_only_the_exact_declared_import_has_formal_parameter_metadata(self):
        profile=self.profile()
        self.assertEqual({('kernel32.dll','EnterCriticalSection')},set(profile))
        self.assertEqual(['rcx'],profile[('kernel32.dll','EnterCriticalSection')]['registerParameters'])
        with self.assertRaises(ValueError):self.profile(DECLARATION.replace('lpCriticalSection','lpCriticalSection, void *other'))
        with self.assertRaises(ValueError):self.profile(DECLARATION.replace('EnterCriticalSection','UnknownImport'))

    def test_stale_r8_is_preserved_as_a_non_argument_diagnostic(self):
        # Synthetic analyzer program only: r8=node, rcx=0, indirect import call.
        image=BytesImage({0x1000:'4989C831C9FF15F5000000C3'})
        with patch('ai_attack_boundaries.imports',return_value={0x1100:dict(module='KERNEL32.dll',name='EnterCriticalSection')}):
            unknown=closure(image,8,(0x1000,))
            known=closure(image,8,(0x1000,),self.profile())
        self.assertEqual(1,len(unknown['unresolvedBoundaries']))
        self.assertEqual([],known['unresolvedBoundaries'])
        row=known['nonArgumentImportLayouts'][0]
        self.assertEqual({'r8':['node',0]},row['rawAliasCandidates'])
        self.assertEqual({},row['args'])

    def test_unknown_export_is_never_filtered_by_a_different_signature(self):
        image=BytesImage({0x1000:'4989C831C9FF15F5000000C3'})
        with patch('ai_attack_boundaries.imports',return_value={0x1100:dict(module='KERNEL32.dll',name='UnknownImport')}):
            result=closure(image,8,(0x1000,),self.profile())
        self.assertEqual(1,len(result['unresolvedBoundaries']))
        self.assertEqual([],result['nonArgumentImportLayouts'])

    def test_declared_rcx_alias_remains_an_unresolved_boundary(self):
        image=BytesImage({0x1000:'FF15FA000000C3'})
        with patch('ai_attack_boundaries.imports',return_value={0x1100:dict(module='KERNEL32.dll',name='EnterCriticalSection')}):
            result=closure(image,8,(0x1000,),self.profile())
        self.assertEqual(1,len(result['unresolvedBoundaries']))
        self.assertEqual({'rcx':['node',0]},result['unresolvedBoundaries'][0]['args'])
        self.assertEqual([],result['nonArgumentImportLayouts'])

    @unittest.skipUnless(SDK.exists(),'Local SDK declaration unavailable')
    def test_saved_game_code_separates_six_stale_register_layouts(self):
        result=closure(Image(),256,import_signatures=load(SDK))
        self.assertTrue(result['converged'])
        layouts=result['nonArgumentImportLayouts']
        self.assertEqual(6,sum(bool(r['rawAliasCandidates']) for r in layouts))
        self.assertEqual(120,sum(not r['rawAliasCandidates'] for r in layouts))
        self.assertTrue(all(not r['args'] and not r['stackCandidates'] for r in layouts))
        self.assertEqual(1,len(result['unresolvedBoundaries']))
        self.assertEqual(0x32F50C,result['unresolvedBoundaries'][0]['rva'])
        self.assertFalse(any(e['kind']=='readOrModify' for row in result['contexts'] for e in row['accesses']))


if __name__=='__main__':unittest.main()
