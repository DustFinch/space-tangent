# SPDX-License-Identifier: GPL-2.0-or-later
"""Capture the complete pinned upstream photograph set without rewriting credits."""
import argparse
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import sys
import urllib.error
import urllib.request


SOURCE = 'https://raw.githubusercontent.com/Stellarium/stellarium/'


def source_manifest(path):
    manifest = json.loads(Path(path).read_text(encoding='utf-8'))
    if manifest.get('version') != 1 or not re.fullmatch('[0-9a-f]{40}', manifest.get('revision', '')):
        raise ValueError('Invalid source manifest version or pinned revision.')
    files = manifest.get('files', [])
    names = set()
    for item in files:
        name = item.get('name', '')
        if (not re.fullmatch(r'(textures\.json|[A-Za-z0-9][A-Za-z0-9_.-]*\.png)', name)
                or name in names or type(item.get('size')) is not int or not 0 < item['size'] <= 8*1024*1024
                or not re.fullmatch('[0-9a-f]{40}', item.get('blob', ''))):
            raise ValueError('Invalid or duplicate source file entry.')
        names.add(name)
    if 'textures.json' not in names or len(names) != manifest.get('images', -1) + 1 or manifest['images'] <= 0:
        raise ValueError('Source manifest needs metadata and its complete image population.')
    return manifest


def checked_bytes(item, data):
    actual = hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()
    if len(data) != item['size'] or actual != item['blob']:
        raise ValueError('Pinned upstream file changed: '+item['name'])
    if item['name'].endswith('.png') and not data.startswith(b'\x89PNG\r\n\x1a\n'):
        raise ValueError('Pinned source does not contain PNG bytes: '+item['name'])
    return {'name': item['name'], 'bytes': len(data), 'gitBlob': actual,
            'sha256': hashlib.sha256(data).hexdigest()}


def verify(root, manifest_path=None):
    manifest = source_manifest(manifest_path or Path(__file__).with_name('photographs-source.json'))
    for item in manifest['files']:
        with (Path(root)/item['name']).open('rb') as source:
            checked_bytes(item, source.read(item['size']+1))
    return manifest['images']


def capture(destination, manifest_path=None, workers=4):
    if type(workers) is not int or not 1 <= workers <= 8:
        raise ValueError('Choose between one and eight download workers.')
    manifest = source_manifest(manifest_path or Path(__file__).with_name('photographs-source.json'))
    destination = Path(destination).resolve()
    destination.mkdir(parents=True, exist_ok=False)
    root = destination / 'photographs'
    root.mkdir()
    records, failed = [], []

    def fetch(item):
        url = SOURCE + manifest['revision'] + '/nebulae/default/' + item['name']
        request = urllib.request.Request(url, headers={'User-Agent': 'SpaceTangent-test-data/1'})
        with urllib.request.urlopen(request, timeout=45) as response:
            data = response.read(item['size'] + 1)
        record = checked_bytes(item, data)
        (root/item['name']).write_bytes(data)
        return dict(record, url=url)

    with ThreadPoolExecutor(max_workers=workers) as executor:
        futures = {executor.submit(fetch, item): item['name'] for item in manifest['files']}
        for future in as_completed(futures):
            try:
                records.append(future.result())
            except (OSError, ValueError, urllib.error.URLError) as error:
                failed.append({'name': futures[future], 'error': str(error)})
    evidence = {'version': 1, 'capturedUtc': datetime.now(timezone.utc).isoformat(),
        'revision': manifest['revision'], 'complete': not failed, 'images': manifest['images'],
        'provenance': 'Unmodified original upstream release files; historical local-cache hash unavailable.',
        'credits': 'Original per-image authorship and links are preserved in textures.json; retain that file.',
        'files': sorted(records, key=lambda item: item['name']), 'failed': failed}
    (destination/'provenance.json').write_text(json.dumps(evidence, indent=2)+'\n', encoding='utf-8')
    if failed:
        raise ValueError(f'Incomplete photograph capture: {len(failed)} failed files; inspect provenance.json.')
    return root


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('destination', type=Path, help='A fresh capture directory, or existing photograph directory with --verify.')
    parser.add_argument('--workers', type=int, choices=range(1, 9), default=4)
    parser.add_argument('--verify', action='store_true', help='Check an existing complete source set without network or mutation.')
    args = parser.parse_args(argv)
    try:
        if args.verify:
            print(f'Verified original metadata and all {verify(args.destination)} pinned photographs.')
            return 0
        root = capture(args.destination, workers=args.workers)
        print(f'Complete pinned capture. Bind SPACETANGENT_SKY_PHOTOGRAPHS={root}')
        return 0
    except (OSError, ValueError, KeyError, urllib.error.URLError) as error:
        print(f'Photograph capture rejected: {error}', file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
