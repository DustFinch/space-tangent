# SPDX-License-Identifier: GPL-2.0-or-later
# Resolve caller inputs before any runner changes the PowerShell location.
# Set-Location does not synchronize .NET's process current directory.
function Resolve-Input([string]$path) {
    return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($path)
}

function Resolve-Engine([string]$path) {
    if (-not [IO.Path]::IsPathRooted($path) -and $path -notmatch '[/\\]') {
        $command=Get-Command -Name ([WildcardPattern]::Escape($path)) -CommandType Application -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($command) { return $command.Source }
    }
    return Resolve-Input $path
}
