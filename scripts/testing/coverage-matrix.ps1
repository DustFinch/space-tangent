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
. (Join-Path $PSScriptRoot 'runner-paths.ps1')
$CoreCheckout=Resolve-Input $CoreCheckout
$out=Resolve-Input $OutputDirectory
$manifestPath=Resolve-Input $Manifest
$profilesPath=Resolve-Input $Profiles
$Godot=Resolve-Engine $Godot
$matrix=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
$profilesMap=Get-Content -LiteralPath $profilesPath -Raw | ConvertFrom-Json -AsHashtable
foreach ($key in @($profilesMap.Keys)) { $profilesMap[$key]=Resolve-Input $profilesMap[$key] }
$required=@('SpaceTangent.Core','SpaceTangent.Contracts','SpaceTangent.Application','SpaceTangent.Godot')
if ($matrix.version -ne 1 -or -not $matrix.runs -or $matrix.runs.Count -gt 256) { throw 'Choose a version 1 matrix with 1 to 256 runs.' }
$names=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($run in $matrix.runs) {
    if ($run.name -notmatch '^[a-z0-9][a-z0-9-]{0,79}$' -or -not $names.Add($run.name)) { throw 'Run names must be unique bounded lowercase identifiers.' }
    if ($run.kind -notin @('core','application','probe')) { throw 'Run kind must be core, application or probe.' }
    if (-not $profilesMap.ContainsKey($run.profile) -or -not (Test-Path -LiteralPath $profilesMap[$run.profile] -PathType Leaf)) { throw "Missing pinned profile: $($run.profile)" }
    if ($run.kind -eq 'probe') {
        if ($run.scene -notmatch '^res://tests/[A-Za-z0-9_-]+\.tscn$' -or [string]::IsNullOrWhiteSpace($run.successMarker)) { throw 'A probe needs an explicit test scene and success marker.' }
        if ($run.ContainsKey('requiredModules')) {
            $modules=$run.requiredModules
            $seenModules=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            if ($modules -isnot [Array] -or $modules.Count -eq 0 -or $modules.Count -gt $required.Count) {
                throw 'Probe coverage modules must be a nonempty unique subset of the measured assemblies.'
            }
            foreach ($module in $modules) {
                if ($module -isnot [string] -or $module -cnotin $required -or -not $seenModules.Add($module)) {
                    throw 'Probe coverage modules must be a nonempty unique subset of the measured assemblies.'
                }
            }
        }
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
function Build-Inputs([string]$root) {
    # Include authored resources of every extension, including new unstaged files.
    # Git excludes ignored caches; the non-Git fallback supports isolated fixtures.
    if (Test-Path -LiteralPath (Join-Path $root '.git')) {
        $paths=@(& git -C $root -c core.quotepath=false ls-files --cached --others --exclude-standard)
        Check 'Build input inventory'
        $files=@($paths | Sort-Object -Unique | Where-Object {
            $_ -match '^(src|tests|tools|bindings|scripts|\.config)/' -or -not $_.Contains('/')
        } | ForEach-Object { $path=Join-Path $root $_; if (Test-Path -LiteralPath $path -PathType Leaf) { Get-Item -LiteralPath $path -Force } })
    } else {
        $files=@(Get-ChildItem -LiteralPath $root -File -Force)
        foreach ($folder in @('src','tests','tools','bindings','scripts','.config')) {
            $base=Join-Path $root $folder
            if (Test-Path -LiteralPath $base -PathType Container) { $files+=Get-ChildItem -LiteralPath $base -Recurse -File -Force }
        }
    }
    return $files | Where-Object { $_.FullName -notmatch '[\\/](bin|obj|\.godot|__pycache__)[\\/]' } | Sort-Object FullName
}
function Core-Production-Snapshot([string]$root) {
    $snapshot=[ordered]@{}
    foreach ($file in (Build-Inputs $root)) {
        $relative=[IO.Path]::GetRelativePath($root,$file.FullName).Replace('\','/')
        # These are the shared assemblies emitted by both test graphs. Native,
        # CLI and editor consumers have separate coverage and need not match.
        if ($relative.Contains('/') -and
            -not $relative.StartsWith('src/SpaceTangent.Core/',[StringComparison]::Ordinal) -and
            -not $relative.StartsWith('src/SpaceTangent.Contracts/',[StringComparison]::Ordinal)) { continue }
        # Git's text conversion can give equivalent checkouts different LF/CRLF
        # bytes. Normalize only that conversion for this cross-checkout comparison;
        # Source-Snapshot below still records each tree's exact bytes.
        $bytes=[IO.File]::ReadAllBytes($file.FullName)
        $textExtensions=@('.cs','.csproj','.props','.targets','.json','.txt','.tsv','.csv','.xml','.ps1','.py','.md','.yml','.yaml','.cfg','.godot','.tscn','.gdshader','.gdshaderinc')
        if ($file.Extension -in $textExtensions -or $file.Name -in @('.gitattributes','.gitignore','.editorconfig','COPYING','LICENSE')) {
            try {
                $text=[Text.UTF8Encoding]::new($false,$true).GetString($bytes)
                if (-not $text.Contains([char]0)) { $bytes=[Text.Encoding]::UTF8.GetBytes($text.Replace("`r`n","`n")) }
            } catch [Text.DecoderFallbackException] { } # Non-text resources retain their exact bytes.
        }
        $snapshot[$relative]=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
    }
    return $snapshot
}
function Source-Snapshot {
    $snapshot=[ordered]@{}
    foreach ($item in @(@('application',$checkout),@('core',$CoreCheckout),@('dependency',(Join-Path $checkout 'deps/space-tangent-core')))) {
        foreach ($file in (Build-Inputs $item[1])) {
            $key=$item[0]+'/'+[IO.Path]::GetRelativePath($item[1],$file.FullName).Replace('\','/')
            $snapshot[$key]=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
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
    $sharedCore=Core-Production-Snapshot $CoreCheckout
    $sharedDependency=Core-Production-Snapshot (Join-Path $checkout 'deps/space-tangent-core')
    $sharedText=$sharedCore | ConvertTo-Json -Compress
    if ($sharedText -cne ($sharedDependency | ConvertTo-Json -Compress)) {
        throw 'Core checkout and application dependency differ; their coverage cannot be combined.'
    }
    $state.sharedCoreInputFiles=$sharedCore.Count
    $state.sharedCoreFingerprint=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($sharedText)))
    & dotnet tool restore
    Check 'Pinned collector restore'
    foreach ($run in $matrix.runs) {
        $dir=Join-Path $out $run.name
        $log=Join-Path $out ($run.name+'.log')
        $profile=$profilesMap[$run.profile]
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
    $rejectFinishedMatrix=$state.status -eq 'passed' -and -not $state.sourcesUnchanged
    if ($rejectFinishedMatrix) {
        $state.status='failed'; $state.error='Sources changed during the matrix; reports cannot be accepted together.'
    }
    $state.finishedUtc=[DateTime]::UtcNow.ToString('o')
    $state | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $out 'status.json') -Encoding utf8
    Get-ChildItem Env:SPACETANGENT_* | Remove-Item
    foreach ($entry in $saved.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key,$entry.Value,'Process') }
    Pop-Location
    if ($rejectFinishedMatrix) { throw $state.error }
}
