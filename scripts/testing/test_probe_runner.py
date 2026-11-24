"""Exercise launch refusals before any editor, build, or display can be started."""
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


@unittest.skipUnless(shutil.which('pwsh'), 'PowerShell 7 is required for the runner contracts.')
class ProbeRunnerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='probe runner ')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.output = self.root / 'evidence'
        # Refusal tests must not contend with a real coverage job's editor lock.
        # Their miniature checkout contains only the runner and data verifier;
        # every case still fails before a build or engine can be launched.
        checkout = self.root / 'checkout'
        scripts = checkout / 'scripts/testing'
        scripts.mkdir(parents=True)
        original = Path(__file__).with_name('godot-probe-coverage.ps1')
        self.runner = scripts / original.name
        shutil.copyfile(original, self.runner)
        shutil.copyfile(original.with_name('runner-paths.ps1'), scripts / 'runner-paths.ps1')
        verifier = checkout / 'deps/space-tangent-core/scripts/testing'
        verifier.mkdir(parents=True)
        shutil.copyfile(Path(__file__).resolve().parents[2] /
            'deps/space-tangent-core/scripts/testing/data_profile.py', verifier / 'data_profile.py')
        self.profile = self.root / 'inputs.json'
        self.input = self.root / 'control.bin'
        self.input.write_bytes(b'This is an integrity-check input, not a scientific ephemeris.')
        self.profile.write_text(json.dumps({'version': 1, 'bindings': {'SPACETANGENT_DE440': {
            'path': str(self.input), 'kind': 'file', 'files': {'.': hashlib.sha256(self.input.read_bytes()).hexdigest()}
        }}}))

    def launch(self, *arguments, scene='res://tests/BootstrapSmoke.tscn'):
        result = subprocess.run(['pwsh', '-NoProfile', '-File', str(self.runner),
            '-DataProfile', str(self.profile), '-OutputDirectory', str(self.output), '-Scene', scene,
            '-SuccessMarker', 'must never run', '-Godot', str(self.root / 'nonexistent-engine'), *arguments],
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding='utf-8', errors='replace', timeout=30)
        self.assertNotEqual(0, result.returncode, result.stdout)
        self.assertFalse((self.output / 'coverage.xml').exists())
        return result.stdout

    def test_render_requires_an_explicit_display_policy(self):
        self.assertIn('Rendering requires an isolated desktop', self.launch('-Render'))
        self.assertFalse(self.output.exists())

    def test_scene_cannot_escape_the_test_directory(self):
        self.assertIn('Choose an explicit test scene', self.launch(scene='res://tests/../Main.tscn'))
        self.assertFalse(self.output.exists())

    def test_previous_evidence_cannot_be_overwritten(self):
        self.output.mkdir()
        previous = self.output / 'previous.xml'
        previous.write_text('retained evidence')
        self.assertIn('Use a fresh output directory', self.launch())
        self.assertEqual('retained evidence', previous.read_text())

    def test_changed_input_fails_before_build_or_engine_launch(self):
        self.input.write_bytes(b'tampered')
        self.assertIn('Pinned input verification failed', self.launch())

    def test_options_cannot_replace_a_pinned_dataset(self):
        options = self.root / 'options.json'
        options.write_text(json.dumps({'SPACETANGENT_DE440': 'different-input.bin'}))
        self.assertIn('cannot override pinned inputs', self.launch('-ProbeOptions', str(options)))

    def test_user_arguments_cannot_contain_quotes(self):
        self.assertIn('User arguments must be bounded', self.launch('-UserArguments', 'unsafe"value'))
        self.assertFalse(self.output.exists())

    def test_user_arguments_cannot_contain_control_characters(self):
        self.assertIn('User arguments must be bounded', self.launch('-UserArguments', 'line\nbreak'))
        self.assertFalse(self.output.exists())


if __name__ == '__main__':
    unittest.main()
