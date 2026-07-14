[CmdletBinding()]
param(
    [switch]$Silent
)

$ErrorActionPreference = 'Stop'
$productName = 'Community Relay POC'
$productId = 'community-relay-poc.windows'
$schemaVersion = 1
$markerFileName = '.community-relay-install'
$manifestFileName = 'install.json'
$uninstallerFileName = 'Uninstall Community Relay.ps1'
$embeddedTrackedFilesBase64 = '__COMMUNITY_RELAY_TRACKED_FILES_BASE64__'
$scriptPath = [IO.Path]::GetFullPath($MyInvocation.MyCommand.Path)
$installDirectory = [IO.Path]::GetDirectoryName($scriptPath).TrimEnd([IO.Path]::DirectorySeparatorChar)
$rootWithSeparator = $installDirectory + [IO.Path]::DirectorySeparatorChar
$manifestPath = Join-Path $installDirectory $manifestFileName
$markerPath = Join-Path $installDirectory $markerFileName

function Assert-NotReparsePoint {
    param([Parameter(Mandatory)][string]$Path)

    $attributes = [IO.File]::GetAttributes($Path)
    if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing to operate through a reparse point or junction: $Path"
    }
}

function Assert-NoReparseInAncestry {
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    if ([IO.File]::Exists($fullPath)) {
        Assert-NotReparsePoint -Path $fullPath
    }

    if ([IO.Directory]::Exists($fullPath)) {
        $current = [IO.DirectoryInfo]::new($fullPath)
    }
    else {
        $parent = [IO.Path]::GetDirectoryName($fullPath)
        if (-not $parent) {
            throw "Path has no parent: $fullPath"
        }
        $current = [IO.DirectoryInfo]::new($parent)
    }

    while ($current) {
        if ($current.Exists) {
            Assert-NotReparsePoint -Path $current.FullName
        }
        $current = $current.Parent
    }
}

function Assert-NoReparseInTree {
    param([Parameter(Mandatory)][string]$Root)

    Assert-NoReparseInAncestry -Path $Root
    if (-not [IO.Directory]::Exists($Root)) {
        return
    }

    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push([IO.Path]::GetFullPath($Root))
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        Assert-NotReparsePoint -Path $directory
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
            $attributes = [IO.File]::GetAttributes($entry)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing to operate through a reparse point or junction: $entry"
            }
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                $pending.Push($entry)
            }
        }
    }
}

function Get-SafeInstalledPath {
    param([Parameter(Mandatory)][string]$RelativePath)

    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [IO.Path]::IsPathRooted($RelativePath)) {
        throw "Unsafe path in install manifest: $RelativePath"
    }

    $candidate = [IO.Path]::GetFullPath((Join-Path $installDirectory $RelativePath))
    if (-not $candidate.StartsWith($rootWithSeparator, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path escapes the installation directory: $RelativePath"
    }

    Assert-NoReparseInAncestry -Path $candidate
    return $candidate
}

function Get-ShellRoot {
    param(
        [Parameter(Mandatory)][string]$OverrideVariable,
        [Parameter(Mandatory)][Environment+SpecialFolder]$SpecialFolder
    )

    $override = [Environment]::GetEnvironmentVariable($OverrideVariable)
    if ([string]::IsNullOrWhiteSpace($override)) {
        $selected = [Environment]::GetFolderPath($SpecialFolder)
    }
    else {
        $selected = $override
    }

    if ([string]::IsNullOrWhiteSpace($selected)) {
        throw 'A required current-user shell folder is unavailable.'
    }
    return [IO.Path]::GetFullPath($selected)
}

if (-not (Test-Path -LiteralPath $installDirectory -PathType Container)) {
    throw 'The Community Relay installation directory does not exist. Nothing was removed.'
}
Assert-NoReparseInTree -Root $installDirectory

if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
    throw 'The product ownership marker or install manifest is missing. Nothing was removed.'
}

$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$marker = Get-Content -Raw -LiteralPath $markerPath | ConvertFrom-Json
$recordedManifestRoot = [IO.Path]::GetFullPath([string]$manifest.installDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)
$recordedMarkerRoot = [IO.Path]::GetFullPath([string]$marker.installDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar)

if ([int]$manifest.schemaVersion -ne $schemaVersion -or
    [int]$marker.schemaVersion -ne $schemaVersion -or
    $manifest.product -cne $productName -or $marker.product -cne $productName -or
    $manifest.productId -cne $productId -or $marker.productId -cne $productId -or
    -not [string]::Equals($recordedManifestRoot, $installDirectory, [StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::Equals($recordedMarkerRoot, $installDirectory, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The product ownership identity is invalid. Nothing was removed.'
}

$manifestId = [Guid]::Empty
$markerId = [Guid]::Empty
if (-not [Guid]::TryParse([string]$manifest.installId, [ref]$manifestId) -or
    -not [Guid]::TryParse([string]$marker.installId, [ref]$markerId) -or
    $manifestId -eq [Guid]::Empty -or $manifestId -ne $markerId -or
    [string]::IsNullOrWhiteSpace([string]$manifest.version) -or
    $manifest.version -cne $marker.version) {
    throw 'The product ownership marker and install manifest do not match. Nothing was removed.'
}

$startMenuRoot = Get-ShellRoot -OverrideVariable 'COMMUNITY_RELAY_SETUP_START_MENU_ROOT' -SpecialFolder StartMenu
$desktopRoot = Get-ShellRoot -OverrideVariable 'COMMUNITY_RELAY_SETUP_DESKTOP_ROOT' -SpecialFolder DesktopDirectory
$productStartMenu = Join-Path $startMenuRoot 'Programs\Community Relay POC'
$exactProductShortcuts = @(
    [IO.Path]::GetFullPath((Join-Path $productStartMenu 'Community Relay POC.lnk')),
    [IO.Path]::GetFullPath((Join-Path $productStartMenu 'Uninstall Community Relay POC.lnk')),
    [IO.Path]::GetFullPath((Join-Path $desktopRoot 'Community Relay POC.lnk'))
)
$allowedShortcuts = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($exactShortcut in $exactProductShortcuts) {
    [void]$allowedShortcuts.Add($exactShortcut)
}
$seenShortcuts = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($recordedShortcut in @($manifest.shortcuts)) {
    $fullShortcut = [IO.Path]::GetFullPath([string]$recordedShortcut)
    if (-not $allowedShortcuts.Contains($fullShortcut) -or -not $seenShortcuts.Add($fullShortcut)) {
        throw 'The install manifest contains a shortcut that is not owned by Community Relay POC. Nothing was removed.'
    }
}

$trackedFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$embeddedTrackedFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$embeddedTrackedFilesJson = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($embeddedTrackedFilesBase64))
$decodedEmbeddedTrackedFiles = ConvertFrom-Json -InputObject $embeddedTrackedFilesJson
if ($decodedEmbeddedTrackedFiles -isnot [Array] -or $decodedEmbeddedTrackedFiles.Count -eq 0) {
    throw 'The embedded uninstall allowlist is invalid. Nothing was removed.'
}
foreach ($embeddedRelativeFile in $decodedEmbeddedTrackedFiles) {
    if (-not $embeddedTrackedFiles.Add([string]$embeddedRelativeFile)) {
        throw 'The embedded uninstall allowlist contains a duplicate path. Nothing was removed.'
    }
}
foreach ($relativeFile in @($manifest.files)) {
    $relative = ([string]$relativeFile).Replace('/', [IO.Path]::DirectorySeparatorChar).Replace('\', [IO.Path]::DirectorySeparatorChar).TrimStart([IO.Path]::DirectorySeparatorChar)
    $segments = $relative.Split([IO.Path]::DirectorySeparatorChar)
    if ([string]::IsNullOrWhiteSpace($relative) -or
        $segments -contains '' -or $segments -contains '.' -or $segments -contains '..') {
        throw "Unsafe path in tracked install inventory: $relativeFile"
    }
    $target = Get-SafeInstalledPath -RelativePath $relative
    if (-not $embeddedTrackedFiles.Contains($relative) -or
        -not $trackedFiles.Add($relative) -or
        -not (Test-Path -LiteralPath $target -PathType Leaf)) {
        throw "The tracked install inventory is invalid or incomplete: $relative"
    }
}
if ($trackedFiles.Count -ne $embeddedTrackedFiles.Count -or
    -not $trackedFiles.Contains($markerFileName) -or
    -not $trackedFiles.Contains($uninstallerFileName)) {
    throw 'The install manifest does not track its required ownership files. Nothing was removed.'
}

if (-not $Silent) {
    $answer = Read-Host "Uninstall Community Relay POC from '$installDirectory'? Settings and authorization data will be kept. [y/N]"
    if ($answer -notmatch '^(?i:y|yes)$') {
        Write-Host 'Uninstall cancelled.'
        exit 0
    }
}

$applicationPath = Join-Path $installDirectory 'CommunityRelay.exe'
Get-Process -Name 'CommunityRelay' -ErrorAction SilentlyContinue | ForEach-Object {
    try {
        if ([string]::Equals($_.Path, $applicationPath, [StringComparison]::OrdinalIgnoreCase)) {
            Stop-Process -Id $_.Id -Force -ErrorAction Stop
        }
    }
    catch {
        # A process owned by another user may not expose its Path. It is left untouched.
    }
}

$removedStartMenuShortcut = $false
foreach ($shortcut in $seenShortcuts) {
    Assert-NoReparseInAncestry -Path $shortcut
    if (Test-Path -LiteralPath $shortcut -PathType Container) {
        throw "A directory occupies an exact product shortcut path: $shortcut"
    }
    Remove-Item -LiteralPath $shortcut -Force -ErrorAction SilentlyContinue
    if ([string]::Equals($shortcut, $exactProductShortcuts[0], [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($shortcut, $exactProductShortcuts[1], [StringComparison]::OrdinalIgnoreCase)) {
        $removedStartMenuShortcut = $true
    }
}
if ($removedStartMenuShortcut -and
    (Test-Path -LiteralPath $productStartMenu -PathType Container) -and
    -not (Get-ChildItem -LiteralPath $productStartMenu -Force | Select-Object -First 1)) {
    Remove-Item -LiteralPath $productStartMenu -Force -ErrorAction SilentlyContinue
}

if ($env:TEMP -and (Test-Path -LiteralPath $env:TEMP -PathType Container)) {
    Set-Location -LiteralPath $env:TEMP
}
else {
    Set-Location -LiteralPath ([IO.Path]::GetDirectoryName($installDirectory))
}

Assert-NoReparseInTree -Root $installDirectory
foreach ($relativeFile in @($manifest.files)) {
    $relative = ([string]$relativeFile).Replace('/', [IO.Path]::DirectorySeparatorChar).Replace('\', [IO.Path]::DirectorySeparatorChar).TrimStart([IO.Path]::DirectorySeparatorChar)
    $target = Get-SafeInstalledPath -RelativePath $relative
    if (-not [string]::Equals($target, $scriptPath, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $target -Force
    }
}

Remove-Item -LiteralPath $manifestPath -Force
Remove-Item -LiteralPath $scriptPath -Force

if (Test-Path -LiteralPath $installDirectory -PathType Container) {
    Assert-NoReparseInTree -Root $installDirectory
    Get-ChildItem -LiteralPath $installDirectory -Directory -Recurse -Force |
        Sort-Object { $_.FullName.Length } -Descending |
        ForEach-Object {
            if (-not (Get-ChildItem -LiteralPath $_.FullName -Force | Select-Object -First 1)) {
                Remove-Item -LiteralPath $_.FullName -Force
            }
        }

    if (-not (Get-ChildItem -LiteralPath $installDirectory -Force | Select-Object -First 1)) {
        Remove-Item -LiteralPath $installDirectory -Force
        Write-Host 'Community Relay POC program files were uninstalled. Settings and authorization data were preserved.'
    }
    else {
        Write-Host 'Community Relay POC program files were removed; untracked files in the custom install folder were preserved.'
    }
}
