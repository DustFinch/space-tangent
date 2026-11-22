"""Check scientific-input capture without using the network in ordinary CI."""
import hashlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock
import urllib.error

import fetch_survey_sample as survey


class Response(io.BytesIO):
    url = 'https://example.invalid/explicit-test-fixture'
    headers = {'Last-Modified': 'fixture timestamp'}


class SurveyCaptureTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='survey capture ')
        self.addCleanup(self.temp.cleanup)
        self.output = Path(self.temp.name) / 'fresh'
        self.content = b'This is an independently authored orchestration test, not DSS data.'
        self.samples = [('properties', len(self.content), hashlib.sha256(self.content).hexdigest())]

    def fetch(self, content=None):
        return mock.patch.object(survey.urllib.request, 'urlopen',
                                 return_value=Response(self.content if content is None else content))

    def test_pins_original_bytes_and_records_provenance_only_after_success(self):
        with mock.patch.object(survey, 'SAMPLES', self.samples), self.fetch() as fetch:
            root = survey.capture(self.output)
        self.assertEqual(self.output / 'surveys', root)
        self.assertEqual(self.content, (root / 'dss/properties').read_bytes())
        metadata = json.loads((self.output / 'provenance.json').read_text())
        self.assertIn('no claim of byte identity', metadata['provenance'])
        self.assertEqual(self.samples[0][2], metadata['files'][0]['sha256'])
        self.assertEqual(45, fetch.call_args.kwargs['timeout'])

    def test_changed_same_size_bytes_are_rejected(self):
        with mock.patch.object(survey, 'SAMPLES', self.samples), self.fetch(b'x' * len(self.content)):
            with self.assertRaisesRegex(ValueError, 'Upstream bytes changed'):
                survey.capture(self.output)
        self.assertFalse((self.output / 'provenance.json').exists())
        self.assertFalse((self.output / 'surveys/dss/properties').exists())

    def test_oversized_reply_is_rejected_before_writing_it(self):
        with mock.patch.object(survey, 'SAMPLES', self.samples), self.fetch(self.content + b'extra'):
            with self.assertRaisesRegex(ValueError, 'Upstream bytes changed'):
                survey.capture(self.output)
        self.assertFalse((self.output / 'provenance.json').exists())

    def test_previous_capture_is_not_overwritten_or_requested_again(self):
        self.output.mkdir()
        evidence = self.output / 'provenance.json'
        evidence.write_text('retained')
        with self.fetch() as fetch, self.assertRaises(FileExistsError):
            survey.capture(self.output)
        fetch.assert_not_called()
        self.assertEqual('retained', evidence.read_text())

    def test_partial_capture_is_not_a_complete_data_profile(self):
        sample = self.samples[0]
        samples = [sample, ('Norder3/Allsky.jpg', sample[1], sample[2])]
        with mock.patch.object(survey, 'SAMPLES', samples), mock.patch.object(
                survey.urllib.request, 'urlopen', side_effect=[Response(self.content), urllib.error.URLError('fixture failure')]):
            with self.assertRaises(urllib.error.URLError):
                survey.capture(self.output)
        self.assertTrue((self.output / 'surveys/dss/properties').is_file())
        self.assertFalse((self.output / 'provenance.json').exists())


if __name__ == '__main__':
    unittest.main()
