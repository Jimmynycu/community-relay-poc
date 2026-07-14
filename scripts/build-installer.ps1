[CmdletBinding()]
param(
    [ValidateSet('win-x64')]
    [string]$RuntimeIdentifier = 'win-x64',

    [string]$DotNetPath,

    [switch]$SkipVerification
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repoPrefix = $repoRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

function Assert-WorkspacePath {
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path must stay beneath '$repoRoot': $fullPath"
    }

    $current = $fullPath
    while ($current -and $current.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
        if ([IO.File]::Exists($current) -or [IO.Directory]::Exists($current)) {
            $attributes = [IO.File]::GetAttributes($current)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Workspace build path contains a reparse point or junction: $current"
            }
        }
        if ([string]::Equals($current, $repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
            break
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }

    return $fullPath
}

function Remove-WorkspaceItem {
    param([Parameter(Mandatory)][string]$Path)

    $safePath = Assert-WorkspacePath -Path $Path
    if (Test-Path -LiteralPath $safePath) {
        $pending = [Collections.Generic.Stack[string]]::new()
        $pending.Push($safePath)
        while ($pending.Count -gt 0) {
            $current = $pending.Pop()
            $attributes = [IO.File]::GetAttributes($current)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing build cleanup through a reparse point: $current"
            }
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($current)) {
                    $pending.Push($entry)
                }
            }
        }
        Remove-Item -LiteralPath $safePath -Recurse -Force
    }
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
            throw "Workspace build state contains a reparse point: $current"
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

function Get-FrameworkVersions {
    param([Parameter(Mandatory)][string]$RuntimeConfigPath)

    if (-not (Test-Path -LiteralPath $RuntimeConfigPath -PathType Leaf)) {
        throw "Runtime config not found: $RuntimeConfigPath"
    }

    $runtimeConfig = Get-Content -Raw -LiteralPath $RuntimeConfigPath | ConvertFrom-Json
    $versions = @{}
    foreach ($framework in @($runtimeConfig.runtimeOptions.includedFrameworks)) {
        $versions[[string]$framework.name] = [string]$framework.version
    }

    foreach ($requiredFramework in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
        if (-not $versions.ContainsKey($requiredFramework)) {
            throw "Published output does not identify $requiredFramework in $RuntimeConfigPath."
        }
        if ([Version]$versions[$requiredFramework] -lt [Version]'8.0.28') {
            throw "Refusing to package vulnerable runtime $requiredFramework $($versions[$requiredFramework]); version 8.0.28 or newer is required."
        }
    }

    return [ordered]@{
        microsoftNetCoreApp = $versions['Microsoft.NETCore.App']
        microsoftWindowsDesktopApp = $versions['Microsoft.WindowsDesktop.App']
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

$buildState = Assert-WorkspacePath (Join-Path $repoRoot '.build\installer')
$dotnetHome = Assert-WorkspacePath (Join-Path $buildState 'dotnet-home')
$nugetPackages = Assert-WorkspacePath (Join-Path $buildState 'nuget-packages')
$nugetHttpCache = Assert-WorkspacePath (Join-Path $buildState 'nuget-http-cache')
$nugetPluginCache = Assert-WorkspacePath (Join-Path $buildState 'nuget-plugin-cache')
$nugetScratch = Assert-WorkspacePath (Join-Path $buildState 'nuget-scratch')
$tempRoot = Assert-WorkspacePath (Join-Path $buildState 'temp')
$bundleCache = Assert-WorkspacePath (Join-Path $buildState 'bundle-cache')
$profileRoot = Assert-WorkspacePath (Join-Path $buildState 'profile')
$roamingAppData = Assert-WorkspacePath (Join-Path $profileRoot 'AppData\Roaming')
$localAppData = Assert-WorkspacePath (Join-Path $profileRoot 'AppData\Local')
$xdgCache = Assert-WorkspacePath (Join-Path $profileRoot '.cache')
$xdgConfig = Assert-WorkspacePath (Join-Path $profileRoot '.config')
$xdgData = Assert-WorkspacePath (Join-Path $profileRoot '.local\share')
$traceRoot = Assert-WorkspacePath (Join-Path $buildState 'trace')
$fallbackAppData = Assert-WorkspacePath (Join-Path $buildState 'app-data')
$appPublish = Assert-WorkspacePath (Join-Path $repoRoot "artifacts\publish\CommunityRelay-$RuntimeIdentifier")
$setupPublish = Assert-WorkspacePath (Join-Path $repoRoot "artifacts\publish\CommunityRelay.Setup-$RuntimeIdentifier")
$installerDirectory = Assert-WorkspacePath (Join-Path $repoRoot 'artifacts\installer')
$verificationDirectory = Assert-WorkspacePath (Join-Path $repoRoot 'artifacts\verification')
$verificationReportPath = Assert-WorkspacePath (Join-Path $verificationDirectory 'verification-report.json')
$buildMetadataPath = Assert-WorkspacePath (Join-Path $verificationDirectory 'build-metadata.json')
$payloadDirectory = Assert-WorkspacePath (Join-Path $repoRoot 'src\CommunityRelay.Setup\Payload')
$payloadZip = Assert-WorkspacePath (Join-Path $payloadDirectory 'payload.zip')
$payloadManifest = Assert-WorkspacePath (Join-Path $payloadDirectory 'payload-manifest.json')
$appProject = Assert-WorkspacePath (Join-Path $repoRoot 'src\CommunityRelay\CommunityRelay.csproj')
$setupProject = Assert-WorkspacePath (Join-Path $repoRoot 'src\CommunityRelay.Setup\CommunityRelay.Setup.csproj')
$finalInstaller = Assert-WorkspacePath (Join-Path $installerDirectory 'CommunityRelayPOC-Setup.exe')
$appObj = Assert-WorkspacePath (Join-Path $repoRoot 'src\CommunityRelay\obj')
$appBin = Assert-WorkspacePath (Join-Path $repoRoot 'src\CommunityRelay\bin')
$setupObj = Assert-WorkspacePath (Join-Path $repoRoot 'src\CommunityRelay.Setup\obj')
$setupBin = Assert-WorkspacePath (Join-Path $repoRoot 'src\CommunityRelay.Setup\bin')

New-Item -ItemType Directory -Force -Path @(
    $buildState,
    $dotnetHome,
    $nugetPackages,
    $nugetHttpCache,
    $nugetPluginCache,
    $nugetScratch,
    $tempRoot,
    $bundleCache,
    $profileRoot,
    $roamingAppData,
    $localAppData,
    $xdgCache,
    $xdgConfig,
    $xdgData,
    $traceRoot,
    $fallbackAppData
) | Out-Null
foreach ($writableTree in @($buildState, $appObj, $appBin, $setupObj, $setupBin)) {
    Assert-NoReparseTree -Path $writableTree
}
$homeDrive = [IO.Path]::GetPathRoot($profileRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
$homePath = $profileRoot.Substring($homeDrive.Length)
$readOnlyRealStartMenuRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::StartMenu)
$readOnlyRealDesktopRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
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
    NUGET_XMLDOC_MODE = 'skip'
    MSBUILDDISABLENODEREUSE = '1'
    MSBUILDDEBUGPATH = $traceRoot
    UseSharedCompilation = 'false'
    COREHOST_TRACEFILE = (Join-Path $traceRoot 'corehost-trace.txt')
    DOTNET_HOST_TRACEFILE = (Join-Path $traceRoot 'dotnet-host-trace.txt')
    COMMUNITY_RELAY_DATA_DIR = $fallbackAppData
    COMMUNITY_RELAY_VERIFY_REAL_START_MENU_ROOT = $readOnlyRealStartMenuRoot
    COMMUNITY_RELAY_VERIFY_REAL_DESKTOP_ROOT = $readOnlyRealDesktopRoot
}
$savedEnvironment = @{}
foreach ($name in $environmentUpdates.Keys) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    [Environment]::SetEnvironmentVariable($name, $environmentUpdates[$name], 'Process')
}

try {
$dotnetSdkOutput = @(& $script:dotnetExecutable --version)
$dotnetSdkVersion = ([string]($dotnetSdkOutput | Select-Object -Last 1)).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($dotnetSdkVersion)) {
    throw 'Unable to identify the selected .NET SDK version.'
}

Remove-WorkspaceItem -Path $appPublish
Remove-WorkspaceItem -Path $setupPublish
Remove-WorkspaceItem -Path $installerDirectory
Remove-WorkspaceItem -Path $payloadDirectory
foreach ($staleEvidence in @($verificationReportPath, $buildMetadataPath)) {
    if (Test-Path -LiteralPath $staleEvidence -PathType Leaf) {
        Remove-Item -LiteralPath $staleEvidence -Force
    }
}
New-Item -ItemType Directory -Force -Path $appPublish, $setupPublish, $installerDirectory, $verificationDirectory, $payloadDirectory | Out-Null

Write-Host 'Publishing the self-contained Community Relay application...'
Invoke-DotNet -Arguments @(
    'publish', $appProject,
    '--configuration', 'Release',
    '--runtime', $RuntimeIdentifier,
    '--self-contained', 'true',
    '--force',
    '--output', $appPublish,
    '--nologo',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-p:PublishSingleFile=false',
    '-p:TargetLatestRuntimePatch=true',
    "-p:RestorePackagesPath=$nugetPackages"
)

if (-not (Test-Path -LiteralPath (Join-Path $appPublish 'CommunityRelay.exe') -PathType Leaf)) {
    throw 'The application publish did not produce CommunityRelay.exe.'
}
$appFrameworkVersions = Get-FrameworkVersions -RuntimeConfigPath (Join-Path $appPublish 'CommunityRelay.runtimeconfig.json')

[xml]$appProjectXml = Get-Content -Raw -LiteralPath $appProject
$version = [string]$appProjectXml.Project.PropertyGroup.Version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw 'The application project does not declare a Version.'
}

$payloadFiles = @(
    Get-ChildItem -LiteralPath $appPublish -File -Recurse |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = $_.FullName.Substring($appPublish.Length).TrimStart('\', '/').Replace('\', '/')
            [ordered]@{
                path = $relativePath
                length = $_.Length
                sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
            }
        }
)

if ($payloadFiles.Count -eq 0) {
    throw 'The self-contained publish folder is empty.'
}

$manifestObject = [ordered]@{
    version = $version
    runtimeIdentifier = $RuntimeIdentifier
    frameworks = $appFrameworkVersions
    files = $payloadFiles
}
$manifestObject | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $payloadManifest -Encoding UTF8

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory(
    $appPublish,
    $payloadZip,
    [IO.Compression.CompressionLevel]::Optimal,
    $false)

Write-Host "Publishing the self-contained installer with $($payloadFiles.Count) payload files..."
Invoke-DotNet -Arguments @(
    'publish', $setupProject,
    '--configuration', 'Release',
    '--runtime', $RuntimeIdentifier,
    '--self-contained', 'true',
    '--force',
    '--output', $setupPublish,
    '--nologo',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-p:TargetLatestRuntimePatch=true',
    "-p:RestorePackagesPath=$nugetPackages"
)

$publishedSetup = Join-Path $setupPublish 'CommunityRelayPOC-Setup.exe'
if (-not (Test-Path -LiteralPath $publishedSetup -PathType Leaf)) {
    throw 'The setup publish did not produce CommunityRelayPOC-Setup.exe.'
}

$setupRuntimeConfig = Join-Path $repoRoot "src\CommunityRelay.Setup\bin\Release\net8.0-windows\$RuntimeIdentifier\CommunityRelayPOC-Setup.runtimeconfig.json"
$setupFrameworkVersions = Get-FrameworkVersions -RuntimeConfigPath $setupRuntimeConfig

Copy-Item -LiteralPath $publishedSetup -Destination $finalInstaller -Force
$installerHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $finalInstaller).Hash
@(
    "$installerHash  CommunityRelayPOC-Setup.exe"
    "version=$version"
    "runtime=$RuntimeIdentifier"
    "app_Microsoft.NETCore.App=$($appFrameworkVersions.microsoftNetCoreApp)"
    "app_Microsoft.WindowsDesktop.App=$($appFrameworkVersions.microsoftWindowsDesktopApp)"
    "setup_Microsoft.NETCore.App=$($setupFrameworkVersions.microsoftNetCoreApp)"
    "setup_Microsoft.WindowsDesktop.App=$($setupFrameworkVersions.microsoftWindowsDesktopApp)"
    "payload_files=$($payloadFiles.Count)"
) | Set-Content -LiteralPath (Join-Path $installerDirectory 'CommunityRelayPOC-Setup.sha256.txt') -Encoding ASCII

$buildMetadata = [ordered]@{
    product = 'Community Relay POC'
    version = $version
    runtimeIdentifier = $RuntimeIdentifier
    dotnetSdkVersion = $dotnetSdkVersion
    dotnetExecutableName = [IO.Path]::GetFileName($script:dotnetExecutable)
    appFrameworks = $appFrameworkVersions
    setupFrameworks = $setupFrameworkVersions
    payloadFiles = $payloadFiles.Count
    installerBytes = (Get-Item -LiteralPath $finalInstaller).Length
    installerSha256 = $installerHash
}
$buildMetadata | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $buildMetadataPath -Encoding UTF8

if (-not $SkipVerification) {
    Write-Host 'Running the workspace-local install, launch, and uninstall verification...'
    & (Join-Path $PSScriptRoot 'verify-installer.ps1') -SetupPath $finalInstaller
    if ($LASTEXITCODE -ne 0) {
        throw "Installer verification exited with code $LASTEXITCODE."
    }
    if (-not (Test-Path -LiteralPath $verificationReportPath -PathType Leaf)) {
        throw 'Installer verification did not produce a fresh report.'
    }
    $verificationReport = Get-Content -Raw -LiteralPath $verificationReportPath | ConvertFrom-Json
    if (-not $verificationReport.verified -or
        -not [string]::Equals([string]$verificationReport.installerSha256, $installerHash, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Installer verification report does not match the freshly built artifact.'
    }
}

$installerInfo = Get-Item -LiteralPath $finalInstaller
Write-Host ''
Write-Host 'Installer build complete:'
Write-Host "  Path: $($installerInfo.FullName)"
Write-Host "  Size: $($installerInfo.Length) bytes"
Write-Host "  SHA256: $installerHash"
}
finally {
    foreach ($name in $environmentUpdates.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
}
