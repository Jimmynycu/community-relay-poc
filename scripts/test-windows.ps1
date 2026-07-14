[CmdletBinding()]
param(
    [string]$DotNetPath,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repoPrefix = $repoRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) +
    [IO.Path]::DirectorySeparatorChar

function Assert-WorkspacePath {
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Build/test state must stay beneath '$repoRoot': $fullPath"
    }

    $current = $fullPath
    while ($current -and $current.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
        if ([IO.File]::Exists($current) -or [IO.Directory]::Exists($current)) {
            $attributes = [IO.File]::GetAttributes($current)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Workspace build/test path contains a reparse point or junction: $current"
            }
        }
        if ([string]::Equals($current, $repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
            break
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }

    return $fullPath
}

function Assert-NoReparseTree {
    param([Parameter(Mandatory)][string]$Path)

    $safePath = Assert-WorkspacePath -Path $Path
    if (-not (Test-Path -LiteralPath $safePath)) {
        return
    }

    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($safePath)
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        $attributes = [IO.File]::GetAttributes($current)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Workspace build/test state contains a reparse point: $current"
        }
        if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
            foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($current)) {
                $pending.Push($entry)
            }
        }
    }
}

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & $script:dotnetExecutable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet exited with code $LASTEXITCODE."
    }
}

if ([string]::IsNullOrWhiteSpace($DotNetPath)) {
    $dotnetCommand = Get-Command dotnet -CommandType Application -ErrorAction Stop
    $script:dotnetExecutable = $dotnetCommand.Source
}
else {
    $script:dotnetExecutable = [IO.Path]::GetFullPath($DotNetPath)
}
if (-not (Test-Path -LiteralPath $script:dotnetExecutable -PathType Leaf)) {
    throw "dotnet executable not found: $script:dotnetExecutable"
}

$stateRoot = Assert-WorkspacePath (Join-Path $repoRoot '.build\test-windows')
$profileRoot = Assert-WorkspacePath (Join-Path $stateRoot 'profile')
$roamingAppData = Assert-WorkspacePath (Join-Path $profileRoot 'AppData\Roaming')
$localAppData = Assert-WorkspacePath (Join-Path $profileRoot 'AppData\Local')
$dotnetHome = Assert-WorkspacePath (Join-Path $stateRoot 'dotnet-home')
$nugetPackages = Assert-WorkspacePath (Join-Path $stateRoot 'nuget-packages')
$nugetHttpCache = Assert-WorkspacePath (Join-Path $stateRoot 'nuget-http-cache')
$nugetPluginCache = Assert-WorkspacePath (Join-Path $stateRoot 'nuget-plugin-cache')
$nugetScratch = Assert-WorkspacePath (Join-Path $stateRoot 'nuget-scratch')
$tempRoot = Assert-WorkspacePath (Join-Path $stateRoot 'temp')
$bundleCache = Assert-WorkspacePath (Join-Path $stateRoot 'bundle-cache')
$appData = Assert-WorkspacePath (Join-Path $stateRoot 'app-data')
$traceRoot = Assert-WorkspacePath (Join-Path $stateRoot 'trace')
$xdgCache = Assert-WorkspacePath (Join-Path $profileRoot '.cache')
$xdgConfig = Assert-WorkspacePath (Join-Path $profileRoot '.config')
$xdgData = Assert-WorkspacePath (Join-Path $profileRoot '.local\share')
$appProject = Assert-WorkspacePath (Join-Path $repoRoot 'src\CommunityRelay\CommunityRelay.csproj')
$testProject = Assert-WorkspacePath (Join-Path $repoRoot 'tests\CommunityRelay.SelfTest\CommunityRelay.SelfTest.csproj')

Assert-NoReparseTree -Path $stateRoot
foreach ($writableTree in @(
    (Join-Path $repoRoot 'src\CommunityRelay\bin'),
    (Join-Path $repoRoot 'src\CommunityRelay\obj'),
    (Join-Path $repoRoot 'tests\CommunityRelay.SelfTest\bin'),
    (Join-Path $repoRoot 'tests\CommunityRelay.SelfTest\obj'),
    (Join-Path $repoRoot 'artifacts\self-test-data')
)) {
    Assert-NoReparseTree -Path $writableTree
}

$workspaceDirectories = @(
    $stateRoot,
    $profileRoot,
    $roamingAppData,
    $localAppData,
    $dotnetHome,
    $nugetPackages,
    $nugetHttpCache,
    $nugetPluginCache,
    $nugetScratch,
    $tempRoot,
    $bundleCache,
    $appData,
    $traceRoot,
    $xdgCache,
    $xdgConfig,
    $xdgData
)
New-Item -ItemType Directory -Force -Path $workspaceDirectories | Out-Null
Assert-NoReparseTree -Path $stateRoot

$homeDrive = [IO.Path]::GetPathRoot($profileRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$homePath = $profileRoot.Substring($homeDrive.Length)
$environmentUpdates = [ordered]@{
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
    DOTNET_CLI_HOME = $dotnetHome
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_NOLOGO = '1'
    DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = '0'
    DOTNET_MULTILEVEL_LOOKUP = '0'
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = '1'
    DOTNET_CLI_USE_MSBUILD_SERVER = '0'
    DOTNET_BUNDLE_EXTRACT_BASE_DIR = $bundleCache
    NUGET_PACKAGES = $nugetPackages
    NUGET_HTTP_CACHE_PATH = $nugetHttpCache
    NUGET_PLUGINS_CACHE_PATH = $nugetPluginCache
    NUGET_SCRATCH = $nugetScratch
    MSBUILDDISABLENODEREUSE = '1'
    MSBUILDDEBUGPATH = $traceRoot
    COREHOST_TRACEFILE = (Join-Path $traceRoot 'corehost-trace.txt')
    DOTNET_HOST_TRACEFILE = (Join-Path $traceRoot 'dotnet-host-trace.txt')
    COMMUNITY_RELAY_DATA_DIR = $appData
}
$savedEnvironment = @{}
foreach ($name in $environmentUpdates.Keys) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    [Environment]::SetEnvironmentVariable($name, $environmentUpdates[$name], 'Process')
}

Push-Location $repoRoot
try {
    Write-Host "Using dotnet: $script:dotnetExecutable"
    Write-Host "Workspace-contained build/test state: $stateRoot"
    Invoke-DotNet -Arguments @('--version')
    Invoke-DotNet -Arguments @(
        'restore',
        $testProject,
        '--packages', $nugetPackages,
        '--force-evaluate'
    )
    Invoke-DotNet -Arguments @(
        'build',
        $testProject,
        '--configuration', $Configuration,
        '--no-restore'
    )
    Invoke-DotNet -Arguments @(
        'run',
        '--project', $testProject,
        '--configuration', $Configuration,
        '--no-build',
        '--no-restore'
    )
    Write-Host 'Workspace-contained restore, build, and self-tests passed.'
}
finally {
    Pop-Location
    foreach ($name in $environmentUpdates.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
}
