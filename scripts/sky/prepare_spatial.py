# SPDX-License-Identifier: GPL-2.0-or-later
"""Prepare a reproducible HYG astrometry fixture, with explicit display proxies."""
import argparse
import csv
import hashlib
import json
import math
from pathlib import Path
import sys


HYG_SHA256 = 'd9f69fd86bbf90a4e4d52b4c5c53eacfa6dfc0bfdef85bfd94f095e0bebe4ebd'
SOURCE_URL = 'https://github.com/astronexus/HYG-Database/blob/c7f7f883fe678cc7680169a50ccd7dcc49b060ce/hyg/CURRENT/hygdata_v41.csv'
COLUMNS = ('id', 'name', 'dist_pc', 'x_pc', 'y_pc', 'z_pc', 'vx_pc_y', 'vy_pc_y', 'vz_pc_y', 'v_mag', 'b_v', 'r_km')


def prepare(source, destination):
    source, destination = Path(source), Path(destination)
    with source.open('rb') as file:
        digest = hashlib.file_digest(file, 'sha256').hexdigest()
    if digest != HYG_SHA256:
        raise ValueError('Unexpected upstream HYG SHA-256; no adapted data were written.')
    destination.mkdir(parents=True, exist_ok=False)
    target = destination / 'hyg-astrometry.tsv'
    ids = set()
    missing_colours = 0
    with source.open(encoding='utf-8', newline='') as original, target.open('x', encoding='utf-8', newline='') as output:
        writer = csv.writer(output, delimiter='\t', lineterminator='\n', quoting=csv.QUOTE_NONE)
        writer.writerow(COLUMNS)
        for row in csv.DictReader(original):
            distance = float(row['dist'])
            # This is HYG's parallax-validity convention, not a desired row count.
            if not math.isfinite(distance):
                raise ValueError('Nonfinite upstream distance.')
            if not 0 < distance < 100000:
                continue
            identifier = 'hip/' + row['hip'] if row['hip'] else 'hyg/' + row['id']
            name = row['proper'] or row['bf'] or row['gl'] or identifier
            if identifier in ids or not name or any(char in name for char in '\t\r\n'):
                raise ValueError('Duplicate identity or unsupported name in upstream data.')
            ids.add(identifier)
            position = [float(row[key]) for key in ('x', 'y', 'z')]
            motion = [float(row[key]) for key in ('vx', 'vy', 'vz')]
            colour = float(row['ci']) if row['ci'] else .65
            luminosity, magnitude = float(row['lum']), float(row['mag'])
            if not all(math.isfinite(value) for value in position + motion + [colour, luminosity, magnitude]) or luminosity <= 0:
                raise ValueError('Nonfinite or invalid upstream astrometry/photometry.')
            # HYG's rounded scalar distance and Cartesian norm can differ. Keep
            # the original Cartesian strings; derive the redundant scalar norm.
            distance = math.sqrt(sum(value * value for value in position))
            if not 0 < distance < 100000:
                raise ValueError('Cartesian distance is outside the supported range.')
            # A required schema field for this astrometry fixture. HYG visual
            # luminosity alone does not establish a physical stellar radius.
            radius_proxy = 695700 * math.sqrt(luminosity)
            missing_colours += not row['ci']
            writer.writerow([identifier, name, format(distance, '.17g'), row['x'], row['y'], row['z'],
                row['vx'], row['vy'], row['vz'], row['mag'], row['ci'] or '.65', format(radius_proxy, '.17g')])
    evidence = {
        'version': 1, 'source': SOURCE_URL, 'upstreamSha256': digest,
        'outputSha256': hashlib.sha256(target.read_bytes()).hexdigest(), 'rows': len(ids),
        'provenance': 'Regenerated from pinned upstream data; historical adapter output hash is unavailable.',
        'credit': 'David Nash / Astronexus, HYG 4.1; CC BY-SA 4.0',
        'selection': 'Source distance >0 and <100000 pc; no count-based truncation.',
        'astrometry': 'Original Cartesian position and velocity strings, J2000 equatorial pc and pc/year.',
        'distance': 'Norm of original Cartesian coordinates.',
        'missingColour': {'count': missing_colours, 'displayDefault': .65},
        'radius': '695700 km * sqrt(visual luminosity): a display proxy, not a physical radius or rendering reference.',
    }
    (destination / 'provenance.json').write_text(json.dumps(evidence, indent=2) + '\n', encoding='utf-8')
    (destination / 'ATTRIBUTION.txt').write_text(
        'HYG 4.1 by David Nash / Astronexus. Adapted as described in provenance.json.\n'
        'Data: CC BY-SA 4.0, https://creativecommons.org/licenses/by-sa/4.0/\n' + SOURCE_URL + '\n', encoding='utf-8')
    return target


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path)
    parser.add_argument('destination', type=Path, help='A fresh directory outside the checkout.')
    args = parser.parse_args(argv)
    try:
        print(prepare(args.source, args.destination))
        print('Astrometry fixture only: display proxies are not measured stellar radii.')
        return 0
    except (OSError, ValueError, KeyError, csv.Error) as error:
        print(f'Spatial preparation rejected: {error}', file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
