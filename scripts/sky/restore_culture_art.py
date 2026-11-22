# SPDX-License-Identifier: GPL-2.0-or-later
"""Restore pinned available artwork into a copy of matching sky definitions."""
import argparse
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import shutil
import sys
import urllib.error
import urllib.request


SOURCE = 'https://raw.githubusercontent.com/Stellarium/stellarium-skycultures/'


def semantic_hash(path):
    value = json.loads(Path(path).read_text(encoding='utf-8-sig'))
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(',', ':'), ensure_ascii=False).encode()).hexdigest()


def checked(item, content):
    blob = hashlib.sha1(b'blob '+str(len(content)).encode()+b'\0'+content).hexdigest()
    if len(content) != item['size'] or blob != item['blob']:
        raise ValueError('Pinned source bytes differ: '+item['name'])
    if item['name'].endswith('.webp') and (content[:4] != b'RIFF' or content[8:12] != b'WEBP'):
        raise ValueError('The pinned source is not WebP image data: '+item['name'])
    if item['name'].endswith('.png') and not content.startswith(b'\x89PNG\r\n\x1a\n'):
        raise ValueError('The pinned source is not PNG image data: '+item['name'])
    return {'name': item['name'], 'bytes': len(content), 'gitBlob': blob,
            'sha256': hashlib.sha256(content).hexdigest()}


def restore(source, destination, manifest_path=None):
    source, destination = Path(source).resolve(), Path(destination).resolve()
    path = manifest_path or Path(__file__).with_name('culture-artwork-source.json')
    manifest = json.loads(Path(path).read_text())
    if manifest.get('version') != 1 or not re.fullmatch('[0-9a-f]{40}', manifest.get('revision', '')):
        raise ValueError('Invalid pinned culture manifest.')
    if not 1 <= len(manifest.get('cultures', [])) <= 128 or not manifest.get('files'):
        raise ValueError('The manifest needs culture definitions and artwork.')
    ids, references = set(), set()
    for culture in manifest['cultures']:
        name = culture['id']
        if not re.fullmatch('[A-Za-z0-9_]+', name) or name in ids:
            raise ValueError('Invalid or duplicate culture identifier.')
        ids.add(name)
        if semantic_hash(source/name/'index.json') != culture['semanticSha256']:
            raise ValueError('The source definition differs from the pinned culture: '+name)
        definition = json.loads((source/name/'index.json').read_text(encoding='utf-8-sig'))
        for constellation in definition.get('constellations', []):
            image = constellation.get('image')
            if isinstance(image, dict) and image.get('file'):
                references.add((PurePosixPath(name)/image['file']).as_posix())
    available = {item['name'] for item in manifest['files']}
    unavailable = set(manifest['unavailable'])
    if available & unavailable or available | unavailable != references:
        raise ValueError('The source manifest must account for every constellation-art reference.')
    records = manifest['files']+[manifest['notice']]
    names = set()
    for item in records:
        relative = PurePosixPath(item['name'])
        valid_name = (item is manifest['notice'] and re.fullmatch(r'LICENSE-[A-Za-z0-9_.-]+\.txt', item['name'])) or re.fullmatch(
            r'[A-Za-z0-9_]+/illustrations/[A-Za-z0-9_.-]+\.(webp|png)', item['name'])
        if (not valid_name or relative.is_absolute() or '..' in relative.parts or '\\' in item['name'] or item['name'] in names
                or (len(relative.parts)>1 and relative.parts[0] not in ids)
                or type(item.get('size')) is not int or not 0<item['size']<=8*1024*1024
                or not re.fullmatch('[0-9a-f]{40}', item.get('blob',''))):
            raise ValueError('Invalid or duplicate culture source file.')
        names.add(item['name'])
    if destination == source or destination.is_relative_to(source):
        raise ValueError('Use a separate destination outside the original source.')
    destination.mkdir(parents=True, exist_ok=False)
    root = destination/'cultures'
    root.mkdir()
    for name in sorted(ids):
        if (source/name).is_symlink() or any(child.is_symlink() for child in (source/name).rglob('*')):
            raise ValueError('Culture input must not contain symbolic links.')
        shutil.copytree(source/name, root/name)
    restored, failed = [], []

    def fetch(item):
        target = root/item['name']
        if target.exists():
            with target.open('rb') as stream:
                evidence = checked(item, stream.read(item['size']+1))
            return dict(evidence, origin='preserved-source')
        url = SOURCE+manifest['revision']+'/'+item['name']
        with urllib.request.urlopen(url, timeout=45) as response:
            content = response.read(item['size']+1)
        evidence = checked(item, content)
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(content)
        return dict(evidence, origin='pinned-upstream', url=url)

    with ThreadPoolExecutor(max_workers=4) as executor:
        jobs = {executor.submit(fetch,item):item['name'] for item in records}
        for future in as_completed(jobs):
            try:
                restored.append(future.result())
            except (OSError, ValueError, urllib.error.URLError) as error:
                failed.append({'name':jobs[future], 'error':str(error)})
    evidence = {'version':1, 'capturedUtc':datetime.now(timezone.utc).isoformat(), 'revision':manifest['revision'],
        'selectedCultures':len(ids), 'availableImages':len(manifest['files']),
        'restoredImages':sum(item['origin']=='pinned-upstream' and item['name']!=manifest['notice']['name'] for item in restored),
        'sourceSubsetComplete':not failed, 'allReferencedArtAvailable':not failed and not manifest['unavailable'],
        'unavailableUpstream':manifest['unavailable'], 'failed':failed, 'files':sorted(restored,key=lambda item:item['name'])}
    (destination/'provenance.json').write_text(json.dumps(evidence,indent=2)+'\n', encoding='utf-8')
    if failed:
        raise ValueError(f'Incomplete culture recovery: {len(failed)} source files failed; originals preserved.')
    return root, evidence


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path, help='Existing matching culture directories; preserved unchanged.')
    parser.add_argument('destination', type=Path, help='A fresh separate directory outside the source.')
    args = parser.parse_args(argv)
    try:
        root,evidence = restore(args.source,args.destination)
        print(f"Recovered {evidence['restoredImages']} missing images; {evidence['availableImages']} pinned images available in {root}")
        if evidence['unavailableUpstream']:
            print(f"Partial upstream corpus: {len(evidence['unavailableUpstream'])} referenced images are absent from the pinned source tree.",file=sys.stderr)
            return 2
        return 0
    except (OSError, ValueError, KeyError, urllib.error.URLError) as error:
        print(f'Culture recovery rejected: {error}',file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
