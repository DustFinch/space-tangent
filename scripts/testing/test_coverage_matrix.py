"""Reject invalid matrix evidence before any build, GPU launch or source change."""
import json
import os
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
            if os.name == 'nt' and mode == 'engine-relative':
                # Hosted runners can supply TEMP through an 8.3 alias. Exercise
                # the same spelling difference locally instead of comparing strings.
                import ctypes
                buffer = ctypes.create_unicode_buffer(32768)
                length = ctypes.windll.kernel32.GetShortPathNameW(folder, buffer, len(buffer))
                if not length or length >= len(buffer):
                    raise ctypes.WinError()
                root = Path(buffer.value)
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
            core_source = core / 'src/SpaceTangent.Core/Example.cs'
            dependency_source = app / 'deps/space-tangent-core/src/SpaceTangent.Core/Example.cs'
            for path in (core_source, dependency_source):
                path.parent.mkdir(parents=True)
                path.write_text('// Identical core production sources.\n')
            for path in (core/'Directory.Build.props', app/'deps/space-tangent-core/Directory.Build.props'):
                path.write_text('<Project />\n')
            resources = {
                'shader': app/'src/SpaceTangent.Godot/Shaders/surface.gdshaderinc',
                'project': app/'src/SpaceTangent.Godot/project.godot',
                'export': app/'src/SpaceTangent.Godot/export_presets.cfg',
                'sky': app/'src/SpaceTangent.Godot/SkyData/bright-stars.tsv',
                'embedded': core_source.parent/'Data/blackholes.txt',
                'binary': core_source.parent/'Data/fixture.bin',
            }
            for path in resources.values():
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(b'original resource\n')
            for name in ('embedded', 'binary'):
                relative=resources[name].relative_to(core)
                target=app/'deps/space-tangent-core'/relative
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(resources[name].read_bytes())
                if mode == 'different-'+name:
                    target.write_bytes(b'different resource\n')
            if mode == 'binary-line-endings':
                resources['binary'].write_bytes(b'\xff\r\n')
                (app/'deps/space-tangent-core'/resources['binary'].relative_to(core)).write_bytes(b'\xff\n')
            if mode == 'different-core':
                dependency_source.write_text('// Different core source built by the application.\n')
            if mode == 'core-line-endings':
                content = core_source.read_bytes().replace(b'\r\n', b'\n')
                core_source.write_bytes(content)
                dependency_source.write_bytes(content.replace(b'\n', b'\r\n'))
            if mode == 'different-native':
                native_source = core/'src/SpaceTangent.Native/Example.cs'
                native_source.parent.mkdir(parents=True)
                native_source.write_text('// An independently tested consumer outside the shared assemblies.\n')
            test = core / 'tests/SpaceTangent.Core.Tests'
            binary = test / 'bin/Debug/net8.0'
            binary.mkdir(parents=True)
            (binary / 'SpaceTangent.Core.Tests.dll').write_bytes(b'Fake collector input; not a managed assembly.')
            (test / 'SpaceTangent.Core.Tests.csproj').write_text('<Project />')
            profile = root / 'profile.json'
            profile.write_text('{}')
            (root / 'profiles.json').write_text(json.dumps({'fake': profile.name if mode == 'relative-paths' else str(profile)}))
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
SET_CALLER_LOCATION
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
    } else {
        & REAL_PYTHON @args
        MUTATE_SUMMARY_SOURCE
    }
}
& $Runner -Manifest $Manifest -Profiles $Profiles -OutputDirectory $Output -Godot 'unused' -CoreCheckout $Core
""".replace('SET_CALLER_LOCATION',
                'Set-Location -LiteralPath '+literal(root)+"\n$Manifest='matrix.json'; $Profiles='profiles.json'; $Output='evidence'; $Core='core'"
                if mode == 'relative-paths' or mode.startswith('engine-') else '').replace('TRX_LITERAL', literal(trx)).replace('XML_LITERAL', literal(xml))
                .replace('REAL_PYTHON', literal(sys.executable))
                .replace('MUTATE_SUMMARY_SOURCE', 'Add-Content -LiteralPath ' + literal(source) + " -Value '// Changed during aggregation.'"
                         if mode == 'changed-during-summary' else '')
                .replace('MUTATE_SOURCE', 'Add-Content -LiteralPath ' + literal(source) + " -Value '// Changed during collection.'"
                         if mode == 'changed-source' else
                         'Add-Content -LiteralPath ' + literal(dependency_source) + " -Value '// Changed dependency.'"
                         if mode == 'changed-dependency' else
                         'Add-Content -LiteralPath ' + literal(app/'Directory.Build.props') + " -Value '<Project />'"
                         if mode == 'changed-build-settings' else
                         'Set-Content -LiteralPath ' + literal(core_source.parent/'packages.lock.json') + " -Value '{}'"
                         if mode == 'changed-lock' else
                         'Add-Content -LiteralPath ' + literal(core_scripts/'coverage_report.py') + " -Value '# Changed collector.'"
                         if mode == 'changed-collector' else
                         'Add-Content -LiteralPath '+literal(resources[mode.removeprefix('changed-')])+" -Value 'changed resource'"
                         if mode.removeprefix('changed-') in resources else
                         'Remove-Item -LiteralPath '+literal(resources['shader']) if mode == 'deleted-resource' else
                         'Set-Content -LiteralPath '+literal(source.parent/'new.unknown-resource')+" -Value 'new resource'"
                         if mode == 'added-resource' else ''))
            if mode.startswith('git-'):
                for repository in (app, core, app/'deps/space-tangent-core'):
                    subprocess.run(['git', 'init', '-q', str(repository)], check=True, capture_output=True)
                # All source fixtures are deliberately unstaged. Ignored data must
                # not be inventoried; unstaged authored resources must be.
                (app/'.gitignore').write_text('src/ignored-cache.bin\n')
                if os.name == 'nt':
                    # Unix dotfiles are hidden to PowerShell. Reproduce that
                    # attribute on Windows so both platforms test the same input.
                    import ctypes
                    if not ctypes.windll.kernel32.SetFileAttributesW(str(app/'.gitignore'), 2):
                        raise ctypes.WinError()
                (app/'src/ignored-cache.bin').write_bytes(b'cache')
                mutation = ("Add-Content -LiteralPath "+literal(resources['shader'])+" -Value 'changed unstaged shader'"
                            if mode == 'git-untracked-resource' else
                            "Add-Content -LiteralPath "+literal(app/'src/ignored-cache.bin')+" -Value 'changed ignored cache'")
                wrapper.write_text(wrapper.read_text().replace("            \n        }", "            "+mutation+"\n        }", 1))
            environment = os.environ.copy()
            if mode.startswith('engine-'):
                engine_directory = root/'engine tools'
                engine_directory.mkdir()
                name = 'st-matrix-engine.exe' if os.name == 'nt' else 'st-matrix-engine'
                executable = engine_directory/name
                # A real native executable lets Get-Command exercise the platform's
                # PATH lookup. The fixture is never executed by this orchestration test.
                shutil.copy2(sys.executable, executable)
                if mode == 'engine-path':
                    argument = 'st-matrix-engine'
                    path_directory = engine_directory
                else:
                    argument = str(executable) if mode == 'engine-absolute' else 'engine tools/'+name
                    # Explicit paths must win even with a same-named PATH entry.
                    path_directory = root/'other tools'
                    path_directory.mkdir()
                    shutil.copy2(sys.executable, path_directory/name)
                environment['PATH'] = str(path_directory)+os.pathsep+environment.get('PATH', '')
                script = wrapper.read_text().replace('& $Runner', '. $Runner').replace(
                    "-Godot 'unused'", '-Godot '+literal(argument))
                script += "\nWrite-Output ('Resolved engine: '+$Godot)\n"
                wrapper.write_text(script)
            output = root / 'evidence'
            result = subprocess.run(['pwsh', '-NoProfile', '-File', str(wrapper),
                '-Runner', str(runner), '-Manifest', str(root / 'matrix.json'),
                '-Profiles', str(root / 'profiles.json'), '-Output', str(output), '-Core', str(core)],
                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding='utf-8',
                errors='replace', timeout=60, env=environment)
            if mode.startswith('engine-') and result.returncode == 0:
                paths = [line.removeprefix('Resolved engine: ') for line in result.stdout.splitlines()
                         if line.startswith('Resolved engine: ')]
                self.assertEqual(1, len(paths), result.stdout)
                resolved = Path(paths[0])
                self.assertTrue(resolved.is_absolute() and resolved.is_file(), result.stdout)
                self.assertTrue(resolved.samefile(executable), result.stdout)
            self.assertTrue((output / 'status.json').is_file(), result.stdout)
            state = json.loads((output / 'status.json').read_text(encoding='utf-8-sig'))
            summary = json.loads((output / 'summary.json').read_text()) if (output / 'summary.json').exists() else None
            return result, state, summary

    def test_valid_fresh_evidence_passes_and_records_hashes(self):
        result, state, summary = self.exercise('passed')
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertEqual('passed', state['status'])
        self.assertTrue(state['sourcesUnchanged'])
        self.assertEqual(64, len(state['sharedCoreFingerprint']))
        self.assertEqual(4, state['sharedCoreInputFiles'])
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

    def test_different_application_and_standalone_core_sources_are_rejected(self):
        result, state, _ = self.exercise('different-core')
        self.assertNotEqual(0, result.returncode, result.stdout)
        self.assertIn('Core checkout and application dependency differ', result.stdout)
        self.assertEqual('failed', state['status'])
        self.assertEqual([], state['reports'])

    def test_independent_native_consumer_does_not_change_the_shared_core_input_contract(self):
        result, state, _ = self.exercise('different-native')
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertEqual('passed', state['status'])

    def test_git_line_ending_conversion_does_not_create_a_different_core_revision(self):
        result, state, _ = self.exercise('core-line-endings')
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertTrue(state['sourcesUnchanged'])

    def test_dependency_changes_invalidate_the_matrix(self):
        result, state, _ = self.exercise('changed-dependency')
        self.assertNotEqual(0, result.returncode, result.stdout)
        self.assertFalse(state['sourcesUnchanged'])
        self.assertEqual('failed', state['status'])

    def test_root_build_settings_are_part_of_the_source_snapshot(self):
        result, state, _ = self.exercise('changed-build-settings')
        self.assertNotEqual(0, result.returncode, result.stdout)
        self.assertFalse(state['sourcesUnchanged'])

    def test_source_changes_during_aggregation_cannot_leave_a_passed_status(self):
        result, state, summary = self.exercise('changed-during-summary')
        self.assertIsNotNone(summary)
        self.assertNotEqual(0, result.returncode, result.stdout)
        self.assertFalse(state['sourcesUnchanged'])
        self.assertEqual('failed', state['status'])

    def test_dependency_lock_changes_invalidate_the_evidence(self):
        result, state, _ = self.exercise('changed-lock')
        self.assertNotEqual(0, result.returncode, result.stdout)
        self.assertFalse(state['sourcesUnchanged'])

    def test_collector_source_changes_invalidate_the_evidence(self):
        result, state, _ = self.exercise('changed-collector')
        self.assertNotEqual(0, result.returncode, result.stdout)
        self.assertFalse(state['sourcesUnchanged'])


    def test_relative_parameters_and_profiles_use_the_callers_powershell_location(self):
        result, state, _ = self.exercise('relative-paths')
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertEqual('passed', state['status'])

    def test_engine_name_resolves_from_path_before_changing_location(self):
        result, state, _ = self.exercise('engine-path')
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertEqual('passed', state['status'])

    def test_relative_engine_path_uses_the_caller_location_instead_of_path(self):
        result, state, _ = self.exercise('engine-relative')
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertEqual('passed', state['status'])

    def test_absolute_engine_path_is_preserved_with_a_same_named_path_entry(self):
        result, state, _ = self.exercise('engine-absolute')
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertEqual('passed', state['status'])

    def test_resource_changes_additions_and_deletions_invalidate_the_matrix(self):
        for mode in ('changed-shader', 'changed-project', 'changed-export', 'changed-sky',
                     'changed-embedded', 'changed-binary', 'added-resource', 'deleted-resource'):
            with self.subTest(mode=mode):
                result, state, _ = self.exercise(mode)
                self.assertNotEqual(0, result.returncode, result.stdout)
                self.assertFalse(state['sourcesUnchanged'])
                self.assertEqual('failed', state['status'])

    def test_shared_embedded_and_binary_resources_must_match(self):
        for mode in ('different-embedded', 'different-binary', 'binary-line-endings'):
            with self.subTest(mode=mode):
                result, state, _ = self.exercise(mode)
                self.assertNotEqual(0, result.returncode, result.stdout)
                self.assertIn('Core checkout and application dependency differ', result.stdout)
                self.assertEqual([], state['reports'])


    @unittest.skipUnless(shutil.which('git'), 'Git is required for the input inventory contract.')
    def test_real_git_inventory_includes_untracked_resources_and_excludes_ignored_caches(self):
        for mode, passes in (('git-untracked-resource', False), ('git-ignored-cache', True)):
            with self.subTest(mode=mode):
                result, state, _ = self.exercise(mode)
                self.assertEqual(passes, result.returncode == 0, result.stdout)
                self.assertEqual(passes, state['sourcesUnchanged'])


if __name__ == '__main__':
    unittest.main()
