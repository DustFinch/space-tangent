"""Exercise real runner path handling with explicit fake collectors, never a GPU."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


def literal(value):
    return "'" + str(value).replace("'", "''") + "'"


@unittest.skipUnless(shutil.which('pwsh'), 'PowerShell 7 is required for runner path contracts.')
class RunnerPathTests(unittest.TestCase):
    def exercise(self, suite, engine_mode='relative', failure=None, short_path=False):
        with tempfile.TemporaryDirectory(prefix='runner paths ') as folder:
            root = Path(folder)
            if short_path:
                # Hosted Windows TEMP can contain an 8.3 alias such as RUNNER~1.
                # Force that spelling locally instead of relying on runner setup.
                import ctypes
                buffer = ctypes.create_unicode_buffer(32768)
                length = ctypes.windll.kernel32.GetShortPathNameW(folder, buffer, len(buffer))
                if not length or length >= len(buffer):
                    raise ctypes.WinError()
                root = Path(buffer.value)
            caller, process = root / 'caller directory', root / 'process directory'
            caller.mkdir()
            process.mkdir()
            scripts = root / 'checkout/scripts/testing'
            scripts.mkdir(parents=True)
            original = Path(__file__).parent
            name = 'resource-probes.ps1' if suite else 'godot-probe-coverage.ps1'
            for filename in (name, 'runner-paths.ps1'):
                shutil.copyfile(original / filename, scripts / filename)
            data = caller / 'control.bin'
            data.write_bytes(b'Path orchestration input, not an ephemeris.')
            profile = caller / 'inputs.json'
            profile.write_text(json.dumps({'version': 1, 'bindings': {'SPACETANGENT_DE440': {
                'path': str(data), 'kind': 'file', 'files': {'.': hashlib.sha256(data.read_bytes()).hexdigest()}
            }}}))
            (caller / 'options.json').write_text(json.dumps({'SPACETANGENT_RUNNER_CONTROL': 'caller-option'}))
            engine_dir = caller / 'engine tools'
            engine_dir.mkdir()
            engine_name = 'st-probe-engine.exe' if os.name == 'nt' else 'st-probe-engine'
            engine = engine_dir / engine_name
            shutil.copy2(sys.executable, engine)
            other_dir = caller / 'other tools'
            other_dir.mkdir()
            shutil.copy2(sys.executable, other_dir / engine_name)
            argument = (engine_name if engine_mode == 'path' else str(engine) if engine_mode == 'absolute'
                        else 'engine tools/' + engine_name)
            environment = os.environ.copy()
            environment['PATH'] = str(engine_dir if engine_mode == 'path' else other_dir) + os.pathsep + environment.get('PATH', '')
            environment['ST_RUNNER_FAILURE'] = failure or ''
            if suite:
                # Only this child collector is fake. The actual suite must resolve
                # inputs, propagate statuses and reject diagnostics itself.
                (scripts / 'godot-probe-coverage.ps1').write_text('''
param([string]$DataProfile,[string]$OutputDirectory,[string]$Godot,[string]$Scene,[string]$SuccessMarker,
      [switch]$Render,[switch]$IsolatedDesktop,[switch]$DedicatedDisplay,[switch]$SkipBuild,
      [string[]]$RequiredModules,[string]$ProbeOptions)
$ErrorActionPreference='Stop'
foreach($path in @($DataProfile,$Godot,$OutputDirectory)) {
    if(-not [IO.Path]::IsPathRooted($path)) {throw 'A suite input was not resolved before collection.'}
}
if(-not(Test-Path -LiteralPath $DataProfile -PathType Leaf) -or -not(Test-Path -LiteralPath $Godot -PathType Leaf)) {
    throw 'A resolved suite input is missing.'
}
[IO.Directory]::CreateDirectory($OutputDirectory)|Out-Null
@{profile=$DataProfile;engine=$Godot;skipBuild=[bool]$SkipBuild;options=$ProbeOptions} |
    ConvertTo-Json|Set-Content -LiteralPath (Join-Path $OutputDirectory 'observed.json')
if($ProbeOptions) {Get-Content -LiteralPath $ProbeOptions -Raw|ConvertFrom-Json|Out-Null}
Set-Content -LiteralPath (Join-Path $OutputDirectory 'godot.log') -Value $SuccessMarker
$global:LASTEXITCODE=0
if($Scene -eq 'res://tests/AddonPreferenceFailureProbe.tscn') {
    if($env:ST_RUNNER_FAILURE -eq 'child-failure') {$global:LASTEXITCODE=9}
    if($env:ST_RUNNER_FAILURE -eq 'engine-error') {
        Add-Content -LiteralPath (Join-Path $OutputDirectory 'godot.log') -Value 'ERROR: deliberate orchestration fixture'
    }
}
''')
                setup = ''
                arguments = '-DedicatedDisplay'
            else:
                core_scripts = root / 'checkout/deps/space-tangent-core/scripts/testing'
                core_scripts.mkdir(parents=True)
                for filename in ('data_profile.py', 'coverage_report.py'):
                    shutil.copyfile(original.parents[1] / 'deps/space-tangent-core/scripts/testing' / filename,
                                    core_scripts / filename)
                packages = ''.join(f'<package name="SpaceTangent.{module}"><classes><class filename="SpaceTangent.{module}/Fixture.cs">'
                                   '<lines><line number="1" hits="1" /></lines></class></classes></package>'
                                   for module in ('Core', 'Contracts', 'Application', 'Godot'))
                xml = '<coverage><packages>' + packages + '</packages></coverage>'
                # Retain real profile verification, report parsing and final
                # environment restoration. Fake only the external .NET collector.
                setup = '''
function global:dotnet {
    $global:LASTEXITCODE=0
    if($args -contains 'coverlet') {
        $values=[string[]]$args
        $xml=$values[[Array]::IndexOf($values,'--output')+1]
        $engine=$values[[Array]::IndexOf($values,'--target')+1]
        if(-not [IO.Path]::IsPathRooted($engine) -or -not(Test-Path -LiteralPath $engine -PathType Leaf)) {
            throw 'The collector engine was not resolved before Push-Location.'
        }
        $directory=Split-Path $xml
        $run=Get-Content -LiteralPath (Join-Path $directory 'run.json') -Raw|ConvertFrom-Json
        @{profile=$run.profile;engine=$engine;control=$env:SPACETANGENT_RUNNER_CONTROL} |
            ConvertTo-Json|Set-Content -LiteralPath (Join-Path $directory 'observed.json')
        Set-Content -LiteralPath $xml -Value XML_LITERAL
        $lines=@($run.successMarker)
        foreach($module in @('Core','Contracts','Application','Godot')) {$lines+=('SpaceTangent coverage flushed: SpaceTangent.'+$module)}
        $lines|Set-Content -LiteralPath (Join-Path $directory 'godot.log')
    }
}
'''.replace('XML_LITERAL', literal(xml))
                arguments = "-Scene res://tests/BootstrapSmoke.tscn -SuccessMarker 'Fake path collector completed' -ProbeOptions options.json -SkipBuild"
            wrapper = root / 'invoke.ps1'
            wrapper.write_text("$ErrorActionPreference='Stop'\nSet-Location -LiteralPath " + literal(caller) + '\n' + setup +
                               '\n& ' + literal(scripts / name) + ' -DataProfile inputs.json -OutputDirectory evidence -Godot ' +
                               literal(argument) + ' ' + arguments + '\n')
            result = subprocess.run(['pwsh', '-NoProfile', '-File', str(wrapper)], cwd=process, env=environment,
                                    stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                                    encoding='utf-8', errors='replace', timeout=60)
            output = caller / 'evidence'
            self.assertFalse((process / 'evidence').exists(), result.stdout)
            self.assertTrue(output.is_dir(), result.stdout)
            if failure:
                self.assertNotEqual(0, result.returncode, result.stdout)
                status = json.loads((output / 'status.json').read_text(encoding='utf-8-sig'))
                self.assertEqual('failed', status['status'])
                self.assertEqual(['ownership', 'lifecycle'], status['completed'])
                self.assertEqual('preference-rollback', status['current'])
                return result.stdout
            self.assertEqual(0, result.returncode, result.stdout)
            if suite:
                status = json.loads((output / 'status.json').read_text(encoding='utf-8-sig'))
                self.assertEqual('passed', status['status'])
                self.assertEqual(6, len(status['completed']))
                for index, case in enumerate(status['completed']):
                    observed = json.loads((output / case / 'observed.json').read_text(encoding='utf-8-sig'))
                    self.assertEqual(index > 0, observed['skipBuild'])
                    self.assertTrue(Path(observed['profile']).samefile(profile))
                    self.assertTrue(Path(observed['engine']).samefile(engine))
                capture = json.loads((output / 'soak-options.json').read_text(encoding='utf-8-sig'))['SPACETANGENT_PROBE_CAPTURE']
                capture = Path(capture)
                self.assertTrue(capture.is_absolute())
                self.assertEqual('memory.json', capture.name)
                self.assertTrue(capture.parent.samefile(output))
            else:
                observed = json.loads((output / 'observed.json').read_text(encoding='utf-8-sig'))
                self.assertTrue(Path(observed['profile']).samefile(profile))
                self.assertTrue(Path(observed['engine']).samefile(engine))
                self.assertEqual('caller-option', observed['control'])

    def test_probe_relative_inputs_follow_set_location(self):
        self.exercise(False)

    def test_resource_suite_relative_inputs_follow_set_location(self):
        self.exercise(True)

    @unittest.skipUnless(os.name == 'nt', 'Windows 8.3 path aliases are platform-specific.')
    def test_resource_evidence_accepts_short_and_long_directory_names(self):
        self.exercise(True, short_path=True)

    def test_both_runners_resolve_an_engine_from_path(self):
        for suite in (False, True):
            with self.subTest(suite=suite):
                self.exercise(suite, 'path')

    def test_absolute_engine_wins_over_same_name_in_path(self):
        for suite in (False, True):
            with self.subTest(suite=suite):
                self.exercise(suite, 'absolute')

    def test_resource_suite_records_a_child_failure(self):
        self.assertIn('Resource probe failed: preference-rollback', self.exercise(True, failure='child-failure'))

    def test_resource_suite_rejects_unexpected_native_diagnostics(self):
        self.assertIn('Unexpected engine diagnostics: preference-rollback', self.exercise(True, failure='engine-error'))


if __name__ == '__main__':
    unittest.main()
