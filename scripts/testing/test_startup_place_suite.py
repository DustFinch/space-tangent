"""Late Godot shutdown failures must not pass on an exit-zero status alone."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('startup_place_suite', Path(__file__).with_name('startup_place_suite.py'))
suite = importlib.util.module_from_spec(spec)
spec.loader.exec_module(suite)


class StartupPlaceAcceptanceTests(unittest.TestCase):
    def setUp(self):
        temp = tempfile.TemporaryDirectory(prefix='startup policy ')
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        self.marker = 'SpaceTangent observing place startup policy passed: batch'
        self.sentinel = self.root/'sentinel-places.json'
        self.sentinel.write_bytes(b'preserved preference bytes')
        self.sentinel.with_suffix('.json.before').write_bytes(self.sentinel.read_bytes())
        (self.root/'policy-result.json').write_text(json.dumps({'passed': True, 'normal': False, 'edited': True}))

    def test_clean_batch_requires_matching_bytes_after_native_exit(self):
        self.assertEqual('passed', suite.validate('defaults', self.root, 0, self.marker)['status'])

    def test_exit_zero_cannot_hide_a_missing_marker_error_or_failed_assertion(self):
        for output in ('', self.marker+'\nERROR: late exit failure', self.marker+'\nSCRIPT ERROR: failure'):
            with self.subTest(output=output), self.assertRaises(ValueError):
                suite.validate('defaults', self.root, 0, output)
        (self.root/'policy-result.json').write_text('{"passed":false,"normal":false}')
        with self.assertRaises(ValueError):
            suite.validate('defaults', self.root, 0, self.marker)

    def test_a_marker_cannot_hide_a_failed_native_status(self):
        with self.assertRaises(ValueError):
            suite.validate('defaults', self.root, 1, self.marker)

    def test_a_late_sentinel_write_invalidates_successful_probe_output(self):
        self.sentinel.write_bytes(b'changed after the probe checked its bytes')
        with self.assertRaisesRegex(ValueError, 'after process exit'):
            suite.validate('scenario', self.root, 0, self.marker)

    def test_a_missing_policy_result_cannot_pass(self):
        (self.root/'policy-result.json').unlink()
        with self.assertRaises(FileNotFoundError):
            suite.validate('smoke', self.root, 0, self.marker)

    def test_normal_control_requires_read_edit_and_persistence(self):
        marker = self.marker.replace('batch', 'normal')
        (self.root/'policy-result.json').write_text('{"passed":true,"normal":true,"edited":true}')
        with self.assertRaises(ValueError):
            suite.validate('normal', self.root, 0, marker)
        self.sentinel.write_bytes(b'normal control saved its edit')
        self.assertEqual('passed', suite.validate('normal', self.root, 0, marker)['status'])

    def test_save_and_exit_requires_a_saved_scenario_as_well_as_the_sentinel(self):
        with self.assertRaises(FileNotFoundError):
            suite.validate('save-exit', self.root, 0, self.marker)
        (self.root/'saved.stscene').write_text('{"views":[]}')
        with self.assertRaises(ValueError):
            suite.validate('save-exit', self.root, 0, self.marker)
        (self.root/'saved.stscene').write_text('{"views":[{}]}')
        self.assertEqual('passed', suite.validate('save-exit', self.root, 0, self.marker)['status'])


if __name__ == '__main__':
    unittest.main()
