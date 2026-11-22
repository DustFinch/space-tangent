# SPDX-License-Identifier: GPL-2.0-or-later
param(
    [Parameter(Mandatory=$true)][string]$Manifest,
    [Parameter(Mandatory=$true)][string]$Profiles,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [Parameter(Mandatory=$true)][string]$Godot,
    [string]$CoreCheckout=(Join-Path $PSScriptRoot '../../deps/space-tangent-core')
)
$ErrorActionPreference='Stop'
$checkout=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$CoreCheckout=[IO.Path]::GetFullPath($CoreCheckout)
$out=[IO.Path]::GetFullPath($OutputDirectory)
$manifestPath=[IO.Path]::GetFullPath($Manifest)
$profilesPath=[IO.Path]::GetFullPath($Profiles)
$matrix=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
$profilesMap=Get-Content -LiteralPath $profilesPath -Raw | ConvertFrom-Json -AsHashtable
$required=@('SpaceTangent.Core','SpaceTangent.Contracts','SpaceTangent.Application','SpaceTangent.Godot')
if ($matrix.version -ne 1 -or -not $matrix.runs -or $matrix.runs.Count -gt 256) { throw 'Choose a version 1 matrix with 1 to 256 runs.' }
$names=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($run in $matrix.runs) {
    if ($run.name -notmatch '^[a-z0-9][a-z0-9-]{0,79}$' -or -not $names.Add($run.name)) { throw 'Run names must be unique bounded lowercase identifiers.' }
    if ($run.kind -notin @('core','application','probe')) { throw 'Run kind must be core, application or probe.' }
    if (-not $profilesMap.ContainsKey($run.profile) -or -not (Test-Path -LiteralPath $profilesMap[$run.profile] -PathType Leaf)) { throw "Missing pinned profile: $($run.profile)" }
    if ($run.kind -eq 'probe') {
        if ($run.scene -notmatch '^res://tests/[A-Za-z0-9_-]+\.tscn$' -or [string]::IsNullOrWhiteSpace($run.successMarker)) { throw 'A probe needs an explicit test scene and success marker.' }
        if ($run.options -and $run.options -isnot [Collections.IDictionary]) { throw 'Probe options must be a string map.' }
        foreach ($key in $run.options.Keys) {
            if ($key -notmatch '^SPACETANGENT_[A-Z0-9_]+$' -or $run.options[$key] -isnot [string]) { throw 'Probe options must be a string map of SPACETANGENT_ controls.' }
        }
    }
}
if (Test-Path -LiteralPath $out) { throw 'Use a fresh matrix output directory; previous evidence cannot be reused.' }
[IO.Directory]::CreateDirectory($out) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $out 'controls')) | Out-Null
function Check([string]$step) { if ($LASTEXITCODE -ne 0) { throw "$step failed: $LASTEXITCODE" } }
function Source-Snapshot {
    $snapshot=[ordered]@{}
    foreach ($item in @(@('application',$checkout),@('core',$CoreCheckout))) {
        foreach ($folder in @('src','tests','tools','bindings')) {
            $base=Join-Path $item[1] $folder
            if (-not (Test-Path -LiteralPath $base -PathType Container)) { continue }
            foreach ($file in (Get-ChildItem -LiteralPath $base -Recurse -File | Where-Object {
                $_.Extension -in @('.cs','.csproj','.props','.targets','.tscn','.gdshader') -and $_.FullName -notmatch '[\\/](bin|obj|\.godot)[\\/]'
            } | Sort-Object FullName)) {
                $key=$item[0]+'/'+[IO.Path]::GetRelativePath($item[1],$file.FullName).Replace('\','/')
                $snapshot[$key]=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            }
        }
    }
    return $snapshot
}
function Quote([string]$value) {
    if ($value.Contains('"')) { throw 'A process argument contains a double quote.' }
    return '"'+$value+'"'
}
$before=Source-Snapshot
$before | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $out 'sources-before.json') -Encoding utf8
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $out 'matrix.json')
$state=@{version=1;startedUtc=[DateTime]::UtcNow.ToString('o');status='running';runs=@();reports=@();
    manifestSha256=(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash;
    profilesSha256=(Get-FileHash -LiteralPath $profilesPath -Algorithm SHA256).Hash}
$saved=@{}
Get-ChildItem Env:SPACETANGENT_* | ForEach-Object { $saved[$_.Name]=$_.Value }
$probeBuilt=$false
Push-Location $CoreCheckout
try {
    & dotnet tool restore
    Check 'Pinned collector restore'
    foreach ($run in $matrix.runs) {
        $dir=Join-Path $out $run.name
        $log=Join-Path $out ($run.name+'.log')
        $profile=[IO.Path]::GetFullPath($profilesMap[$run.profile])
        $item=@{name=$run.name;kind=$run.kind;profile=$run.profile;startedUtc=[DateTime]::UtcNow.ToString('o');status='running';
            profileSha256=(Get-FileHash -LiteralPath $profile -Algorithm SHA256).Hash}
        $state.runs+=,$item
        $state | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $out 'status.json') -Encoding utf8
        Write-Output ('Starting '+$run.name)
        try {
            if ($run.kind -eq 'probe') {
                $options=@{}
                foreach ($key in $run.options.Keys) { $options[$key]=$run.options[$key].Replace('{output}',$dir) }
                $optionsPath=Join-Path $out ('controls/'+$run.name+'.json')
                ConvertTo-Json -InputObject $options | Set-Content -LiteralPath $optionsPath -Encoding utf8
                $parameters=@{DataProfile=$profile;OutputDirectory=$dir;Scene=$run.scene;SuccessMarker=$run.successMarker;Godot=$Godot;
                    ProbeOptions=$optionsPath;SkipBuild=$probeBuilt;TimeoutSeconds=900}
                if ($run.render) { $parameters.Render=$true; $parameters.IsolatedDesktop=$true }
                if ($run.requiredModules) { $parameters.RequiredModules=[string[]]$run.requiredModules }
                if ($run.ContainsKey('expectedExitCode')) { $parameters.ExpectedExitCode=$run.expectedExitCode }
                if ($run.userArguments) { $parameters.UserArguments=[string[]]@($run.userArguments | ForEach-Object { $_.Replace('{output}',$dir) }) }
                & (Join-Path $PSScriptRoot 'godot-probe-coverage.ps1') @parameters *> $log
                $probeBuilt=$true
            } else {
                [IO.Directory]::CreateDirectory($dir) | Out-Null
                $root=if ($run.kind -eq 'core') { $CoreCheckout } else { $checkout }
                $assembly=if ($run.kind -eq 'core') { 'SpaceTangent.Core.Tests' } else { 'SpaceTangent.Application.Tests' }
                $project=Join-Path $root ('tests/'+$assembly+'/'+$assembly+'.csproj')
                & python scripts/testing/data_profile.py verify $profile *> $log
                Check 'Pinned unit data verification'
                & dotnet restore $project --locked-mode *>> $log
                Check 'Unit restore'
                & dotnet build $project --no-restore '-maxcpucount:2' *>> $log
                Check 'Unit build'
                Copy-Item -LiteralPath (Join-Path ([IO.Path]::GetDirectoryName($project)) 'bin/Debug/net8.0') -Destination (Join-Path $dir 'bin') -Recurse
                $dll=Join-Path $dir ('bin/'+$assembly+'.dll')
                $arguments='vstest '+(Quote $dll)+' /ResultsDirectory:'+(Quote $dir)+' /Logger:"trx;LogFileName=tests.trx"'
                if ($run.filter) { $arguments+=' /TestCaseFilter:'+(Quote $run.filter) }
                & python scripts/testing/data_profile.py run --profile $profile -- dotnet tool run coverlet -- $dll --target dotnet --targetargs $arguments `
                    --single-hit --include '[SpaceTangent.Core]*' '[SpaceTangent.Contracts]*' '[SpaceTangent.Application]*' `
                    --exclude-by-file '**/obj/**' --format cobertura --output (Join-Path $dir 'coverage.xml') *>> $log
                Check 'Instrumented unit execution'
                [xml]$trx=Get-Content -LiteralPath (Join-Path $dir 'tests.trx') -Raw
                $counts=$trx.TestRun.ResultSummary.Counters
                if (-not $counts -or [int]$counts.failed -ne 0 -or [int]$counts.passed -le 0) { throw 'Unit evidence needs passed tests and no failures.' }
                $results=@($trx.TestRun.Results.UnitTestResult)
                $passed=@($results | Where-Object outcome -eq 'Passed').Count
                $skipped=@($results | Where-Object outcome -eq 'NotExecuted').Count
                # VSTest can leave Counters.notExecuted at zero for xUnit skips.
                # Check the actual results instead of inventing a zero skip count.
                if ($results.Count -ne [int]$counts.total -or $passed -ne [int]$counts.passed -or
                    ($passed+$skipped) -ne $results.Count) { throw 'Unit counters and individual outcomes do not agree.' }
                $item.tests=@{passed=$passed;failed=0;total=$results.Count;skipped=$skipped}
            }
            $report=Join-Path $dir 'coverage.xml'
            if (-not (Test-Path -LiteralPath $report -PathType Leaf)) { throw 'Missing fresh coverage report.' }
            $item.status='passed'
            $item.reportSha256=(Get-FileHash -LiteralPath $report -Algorithm SHA256).Hash
            $state.reports+=,$report
        } catch {
            $item.status='failed'; $item.error=$_.Exception.Message
            throw
        } finally {
            $item.finishedUtc=[DateTime]::UtcNow.ToString('o')
            $state | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $out 'status.json') -Encoding utf8
        }
        Write-Output ('Passed '+$run.name)
    }
    $after=Source-Snapshot
    $after | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $out 'sources-after.json') -Encoding utf8
    if (($before | ConvertTo-Json -Compress) -cne ($after | ConvertTo-Json -Compress)) { throw 'Sources changed during the matrix; reports cannot be accepted together.' }
    $arguments=@('scripts/testing/coverage_report.py')+$state.reports+@('--output',(Join-Path $out 'summary.json'))
    foreach ($name in $required) { $arguments+=@('--require',$name) }
    & python @arguments
    Check 'Fresh source coverage summary'
    $summary=Get-Content -LiteralPath (Join-Path $out 'summary.json') -Raw | ConvertFrom-Json -AsHashtable
    $below=@($required | Where-Object { $summary.modules[$_].percent -lt 95 })
    if ($below.Count) { throw ('Fresh coverage is below 95%: '+($below -join ', ')) }
    $state.status='passed'
} catch {
    $state.status='failed'; $state.error=$_.Exception.Message
    throw
} finally {
    # Also preserve an interrupted/failed prefix for diagnosis. Such a prefix
    # never receives a successful matrix status or bypasses the final gate.
    $after=Source-Snapshot
    $after | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $out 'sources-after.json') -Encoding utf8
    $state.sourcesUnchanged=($before | ConvertTo-Json -Compress) -ceq ($after | ConvertTo-Json -Compress)
    $state.finishedUtc=[DateTime]::UtcNow.ToString('o')
    $state | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $out 'status.json') -Encoding utf8
    Get-ChildItem Env:SPACETANGENT_* | Remove-Item
    foreach ($entry in $saved.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key,$entry.Value,'Process') }
    Pop-Location
}
