"""Keep pinned artwork recovery separate from unavailable upstream references."""
import hashlib
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock

spec = importlib.util.spec_from_file_location('culture_recovery', Path(__file__).resolve().parents[1]/'sky/restore_culture_art.py')
cultures = importlib.util.module_from_spec(spec)
spec.loader.exec_module(cultures)


class CultureRecoveryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='culture recovery ')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.source = self.root/'source'
        self.output = self.root/'fresh'
        self.directory = self.source/'fixture'
        self.directory.mkdir(parents=True)
        index = {'id':'fixture', 'constellations':[{'image':{'file':'illustrations/one.webp'}},
                                                 {'image':{'file':'illustrations/missing.webp'}}]}
        self.index = self.directory/'index.json'
        self.index.write_text(json.dumps(index))
        self.payloads = {'fixture/illustrations/one.webp':b'RIFF\x20\x00\x00\x00WEBPExplicitly authored fixture bytes',
                         'LICENSE-AGPL-3.0.txt':b'Fixture notice; not an upstream license copy.'}
        def record(name):
            data=self.payloads[name]
            return {'name':name, 'size':len(data), 'blob':hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()}
        self.manifest = {'version':1,'revision':'b'*40,
            'cultures':[{'id':'fixture','semanticSha256':cultures.semantic_hash(self.index)}],
            'files':[record('fixture/illustrations/one.webp')], 'notice':record('LICENSE-AGPL-3.0.txt'),
            'unavailable':['fixture/illustrations/missing.webp']}
        self.manifest_path = self.root/'manifest.json'

    def recover(self, changed=False):
        self.manifest_path.write_text(json.dumps(self.manifest))
        def reply(url, timeout):
            self.assertEqual(45,timeout)
            name=url.split('/'+'b'*40+'/',1)[1]
            return io.BytesIO(b'changed' if changed else self.payloads[name])
        with mock.patch.object(cultures.urllib.request,'urlopen',side_effect=reply):
            return cultures.restore(self.source,self.output,self.manifest_path)

    def test_recovery_preserves_definitions_and_keeps_upstream_gaps_explicit(self):
        original=self.index.read_bytes()
        root,evidence=self.recover()
        self.assertEqual(original,self.index.read_bytes())
        self.assertEqual(original,(root/'fixture/index.json').read_bytes())
        self.assertTrue(evidence['sourceSubsetComplete'])
        self.assertFalse(evidence['allReferencedArtAvailable'])
        self.assertEqual(['fixture/illustrations/missing.webp'],evidence['unavailableUpstream'])
        self.assertEqual(1,evidence['restoredImages'])

    def test_semantic_change_fails_before_copy_or_download(self):
        self.index.write_text('{"id":"different"}')
        with self.assertRaisesRegex(ValueError,'source definition differs'):
            self.recover()
        self.assertFalse(self.output.exists())

    def test_a_reference_cannot_be_silently_removed_from_the_manifest(self):
        self.manifest['unavailable']=[]
        with self.assertRaisesRegex(ValueError,'account for every'):
            self.recover()
        self.assertFalse(self.output.exists())

    def test_changed_existing_art_is_preserved_and_reported_incomplete(self):
        image=self.directory/'illustrations/one.webp'
        image.parent.mkdir()
        image.write_bytes(b'preserve these different source bytes')
        with self.assertRaisesRegex(ValueError,'Incomplete culture recovery'):
            self.recover()
        self.assertEqual(b'preserve these different source bytes',image.read_bytes())
        self.assertFalse(json.loads((self.output/'provenance.json').read_text())['sourceSubsetComplete'])

    def test_changed_download_never_becomes_a_valid_image(self):
        with self.assertRaisesRegex(ValueError,'Incomplete culture recovery'):
            self.recover(changed=True)
        self.assertFalse((self.output/'cultures/fixture/illustrations/one.webp').exists())

    def test_destination_cannot_mutate_original_input(self):
        self.output=self.source/'nested'
        with self.assertRaisesRegex(ValueError,'separate destination'):
            self.recover()
        self.assertFalse(self.output.exists())

    def test_cli_does_not_report_zero_status_for_known_upstream_gaps(self):
        evidence={'restoredImages':1,'availableImages':1,'unavailableUpstream':['missing']}
        with mock.patch.object(cultures,'restore',return_value=(self.output,evidence)), contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(2,cultures.main([str(self.source),str(self.output)]))


if __name__=='__main__':
    unittest.main()
