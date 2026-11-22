"""Check a complete pinned capture using labelled synthetic network replies."""
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock
import urllib.error

spec = importlib.util.spec_from_file_location('photograph_capture', Path(__file__).resolve().parents[1] / 'sky/fetch_photographs.py')
photos = importlib.util.module_from_spec(spec)
spec.loader.exec_module(photos)


class PhotographCaptureTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='photograph capture ')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.output = self.root/'fresh'
        self.contents = {'textures.json': b'{"credit":"Independent fixture author"}',
                         'fixture.png': b'\x89PNG\r\n\x1a\nIndependently authored bytes, not an image oracle.'}
        files = [{'name': name, 'size': len(data),
                  'blob': hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()}
                 for name, data in self.contents.items()]
        self.manifest = {'version': 1, 'revision': 'a'*40, 'images': 1, 'files': files}
        self.path = self.root/'source.json'

    def capture(self, reply=None):
        self.path.write_text(json.dumps(self.manifest))
        def fetch(request, timeout):
            self.assertEqual(45, timeout)
            name = request.full_url.rsplit('/', 1)[1]
            return io.BytesIO(reply(name) if reply else self.contents[name])
        with mock.patch.object(photos.urllib.request, 'urlopen', side_effect=fetch):
            return photos.capture(self.output, self.path, workers=1)

    def test_complete_capture_preserves_metadata_and_records_all_source_hashes(self):
        root = self.capture()
        self.assertEqual(self.contents['textures.json'], (root/'textures.json').read_bytes())
        evidence = json.loads((self.output/'provenance.json').read_text())
        self.assertTrue(evidence['complete'])
        self.assertEqual(2, len(evidence['files']))
        self.assertTrue(all(len(item['sha256']) == 64 for item in evidence['files']))

    def test_partial_capture_keeps_original_metadata_but_cannot_pass(self):
        with self.assertRaisesRegex(ValueError, 'Incomplete photograph capture'):
            self.capture(lambda name: b'changed' if name.endswith('.png') else self.contents[name])
        evidence = json.loads((self.output/'provenance.json').read_text())
        self.assertFalse(evidence['complete'])
        self.assertEqual('fixture.png', evidence['failed'][0]['name'])
        self.assertFalse((self.output/'photographs/fixture.png').exists())

    def test_manifest_cannot_escape_the_capture_directory(self):
        self.manifest['files'][1]['name'] = '../fixture.png'
        with self.assertRaisesRegex(ValueError, 'Invalid or duplicate'):
            self.capture()
        self.assertFalse(self.output.exists())

    def test_missing_population_is_rejected_before_download(self):
        self.manifest['images'] = 2
        with self.assertRaisesRegex(ValueError, 'complete image population'):
            self.capture()
        self.assertFalse(self.output.exists())

    def test_existing_evidence_is_preserved(self):
        self.output.mkdir()
        marker = self.output/'preserved'
        marker.write_text('original')
        with self.assertRaises(FileExistsError):
            self.capture()
        self.assertEqual('original', marker.read_text())

    def test_an_offline_source_records_incomplete_provenance(self):
        self.path.write_text(json.dumps(self.manifest))
        with mock.patch.object(photos.urllib.request, 'urlopen', side_effect=urllib.error.URLError('offline fixture')):
            with self.assertRaisesRegex(ValueError, '2 failed files'):
                photos.capture(self.output, self.path, workers=1)
        self.assertFalse(json.loads((self.output/'provenance.json').read_text())['complete'])

    def test_verification_needs_all_original_bytes_and_does_not_use_network(self):
        root = self.capture()
        with mock.patch.object(photos.urllib.request, 'urlopen') as fetch:
            self.assertEqual(1, photos.verify(root, self.path))
        fetch.assert_not_called()
        (root/'fixture.png').write_bytes(b'changed image')
        with self.assertRaisesRegex(ValueError, 'Pinned upstream file changed'):
            photos.verify(root, self.path)


if __name__ == '__main__':
    unittest.main()
