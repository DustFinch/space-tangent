# SPDX-License-Identifier: GPL-2.0-or-later
param(
    [Parameter(Mandatory=$true)][string]$Godot,
    [Parameter(Mandatory=$true)][string]$De440,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [switch]$SkipBuild
)
$ErrorActionPreference='Stop'
$checkout=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$root=[IO.Path]::GetFullPath($OutputDirectory)
$De440=[IO.Path]::GetFullPath($De440)
if (Test-Path -LiteralPath $root) { throw 'Use a fresh public-data coverage directory.' }
if ((Get-FileHash -LiteralPath $De440 -Algorithm SHA256).Hash.ToLowerInvariant() -ne
    '29915576d0a6555766b99485ac3056ee415e86df4fce282611c31afb329ad062') { throw 'Public DE440 SHA-256 mismatch.' }
[IO.Directory]::CreateDirectory($root) | Out-Null
$profile=Join-Path $root 'profile.json'
$options=Join-Path $root 'options.json'
& python (Join-Path $checkout 'deps/space-tangent-core/scripts/testing/data_profile.py') capture --output $profile --bind "SPACETANGENT_DE440=$De440"
if ($LASTEXITCODE -ne 0) { throw 'Public-data profile capture failed.' }
@{SPACETANGENT_REQUIRE_SIMULATION='1'} | ConvertTo-Json | Set-Content -LiteralPath $options -Encoding utf8
$capture=Join-Path $root 'capture'
& (Join-Path $checkout 'scripts/testing/godot-probe-coverage.ps1') -DataProfile $profile -OutputDirectory $capture `
    -Scene res://tests/BootstrapSmoke.tscn -SuccessMarker 'SpaceTangent bootstrap smoke passed' `
    -Godot $Godot -ProbeOptions $options -SkipBuild:$SkipBuild
$ownership=Join-Path $root 'native-ownership'
& (Join-Path $checkout 'scripts/testing/godot-probe-coverage.ps1') -DataProfile $profile -OutputDirectory $ownership `
    -Scene res://tests/GpuAssetOwnershipProbe.tscn -SuccessMarker 'SpaceTangent GPU asset ownership probe passed' `
    -Godot $Godot -RequiredModules @('SpaceTangent.Godot') -SkipBuild
foreach ($run in @($capture, $ownership)) {
    if ((Get-Content -LiteralPath (Join-Path $run 'godot.log') -Raw) -match '(?m)^ERROR:|SCRIPT ERROR|SHADER ERROR') {
        throw 'Public-data Godot collection produced unexpected engine diagnostics.'
    }
}
# This public-data headless baseline is deliberately separate from the full optional-data GPU matrix.
$report=@((Join-Path $capture 'coverage.xml'),(Join-Path $ownership 'coverage.xml'),'--require','SpaceTangent.Core','--require','SpaceTangent.Contracts',
    '--require','SpaceTangent.Application','--require','SpaceTangent.Godot','--output',(Join-Path $root 'summary.json'))
if ($env:GITHUB_STEP_SUMMARY) {
    'Public-data headless startup and native resource ownership; optional scientific datasets and rendered GPU acceptance are separate.' |
        Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding utf8
    $report+=@('--markdown-output',$env:GITHUB_STEP_SUMMARY)
}
& python (Join-Path $checkout 'deps/space-tangent-core/scripts/testing/coverage_report.py') @report
if ($LASTEXITCODE -ne 0) { throw 'Public-data Godot coverage report failed.' }
