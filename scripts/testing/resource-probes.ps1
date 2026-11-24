# SPDX-License-Identifier: GPL-2.0-or-later
param(
    [Parameter(Mandatory)][string]$DataProfile,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$Godot,
    [switch]$IsolatedDesktop,
    [switch]$DedicatedDisplay
)
$ErrorActionPreference='Stop'
if (-not $IsolatedDesktop -and -not $DedicatedDisplay) { throw 'Choose an inactive desktop or an explicit dedicated display.' }
if ($IsolatedDesktop -and -not $IsWindows) { throw 'Inactive desktop isolation requires Windows.' }
foreach ($file in @($DataProfile, $Godot)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Required file missing: $file" }
}
$root=[IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $root) { throw 'Use a fresh resource-suite directory.' }
[IO.Directory]::CreateDirectory($root) | Out-Null
$runs=@(
    @{name='ownership'; scene='GpuAssetOwnershipProbe'; marker='SpaceTangent GPU asset ownership probe passed'; modules=@('SpaceTangent.Godot')},
    @{name='lifecycle'; scene='AddonLifecycleProbe'; marker='SpaceTangent add-on lifecycle probe passed'},
    @{name='preference-rollback'; scene='AddonPreferenceFailureProbe'; marker='SpaceTangent add-on preference failure probe passed'},
    @{name='binding-recovery'; scene='AddonBindingFailureProbe'; marker='SpaceTangent add-on binding failure probe passed'},
    @{name='closed-viewport'; scene='ClosedViewportProbe'; marker='SpaceTangent closed viewport probe passed'},
    @{name='resource-soak'; scene='ResourceLifecycleProbe'; marker='SpaceTangent resource lifecycle probe passed'}
)
$state=@{status='running'; completed=@(); startedUtc=[DateTime]::UtcNow.ToString('o')}
function Save-State { $state | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $root 'status.json') -Encoding utf8 }
Save-State
try {
    for ($index=0; $index -lt $runs.Count; $index++) {
        $run=$runs[$index]
        $state.current=$run.name; Save-State
        $parameters=@{DataProfile=$DataProfile; OutputDirectory=(Join-Path $root $run.name); Godot=$Godot;
            Scene=('res://tests/'+$run.scene+'.tscn'); SuccessMarker=$run.marker; Render=$true;
            IsolatedDesktop=$IsolatedDesktop; DedicatedDisplay=$DedicatedDisplay; SkipBuild=($index -gt 0)}
        if ($run.ContainsKey('modules')) { $parameters.RequiredModules=$run.modules }
        if ($run.name -eq 'resource-soak') {
            $options=Join-Path $root 'soak-options.json'
            @{SPACETANGENT_PROBE_CAPTURE=(Join-Path $root 'memory.json')} | ConvertTo-Json | Set-Content -LiteralPath $options -Encoding utf8
            $parameters.ProbeOptions=$options
        }
        & (Join-Path $PSScriptRoot 'godot-probe-coverage.ps1') @parameters
        if ($LASTEXITCODE -ne 0) { throw "Resource probe failed: $($run.name)" }
        if ((Get-Content -LiteralPath (Join-Path $parameters.OutputDirectory 'godot.log') -Raw) -match '(?m)^ERROR:|SCRIPT ERROR|SHADER ERROR') {
            throw "Unexpected engine diagnostics: $($run.name)"
        }
        $state.completed+=@($run.name); Save-State
    }
    $state.status='passed'
} catch { $state.status='failed'; $state.error=$_.Exception.Message; throw }
finally { $state.finishedUtc=[DateTime]::UtcNow.ToString('o'); Save-State }
