"""Verify HYG adaptation with independently authored rows, not science oracles."""
import csv
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock

spec = importlib.util.spec_from_file_location('spatial_prepare', Path(__file__).resolve().parents[1] / 'sky/prepare_spatial.py')
spatial = importlib.util.module_from_spec(spec)
spec.loader.exec_module(spatial)


class SpatialPreparationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='spatial adaptation ')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.output = self.root / 'fresh'
        self.source = self.root / 'hyg.csv'
        self.row = dict(id='1', hip='2', proper='Fixture', bf='', gl='', dist='5', x='3', y='4', z='0',
                        vx='0.000001', vy='-0.000002', vz='0', mag='4', ci='0.4', lum='1')

    def prepare(self, *rows):
        text = io.StringIO(newline='')
        writer = csv.DictWriter(text, list(self.row))
        writer.writeheader()
        writer.writerows(rows)
        self.source.write_text(text.getvalue(), encoding='utf-8', newline='')
        with mock.patch.object(spatial, 'HYG_SHA256', hashlib.sha256(self.source.read_bytes()).hexdigest()):
            return spatial.prepare(self.source, self.output)

    def test_copies_cartesian_astrometry_and_excludes_unknown_distances(self):
        path = self.prepare(self.row, dict(self.row, id='3', hip='4', dist='100000'),
                            dict(self.row, id='5', hip='6', dist='0'))
        with path.open() as file:
            rows = list(csv.DictReader(file, delimiter='\t'))
        self.assertEqual(1, len(rows))
        self.assertEqual('hip/2', rows[0]['id'])
        self.assertEqual(['3', '4', '0', '0.000001', '-0.000002', '0'],
                         [rows[0][key] for key in ['x_pc', 'y_pc', 'z_pc', 'vx_pc_y', 'vy_pc_y', 'vz_pc_y']])

    def test_distance_uses_coordinate_norm_and_missing_colour_is_declared(self):
        path = self.prepare(dict(self.row, hip='', proper='', dist='5.001', ci=''))
        with path.open() as file:
            row = next(csv.DictReader(file, delimiter='\t'))
        self.assertEqual(('hyg/1', 'hyg/1', '5'), (row['id'], row['name'], row['dist_pc']))
        evidence = json.loads((self.output / 'provenance.json').read_text())
        self.assertEqual(1, evidence['missingColour']['count'])
        self.assertIn('not a physical radius', evidence['radius'])
        self.assertIn('CC BY-SA 4.0', (self.output / 'ATTRIBUTION.txt').read_text())

    def test_changed_upstream_hash_fails_without_creating_output(self):
        self.source.write_text('different source version')
        with self.assertRaisesRegex(ValueError, 'Unexpected upstream HYG SHA-256'):
            spatial.prepare(self.source, self.output)
        self.assertFalse(self.output.exists())

    def test_duplicate_ids_do_not_produce_a_completed_capture(self):
        with self.assertRaisesRegex(ValueError, 'Duplicate identity'):
            self.prepare(self.row, self.row)
        self.assertFalse((self.output / 'provenance.json').exists())

    def test_nonfinite_values_do_not_become_zero_velocity(self):
        with self.assertRaisesRegex(ValueError, 'Nonfinite'):
            self.prepare(dict(self.row, vx='NaN'))
        self.assertFalse((self.output / 'provenance.json').exists())

    def test_existing_destination_is_preserved(self):
        self.output.mkdir()
        marker = self.output / 'retained.txt'
        marker.write_text('original evidence')
        with self.assertRaises(FileExistsError):
            self.prepare(self.row)
        self.assertEqual('original evidence', marker.read_text())


if __name__ == '__main__':
    unittest.main()
