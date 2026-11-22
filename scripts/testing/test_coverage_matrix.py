"""Reject invalid matrix evidence before any build, GPU launch or source change."""
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


@unittest.skipUnless(shutil.which('pwsh'), 'PowerShell 7 is required.')
class CoverageMatrixBoundaryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='coverage matrix ')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.output = self.root / 'fresh evidence'
        self.profile = self.root / 'profile.json'
        self.profile.write_text('{}')
        self.profiles = self.root / 'profiles.json'
        self.profiles.write_text(json.dumps({'local': str(self.profile)}))
        self.run = {'name': 'case', 'kind': 'probe', 'profile': 'local',
                    'scene': 'res://tests/BootstrapSmoke.tscn', 'successMarker': 'must not launch'}
        self.matrix = {'version': 1, 'runs': [self.run]}

    def reject(self, message):
        manifest = self.root / 'matrix.json'
        manifest.write_text(json.dumps(self.matrix))
        runner = Path(__file__).with_name('coverage-matrix.ps1')
        result = subprocess.run(['pwsh', '-NoProfile', '-File', str(runner),
            '-Manifest', str(manifest), '-Profiles', str(self.profiles),
            '-OutputDirectory', str(self.output), '-Godot', str(self.root / 'absent-engine')],
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
            encoding='utf-8', errors='replace', timeout=30)
        self.assertNotEqual(0, result.returncode, result.stdout)
        self.assertIn(message, result.stdout)
        self.assertFalse((self.output / 'status.json').exists(), result.stdout)
        self.assertFalse((self.output / 'coverage.xml').exists(), result.stdout)

    def test_duplicate_names_cannot_overwrite_an_earlier_case(self):
        self.matrix['runs'].append(dict(self.run))
        self.reject('Run names must be unique')

    def test_case_insensitive_duplicate_names_are_rejected(self):
        self.matrix['runs'].append(dict(self.run, name='CASE'))
        self.reject('Run names must be unique')

    def test_case_name_cannot_escape_output(self):
        self.run['name'] = '../previous'
        self.reject('Run names must be unique')

    def test_scene_cannot_escape_test_directory(self):
        self.run['scene'] = 'res://tests/../Main.tscn'
        self.reject('A probe needs an explicit test scene')

    def test_profiles_must_resolve_to_existing_files(self):
        self.profile.unlink()
        self.reject('Missing pinned profile')

    def test_a_success_marker_is_required(self):
        self.run['successMarker'] = ''
        self.reject('A probe needs an explicit test scene')

    def test_options_cannot_include_unrelated_environment_variables(self):
        self.run['options'] = {'PATH': 'replacement'}
        self.reject('Probe options must be a string map')

    def test_controls_must_be_explicit_strings(self):
        self.run['options'] = {'SPACETANGENT_TEST': True}
        self.reject('Probe options must be a string map')

    def test_existing_output_is_preserved(self):
        self.output.mkdir()
        previous = self.output / 'previous.xml'
        previous.write_text('retained')
        self.reject('Use a fresh matrix output directory')
        self.assertEqual('retained', previous.read_text())

    def test_empty_matrix_cannot_claim_full_coverage(self):
        self.matrix['runs'] = []
        self.reject('Choose a version 1 matrix')

    def test_unknown_run_kind_is_rejected(self):
        self.run['kind'] = 'shell'
        self.reject('Run kind must be core')


@unittest.skipUnless(shutil.which('pwsh'), 'PowerShell 7 is required.')
class CoverageMatrixExecutionTests(unittest.TestCase):
    """Use explicit fake collectors to exercise orchestration, never science/GPU."""

    def exercise(self, mode):
        with tempfile.TemporaryDirectory(prefix='matrix execution ') as folder:
            root = Path(folder)
            app, core = root / 'application', root / 'core'
            scripts = app / 'scripts/testing'
            scripts.mkdir(parents=True)
            runner = scripts / 'coverage-matrix.ps1'
            shutil.copyfile(Path(__file__).with_name(runner.name), runner)
            core_scripts = core / 'scripts/testing'
            core_scripts.mkdir(parents=True)
            shutil.copyfile(Path(__file__).resolve().parents[2] /
                'deps/space-tangent-core/scripts/testing/coverage_report.py', core_scripts / 'coverage_report.py')
            source = app / 'src/SpaceTangent.Application/Example.cs'
            source.parent.mkdir(parents=True)
            source.write_text('// Original source used by the orchestration test.\n')
            test = core / 'tests/SpaceTangent.Core.Tests'
            binary = test / 'bin/Debug/net8.0'
            binary.mkdir(parents=True)
            (binary / 'SpaceTangent.Core.Tests.dll').write_bytes(b'Fake collector input; not a managed assembly.')
            (test / 'SpaceTangent.Core.Tests.csproj').write_text('<Project />')
            profile = root / 'profile.json'
            profile.write_text('{}')
            (root / 'profiles.json').write_text(json.dumps({'fake': str(profile)}))
            (root / 'matrix.json').write_text(json.dumps({'version': 1, 'runs': [
                {'name': 'fake-unit', 'kind': 'core', 'profile': 'fake'}]}))
            counts = ('passed="0" failed="1" total="1" notExecuted="0"' if mode == 'failed-trx'
                      else 'passed="1" failed="0" total="1" notExecuted="0"')
            outcome = 'Failed' if mode == 'failed-trx' else 'Passed'
            trx = ('<TestRun><Results><UnitTestResult outcome="' + outcome + '" /></Results>'
                   '<ResultSummary><Counters ' + counts + ' /></ResultSummary></TestRun>')
            modules = ('Core', 'Contracts', 'Application', 'Godot')
            packages = []
            for module in modules:
                extra = '<line number="2" hits="0" />' if mode == 'below-minimum' and module == 'Godot' else ''
                packages.append(f'<package name="SpaceTangent.{module}"><classes><class '
                    f'filename="SpaceTangent.{module}/Example.cs"><lines><line number="1" hits="1" />'
                    f'{extra}</lines></class></classes></package>')
            xml = '<coverage><packages>' + ''.join(packages) + '</packages></coverage>'
            # This wrapper replaces only external tools. The real matrix reads
            # evidence, snapshots sources, computes coverage and enforces gates.
            wrapper = root / 'fake-tools.ps1'
            def literal(value):
                return "'" + str(value).replace("'", "''") + "'"
            wrapper.write_text("""
param([string]$Runner,[string]$Manifest,[string]$Profiles,[string]$Output,[string]$Core)
$ErrorActionPreference='Stop'
function global:dotnet { $global:LASTEXITCODE=0 }
function global:python {
    if ($args[0] -eq 'scripts/testing/data_profile.py') {
        if ($args[1] -eq 'run') {
            $dll=$args | Where-Object { $_ -like '*.Tests.dll' } | Select-Object -First 1
            $dir=Split-Path (Split-Path $dll)
            Set-Content -LiteralPath (Join-Path $dir 'tests.trx') -Value TRX_LITERAL
            Set-Content -LiteralPath (Join-Path $dir 'coverage.xml') -Value XML_LITERAL
            MUTATE_SOURCE
        }
        $global:LASTEXITCODE=0
    } else { & REAL_PYTHON @args }
}
& $Runner -Manifest $Manifest -Profiles $Profiles -OutputDirectory $Output -Godot 'unused' -CoreCheckout $Core
""".replace('TRX_LITERAL', literal(trx)).replace('XML_LITERAL', literal(xml))
                .replace('REAL_PYTHON', literal(sys.executable))
                .replace('MUTATE_SOURCE', 'Add-Content -LiteralPath ' + literal(source) + " -Value '// Changed during collection.'"
                         if mode == 'changed-source' else ''))
            output = root / 'evidence'
            result = subprocess.run(['pwsh', '-NoProfile', '-File', str(wrapper),
                '-Runner', str(runner), '-Manifest', str(root / 'matrix.json'),
                '-Profiles', str(root / 'profiles.json'), '-Output', str(output), '-Core', str(core)],
                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding='utf-8',
                errors='replace', timeout=60)
            state = json.loads((output / 'status.json').read_text(encoding='utf-8-sig'))
            summary = json.loads((output / 'summary.json').read_text()) if (output / 'summary.json').exists() else None
            return result, state, summary

    def test_valid_fresh_evidence_passes_and_records_hashes(self):
        result, state, summary = self.exercise('passed')
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertEqual('passed', state['status'])
        self.assertTrue(state['sourcesUnchanged'])
        self.assertEqual(64, len(state['runs'][0]['reportSha256']))
        self.assertEqual(100, summary['modules']['SpaceTangent.Godot']['percent'])

    def test_changed_sources_invalidate_the_entire_matrix(self):
        result, state, summary = self.exercise('changed-source')
        self.assertNotEqual(0, result.returncode)
        self.assertIn('Sources changed during the matrix', result.stdout)
        self.assertEqual('failed', state['status'])
        self.assertFalse(state['sourcesUnchanged'])
        self.assertIsNone(summary)

    def test_zero_process_status_does_not_hide_a_failed_test(self):
        result, state, summary = self.exercise('failed-trx')
        self.assertNotEqual(0, result.returncode)
        self.assertIn('Unit evidence needs passed tests and no failures', result.stdout)
        self.assertEqual('failed', state['runs'][0]['status'])
        self.assertEqual([], state['reports'])
        self.assertIsNone(summary)

    def test_low_module_keeps_its_summary_but_fails_the_gate(self):
        result, state, summary = self.exercise('below-minimum')
        self.assertNotEqual(0, result.returncode)
        self.assertIn('Fresh coverage is below 95%', result.stdout)
        self.assertEqual('failed', state['status'])
        self.assertEqual(50, summary['modules']['SpaceTangent.Godot']['percent'])


if __name__ == '__main__':
    unittest.main()
