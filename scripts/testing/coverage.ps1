# SPDX-License-Identifier: GPL-2.0-or-later
param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$CoreCheckout = (Join-Path $PSScriptRoot '../../deps/space-tangent-core'),
    [string]$CoreDataProfile,
    [string]$ApplicationDataProfile,
    [string]$Godot = 'godot',
    [string]$Scene = 'res://tests/BootstrapSmoke.tscn',
    [switch]$Render
)
$ErrorActionPreference = 'Stop'
$checkout = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$CoreCheckout = [IO.Path]::GetFullPath($CoreCheckout)
$output = [IO.Path]::GetFullPath($OutputDirectory)
$staging = Join-Path $output ('staging-' + [guid]::NewGuid().ToString('N'))
$project = Join-Path $checkout 'src/SpaceTangent.Godot'
$manifest = Join-Path $CoreCheckout '.config/dotnet-tools.json'
$profileTool = Join-Path $CoreCheckout 'scripts/testing/data_profile.py'
$reportTool = Join-Path $CoreCheckout 'scripts/testing/coverage_report.py'
[IO.Directory]::CreateDirectory($staging) | Out-Null

function Check([string]$step) {
    if ($LASTEXITCODE -ne 0) { throw "$step failed with exit code $LASTEXITCODE." }
}
function Quote([string]$value) {
    if ($value.Contains('"')) { throw 'A runner argument contains a double quote.' }
    return '"' + $value + '"'
}
function Set-DataProfile([string]$path) {
    Get-ChildItem Env:SPACETANGENT_* | Remove-Item
    if (-not [string]::IsNullOrWhiteSpace($path)) {
        & python $profileTool verify $path --require SPACETANGENT_DE440
        Check 'Data verification'
        $profile = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        foreach ($entry in $profile.bindings.PSObject.Properties) {
            $value = if ($entry.Value.kind -eq 'path-list') { ($entry.Value.paths | ForEach-Object path) -join [IO.Path]::PathSeparator }
                else { $entry.Value.path }
            [Environment]::SetEnvironmentVariable($entry.Name, $value, 'Process')
        }
    }
    $env:SPACETANGENT_HIPS_OFFLINE = '1'
}
function Collect-Unit([string]$name, [string]$testProject, [string]$data) {
    Set-DataProfile $data
    & dotnet restore $testProject --locked-mode
    Check "$name restore"
    & dotnet build $testProject --no-restore
    Check "$name build"
    $assembly = [IO.Path]::GetFileNameWithoutExtension($testProject)
    $directory = Join-Path ([IO.Path]::GetDirectoryName($testProject)) 'bin/Debug/net8.0'
    $copy = Join-Path $staging $name
    Copy-Item -LiteralPath $directory -Destination $copy -Recurse
    $dll = Join-Path $copy ($assembly + '.dll')
    $results = Join-Path $output $name
    [IO.Directory]::CreateDirectory($results) | Out-Null
    $arguments = 'vstest ' + (Quote $dll) + ' /ResultsDirectory:' + (Quote $results) + ' /Logger:trx;LogFileName=tests.trx'
    & dotnet tool run coverlet -- $dll --target dotnet --targetargs $arguments `
        --single-hit --include '[SpaceTangent.Core]*' '[SpaceTangent.Contracts]*' '[SpaceTangent.Application]*' `
        --exclude-by-file '**/obj/**' --format cobertura --output (Join-Path $results 'coverage.xml')
    Check "$name coverage"
}

# Preserve the caller's data/settings variables, even when a step fails.
$saved = @{}
Get-ChildItem Env:SPACETANGENT_* | ForEach-Object { $saved[$_.Name] = $_.Value }
Push-Location $CoreCheckout
try {
    & dotnet tool restore --tool-manifest $manifest
    Check 'Coverage tool restore'
    Collect-Unit 'core' (Join-Path $CoreCheckout 'tests/SpaceTangent.Core.Tests/SpaceTangent.Core.Tests.csproj') $CoreDataProfile
    Collect-Unit 'application' (Join-Path $checkout 'tests/SpaceTangent.Application.Tests/SpaceTangent.Application.Tests.csproj') $ApplicationDataProfile
    Set-DataProfile $ApplicationDataProfile
    if ([string]::IsNullOrWhiteSpace($env:SPACETANGENT_DE440)) {
        throw 'Godot coverage requires a verified application data profile with DE440.'
    }
    $env:SPACETANGENT_COVERAGE_CAPTURE = '1'
    $env:SPACETANGENT_REQUIRE_SIMULATION = '1'
    $env:SPACETANGENT_SETTINGS_PATH = Join-Path $staging 'settings.txt'
    $env:SPACETANGENT_ADDONS_SETTINGS_PATH = Join-Path $staging 'addons.json'
    $env:SPACETANGENT_BOOKMARKS_PATH = Join-Path $staging 'bookmarks.json'
    $env:SPACETANGENT_STARTUP_WORKSPACE = Join-Path $staging 'absent-startup.stscene'
    & dotnet restore (Join-Path $project 'SpaceTangent.Godot.csproj') --locked-mode
    Check 'Godot restore'
    & dotnet build (Join-Path $project 'SpaceTangent.Godot.csproj') --no-restore
    Check 'Godot build'
    & $Godot --headless --path $project --editor --import --quit
    Check 'Godot resource import'
    $arguments = '--path ' + (Quote $project) + ' ' + $Scene + ' --audio-driver Dummy --log-file ' + (Quote (Join-Path $output 'godot.log'))
    if (-not $Render) { $arguments = '--headless ' + $arguments }
    $results = Join-Path $output 'godot'
    [IO.Directory]::CreateDirectory($results) | Out-Null
    & dotnet tool run coverlet -- (Join-Path $project '.godot/mono/temp/bin/Debug/SpaceTangent.Godot.dll') `
        --include-test-assembly --target $Godot --targetargs $arguments --single-hit `
        --include '[SpaceTangent.Godot]*' '[SpaceTangent.Application]*' '[SpaceTangent.Core]*' '[SpaceTangent.Contracts]*' `
        --exclude-by-file '**/tests/**' '**/obj/**' --format cobertura --output (Join-Path $results 'coverage.xml')
    Check 'Godot coverage'
    & python $reportTool (Join-Path $output 'core/coverage.xml') (Join-Path $output 'application/coverage.xml') `
        (Join-Path $output 'godot/coverage.xml') --require SpaceTangent.Core --require SpaceTangent.Contracts `
        --require SpaceTangent.Application --require SpaceTangent.Godot --output (Join-Path $output 'summary.json')
    Check 'Coverage completeness'
}
finally {
    Get-ChildItem Env:SPACETANGENT_* | Remove-Item
    foreach ($entry in $saved.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process') }
    Pop-Location
}
