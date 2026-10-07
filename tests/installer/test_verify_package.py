"""Host-free verification guard tests; fixtures contain no real ZIP/server/profile data."""
import importlib.util
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location('package_verifier', Path(__file__).with_name('verify-package.py'))
verifier = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verifier)
digest = verifier.digest
safe_path = verifier.safe_path
verify_checksum_sidecar = verifier.verify_checksum_sidecar


class ChecksumSidecarTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='rvt-zip-verifier-')
        self.addCleanup(self.temp.cleanup)
        self.package = Path(self.temp.name) / 'RvtMcp.Setup-v1.1.0-win-x64.zip'
        self.package.write_bytes(b'disposable payload')
        self.sidecar = Path(str(self.package) + '.sha256')
        self.sha256 = digest(self.package.read_bytes())

    def test_matching_sha256_accepts_both_hex_cases(self):
        for value in (self.sha256, self.sha256.lower()):
            with self.subTest(value=value):
                self.sidecar.write_text(f'{value}  {self.package.name}\n', encoding='ascii')
                verify_checksum_sidecar(self.package, self.sha256)

    def test_modified_payload_rejects_checksum(self):
        self.sidecar.write_text(f'{self.sha256}  {self.package.name}', encoding='ascii')
        self.package.write_bytes(b'modified payload')
        with self.assertRaisesRegex(AssertionError, 'checksum mismatch'):
            verify_checksum_sidecar(self.package, digest(self.package.read_bytes()))

    def test_sidecar_for_another_version_rejects_even_with_matching_bytes(self):
        self.sidecar.write_text(f'{self.sha256}  RvtMcp.Setup-v1.0.1-win-x64.zip', encoding='ascii')
        with self.assertRaisesRegex(AssertionError, 'filename/format mismatch'):
            verify_checksum_sidecar(self.package, self.sha256)

    def test_malformed_sidecar_rejects(self):
        for value in ('', self.sha256, f'{self.sha256}  {self.package.name} extra'):
            with self.subTest(value=value):
                self.sidecar.write_text(value, encoding='ascii')
                with self.assertRaises(AssertionError):
                    verify_checksum_sidecar(self.package, self.sha256)

    def test_missing_sidecar_rejects(self):
        with self.assertRaises(FileNotFoundError):
            verify_checksum_sidecar(self.package, self.sha256)


class ArchivePathTests(unittest.TestCase):
    def test_safe_relative_payload_paths(self):
        for name in ('manifest.json', 'server/rvt-mcp.exe', 'plugins/RvtMcp.Plugin.R27.zip'):
            with self.subTest(name=name):
                safe_path(name)

    def test_unsafe_paths_reject(self):
        for name in ('../outside.txt', 'server/../../outside.txt', '/absolute.txt',
                     'C:/outside.txt', 'C:\\outside.txt', '..\\outside.txt'):
            with self.subTest(name=name):
                with self.assertRaises(AssertionError):
                    safe_path(name)


if __name__ == '__main__':
    unittest.main()
