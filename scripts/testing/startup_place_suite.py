# SPDX-License-Identifier: GPL-2.0-or-later
"""Run six real, headless startup modes with isolated observer-place preferences."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys


MODES = ('normal', 'defaults', 'scenario', 'replay', 'smoke', 'save-exit')
DE440_SHA256 = '29915576d0a6555766b99485ac3056ee415e86df4fce282611c31afb329ad062'


def arguments(mode, directory):
    return {
        'normal': [],
        'defaults': ['--startup-default-settings'],
        'scenario': ['--startup-scenario', str(directory/'startup.stscene')],
        'replay': ['--startup-replay', str(directory/'startup.stframes')],
        'smoke': ['--smoke-startup'],
        'save-exit': ['--startup-scenario', str(directory/'startup.stscene'),
                      '--startup-save-scenario', str(directory/'saved.stscene'), '--startup-exit'],
    }[mode]


def validate(mode, directory, returncode, output):
    """Check evidence only after the native process has exited and flushed saves."""
    normal = mode == 'normal'
    marker = 'SpaceTangent observing place startup policy passed: ' + ('normal' if normal else 'batch')
    if returncode != 0 or marker not in output or re.search(r'(?m)^\s*ERROR:|SCRIPT ERROR|SHADER ERROR', output):
        raise ValueError(f'{mode}: expected exit 0, success marker and no engine errors; got {returncode}.')
    result = json.loads((directory/'policy-result.json').read_text(encoding='utf-8'))
    if (result.get('passed') is not True or result.get('normal') is not normal
            or (normal and result.get('edited') is not True)):
        raise ValueError(f'{mode}: startup policy assertions did not pass.')
    path = directory/'sentinel-places.json'
    before, after = path.with_suffix('.json.before').read_bytes(), path.read_bytes()
    if (before == after) == normal:
        raise ValueError(f'{mode}: preference bytes violate the write policy after process exit.')
    if mode == 'save-exit':
        # The probe loads this with the real ScenarioFile validator as well.
        saved = json.loads((directory/'saved.stscene').read_text(encoding='utf-8'))
        if not isinstance(saved, dict) or not saved.get('views'):
            raise ValueError('save-exit: no saved scenario views.')
    return {'mode': mode, 'status': 'passed', 'exitCode': returncode,
            'beforeSha256': hashlib.sha256(before).hexdigest(),
            'afterSha256': hashlib.sha256(after).hexdigest()}


def run(engine, project, de440, output):
    if hashlib.sha256(de440.read_bytes()).hexdigest() != DE440_SHA256:
        raise ValueError('Use the CI-pinned public DE440 kernel.')
    output.mkdir(parents=True, exist_ok=False)
    state = {'version': 1, 'status': 'running', 'cases': []}
    try:
        for mode in MODES:
            directory = output/mode
            directory.mkdir()
            # None of the user's data paths or coverage controls enter these runs.
            env = {key: value for key, value in os.environ.items() if not key.upper().startswith('SPACETANGENT_')}
            env.update({
                'SPACETANGENT_DE440': str(de440), 'SPACETANGENT_REQUIRE_SIMULATION': '1',
                'SPACETANGENT_HIPS_OFFLINE': '1', 'SPACETANGENT_EARTH_PROBE_OUTPUT': str(directory),
                'SPACETANGENT_SETTINGS_PATH': str(directory/'settings.txt'),
                'SPACETANGENT_ADDONS_SETTINGS_PATH': str(directory/'addons.json'),
                'SPACETANGENT_BOOKMARKS_PATH': str(directory/'bookmarks.json'),
                'SPACETANGENT_OBSERVER_PLACES_PATH': str(directory/'sentinel-places.json'),
                'SPACETANGENT_STARTUP_WORKSPACE': str(directory/'absent-startup.stscene'),
            })
            command = [str(engine), '--headless', '--path', str(project),
                       'res://tests/ObserverPlaceStartupProbe.tscn', '--audio-driver', 'Dummy',
                       '--log-file', str(directory/'godot.log')]
            user_args = arguments(mode, directory)
            if user_args:
                command += ['--', *user_args]
            result = subprocess.run(command, env=env, cwd=project, stdout=subprocess.PIPE,
                                    stderr=subprocess.STDOUT, timeout=90)
            (directory/'process.log').write_bytes(result.stdout)
            log = (directory/'godot.log').read_text(encoding='utf-8', errors='replace')
            case = validate(mode, directory, result.returncode, result.stdout.decode('utf-8', errors='replace')+'\n'+log)
            state['cases'].append(case)
            print('Startup place isolation passed: '+mode, flush=True)
        state['status'] = 'passed'
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        state.update(status='failed', error=str(error))
        raise
    finally:
        (output/'status.json').write_text(json.dumps(state, indent=2)+'\n', encoding='utf-8')


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('godot', 'project', 'de440', 'output'):
        parser.add_argument('--'+name, type=lambda value: Path(value).resolve(), required=True)
    args = parser.parse_args(argv)
    try:
        run(args.godot, args.project, args.de440, args.output)
        return 0
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        print('Startup place suite failed: '+str(error), file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
