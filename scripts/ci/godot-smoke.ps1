# SPDX-License-Identifier: GPL-2.0-or-later
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '../../src/SpaceTangent.Godot'
$checkout = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('SpaceTangent-CI-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($scratch) | Out-Null
$platform = if ($IsWindows) { 'win64' } else { 'linux_x86_64' }
$expected = if ($IsWindows) { 'a2a48473a7414c5f19fab690518caebb738c09ef9601f6bd2388676a7f53b3c0' }
    else { '129f82db7bafd54ae14bb5bb284041c73860e8c7a009a3a026ca5e946cbff247' }
$archive = Join-Path $scratch 'godot.zip'
Invoke-WebRequest "https://github.com/godotengine/godot-builds/releases/download/4.7.2-stable/Godot_v4.7.2-stable_mono_$platform.zip" -OutFile $archive
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) {
    throw 'Godot archive SHA-256 mismatch.'
}
Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $scratch 'engine')
$pattern = if ($IsWindows) { '*mono_win64_console.exe' } else { '*mono_linux.x86_64' }
$executables = @(Get-ChildItem -LiteralPath (Join-Path $scratch 'engine') -Recurse -File -Filter $pattern)
if ($executables.Count -ne 1) { throw 'Expected one Godot executable.' }
$engine = $executables[0].FullName
if (-not $IsWindows) { & chmod +x $engine; if ($LASTEXITCODE -ne 0) { throw 'Could not mark Godot executable.' } }
$data = Join-Path $checkout '.ci'
[IO.Directory]::CreateDirectory($data) | Out-Null
$de440 = Join-Path $data 'de440.440'
if (-not (Test-Path -LiteralPath $de440)) {
    Invoke-WebRequest 'https://ssd.jpl.nasa.gov/ftp/eph/planets/Linux/de440/linux_p1550p2650.440' -OutFile $de440
}
if ((Get-FileHash -LiteralPath $de440 -Algorithm SHA256).Hash.ToLowerInvariant() -ne
    '29915576d0a6555766b99485ac3056ee415e86df4fce282611c31afb329ad062') { throw 'DE440 SHA-256 mismatch.' }
$env:SPACETANGENT_DE440 = $de440
$env:SPACETANGENT_REQUIRE_SIMULATION = '1'
$env:SPACETANGENT_HIPS_OFFLINE = '1'
$env:SPACETANGENT_SETTINGS_PATH = Join-Path $scratch 'settings.txt'
$env:SPACETANGENT_ADDONS_SETTINGS_PATH = Join-Path $scratch 'addons.json'
$env:SPACETANGENT_BOOKMARKS_PATH = Join-Path $scratch 'bookmarks.json'
$env:SPACETANGENT_STARTUP_WORKSPACE = ''
& $engine --headless --path $project --editor --import --quit
if ($LASTEXITCODE -ne 0) { throw 'Godot import failed.' }
$log = Join-Path $scratch 'bootstrap.log'
& $engine --headless --path $project res://tests/BootstrapSmoke.tscn --log-file $log
if ($LASTEXITCODE -ne 0) { throw 'Bootstrap smoke failed.' }
$output = Get-Content -LiteralPath $log -Raw
if ($output -notmatch 'SpaceTangent world ready: DE440' -or $output -notmatch 'SpaceTangent bootstrap smoke passed' -or
    $output -match '(?m)^ERROR:|SCRIPT ERROR|SHADER ERROR') { throw 'The smoke did not prove a clean simulation startup.' }
Write-Output 'Public-data Godot startup passed.'
foreach ($duration in @('NaN', 'Infinity', '0.05', '601')) {
    $movieLog = Join-Path $scratch ('invalid-movie-' + $duration + '.log')
    & $engine --headless --path $project res://tests/StartupCommandProbe.tscn --log-file $movieLog -- --startup-movie $duration --startup-exit
    if ($LASTEXITCODE -ne 1 -or (Get-Content -LiteralPath $movieLog -Raw) -notmatch 'Movie duration must be') {
        throw "The startup command did not reject invalid movie duration: $duration"
    }
}
Write-Output 'Public-data startup movie duration refusals passed.'
