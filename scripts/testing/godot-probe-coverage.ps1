# SPDX-License-Identifier: GPL-2.0-or-later
param(
    [Parameter(Mandatory=$true)][string]$DataProfile,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][string]$Scene,
    [Parameter(Mandatory=$true)][string]$SuccessMarker,
    [Parameter(Mandatory=$true)][string]$Godot,
    [string]$ProbeOptions,
    [string[]]$UserArguments=@(),
    [ValidateSet(0,1)][int]$ExpectedExitCode=0,
    [switch]$Render,
    [switch]$IsolatedDesktop,
    [switch]$DedicatedDisplay,
    [switch]$SkipBuild,
    [ValidateRange(1,3600)][int]$TimeoutSeconds=600,
    [string[]]$RequiredModules=@('SpaceTangent.Core','SpaceTangent.Contracts','SpaceTangent.Application','SpaceTangent.Godot')
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'runner-paths.ps1')
$checkout=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$core=Join-Path $checkout 'deps/space-tangent-core'
$project=Join-Path $checkout 'src/SpaceTangent.Godot'
$out=Resolve-Input $OutputDirectory
$DataProfile=Resolve-Input $DataProfile
if ($ProbeOptions) { $ProbeOptions=Resolve-Input $ProbeOptions }
$Godot=Resolve-Engine $Godot
if ($Scene -notmatch '^res://tests/[A-Za-z0-9_-]+\.tscn$') { throw 'Choose an explicit test scene.' }
if ($UserArguments.Count -gt 64 -or @($UserArguments | Where-Object {
    [string]::IsNullOrWhiteSpace($_) -or $_.Length -gt 32768 -or $_.Contains('"') -or
        @($_.ToCharArray() | Where-Object { [char]::IsControl($_) }).Count -ne 0
}).Count -ne 0) { throw 'User arguments must be bounded nonempty strings without quotes or control characters.' }
if ($Render -and -not $IsolatedDesktop -and -not $DedicatedDisplay) { throw 'Rendering requires an isolated desktop or an explicit dedicated display.' }
if ($IsolatedDesktop -and -not $IsWindows) { throw 'Inactive desktop isolation requires Windows.' }
if (Test-Path -LiteralPath $out) { throw 'Use a fresh output directory to avoid accepting stale evidence.' }
[IO.Directory]::CreateDirectory($out) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $project '.godot')) | Out-Null
$lock=$null
$saved=@{}
Get-ChildItem Env:SPACETANGENT_* | ForEach-Object { $saved[$_.Name]=$_.Value }
function Check([string]$step) { if ($LASTEXITCODE -ne 0) { throw "$step failed: $LASTEXITCODE" } }
function Quote([string]$value) {
    if ($value.Contains('"')) { throw 'A runner argument contains a double quote.' }
    return '"'+$value+'"'
}
Push-Location $core
try {
    $lock=[IO.File]::Open((Join-Path $project '.godot/coverage.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    & python scripts/testing/data_profile.py verify $DataProfile --require SPACETANGENT_DE440
    Check 'Pinned input verification'
    Get-ChildItem Env:SPACETANGENT_* | Remove-Item
    $profile=Get-Content -LiteralPath $DataProfile -Raw | ConvertFrom-Json
    foreach ($binding in $profile.bindings.PSObject.Properties) {
        $value=if ($binding.Value.kind -eq 'path-list') { ($binding.Value.paths | ForEach-Object path) -join [IO.Path]::PathSeparator } else { $binding.Value.path }
        [Environment]::SetEnvironmentVariable($binding.Name,$value,'Process')
    }
    $options=@{}
    if ($ProbeOptions) {
        $options=Get-Content -LiteralPath $ProbeOptions -Raw | ConvertFrom-Json -AsHashtable
        foreach ($key in $options.Keys) {
            if ($key -notmatch '^SPACETANGENT_[A-Z0-9_]+$' -or $profile.bindings.PSObject.Properties.Name -contains $key -or $options[$key] -isnot [string]) {
                throw "Probe options must be string controls and cannot override pinned inputs: $key"
            }
            [Environment]::SetEnvironmentVariable($key,$options[$key],'Process')
        }
    }
    foreach ($pair in @{
        SPACETANGENT_COVERAGE_CAPTURE='1'; SPACETANGENT_HIPS_OFFLINE='1';
        SPACETANGENT_SETTINGS_PATH=(Join-Path $out 'settings.txt');
        SPACETANGENT_ADDONS_SETTINGS_PATH=(Join-Path $out 'addons.json');
        SPACETANGENT_BOOKMARKS_PATH=(Join-Path $out 'bookmarks.json');
        SPACETANGENT_STARTUP_WORKSPACE=(Join-Path $out 'absent-startup.stscene')
    }.GetEnumerator()) { [Environment]::SetEnvironmentVariable($pair.Key,$pair.Value,'Process') }
    @{ scene=$Scene; profile=$DataProfile; profileSha256=(Get-FileHash -LiteralPath $DataProfile -Algorithm SHA256).Hash;
       options=$options; render=[bool]$Render; isolatedDesktop=[bool]$IsolatedDesktop; requiredModules=$RequiredModules;
       userArguments=$UserArguments; expectedExitCode=$ExpectedExitCode;
       successMarker=$SuccessMarker } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $out 'run.json') -Encoding utf8
    & dotnet tool restore
    Check 'Pinned collector restore'
    if (-not $SkipBuild) {
        & dotnet restore (Join-Path $project 'SpaceTangent.Godot.csproj') --locked-mode
        Check 'Godot restore'
        & dotnet build (Join-Path $project 'SpaceTangent.Godot.csproj') --no-restore
        Check 'Godot build'
        & $Godot --headless --path $project --editor --import --quit
        Check 'Godot import'
    }
    $log=Join-Path $out 'godot.log'
    $arguments='--path '+(Quote $project)+' '+$Scene+' --audio-driver Dummy --log-file '+(Quote $log)
    if ($Render) { $arguments+=' --rendering-method gl_compatibility --rendering-driver opengl3 --resolution 1600x900' }
    else { $arguments='--headless '+$arguments }
    if ($UserArguments.Count -ne 0) { $arguments+=' -- '+(($UserArguments | ForEach-Object { Quote $_ }) -join ' ') }
    $target=$Godot
    if ($IsolatedDesktop) {
        $payload=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($arguments))
        $target=(Get-Command pwsh).Source
        $arguments='-NoProfile -File '+(Quote (Join-Path $PSScriptRoot 'run-isolated.ps1'))+' -Executable '+(Quote $Godot)+
            ' -WorkingDirectory '+(Quote $project)+' -Log '+(Quote ($log+'.stdout'))+' -ArgumentsBase64 '+$payload+' -TimeoutSeconds '+$TimeoutSeconds
    }
    & dotnet tool run coverlet -- (Join-Path $project '.godot/mono/temp/bin/Debug/SpaceTangent.Godot.dll') `
        --include-test-assembly --target $target --targetargs $arguments --single-hit `
        --include '[SpaceTangent.Godot]*' '[SpaceTangent.Application]*' '[SpaceTangent.Core]*' '[SpaceTangent.Contracts]*' `
        --exclude-by-file '**/tests/**' '**/obj/**' --format cobertura --output (Join-Path $out 'coverage.xml')
    if ($LASTEXITCODE -ne $ExpectedExitCode) { throw "Instrumented Godot execution returned $LASTEXITCODE; expected $ExpectedExitCode." }
    $content=Get-Content -LiteralPath $log -Raw
    if (-not $content.Contains($SuccessMarker)) { throw 'The requested probe success marker is absent.' }
    $reportArguments=@((Join-Path $out 'coverage.xml'),'--output',(Join-Path $out 'summary.json'))
    foreach ($module in $RequiredModules) {
        if (-not $content.Contains('SpaceTangent coverage flushed: '+$module)) { throw "Coverage tracker was not flushed: $module" }
        $reportArguments+=@('--require',$module)
    }
    & python scripts/testing/coverage_report.py @reportArguments
    Check 'Required source coverage'
}
finally {
    if ($lock) { $lock.Dispose() }
    Get-ChildItem Env:SPACETANGENT_* | Remove-Item
    foreach ($entry in $saved.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key,$entry.Value,'Process') }
    Pop-Location
}
