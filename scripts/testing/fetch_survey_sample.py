# SPDX-License-Identifier: GPL-2.0-or-later
"""Capture the pinned upstream DSS sample used by the external-data contract."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import sys
import urllib.error
import urllib.request


ORIGIN = 'https://irsa.ipac.caltech.edu/data/hips/CDS/DSS2/color/'
REGISTRY = 'https://alasky.cds.unistra.fr/MocServer/query?ID=CDS%2FP%2FDSS2%2Fcolor&get=record'
SAMPLES = (
    ('properties', 3971, '7e5a2618bda285941b6d77303d45443d56a4ec4fae143cfdba58ee7c9034eda8'),
    ('Norder3/Allsky.jpg', 806441, '9c9434b990fc6cc661cc17171cf52805fdb8679515b7c884abed914d5a6054dd'),
    ('Norder5/Dir0/Npix5359.jpg', 72493, '32fcbd8cc78f0e2090e2ecbe64f10892fc91786c31afd2b72ea769d9e22245ae'),
)


def capture(destination):
    """Download original upstream bytes; never manufacture survey metadata."""
    destination = Path(destination).resolve()
    destination.mkdir(parents=True, exist_ok=False)
    survey = destination / 'surveys/dss'
    evidence = {
        'version': 1,
        'capturedUtc': datetime.now(timezone.utc).isoformat(),
        'dataset': 'CDS/P/DSS2/color',
        'registry': REGISTRY,
        'provenance': 'Pinned upstream mirror capture; no claim of byte identity with a lost historical local cache.',
        'credit': 'Digitized Sky Survey - STScI/NASA; Colored & Healpixed by CDS',
        'files': [],
    }
    for relative, size, expected in SAMPLES:
        url = ORIGIN + relative
        request = urllib.request.Request(url, headers={'User-Agent': 'SpaceTangent-test-data/1'})
        with urllib.request.urlopen(request, timeout=45) as response:
            content = response.read(size + 1)
            actual = hashlib.sha256(content).hexdigest()
            if len(content) != size or actual != expected:
                raise ValueError(f'Upstream bytes changed for {relative}; preserve the failed capture and review provenance.')
            target = survey / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(content)
            evidence['files'].append({'relative': relative, 'url': response.url,
                'bytes': len(content), 'sha256': actual, 'lastModified': response.headers.get('Last-Modified')})
    # A failed or partial download never receives a completed provenance record.
    (destination / 'provenance.json').write_text(json.dumps(evidence, indent=2) + '\n', encoding='utf-8')
    return survey.parent


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('destination', type=Path, help='A fresh directory outside the source checkout.')
    args = parser.parse_args(argv)
    try:
        root = capture(args.destination)
        print(f'Captured pinned survey sample. Bind SPACETANGENT_SKY_SURVEYS={root}')
        return 0
    except (OSError, ValueError, urllib.error.URLError) as error:
        print(f'Survey capture rejected: {error}', file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
