[CmdletBinding()]
param(
    [switch]$Quiet
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repoPrefix = $repoRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) +
    [IO.Path]::DirectorySeparatorChar
$stateRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot '.build\devvit'))

function Assert-DevvitWorkspacePath {
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Devvit writable state must stay beneath '$repoRoot': $fullPath"
    }

    $current = $fullPath
    while ($current -and $current.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
        if ([IO.File]::Exists($current) -or [IO.Directory]::Exists($current)) {
            $attributes = [IO.File]::GetAttributes($current)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Devvit workspace path contains a reparse point or junction: $current"
            }
        }
        if ([string]::Equals($current, $repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
            break
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    return $fullPath
}

$profileRoot = Assert-DevvitWorkspacePath (Join-Path $stateRoot 'profile')
$roamingAppData = Assert-DevvitWorkspacePath (Join-Path $profileRoot 'AppData\Roaming')
$localAppData = Assert-DevvitWorkspacePath (Join-Path $profileRoot 'AppData\Local')
$tempRoot = Assert-DevvitWorkspacePath (Join-Path $stateRoot 'temp')
$npmCache = Assert-DevvitWorkspacePath (Join-Path $stateRoot 'npm-cache')
$npmPrefix = Assert-DevvitWorkspacePath (Join-Path $stateRoot 'npm-prefix')
$npmConfig = Assert-DevvitWorkspacePath (Join-Path $stateRoot 'npmrc')
$npmGlobalConfig = Assert-DevvitWorkspacePath (Join-Path $stateRoot 'global-npmrc')
$npmLogs = Assert-DevvitWorkspacePath (Join-Path $stateRoot 'npm-logs')
$devvitState = Assert-DevvitWorkspacePath (Join-Path $stateRoot 'devvit-state')
$xdgCache = Assert-DevvitWorkspacePath (Join-Path $profileRoot '.cache')
$xdgConfig = Assert-DevvitWorkspacePath (Join-Path $profileRoot '.config')
$xdgData = Assert-DevvitWorkspacePath (Join-Path $profileRoot '.local\share')
$nodeCache = Assert-DevvitWorkspacePath (Join-Path $stateRoot 'node-compile-cache')
$nodeHistory = Assert-DevvitWorkspacePath (Join-Path $stateRoot 'node-repl-history.txt')

New-Item -ItemType Directory -Force -Path @(
    $stateRoot,
    $profileRoot,
    $roamingAppData,
    $localAppData,
    $tempRoot,
    $npmCache,
    $npmPrefix,
    $npmLogs,
    $devvitState,
    $xdgCache,
    $xdgConfig,
    $xdgData,
    $nodeCache
) | Out-Null

if (-not (Test-Path -LiteralPath $npmConfig -PathType Leaf)) {
    New-Item -ItemType File -Path $npmConfig | Out-Null
}
if (-not (Test-Path -LiteralPath $npmGlobalConfig -PathType Leaf)) {
    New-Item -ItemType File -Path $npmGlobalConfig | Out-Null
}

$homeDrive = [IO.Path]::GetPathRoot($profileRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$homePath = $profileRoot.Substring($homeDrive.Length)
$updates = [ordered]@{
    USERPROFILE = $profileRoot
    HOME = $profileRoot
    HOMEDRIVE = $homeDrive
    HOMEPATH = $homePath
    APPDATA = $roamingAppData
    LOCALAPPDATA = $localAppData
    XDG_CACHE_HOME = $xdgCache
    XDG_CONFIG_HOME = $xdgConfig
    XDG_DATA_HOME = $xdgData
    TEMP = $tempRoot
    TMP = $tempRoot
    TMPDIR = $tempRoot
    NPM_CONFIG_CACHE = $npmCache
    NPM_CONFIG_PREFIX = $npmPrefix
    NPM_CONFIG_USERCONFIG = $npmConfig
    NPM_CONFIG_GLOBALCONFIG = $npmGlobalConfig
    NPM_CONFIG_LOGS_DIR = $npmLogs
    NPM_CONFIG_UPDATE_NOTIFIER = 'false'
    NPM_CONFIG_FUND = 'false'
    DEVVIT_ROOT_DIR = $devvitState
    NODE_COMPILE_CACHE = $nodeCache
    NODE_REPL_HISTORY = $nodeHistory
}

foreach ($name in $updates.Keys) {
    [Environment]::SetEnvironmentVariable($name, $updates[$name], 'Process')
}

if (-not $Quiet) {
    Write-Host "Devvit writable tooling state is confined to: $stateRoot"
    Write-Host 'These values apply only to this PowerShell process; close it to restore the prior environment.'
}
