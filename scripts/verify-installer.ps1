[CmdletBinding()]
param(
    [string]$SetupPath,
    [string]$TestInstallDirectory
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repoPrefix = $repoRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

function Assert-WorkspacePath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [switch]$AllowLeafReparsePoint
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Verification paths must stay beneath '$repoRoot': $fullPath"
    }
    $current = $fullPath
    $isLeaf = $true
    while ($current -and $current.StartsWith($repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
        if ([IO.File]::Exists($current) -or [IO.Directory]::Exists($current)) {
            $attributes = [IO.File]::GetAttributes($current)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -and
                -not ($isLeaf -and $AllowLeafReparsePoint)) {
                throw "Workspace verification path contains a reparse point or junction: $current"
            }
        }
        if ([string]::Equals($current, $repoRoot, [StringComparison]::OrdinalIgnoreCase)) {
            break
        }
        $current = [IO.Path]::GetDirectoryName($current)
        $isLeaf = $false
    }
    return $fullPath
}

function Get-WorkspaceRelativePath {
    param([Parameter(Mandatory)][string]$Path)

    $safePath = Assert-WorkspacePath -Path $Path
    return $safePath.Substring($repoPrefix.Length)
}

function Remove-KnownJunction {
    param([Parameter(Mandatory)][string]$Path)

    $safePath = Assert-WorkspacePath -Path $Path -AllowLeafReparsePoint
    if (-not (Test-Path -LiteralPath $safePath)) {
        return
    }
    $item = Get-Item -Force -LiteralPath $safePath
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
        throw "Expected a reparse point at cleanup path: $safePath"
    }
    # PowerShell 7's Remove-Item can throw an internal NullReferenceException for
    # directory junctions. Directory.Delete(path, false) removes the link itself
    # without traversing or deleting its target.
    [IO.Directory]::Delete($safePath, $false)
}

function Remove-WorkspaceItem {
    param([Parameter(Mandatory)][string]$Path)

    $safePath = Assert-WorkspacePath -Path $Path
    if (Test-Path -LiteralPath $safePath) {
        $item = Get-Item -Force -LiteralPath $safePath
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing recursive cleanup through a reparse point: $safePath"
        }
        $pending = [Collections.Generic.Stack[string]]::new()
        $pending.Push($safePath)
        while ($pending.Count -gt 0) {
            $current = $pending.Pop()
            $attributes = [IO.File]::GetAttributes($current)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing recursive cleanup through a nested reparse point: $current"
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

function Get-ShortcutFingerprint {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return '<missing>'
    }
    $item = Get-Item -LiteralPath $Path
    return "$($item.Length)|$($item.LastWriteTimeUtc.Ticks)|$((Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash)"
}

function Invoke-ProcessExpectExit {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][int]$ExpectedExitCode,
        [string]$WorkingDirectory = $repoRoot
    )

    $start = @{
        FilePath = $FilePath
        ArgumentList = $Arguments
        WorkingDirectory = $WorkingDirectory
        WindowStyle = 'Hidden'
        Wait = $true
        PassThru = $true
    }
    $process = Start-Process @start
    if ($process.ExitCode -ne $ExpectedExitCode) {
        throw "'$FilePath' exited with $($process.ExitCode); expected $ExpectedExitCode."
    }
}

function Invoke-Setup {
    param(
        [Parameter(Mandatory)][string]$InstallDirectory,
        [Parameter(Mandatory)][string[]]$Options,
        [int]$ExpectedExitCode = 0
    )

    $arguments = @('--silent', '--install-dir', "`"$InstallDirectory`"") + $Options
    Invoke-ProcessExpectExit -FilePath $setup -Arguments $arguments -ExpectedExitCode $ExpectedExitCode
}

function Invoke-Uninstaller {
    param(
        [Parameter(Mandatory)][string]$InstallDirectory,
        [int]$ExpectedExitCode = 0
    )

    $uninstaller = Join-Path $InstallDirectory 'Uninstall Community Relay.ps1'
    if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
        throw "Uninstaller not found: $uninstaller"
    }
    $arguments = @(
        '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
        '-File', "`"$uninstaller`"", '-Silent'
    )
    Invoke-ProcessExpectExit -FilePath $windowsPowerShell -Arguments $arguments -ExpectedExitCode $ExpectedExitCode
}

function Assert-RuntimeAtLeastPatched {
    param([Parameter(Mandatory)][string]$RuntimeConfigPath)

    $config = Get-Content -Raw -LiteralPath $RuntimeConfigPath | ConvertFrom-Json
    $result = [ordered]@{}
    foreach ($framework in @($config.runtimeOptions.includedFrameworks)) {
        $name = [string]$framework.name
        $version = [string]$framework.version
        if ($name -in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
            if ([Version]$version -lt [Version]'8.0.28') {
                throw "Installed app contains vulnerable runtime $name $version."
            }
            $result[$name] = $version
        }
    }
    foreach ($required in @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) {
        if (-not $result.Contains($required)) {
            throw "Installed runtime config does not list $required."
        }
    }
    return $result
}

if ([string]::IsNullOrWhiteSpace($SetupPath)) {
    $SetupPath = Join-Path $repoRoot 'artifacts\installer\CommunityRelayPOC-Setup.exe'
}
if ([string]::IsNullOrWhiteSpace($TestInstallDirectory)) {
    $TestInstallDirectory = Join-Path $repoRoot 'artifacts\verification\test-install'
}

$setup = Assert-WorkspacePath -Path $SetupPath
$testInstall = Assert-WorkspacePath -Path $TestInstallDirectory
$verificationRoot = Assert-WorkspacePath -Path (Join-Path $repoRoot 'artifacts\verification')
$runtimeState = Assert-WorkspacePath -Path (Join-Path $verificationRoot 'runtime-state')
$normalInstall = Assert-WorkspacePath -Path (Join-Path $verificationRoot 'normal-shortcut-install')
$rollbackInstall = Assert-WorkspacePath -Path (Join-Path $verificationRoot 'rollback-install')
$nonemptyTarget = Assert-WorkspacePath -Path (Join-Path $verificationRoot 'nonempty-target')
$reparseOutside = Assert-WorkspacePath -Path (Join-Path $verificationRoot 'reparse-outside')
$reparseLink = Assert-WorkspacePath -Path (Join-Path $verificationRoot 'reparse-link') -AllowLeafReparsePoint
$subtreeReparseLink = Assert-WorkspacePath -Path (Join-Path $rollbackInstall 'unexpected-junction') -AllowLeafReparsePoint
$tempRoot = Assert-WorkspacePath -Path (Join-Path $runtimeState 'temp')
$bundleCache = Assert-WorkspacePath -Path (Join-Path $runtimeState 'bundle-cache')
$appData = Assert-WorkspacePath -Path (Join-Path $runtimeState 'app-data')
$shellStartMenu = Assert-WorkspacePath -Path (Join-Path $runtimeState 'shell\start-menu')
$shellDesktop = Assert-WorkspacePath -Path (Join-Path $runtimeState 'shell\desktop')
$reportPath = Assert-WorkspacePath -Path (Join-Path $verificationRoot 'verification-report.json')
$buildMetadataPath = Assert-WorkspacePath -Path (Join-Path $repoRoot 'artifacts\verification\build-metadata.json')
$windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'

# A failed run must not leave an older verified:true report looking current.
if (Test-Path -LiteralPath $reportPath -PathType Leaf) {
    Remove-Item -LiteralPath $reportPath -Force
}

if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) {
    throw "Installer not found: $setup"
}
if (-not (Test-Path -LiteralPath $buildMetadataPath -PathType Leaf)) {
    throw "Installer build metadata not found: $buildMetadataPath"
}

# Remove only the two exact junctions a prior interrupted verification may have created.
Remove-KnownJunction -Path $subtreeReparseLink
Remove-KnownJunction -Path $reparseLink
foreach ($path in @($testInstall, $normalInstall, $rollbackInstall, $nonemptyTarget, $reparseOutside, $runtimeState)) {
    Remove-WorkspaceItem -Path $path
}
New-Item -ItemType Directory -Force -Path $verificationRoot, $runtimeState, $tempRoot, $bundleCache, $appData, $shellStartMenu, $shellDesktop | Out-Null

$environmentUpdates = [ordered]@{
    TEMP = $tempRoot
    TMP = $tempRoot
    DOTNET_BUNDLE_EXTRACT_BASE_DIR = $bundleCache
    COMMUNITY_RELAY_DATA_DIR = $appData
    COMMUNITY_RELAY_SETUP_START_MENU_ROOT = $shellStartMenu
    COMMUNITY_RELAY_SETUP_DESKTOP_ROOT = $shellDesktop
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_NOLOGO = '1'
}
$savedEnvironment = @{}
foreach ($name in $environmentUpdates.Keys) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    [Environment]::SetEnvironmentVariable($name, $environmentUpdates[$name], 'Process')
}

try {
$actualStartMenuRoot = $env:COMMUNITY_RELAY_VERIFY_REAL_START_MENU_ROOT
if ([string]::IsNullOrWhiteSpace($actualStartMenuRoot)) {
    $actualStartMenuRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::StartMenu)
}
$actualDesktopRoot = $env:COMMUNITY_RELAY_VERIFY_REAL_DESKTOP_ROOT
if ([string]::IsNullOrWhiteSpace($actualDesktopRoot)) {
    $actualDesktopRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
}
$actualShortcutPaths = @()
if (-not [string]::IsNullOrWhiteSpace($actualStartMenuRoot)) {
    $actualShortcutPaths += Join-Path $actualStartMenuRoot 'Programs\Community Relay POC\Community Relay POC.lnk'
    $actualShortcutPaths += Join-Path $actualStartMenuRoot 'Programs\Community Relay POC\Uninstall Community Relay POC.lnk'
}
if (-not [string]::IsNullOrWhiteSpace($actualDesktopRoot)) {
    $actualShortcutPaths += Join-Path $actualDesktopRoot 'Community Relay POC.lnk'
}
$actualShortcutBefore = @{}
foreach ($shortcutPath in $actualShortcutPaths) {
    $actualShortcutBefore[$shortcutPath] = Get-ShortcutFingerprint -Path $shortcutPath
}

# 1. Integrity-mode install, patched-runtime gate, and real UI readiness.
Invoke-Setup -InstallDirectory $testInstall -Options @('--verify', '--no-shortcuts', '--no-launch')
$applicationPath = Join-Path $testInstall 'CommunityRelay.exe'
$manifestPath = Join-Path $testInstall 'install.json'
$markerPath = Join-Path $testInstall '.community-relay-install'
$verificationResultPath = Join-Path $testInstall 'verification-result.json'
foreach ($requiredFile in @($applicationPath, $manifestPath, $markerPath, $verificationResultPath, (Join-Path $testInstall 'Uninstall Community Relay.ps1'))) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Installed file is missing: $requiredFile"
    }
}

$installManifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$installMarker = Get-Content -Raw -LiteralPath $markerPath | ConvertFrom-Json
$verificationResult = Get-Content -Raw -LiteralPath $verificationResultPath | ConvertFrom-Json
if ($installManifest.productId -cne 'community-relay-poc.windows' -or
    $installManifest.installId -ne $installMarker.installId -or
    -not $installManifest.verificationInstall -or -not $verificationResult.verified -or
    @($installManifest.shortcuts).Count -ne 0) {
    throw 'The integrity-mode ownership metadata is invalid.'
}

$appFrameworks = Assert-RuntimeAtLeastPatched -RuntimeConfigPath (Join-Path $testInstall 'CommunityRelay.runtimeconfig.json')
$buildMetadata = Get-Content -Raw -LiteralPath $buildMetadataPath | ConvertFrom-Json
foreach ($frameworkVersion in @(
    $buildMetadata.setupFrameworks.microsoftNetCoreApp,
    $buildMetadata.setupFrameworks.microsoftWindowsDesktopApp)) {
    if ([Version][string]$frameworkVersion -lt [Version]'8.0.28') {
        throw "Setup executable metadata identifies vulnerable runtime $frameworkVersion."
    }
}

$dataSentinel = Join-Path $appData 'preserve-after-uninstall.txt'
'settings and authorization root must survive program uninstall' | Set-Content -LiteralPath $dataSentinel -Encoding UTF8
$appProcess = $null
try {
    $appProcess = Start-Process -FilePath $applicationPath -WorkingDirectory $testInstall -WindowStyle Hidden -PassThru
    if (-not $appProcess.WaitForInputIdle(15000)) {
        throw 'The installed WPF app did not reach an input-idle UI state.'
    }
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $appProcess.Refresh()
        if ($appProcess.HasExited) {
            throw "The installed app exited during UI readiness with code $($appProcess.ExitCode)."
        }
        if ($appProcess.MainWindowHandle -ne [IntPtr]::Zero -and $appProcess.MainWindowTitle -eq 'Community Relay POC') {
            break
        }
        Start-Sleep -Milliseconds 250
    }
    $appProcess.Refresh()
    if ($appProcess.MainWindowHandle -eq [IntPtr]::Zero -or $appProcess.MainWindowTitle -ne 'Community Relay POC') {
        throw 'The installed app did not expose the expected Community Relay POC main window.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $appData 'logs') -PathType Container)) {
        throw 'The ready app did not initialize its redirected workspace data root.'
    }
}
finally {
    if ($appProcess -and -not $appProcess.HasExited) {
        Stop-Process -Id $appProcess.Id -Force -ErrorAction SilentlyContinue
        $appProcess.WaitForExit(5000) | Out-Null
    }
}
Invoke-Uninstaller -InstallDirectory $testInstall
if (Test-Path -LiteralPath $testInstall -PathType Container) {
    throw 'The integrity-mode program directory survived uninstall.'
}
if (-not (Test-Path -LiteralPath $dataSentinel -PathType Leaf)) {
    throw 'The separate app data root was incorrectly removed by program uninstall.'
}

# 2. A first install must reject an exact-name shortcut it does not own, then a
# collision-free normal install must create/remove only workspace-local shortcuts.
$workspaceStartShortcut = Join-Path $shellStartMenu 'Programs\Community Relay POC\Community Relay POC.lnk'
$workspaceUninstallShortcut = Join-Path $shellStartMenu 'Programs\Community Relay POC\Uninstall Community Relay POC.lnk'
$workspaceDesktopShortcut = Join-Path $shellDesktop 'Community Relay POC.lnk'
New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($workspaceStartShortcut)) | Out-Null
'pre-existing exact-name shortcut must not be overwritten' | Set-Content -LiteralPath $workspaceStartShortcut -Encoding UTF8
$shortcutCollisionHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $workspaceStartShortcut).Hash
Invoke-Setup -InstallDirectory $normalInstall -Options @('--start-menu-shortcut', '--desktop-shortcut', '--no-launch') -ExpectedExitCode 1
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $workspaceStartShortcut).Hash -ne $shortcutCollisionHash -or
    (Test-Path -LiteralPath (Join-Path $normalInstall 'CommunityRelay.exe'))) {
    throw 'First install overwrote an exact-name shortcut it did not own.'
}
Remove-Item -LiteralPath $workspaceStartShortcut -Force

Invoke-Setup -InstallDirectory $normalInstall -Options @('--start-menu-shortcut', '--desktop-shortcut', '--no-launch')
foreach ($shortcut in @($workspaceStartShortcut, $workspaceUninstallShortcut, $workspaceDesktopShortcut)) {
    if (-not (Test-Path -LiteralPath $shortcut -PathType Leaf)) {
        throw "Normal install did not create the redirected product shortcut: $shortcut"
    }
}

$shell = New-Object -ComObject WScript.Shell
try {
    $appShortcutObject = $shell.CreateShortcut($workspaceStartShortcut)
    if (-not [string]::Equals(
        [IO.Path]::GetFullPath($appShortcutObject.TargetPath),
        [IO.Path]::GetFullPath((Join-Path $normalInstall 'CommunityRelay.exe')),
        [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The redirected Start Menu shortcut targets the wrong executable.'
    }
}
finally {
    if ($appShortcutObject) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($appShortcutObject) }
    if ($shell) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
}

# A tampered manifest may not redirect uninstall toward an unrelated shortcut.
$victimShortcut = Join-Path $shellStartMenu 'Programs\Unrelated Product\keep.lnk'
New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($victimShortcut)) | Out-Null
'must survive malicious manifest test' | Set-Content -LiteralPath $victimShortcut -Encoding UTF8
$normalManifestPath = Join-Path $normalInstall 'install.json'
$originalManifestBytes = [IO.File]::ReadAllBytes($normalManifestPath)
$tamperedManifest = Get-Content -Raw -LiteralPath $normalManifestPath | ConvertFrom-Json
$tamperedManifest.shortcuts = @($tamperedManifest.shortcuts) + $victimShortcut
$tamperedManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $normalManifestPath -Encoding UTF8
Invoke-Uninstaller -InstallDirectory $normalInstall -ExpectedExitCode 1
if (-not (Test-Path -LiteralPath $victimShortcut -PathType Leaf) -or
    -not (Test-Path -LiteralPath (Join-Path $normalInstall 'CommunityRelay.exe') -PathType Leaf)) {
    throw 'Tampered-manifest rejection changed an unrelated shortcut or began uninstalling the app.'
}
[IO.File]::WriteAllBytes($normalManifestPath, $originalManifestBytes)
Invoke-Uninstaller -InstallDirectory $normalInstall
foreach ($shortcut in @($workspaceStartShortcut, $workspaceUninstallShortcut, $workspaceDesktopShortcut)) {
    if (Test-Path -LiteralPath $shortcut) {
        throw "Product uninstaller left its exact redirected shortcut behind: $shortcut"
    }
}
if (-not (Test-Path -LiteralPath $victimShortcut -PathType Leaf)) {
    throw 'Product uninstall removed an unrelated shortcut.'
}
Remove-Item -LiteralPath $victimShortcut -Force

# 3. Nonempty unowned folders must be rejected without mutation.
New-Item -ItemType Directory -Force -Path $nonemptyTarget | Out-Null
$unownedSentinel = Join-Path $nonemptyTarget 'keep.txt'
'unowned content' | Set-Content -LiteralPath $unownedSentinel -Encoding UTF8
$unownedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $unownedSentinel).Hash
Invoke-Setup -InstallDirectory $nonemptyTarget -Options @('--verify', '--no-shortcuts') -ExpectedExitCode 1
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $unownedSentinel).Hash -ne $unownedHash -or
    (Test-Path -LiteralPath (Join-Path $nonemptyTarget 'CommunityRelay.exe'))) {
    throw 'The installer mutated an unowned nonempty destination.'
}

# 4. A junction anywhere in target ancestry must be rejected before writes.
New-Item -ItemType Directory -Force -Path $reparseOutside | Out-Null
$outsideSentinel = Join-Path $reparseOutside 'keep.txt'
'junction target content' | Set-Content -LiteralPath $outsideSentinel -Encoding UTF8
$outsideHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $outsideSentinel).Hash
New-Item -ItemType Junction -Path $reparseLink -Target $reparseOutside | Out-Null
Invoke-Setup -InstallDirectory (Join-Path $reparseLink 'install') -Options @('--verify', '--no-shortcuts') -ExpectedExitCode 1
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $outsideSentinel).Hash -ne $outsideHash -or
    (Test-Path -LiteralPath (Join-Path $reparseOutside 'install'))) {
    throw 'The installer followed or mutated a junction in target ancestry.'
}
Remove-KnownJunction -Path $reparseLink

# 5. A late shortcut failure must roll back an existing owned installation byte-for-byte.
Invoke-Setup -InstallDirectory $rollbackInstall -Options @('--no-shortcuts', '--no-launch')
$rollbackManifest = Join-Path $rollbackInstall 'install.json'
$rollbackMarker = Join-Path $rollbackInstall '.community-relay-install'
$rollbackApplication = Join-Path $rollbackInstall 'CommunityRelay.exe'
$rollbackHashes = @{
    manifest = (Get-FileHash -Algorithm SHA256 -LiteralPath $rollbackManifest).Hash
    marker = (Get-FileHash -Algorithm SHA256 -LiteralPath $rollbackMarker).Hash
    application = (Get-FileHash -Algorithm SHA256 -LiteralPath $rollbackApplication).Hash
}

# A forged inventory may not classify a custom-folder file as product-owned.
$untrackedFile = Join-Path $rollbackInstall 'user-owned-note.txt'
'this untracked file must never become installer-owned' | Set-Content -LiteralPath $untrackedFile -Encoding UTF8
$untrackedHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $untrackedFile).Hash
$originalRollbackManifestBytes = [IO.File]::ReadAllBytes($rollbackManifest)
$forgedTrackedManifest = Get-Content -Raw -LiteralPath $rollbackManifest | ConvertFrom-Json
$forgedTrackedManifest.files = @($forgedTrackedManifest.files) + 'user-owned-note.txt'
$forgedTrackedManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $rollbackManifest -Encoding UTF8
$forgedManifestHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $rollbackManifest).Hash
Invoke-Uninstaller -InstallDirectory $rollbackInstall -ExpectedExitCode 1
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $untrackedFile).Hash -ne $untrackedHash -or
    -not (Test-Path -LiteralPath $rollbackApplication -PathType Leaf)) {
    throw 'A forged tracked-file inventory caused uninstall to mutate custom-folder content.'
}
Invoke-Setup -InstallDirectory $rollbackInstall -Options @('--no-shortcuts', '--no-launch') -ExpectedExitCode 1
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $untrackedFile).Hash -ne $untrackedHash -or
    (Get-FileHash -Algorithm SHA256 -LiteralPath $rollbackManifest).Hash -ne $forgedManifestHash -or
    (Get-FileHash -Algorithm SHA256 -LiteralPath $rollbackApplication).Hash -ne $rollbackHashes.application) {
    throw 'A forged tracked-file inventory mutated or dropped custom-folder content.'
}
[IO.File]::WriteAllBytes($rollbackManifest, $originalRollbackManifestBytes)

$badStartMenuRoot = Join-Path $runtimeState 'bad-start-menu-root'
'this file forces shortcut creation to fail after activation' | Set-Content -LiteralPath $badStartMenuRoot -Encoding UTF8
$env:COMMUNITY_RELAY_SETUP_START_MENU_ROOT = $badStartMenuRoot
Invoke-Setup -InstallDirectory $rollbackInstall -Options @('--start-menu-shortcut', '--no-desktop-shortcut', '--no-launch') -ExpectedExitCode 1
$env:COMMUNITY_RELAY_SETUP_START_MENU_ROOT = $shellStartMenu
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $rollbackManifest).Hash -ne $rollbackHashes.manifest -or
    (Get-FileHash -Algorithm SHA256 -LiteralPath $rollbackMarker).Hash -ne $rollbackHashes.marker -or
    (Get-FileHash -Algorithm SHA256 -LiteralPath $rollbackApplication).Hash -ne $rollbackHashes.application) {
    throw 'Late failure rollback did not restore the previous owned installation byte-for-byte.'
}

# 6. Reparse points appearing anywhere inside an owned install subtree must block update/uninstall.
New-Item -ItemType Junction -Path $subtreeReparseLink -Target $reparseOutside | Out-Null
Invoke-Setup -InstallDirectory $rollbackInstall -Options @('--no-shortcuts', '--no-launch') -ExpectedExitCode 1
Invoke-Uninstaller -InstallDirectory $rollbackInstall -ExpectedExitCode 1
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $outsideSentinel).Hash -ne $outsideHash -or
    -not (Test-Path -LiteralPath $rollbackApplication -PathType Leaf)) {
    throw 'A subtree junction was followed or the owned install was mutated despite rejection.'
}
Remove-KnownJunction -Path $subtreeReparseLink
Remove-Item -LiteralPath $untrackedFile -Force
Invoke-Uninstaller -InstallDirectory $rollbackInstall

foreach ($shortcutPath in $actualShortcutPaths) {
    if ($actualShortcutBefore[$shortcutPath] -ne (Get-ShortcutFingerprint -Path $shortcutPath)) {
        throw "Verification changed a real user shortcut outside the workspace: $shortcutPath"
    }
}

$setupInfo = Get-Item -LiteralPath $setup
$report = [ordered]@{
    verified = $true
    verifiedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    installer = Get-WorkspaceRelativePath -Path $setupInfo.FullName
    installerBytes = $setupInfo.Length
    installerSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $setup).Hash
    installedVersion = [string]$installManifest.version
    installedPayloadFiles = [int]$verificationResult.payloadFiles
    installedTrackedFiles = @($installManifest.files).Count
    appFrameworks = $appFrameworks
    setupFrameworks = $buildMetadata.setupFrameworks
    payloadHashVerification = 'passed'
    uiInputIdleAndMainWindow = 'passed'
    separateAppDataPreservedAfterUninstall = 'passed'
    appDataPolicy = 'separate root; always preserved; no recursive data-delete option'
    redirectedNormalShortcutCreateRemove = 'passed'
    preexistingExactShortcutCollisionRejection = 'passed'
    realUserShortcutsUntouched = $true
    realUserShortcutPathsInspected = $actualShortcutPaths.Count
    maliciousManifestShortcutNondeletion = 'passed'
    unownedNonemptyTargetRejection = 'passed'
    forgedTrackedFileInventoryRejection = 'passed'
    reparseAncestryRejection = 'passed'
    reparseSubtreeUpdateAndUninstallRejection = 'passed'
    lateFailureAtomicRollback = 'passed'
    uninstall = 'passed'
    testInstallDirectory = Get-WorkspaceRelativePath -Path $testInstall
}
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8

Write-Host "Installer security and lifecycle verification passed. Report: $reportPath"
}
finally {
    foreach ($name in $environmentUpdates.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
}
