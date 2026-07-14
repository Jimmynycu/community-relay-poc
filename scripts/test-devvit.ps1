[CmdletBinding()]
param(
    [string]$NpmPath
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$devvitRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'devvit'))
$environmentNames = @(
    'USERPROFILE', 'HOME', 'HOMEDRIVE', 'HOMEPATH', 'APPDATA', 'LOCALAPPDATA',
    'XDG_CACHE_HOME', 'XDG_CONFIG_HOME', 'XDG_DATA_HOME', 'TEMP', 'TMP', 'TMPDIR',
    'NPM_CONFIG_CACHE', 'NPM_CONFIG_PREFIX', 'NPM_CONFIG_USERCONFIG',
    'NPM_CONFIG_GLOBALCONFIG', 'NPM_CONFIG_LOGS_DIR', 'NPM_CONFIG_UPDATE_NOTIFIER',
    'NPM_CONFIG_FUND', 'DEVVIT_ROOT_DIR', 'NODE_COMPILE_CACHE', 'NODE_REPL_HISTORY'
)
$savedEnvironment = @{}
foreach ($name in $environmentNames) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

function Invoke-Npm {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & $script:npmExecutable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "npm exited with code $LASTEXITCODE."
    }
}

function Assert-NoReparseTree {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push([IO.Path]::GetFullPath($Path))
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        $attributes = [IO.File]::GetAttributes($current)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Devvit build/test tree contains a reparse point: $current"
        }
        if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
            foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($current)) {
                $pending.Push($entry)
            }
        }
    }
}

if ([string]::IsNullOrWhiteSpace($NpmPath)) {
    $npmCommand = Get-Command npm -CommandType Application -ErrorAction Stop
    $script:npmExecutable = $npmCommand.Source
}
else {
    $script:npmExecutable = [IO.Path]::GetFullPath($NpmPath)
}
if (-not (Test-Path -LiteralPath $script:npmExecutable -PathType Leaf)) {
    throw "npm executable not found: $script:npmExecutable"
}

Assert-NoReparseTree -Path (Join-Path $devvitRoot 'node_modules')
Assert-NoReparseTree -Path (Join-Path $devvitRoot 'dist')

try {
    . (Join-Path $PSScriptRoot 'use-devvit-workspace.ps1') -Quiet
    Push-Location $repoRoot
    try {
        Write-Host "Using npm: $script:npmExecutable"
        Write-Host "Devvit source: $devvitRoot"
        Invoke-Npm -Arguments @('ci', '--prefix', $devvitRoot, '--no-audit', '--no-fund')
        Assert-NoReparseTree -Path (Join-Path $devvitRoot 'node_modules')
        Invoke-Npm -Arguments @('test', '--prefix', $devvitRoot)
        Invoke-Npm -Arguments @('audit', '--prefix', $devvitRoot, '--audit-level=low')
        Write-Host 'Workspace-contained Devvit install, validation, tests, bundle, and audit passed.'
    }
    finally {
        Pop-Location
    }
}
finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
}
