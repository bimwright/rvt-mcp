#Requires -Version 5.1
<#
.SYNOPSIS
  Build the end-user RvtMcp setup ZIP.

.DESCRIPTION
  Produces a single client-facing archive that contains:
    - a self-contained win-x64 MCP server executable under server/
    - per-Revit plugin ZIPs under plugins/
    - install.ps1 and uninstall.ps1 entrypoints
    - manifest.json with file hashes and release metadata

  This is the client installer path. It does not require the target machine to
  have the .NET SDK or the source repository.

.EXAMPLE
  pwsh scripts/package-client-setup.ps1
  pwsh scripts/package-client-setup.ps1 -Version 0.4.0
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Config = 'Release',

    [string]$RepoRoot,

    [string]$Version,

    [string]$OutputDir,

    # Package an uncommitted working tree for a throwaway test build.
    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'

if (-not $RepoRoot) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}

$RepoRoot = (Resolve-Path $RepoRoot).Path

# A package must match a commit, otherwise testers exercise code nobody can
# identify. -AllowDirty marks the manifest instead.
$dirty = $false
$status = $null
try { $status = & git -C $RepoRoot status --porcelain 2>$null } catch { $status = $null } # no git: commit stays unknown, as below
if ($status) {
    if (-not $AllowDirty) { throw 'Working tree has uncommitted changes. Commit them so the package matches a commit, or pass -AllowDirty for a throwaway test package.' }
    $dirty = $true
    Write-Warning 'Packaging a dirty working tree (-AllowDirty): manifest records dirty=true.'
}
if (-not $OutputDir) {
    $OutputDir = Join-Path $RepoRoot 'build\client-setup'
}

if (-not $Version) {
    $serverJsonPath = Join-Path $RepoRoot 'server.json'
    $Version = ((Get-Content -Raw -Path $serverJsonPath) | ConvertFrom-Json).version
}

if (-not $Version) {
    throw 'Could not determine setup version. Pass -Version explicitly.'
}

$displayVersion = if ($Version.StartsWith('v')) { $Version } else { "v$Version" }
$stageRoot = Join-Path $OutputDir 'stage'
$serverStage = Join-Path $stageRoot 'server'
$pluginsStage = Join-Path $stageRoot 'plugins'

if (Test-Path $stageRoot) {
    Remove-Item -Path $stageRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $serverStage -Force | Out-Null
New-Item -ItemType Directory -Path $pluginsStage -Force | Out-Null

Write-Host "=== package-client-setup.ps1 ==="
Write-Host ("RepoRoot : {0}" -f $RepoRoot)
Write-Host ("Version  : {0}" -f $displayVersion)
Write-Host ("Config   : {0}" -f $Config)

$serverProject = Join-Path $RepoRoot 'src\server\RvtMcp.Server.csproj'
Write-Host ""
Write-Host "[server] publishing self-contained win-x64 executable"
& dotnet publish $serverProject `
    -c $Config `
    -r win-x64 `
    --self-contained true `
    /p:PublishSingleFile=true `
    -o $serverStage

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$publishedServerExe = Join-Path $serverStage 'RvtMcp.Server.exe'
$friendlyServerExe = Join-Path $serverStage 'rvt-mcp.exe'
if (-not (Test-Path $publishedServerExe)) {
    throw "Published server executable not found: $publishedServerExe"
}
Move-Item -Path $publishedServerExe -Destination $friendlyServerExe -Force
Write-Host ("[server] staged -> {0}" -f $friendlyServerExe)

$pluginProjects = @(
    'src\plugin-r22\RvtMcp.Plugin.R22.csproj',
    'src\plugin-r23\RvtMcp.Plugin.R23.csproj',
    'src\plugin-r24\RvtMcp.Plugin.R24.csproj',
    'src\plugin-r25\RvtMcp.Plugin.R25.csproj',
    'src\plugin-r26\RvtMcp.Plugin.R26.csproj',
    'src\plugin-r27\RvtMcp.Plugin.R27.csproj'
)

Write-Host ""
Write-Host "[plugins] building Revit shells without local auto-deploy"
foreach ($relativeProject in $pluginProjects) {
    $project = Join-Path $RepoRoot $relativeProject
    Write-Host ("[plugins] build {0}" -f $relativeProject)
    & dotnet build $project -c $Config /p:RvtMcpSkipDeploy=true
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed for $relativeProject with exit code $LASTEXITCODE"
    }
}

Write-Host ""
Write-Host "[plugins] staging per-year plugin ZIPs"
$stagePluginScript = Join-Path $RepoRoot 'scripts\stage-plugin-zip.ps1'
& $stagePluginScript -Config $Config -RepoRoot $RepoRoot
if ($LASTEXITCODE -ne 0) {
    throw "stage-plugin-zip.ps1 failed with exit code $LASTEXITCODE"
}

$pluginZipRoot = Join-Path $RepoRoot 'build\plugin-zip'
$pluginZips = @(Get-ChildItem -Path $pluginZipRoot -Filter 'RvtMcp.Plugin.R*.zip' -File | Sort-Object Name)
if ($pluginZips.Count -eq 0) {
    throw "No plugin ZIPs found in $pluginZipRoot"
}
foreach ($zip in $pluginZips) {
    Copy-Item -Path $zip.FullName -Destination $pluginsStage -Force
}

Copy-Item -Path (Join-Path $RepoRoot 'scripts\install.ps1') -Destination (Join-Path $stageRoot 'install.ps1') -Force
Copy-Item -Path (Join-Path $RepoRoot 'AGENTS.md') -Destination (Join-Path $stageRoot 'AGENTS.md') -Force
Copy-Item -Path (Join-Path $RepoRoot 'scripts\uninstall-all.ps1') -Destination (Join-Path $stageRoot 'uninstall.ps1') -Force
Copy-Item -Path (Join-Path $RepoRoot 'scripts\uninstall-all.ps1') -Destination (Join-Path $stageRoot 'uninstall-all.ps1') -Force

function Get-RelativePackagePath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Path
    )
    return $Path.Substring($Root.Length).TrimStart('\', '/') -replace '\\', '/'
}

function Get-Sha256Lower {
    param([Parameter(Mandatory = $true)][string]$Path)
    return ((Get-FileHash -Algorithm SHA256 -Path $Path).Hash).ToLowerInvariant()
}

$commit = ''
try {
    $commit = (& git -C $RepoRoot rev-parse HEAD).Trim()
} catch {
    $commit = ''
}

$pluginManifest = @()
foreach ($zip in @(Get-ChildItem -Path $pluginsStage -Filter 'RvtMcp.Plugin.R*.zip' -File | Sort-Object Name)) {
    if ($zip.BaseName -match 'R(\d{2})$') {
        $year = 2000 + [int]$Matches[1]
        $pluginManifest += [ordered]@{
            year = $year
            path = Get-RelativePackagePath -Root $stageRoot -Path $zip.FullName
            sha256 = Get-Sha256Lower -Path $zip.FullName
            bytes = $zip.Length
        }
    }
}

$fileManifest = @()
foreach ($file in @(Get-ChildItem -Path $stageRoot -File -Recurse | Sort-Object FullName)) {
    $fileManifest += [ordered]@{
        path = Get-RelativePackagePath -Root $stageRoot -Path $file.FullName
        sha256 = Get-Sha256Lower -Path $file.FullName
        bytes = $file.Length
    }
}

$manifest = [ordered]@{
    name = 'RvtMcp.Setup'
    version = $Version
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    commit = $commit
    dirty = $dirty
    platform = 'win-x64'
    supportedRevitYears = @(2022, 2023, 2024, 2025, 2026, 2027)
    server = [ordered]@{
        command = 'server/rvt-mcp.exe'
        selfContained = $true
        requiresDotnet = $false
    }
    plugins = $pluginManifest
    files = $fileManifest
}

$manifestPath = Join-Path $stageRoot 'manifest.json'
$manifest | ConvertTo-Json -Depth 20 | Set-Content -Path $manifestPath -Encoding UTF8

$setupZip = Join-Path $OutputDir ("RvtMcp.Setup-{0}-win-x64.zip" -f $displayVersion)
if (Test-Path $setupZip) {
    Remove-Item -Path $setupZip -Force
}
Compress-Archive -Path (Join-Path $stageRoot '*') -DestinationPath $setupZip -Force

Write-Host ""
Write-Host "=== client setup package summary ==="
Write-Host ("Output : {0}" -f $setupZip)
Write-Host ("Server : {0}" -f (Get-RelativePackagePath -Root $stageRoot -Path $friendlyServerExe))
Write-Host ("Plugins: {0}" -f ($pluginManifest.Count))
Write-Host ("Files  : {0}" -f ($fileManifest.Count + 1))
