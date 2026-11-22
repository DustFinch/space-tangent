"""Keep the published historical-input inventory linked to live test contracts."""
import json
from pathlib import Path, PurePosixPath
import re
import unittest


class HistoricalInputInventoryTests(unittest.TestCase):
    def test_every_case_has_a_live_source_and_explicit_provenance_status(self):
        app = Path(__file__).resolve().parents[2]
        data = json.loads((app/'docs/optional-test-inputs.json').read_text())
        cases = data['cases']
        self.assertEqual(data['baselineCases'], len(cases))
        self.assertEqual(len(cases), len({row['test'] for row in cases}))
        remaining = sum(row['status'] == 'historical-input-unavailable' for row in cases)
        self.assertEqual(data['remainingCases'], remaining)
        self.assertEqual(data['executedAfterRecovery'], len(cases)-remaining)
        statuses = {'historical-input-unavailable', 'upstream-capture', 'regenerated-astrometry', 'original-plus-regenerated'}
        for row in cases:
            with self.subTest(case=row['test']):
                root = app/'deps/space-tangent-core' if row['repository'] == 'core' else app
                source = PurePosixPath(row['source'])
                self.assertFalse(source.is_absolute())
                self.assertNotIn('..', source.parts)
                self.assertEqual('tests', source.parts[0])
                text = (root/row['source']).read_text(encoding='utf-8-sig')
                method = row['test'].rsplit('.', 1)[1]
                self.assertRegex(text, r'\b'+re.escape(method)+r'\s*\(')
                self.assertIn(row['status'], statuses)
                self.assertTrue(row['evidenceLimit'])
                self.assertTrue(row['nextAction'])
                self.assertTrue(set(row['variablesNotInProfile']) <= set(row['requiredVariables']))
                for variable in row['requiredVariables']:
                    self.assertRegex(variable, r'^SPACETANGENT_[A-Z0-9_]+$')
                self.assertNotRegex(json.dumps(row), r'[A-Z]:[\\/]|/Users/|/home/')


if __name__ == '__main__':
    unittest.main()
