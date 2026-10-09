"""Reject false shader-origin success; all execution is in isolated memory."""
from pathlib import Path
from types import SimpleNamespace
import unittest

from image import Image
from season_shader_origin import audit, device_methods, emulate


class ShaderOriginTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.image = Image()
        cls.sdk = Path('C:/Program Files (x86)/Windows Kits/10/Include/10.0.26100.0/um/d3d11.h').read_text(
            encoding='utf-8-sig')

    def changed(self, rva, replacement):
        data = bytearray(self.image.img)
        data[rva:rva + len(replacement)] = replacement
        return SimpleNamespace(img=bytes(data), base=self.image.base)

    def test_real_saved_path_and_failures_never_establish_live_identity(self):
        result = audit(self.image, self.sdk)
        self.assertEqual(9, len(result['cases']))
        self.assertTrue(result['gpuCreationAndReflectionShareInputs'])
        self.assertEqual('0a233719-3960-4578-9d7c-203b8b1d9cc1', result['reflectionIid'])
        for key in ('gameBytecodeIdentityVerified', 'actualDrawShaderVerified',
                    'callbackBootstrapVerified', 'readyForNextGameTrial', 'productionReady',
                    'gameProcessAccess', 'liveHookInstalled', 'gameRenderModified'):
            self.assertIs(False, result[key])

    def test_divergent_reflection_pointer_is_rejected_by_execution(self):
        # mov rdx,rsi -> mov rdx,rdi: pass byte length instead of bytecode pointer.
        self.assertEqual(bytes.fromhex('488bd6'), self.image.img[0x94cb9a:0x94cb9d])
        with self.assertRaisesRegex(ValueError, 'Creation and reflection inputs differ'):
            emulate(self.changed(0x94cb9a, bytes.fromhex('488bd7')), 'reflection-entry')

    def test_divergent_reflection_length_is_rejected_by_execution(self):
        # mov r8,rdi -> mov r8,rsi: pass bytecode pointer instead of byte length.
        self.assertEqual(bytes.fromhex('4c8bc7'), self.image.img[0x94cb92:0x94cb95])
        with self.assertRaisesRegex(ValueError, 'Creation and reflection inputs differ'):
            emulate(self.changed(0x94cb92, bytes.fromhex('4c8bc6')), 'reflection-entry')

    def test_failed_creation_cannot_be_promoted_to_reflection(self):
        # Invert the original HRESULT success branch; the fake device returns failure.
        self.assertEqual(0x79, self.image.img[0x94cb57])
        with self.assertRaisesRegex(ValueError, 'Creation failure did not return null'):
            emulate(self.changed(0x94cb57, b'\x78'), 'creation-failure')

    def test_altered_image_and_sdk_creation_slot_are_rejected(self):
        with self.assertRaisesRegex(ValueError, 'image hash'):
            audit(SimpleNamespace(img=b'changed image'), self.sdk)
        with self.assertRaisesRegex(ValueError, 'metadata base'):
            audit(SimpleNamespace(img=self.image.img, base=self.image.base + 4096,
                                  u32=self.image.u32, u64=self.image.u64), self.sdk)
        with self.assertRaisesRegex(ValueError, 'creation slot'):
            device_methods(self.sdk.replace('*CreatePixelShader ', '*ChangedPixelShader '))


if __name__ == '__main__':
    unittest.main()
